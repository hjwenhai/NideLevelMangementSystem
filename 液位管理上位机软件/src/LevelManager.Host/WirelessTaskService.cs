using System.Collections.Concurrent;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Host;
public sealed class WirelessTaskService(IWirelessTaskStore tasks,IControlStore controls,IConfigurationStore configs,
    AcquisitionService acquisition,ChannelAccess access,RuntimeOperationGate runtime,WirelessRuntime view,
    TimeProvider clock,ControlService controlService,IEventWriter events,ProductAuthorization authorization,IUserStore users,IRolePermissionStore permissions,MotionEvidenceGuard evidence,ILogger<WirelessTaskService> logger):BackgroundService
{
    private sealed record Running(WirelessTaskRecord Record,WirelessDevice Device,CancellationTokenSource Cancel,Task Completion){public int StopRequested;}
    private readonly ConcurrentDictionary<Guid,Running> running=new();
    private readonly ConcurrentDictionary<Guid,SemaphoreSlim> lanes=new();
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationToken hostToken;
    private readonly ConcurrentDictionary<Guid,Guid> currentTasks=new();
    public bool IsCurrent(Guid id)=>currentTasks.Values.Contains(id);
    public Task Ready=>ready.Task;
    private sealed class Publishing(IWirelessTaskStore inner,WirelessRuntime view):IWirelessTaskStore
    {
        public async Task SaveAsync(WirelessTaskRecord r,CancellationToken ct=default){await inner.SaveAsync(r,ct);view.Publish(r);}
        public Task CreateAsync(WirelessTaskRecord r,CancellationToken ct=default)=>inner.CreateAsync(r,ct);
        public Task<WirelessTaskRecord?> GetAsync(Guid id,CancellationToken ct=default)=>inner.GetAsync(id,ct);
        public Task<IReadOnlyList<WirelessTaskRecord>> ListAsync(Guid? tankId=null,bool activeOnly=false,CancellationToken ct=default)=>inner.ListAsync(tankId,activeOnly,ct);
        public Task<IReadOnlyList<WirelessTaskRecord>> LastSuccessfulAsync(CancellationToken ct=default)=>inner.LastSuccessfulAsync(ct);
        public Task RecoverAsync(DateTimeOffset now,CancellationToken ct=default)=>inner.RecoverAsync(now,ct);
        public Task ReviewAsync(Guid id,AuditActor actor,string reason,DateTimeOffset now,CancellationToken ct=default)=>inner.ReviewAsync(id,actor,reason,now,ct);
    }
    public async Task<WirelessTaskRecord> RequestAsync(Guid tankId,long revision,int points,bool confirmed,AuditActor actor,CancellationToken ct,long? scheduledPermissionRevision=null,long? scheduledSecurityVersion=null)
    {
        actor.Require(AppPermission.AverageTemperatureStart);if(!confirmed||points is <1 or >16)throw new ArgumentException("请选择1～16点并确认现场条件");await Ready.WaitAsync(ct);await evidence.Ready.WaitAsync(ct);await runtime.Gate.WaitAsync(ct);
        try
        {
            evidence.RequireNewActionAllowed();authorization.RequireAverageModule();authorization.RequireLicensed("新的平均测温任务");PermissionPolicy.RequireScheduledAuthority(await users.FindByIdAsync(actor.AccountId,ct),(await permissions.GetPermissionsAsync(ct)).Revision,scheduledPermissionRevision,scheduledSecurityVersion);var config=await configs.GetAsync(ct);if(revision!=config.Revision)throw new RevisionConflictException();var tank=config.Tanks.FirstOrDefault(t=>t.Id==tankId)??throw new ArgumentException("储罐不存在");var channel=config.Channels.First(c=>c.Id==tank.ChannelId);
            if(!tank.Enabled||!channel.Enabled||tank.TemperatureMode!=TemperatureMode.Wireless||!tank.Controls.WirelessMeasurementEnabled)throw new InvalidOperationException("本罐未启用无线浮子平均温度测量");if(WirelessDevice.SupportReason(channel.Protocol) is {} reason)throw new InvalidOperationException(reason);
            if((await controls.ListAsync(tankId,true,ct)).Count>0)throw new InvalidOperationException("本罐仍有执行中或待核查的操作");var current=await acquisition.CurrentAsync(ct);var sample=current.Readings.First(r=>r.TankId==tankId);if(!current.Running||!sample.FloatPositionMm.IsValid||sample.InstrumentFault!=0)throw new InvalidOperationException("启动前请确认采集、实际位置和故障状态正常");
            var record=new WirelessTaskRecord{ScheduledPermissionRevision=scheduledPermissionRevision,ScheduledSecurityVersion=scheduledSecurityVersion,Target=new(channel,tank,revision,current.SessionId,sample.GatewayBootId,sample.GatewayBindingVersion),RequestedPoints=points,ActorId=actor.AccountId,ActorName=actor.DisplayName,CreatedAtUtc=clock.GetUtcNow()};await tasks.CreateAsync(record,ct);currentTasks[tankId]=record.Id;view.Publish(record);
            var cancel=CancellationTokenSource.CreateLinkedTokenSource(hostToken);var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var device=new WirelessDevice(record.Target,access,controls,configs,runtime,clock,authorization,tasks,events,users,permissions,evidence);running[record.Id]=new(record,device,cancel,completion.Task);
            _=Task.Run(async()=>
            {
                var lane=lanes.GetOrAdd(channel.Id,_=>new SemaphoreSlim(1));var entered=false;
                try
                {
                    await lane.WaitAsync(cancel.Token);entered=true;await new WirelessWorkflow(new Publishing(tasks,view),new WorkflowTime(clock)).RunAsync(record,device,cancel.Token);
                }
                catch(Exception e)
                {
                    logger.LogError(e,"测温任务执行或记录失败 {TaskId}",record.Id);
                    try{var latest=await tasks.GetAsync(record.Id);if(latest is not null&&latest.HoldsTank&&latest.Stage!=WirelessStage.RequiresReview){latest=latest with{Version=latest.Version+1,Stage=latest.WriteIntentRecorded?WirelessStage.RequiresReview:WirelessStage.Failed,FinishedAtUtc=clock.GetUtcNow(),Message="任务未继续推进："+e.Message};await tasks.SaveAsync(latest);view.Publish(latest);}}catch(Exception save){logger.LogCritical(save,"测温任务中断无法保存 {TaskId}",record.Id);}
                }
                finally{if(entered)lane.Release();running.TryRemove(record.Id,out _);cancel.Dispose();completion.TrySetResult();}
            });return record;
        }finally{runtime.Gate.Release();}
    }
    public async Task CancelAndStopAsync(Guid id,bool confirmed,AuditActor actor,CancellationToken ct)
    {
        actor.Require(AppPermission.InstrumentStop);if(!confirmed)throw new ArgumentException("请确认取消后续测温并请求停止");await Ready.WaitAsync(ct);
        if(!running.TryGetValue(id,out var item))throw new InvalidOperationException("本任务未在当前程序执行，请人工核查原设备状态");if(Interlocked.Exchange(ref item.StopRequested,1)!=0)throw new InvalidOperationException("本任务已受理停止请求，未重复发送");try{await events.WriteEventAsync(new("操作日志","wireless.cancel",actor.AccountId,actor.DisplayName,"取消后续测温并请求停止","已受理",item.Record.Target.Tank.Id,item.Record.Target.Tank.Code,new{TaskId=id}),ct);}catch{Interlocked.Exchange(ref item.StopRequested,0);throw;}try{item.Cancel.Cancel();}catch(ObjectDisposedException){}await item.Completion.WaitAsync(hostToken);
        var latest=await tasks.GetAsync(id,hostToken)??throw new InvalidOperationException("任务记录不存在");if(!latest.WriteIntentRecorded)return;
        var stops=(await controls.ListAsync(latest.Target.Tank.Id,ct:hostToken)).Where(r=>r.MeasurementTaskId==id&&r.Cycle==ControlCycle.IndependentStop&&r.State==ControlState.Unknown).ToArray();
        if(stops.Length>0)throw new InvalidOperationException("原停止结果未知，未重复发送；请核对现场并人工核查");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(hostToken);deadline.CancelAfter(TimeSpan.FromSeconds(40));await item.Device.CommandAsync(latest with{ActorId=actor.AccountId,ActorName=actor.DisplayName},InstrumentCommand.Stop,deadline.Token);
        // Acknowledgment does not mark the task mechanically stopped. The operator must review it.
    }
    public async Task ReviewAsync(Guid id,bool confirmed,string reason,AuditActor actor,CancellationToken ct)
    {
        if(!confirmed)throw new ArgumentException("请确认已经完成现场核查");await Ready.WaitAsync(ct);await runtime.Gate.WaitAsync(ct);
        try
        {
            if(running.ContainsKey(id))throw new InvalidOperationException("任务仍在执行或收尾");var record=await tasks.GetAsync(id,ct)??throw new ArgumentException("任务不存在");if(record.Stage!=WirelessStage.RequiresReview)throw new InvalidOperationException("此任务无需人工核查");
            foreach(var control in (await controls.ListAsync(record.Target.Tank.Id,true,ct)).Where(r=>r.MeasurementTaskId==id))
            {if(control.State!=ControlState.Unknown)throw new InvalidOperationException("原操作仍在处理");await controls.ReviewAsync(control.Id,actor,reason,clock.GetUtcNow(),ct);}
            await tasks.ReviewAsync(id,actor,reason,clock.GetUtcNow(),ct);view.Publish((await tasks.GetAsync(id,ct))!);
        }finally{runtime.Gate.Release();}
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        hostToken=stoppingToken;await controlService.Ready.WaitAsync(stoppingToken);
        while(!stoppingToken.IsCancellationRequested)
        {
            try{await tasks.RecoverAsync(clock.GetUtcNow(),stoppingToken);foreach(var r in await tasks.LastSuccessfulAsync(stoppingToken))view.Publish(r);foreach(var r in await tasks.ListAsync(activeOnly:true,ct:stoppingToken))view.Publish(r);ready.TrySetResult();break;}
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){return;}catch(Exception e){logger.LogCritical(e,"测温记录恢复失败，暂不受理任务");await Task.Delay(1000,stoppingToken);}
        }
        try{await Task.Delay(Timeout.Infinite,stoppingToken);}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
        await Task.WhenAll(running.Values.Select(v=>v.Completion));
    }
}
