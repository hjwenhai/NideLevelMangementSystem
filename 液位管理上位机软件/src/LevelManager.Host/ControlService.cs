using System.Collections.Concurrent;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;
using Naide.LevelManager.Communication;
namespace Naide.LevelManager.Host;
public sealed class ControlService(RuntimeOperationGate runtime,IConfigurationStore configs,IControlStore records,
    AcquisitionService acquisition,ChannelAccess access,IWirelessTaskStore wirelessTasks,ProductAuthorization authorization,IUserStore users,IRolePermissionStore permissions,MotionEvidenceGuard evidence,TimeProvider clock,ILogger<ControlService> logger):BackgroundService
{
    private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Guid,(ControlRecord Record,CancellationTokenSource Cancel,Task Task)> active=new();
    private CancellationToken hostToken;
    private readonly ConcurrentDictionary<(Guid,ControlCycle),Guid> currentResults=new();
    public bool IsCurrent(ControlRecord r)=>r.State is ControlState.Prepared or ControlState.Queued or ControlState.Checking or ControlState.WriteIntent or ControlState.Submitted or ControlState.Unknown||currentResults.GetValueOrDefault((r.Target.Tank.Id,r.Cycle))==r.Id;
    public Task Ready=>ready.Task;
    public async Task<ControlRecord> RequestAsync(Guid tankId,long revision,InstrumentCommand command,bool confirmed,AuditActor actor,CancellationToken ct,decimal? targetLevelMm=null,string? reason=null)
    {
        actor.Require(CommandPermission(command));
        if(command==InstrumentCommand.RemoteLevel){if(targetLevelMm is null||string.IsNullOrWhiteSpace(reason)||reason.Length>1000)throw new ArgumentException("请填写目标液位和修改原因（最多1000字）");RemoteLevelRules.ToRaw(targetLevelMm.Value);}
        else if(targetLevelMm.HasValue||!string.IsNullOrEmpty(reason))throw new ArgumentException("此操作不使用目标液位或修改原因");
        if(!confirmed||!Enum.IsDefined(command))throw new ArgumentException("请核对储罐和动作后确认执行");await Ready.WaitAsync(ct);await evidence.Ready.WaitAsync(ct);
        await runtime.Gate.WaitAsync(ct);try
        {
            var config=await configs.GetAsync(ct);if(config.Revision!=revision)throw new RevisionConflictException();
            if(!ProductTrust.CommandAvailable(command,authorization.EnabledModules))throw new UnauthorizedAccessException("本操作尚未获得供方授权");
            var tank=config.Tanks.FirstOrDefault(t=>t.Id==tankId)??throw new ArgumentException("储罐不存在");var channel=config.Channels.First(c=>c.Id==tank.ChannelId);
            var activeBlocker=active.Values.FirstOrDefault(v=>v.Record.Target.Tank.Id==tankId&&(command!=InstrumentCommand.Stop||v.Record.Cycle==ControlCycle.IndependentStop)).Record;
            if(activeBlocker is not null)throw new InvalidOperationException(DescribeBlocker(activeBlocker));
            if(command==InstrumentCommand.RemoteLevel&&channel.Protocol==AcquisitionProtocol.IfxL25)throw new ArgumentException("IFX30000未提供远程液位修改功能");
            if(!tank.Enabled||!channel.Enabled)throw new InvalidOperationException("储罐或通道已停用");
            if((await wirelessTasks.ListAsync(tankId,true,ct)).Count>0)throw new InvalidOperationException("本罐有平均测温任务，请在测温任务中停止、取消或核查后再操作");
            if(!tank.Controls.AllowedCommands.Contains(command)&&!(command==InstrumentCommand.Stop&&(await records.ListAsync(tankId,true,ct)).Count>0))throw new UnauthorizedAccessException("本罐未配置开放该操作");
            var current=await acquisition.CurrentAsync(ct);if(!current.Running)throw new InvalidOperationException("请先启动采集并确认设备状态");
            var sample=current.Readings.First(r=>r.TankId==tankId);
            ControlReadiness.RequireCurrentData(sample,command);
            var recovery=command==InstrumentCommand.MeasureI&&sample.InstrumentStatus.HasValue&&!CommandEncoding.ModeMatches(sample.InstrumentStatus.Value,InstrumentCommand.MeasureI,channel.Protocol);
            if(command!=InstrumentCommand.Stop&&!recovery)evidence.RequireNewActionAllowed();
            if(command!=InstrumentCommand.Stop&&!recovery)authorization.RequireLicensed("新的仪表操作");
            var record=new ControlRecord{TargetLevelMm=targetLevelMm,AdjustmentReason=reason?.Trim(),Target=new(channel,tank,config.Revision,current.SessionId,sample.GatewayBootId,sample.GatewayBindingVersion),Command=command,IsRecovery=recovery,Cycle=command==InstrumentCommand.Stop?ControlCycle.IndependentStop:ControlCycle.Normal,State=ControlState.Queued,ActorId=actor.AccountId,ActorName=actor.DisplayName,CreatedAtUtc=clock.GetUtcNow(),Result="已受理，尚未发送"};
            await records.CreateAsync(record,ct);currentResults[(record.Target.Tank.Id,record.Cycle)]=record.Id;
            if(command==InstrumentCommand.Stop)foreach(var operation in active.Values.Where(v=>v.Record.Target.Tank.Id==tankId&&v.Record.Cycle==ControlCycle.Normal&&v.Record.State is not(ControlState.ReadbackConfirmed or ControlState.Rejected)))operation.Cancel.Cancel();
            var cancel=CancellationTokenSource.CreateLinkedTokenSource(hostToken);var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            active[record.Id]=(record,cancel,completion.Task);
            _=Task.Run(async()=>{try{await Run(record,cancel.Token);}finally{active.TryRemove(record.Id,out _);cancel.Dispose();completion.TrySetResult();}});
            return record;
        }finally{runtime.Gate.Release();}
    }
    private static AppPermission CommandPermission(InstrumentCommand command)=>command==InstrumentCommand.Stop?AppPermission.InstrumentStop:command==InstrumentCommand.RemoteLevel?AppPermission.RemoteLevelAdjust:AppPermission.InstrumentControl;
    private async Task Revalidate(ControlRecord record,CancellationToken ct)
    {
        var config=await configs.GetAsync(ct);var tank=config.Tanks.FirstOrDefault(t=>t.Id==record.Target.Tank.Id);var channel=tank is null?null:config.Channels.FirstOrDefault(c=>c.Id==tank.ChannelId);
        if(tank is null||channel is null||!tank.Enabled||!channel.Enabled||tank.Address!=record.Target.Tank.Address||channel with{Name=""}!=record.Target.Channel with{Name=""})throw new InvalidOperationException("原控制对象已改变或停用");
        if(!record.IsReset&&!ProductTrust.CommandAvailable(record.Command!.Value,authorization.EnabledModules))throw new UnauthorizedAccessException("该操作授权已撤销，未继续发送");
        if(!record.IsReset&&!tank.Controls.AllowedCommands.Contains(record.Command!.Value)&&record.Command!=InstrumentCommand.Stop)throw new UnauthorizedAccessException("该操作已被撤销，未继续发送");
    }
    private async Task Run(ControlRecord record,CancellationToken ct)
    {
        var current=record;
        IDisposable? levelScope=null;
        try
        {
            // Finish bounded calibration authentication cleanup before Stop authenticates.
            if(record.Command==InstrumentCommand.Stop)await Task.WhenAll(active.Values.Where(v=>v.Record.Target.Tank.Id==record.Target.Tank.Id&&v.Record.Command==InstrumentCommand.RemoteLevel&&!v.Record.IsReset).Select(v=>v.Task));
            if(record.Command==InstrumentCommand.RemoteLevel&&!record.IsReset)levelScope=acquisition.SuspendLevelAdjustment(record.Target.Tank.Id);
            current=current with{State=ControlState.Checking,Result="正在核验设备和执行条件"};await records.UpdateAsync(current,ct);
            async Task Checkpoint(ControlCheckpoint step)
            {
                await runtime.Gate.WaitAsync(ct);try
                {
                    await Revalidate(current,ct);
                    current=step==ControlCheckpoint.WriteAcknowledged?current with{State=ControlState.Submitted,Result=current.Command==InstrumentCommand.RemoteLevel?"修改已提交，正在核对偏移回读及认证清零":"已取得协议写回执，正在核验实际模式"}:current with{State=ControlState.WriteIntent,WriteIntentAtUtc=current.WriteIntentAtUtc??clock.GetUtcNow(),Result=step==ControlCheckpoint.AuthorizationWrite?"已记录控制认证写入意图":step==ControlCheckpoint.PreparingWrite?"已记录握手准备写入意图":"已记录动作写入意图"};
                    await records.UpdateAsync(current,ct);
                }finally{runtime.Gate.Release();}
            }
            using var dispatchGuard=new RegisterWriteGuardScope(async token=>
            {
                if(current.FirstDispatchAtUtc.HasValue&&(current.Command!=InstrumentCommand.RemoteLevel||current.IsReset))return;
                await runtime.Gate.WaitAsync(token);try
                {
                await Revalidate(current,token);
                var dispatchUser=await users.FindByIdAsync(current.ActorId,token);if(dispatchUser is null||!dispatchUser.Enabled)throw new UnauthorizedAccessException("原操作账号已失效，未发送新的写入");
                var dispatchActor=await permissions.ResolveAsync(dispatchUser,token);dispatchActor.Require(current.IsAutomaticReset?CommandPermission(current.Command!.Value):current.IsReset?(current.RelatedControlId.HasValue||current.GatewayResetExpectation is not null?AppPermission.ControlReview:AppPermission.RecoveryManage):CommandPermission(current.Command!.Value));
                if(current.Command!=InstrumentCommand.Stop&&!current.IsReset&&!current.IsRecovery)
                {
                    evidence.RequireNewActionAllowed();authorization.RequireLicensed("排队等待的仪表操作");var user=await users.FindByIdAsync(current.ActorId,token);if(user is null||!user.Enabled||!(await permissions.ResolveAsync(user,token)).Allows(CommandPermission(current.Command!.Value)))throw new UnauthorizedAccessException("原操作账号权限已失效，未发送新动作");
                }
                if(current.FirstDispatchAtUtc.HasValue)return;
                var dispatched=current with{FirstDispatchAtUtc=clock.GetUtcNow()};await evidence.CommitAsync(new(Guid.NewGuid(),current.Id,current.IsReset?"gateway-result-reset":"instrument-command",clock.GetUtcNow(),current.Target,current.ActorId,current.ActorName),async()=>{await records.UpdateAsync(dispatched,token);current=dispatched;},token);
                }finally{runtime.Gate.Release();}
            });
            async Task PersistLevel(LevelAdjustmentEvidence value){await runtime.Gate.WaitAsync(CancellationToken.None);try{current=current with{LevelAdjustment=value};await records.UpdateAsync(current,CancellationToken.None);}finally{runtime.Gate.Release();}}
            var result=await access.UseAsync(current.Target,current.Cycle==ControlCycle.IndependentStop?RegisterPriority.Stop:RegisterPriority.Control,
                (client,token)=>current.IsReset?new ControlProtocolDriver(clock).ResetGatewayAsync(client,current.Target,current.Cycle,Checkpoint,token,current.GatewayResetExpectation):current.Command==InstrumentCommand.RemoteLevel?new RemoteLevelDriver(clock).ExecuteAsync(client,current.Target,current.TargetLevelMm!.Value,Checkpoint,PersistLevel,token):new ControlProtocolDriver(clock).ExecuteAsync(client,current.Target,current.Command!.Value,Checkpoint,false,token,current.IsRecovery),ct);
            current=current with{State=result.State,FinishedAtUtc=clock.GetUtcNow(),Result=result.Message,GatewayResultSnapshot=result.GatewayResult};await records.UpdateAsync(current,CancellationToken.None);
            if(!current.IsReset&&current.GatewayResultSnapshot is not null&&current.State is ControlState.ReadbackConfirmed or ControlState.Rejected)
            {
                if(active.TryGetValue(current.Id,out var running))active[current.Id]=(current,running.Cancel,running.Task);
                await AutomaticallyResetResult(current,ct);
            }
        }
        catch(Exception e)
        {
            var unknown=current.WriteIntentAtUtc.HasValue;
            current=current with{State=unknown?ControlState.Unknown:ControlState.Rejected,FinishedAtUtc=clock.GetUtcNow(),Result=unknown?"操作中断，已进入写入阶段，结果需人工核查；未重发。"+e.Message:"未发送动作："+e.Message};
            try{await records.UpdateAsync(current,CancellationToken.None);}catch(Exception audit){logger.LogCritical(audit,"控制结果无法保存，请核查原对象 {ControlId}",current.Id);}
            logger.LogWarning(e,"仪表操作中断 {ControlId}",current.Id);
        }
        finally{levelScope?.Dispose();}
    }
    private async Task AutomaticallyResetResult(ControlRecord original,CancellationToken ct)
    {
        // The original terminal result is durable before creating the acknowledgement record.
        // A failure here never rewrites a successful/failed action as a different action outcome.
        try
        {
            var reset=new ControlRecord{Target=original.Target,Command=original.Command,Cycle=original.Cycle,IsReset=true,IsAutomaticReset=true,
                RelatedControlId=original.Id,GatewayResetExpectation=original.GatewayResultSnapshot,State=ControlState.Queued,
                ActorId=original.ActorId,ActorName=original.ActorName,CreatedAtUtc=clock.GetUtcNow(),Result="系统自动收尾：本次结果已保存，仅复位对应结果通道，不重发仪表动作"};
            await records.CreateAsync(reset,CancellationToken.None);currentResults[(original.Target.Tank.Id,original.Cycle)]=reset.Id;
            await Run(reset,ct);
        }
        catch(Exception e){logger.LogWarning(e,"本次网关结果已保存，但自动收尾未完成；保留现场结果供核查 {ControlId}",original.Id);}
    }
    public async Task<ControlRecord> ResetAsync(Guid id,AuditActor actor,string reason,bool confirmed,CancellationToken ct,IReadOnlyList<Guid>? reviewedControlIds=null)
    {
        actor.Require(AppPermission.ControlReview);if(!confirmed||string.IsNullOrWhiteSpace(reason)||reason.Length>1000)throw new ArgumentException("请完成现场核查并填写复位原因");await Ready.WaitAsync(ct);await runtime.Gate.WaitAsync(ct);
        try
        {
            var original=await records.GetAsync(id,ct)??throw new ArgumentException("原操作记录不存在");if(!original.Target.Channel.IsGateway)throw new ArgumentException("本接口仅复位网关结果");
            if(active.ContainsKey(id)||original.State is ControlState.Prepared or ControlState.Queued or ControlState.Checking or ControlState.WriteIntent or ControlState.Submitted)throw new InvalidOperationException("原操作仍在处理，不能复位");
            var latest=(await records.ListAsync(original.Target.Tank.Id,ct:ct)).Where(r=>r.Cycle==original.Cycle&&!r.IsReset).OrderByDescending(r=>r.CreatedAtUtc).FirstOrDefault();if(latest is not null&&latest.Id!=original.Id&&latest.CreatedAtUtc>original.CreatedAtUtc)throw new InvalidOperationException("该通道已有更新的操作，请核查最新记录");
            var config=await configs.GetAsync(ct);var tank=config.Tanks.FirstOrDefault(t=>t.Id==original.Target.Tank.Id)??throw new InvalidOperationException("原储罐已删除");var channel=config.Channels.First(c=>c.Id==tank.ChannelId);var current=await acquisition.CurrentAsync(ct);var sample=current.Readings.First(r=>r.TankId==tank.Id);
            if(!current.Running||tank.Address!=original.Target.Tank.Address||channel with{Name=""}!=original.Target.Channel with{Name=""}||sample.GatewayBootId!=original.Target.GatewayBootId||sample.GatewayBindingVersion!=original.Target.GatewayBindingVersion)throw new InvalidOperationException("原设备绑定、网关会话或采集状态已改变，不能写入原通道复位");
            if(original.State==ControlState.Unknown)await records.ReviewAsync(id,actor,reason,clock.GetUtcNow(),ct);
            var reset=new ControlRecord{Target=new(channel,tank,config.Revision,current.SessionId,sample.GatewayBootId,sample.GatewayBindingVersion),Command=original.Command,TargetLevelMm=original.TargetLevelMm,AdjustmentReason=original.AdjustmentReason,LevelAdjustment=original.LevelAdjustment,Cycle=original.Cycle,IsReset=true,RelatedControlId=original.Id,State=ControlState.Queued,ActorId=actor.AccountId,ActorName=actor.DisplayName,CreatedAtUtc=clock.GetUtcNow(),Result="人工已核查，等待复位选定结果通道",ReviewReason=reason.Trim()};
            await records.CreateAsync(reset,ct);currentResults[(reset.Target.Tank.Id,reset.Cycle)]=reset.Id;var cancel=CancellationTokenSource.CreateLinkedTokenSource(hostToken);var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);active[reset.Id]=(reset,cancel,completion.Task);
            _=Task.Run(async()=>{try{await Run(reset,cancel.Token);}finally{active.TryRemove(reset.Id,out _);cancel.Dispose();completion.TrySetResult();}});return reset;
        }finally{runtime.Gate.Release();}
    }
    private async Task<ControlTarget> CurrentGatewayTarget(Guid tankId,CancellationToken ct)
    {
        var config=await configs.GetAsync(ct);var tank=config.Tanks.FirstOrDefault(t=>t.Id==tankId)??throw new ArgumentException("储罐不存在");
        var channel=config.Channels.First(c=>c.Id==tank.ChannelId);
        if(!channel.IsGateway||!channel.Enabled||!tank.Enabled)throw new InvalidOperationException("请选择启用的网关储罐");
        var current=await acquisition.CurrentAsync(ct);if(!current.Running)throw new InvalidOperationException("采集未运行，网关通道状态未知");
        var sample=current.Readings.First(r=>r.TankId==tankId);
        if(sample.AcquisitionSessionId!=current.SessionId||sample.ConfigurationRevision!=config.Revision||!sample.GatewayBootId.HasValue||!sample.GatewayBindingVersion.HasValue)throw new InvalidOperationException("等待当前网关会话及绑定的新读数，不能判定空闲");
        return new(channel,tank,config.Revision,current.SessionId,sample.GatewayBootId,sample.GatewayBindingVersion);
    }
    private static string DescribeBlocker(ControlRecord record)
    {
        var cycle=record.Cycle==ControlCycle.Normal?"正常动作通道":"独立停止通道";
        var action=record.IsReset?"结果复位":record.Command.HasValue?CommandEncoding.Name(record.Command.Value):"原命令未记录";
        var state=record.State==ControlState.Unknown?"结果未知，需现场核查":"仍在执行或收尾";
        return $"{record.Target.Tank.Code} {cycle}被记录 {record.Id} 拦截。{record.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}，{action}，{state}";
    }
    private Task<GatewayControlSnapshot> ReadGateway(ControlTarget target,CancellationToken ct)=>access.UseAsync(target,RegisterPriority.Background,
        (client,token)=>GatewayControlReader.ReadAsync(client,target,clock,token),ct);
    public async Task<GatewayControlSnapshot> GatewayStatusAsync(Guid tankId,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try{
        var target=await CurrentGatewayTarget(tankId,timeout.Token);var result=await ReadGateway(target,timeout.Token);
        var after=await CurrentGatewayTarget(tankId,timeout.Token);
        if(after.ConfigurationRevision!=target.ConfigurationRevision||after.AcquisitionSessionId!=target.AcquisitionSessionId||after.GatewayBootId!=target.GatewayBootId||after.GatewayBindingVersion!=target.GatewayBindingVersion||after.Tank.Id!=target.Tank.Id||after.Tank.Address!=target.Tank.Address||after.Channel!=target.Channel)throw new InvalidOperationException("配置或采集会话在读取期间改变，请刷新状态");
        return result;
        }catch(IOException e){throw new InvalidOperationException("网关状态读取失败，不能判定空闲："+e.Message,e);}
        catch(TimeoutException e){throw new InvalidOperationException("网关状态读取超时，不能判定空闲",e);}
        catch(OperationCanceledException e)when(!ct.IsCancellationRequested){throw new InvalidOperationException("网关状态读取超时，不能判定空闲",e);}
    }
    public async Task<ControlRecord> ResetLiveGatewayAsync(Guid tankId,GatewayControlSnapshot expected,ControlCycle cycle,
        AuditActor actor,string reason,bool confirmed,CancellationToken ct,IReadOnlyList<Guid>? reviewedControlIds=null)
    {
        actor.Require(AppPermission.ControlReview);
        if(!confirmed||string.IsNullOrWhiteSpace(reason)||reason.Length>1000||expected is null||!Enum.IsDefined(cycle))throw new ArgumentException("请选择结果通道、填写现场核查结论并确认");
        var age=clock.GetUtcNow()-expected.ObservedAtUtc;if(age<TimeSpan.Zero||age>TimeSpan.FromSeconds(20))throw new InvalidOperationException("确认的网关状态已过期，请刷新并重新核查");
        await Ready.WaitAsync(ct);await runtime.Gate.WaitAsync(ct);try
        {
            var activeBlocker=active.Values.FirstOrDefault(v=>v.Record.Target.Tank.Id==tankId).Record;
            if(activeBlocker is not null)throw new InvalidOperationException(DescribeBlocker(activeBlocker));
            if((await wirelessTasks.ListAsync(tankId,true,ct)).Count>0)throw new InvalidOperationException("本罐有测温任务，请在平均温度测量中查看本罐任务");
            var target=await CurrentGatewayTarget(tankId,ct);var actual=await ReadGateway(target,ct);GatewayControlReader.RequireSame(actual,expected,cycle);
            if(!actual.Channel(cycle).CanReset)throw new InvalidOperationException(actual.Channel(cycle).State==0?"该通道已空闲，无需复位":"通道仍在排队或执行，禁止复位");
            var unresolved=(await records.ListAsync(tankId,true,ct)).Where(r=>r.Cycle==cycle).ToArray();
            var reviewIds=(reviewedControlIds??[]).ToHashSet();
            if(unresolved.Any(r=>r.State!=ControlState.Unknown))
                throw new InvalidOperationException(DescribeBlocker(unresolved.First(r=>r.State!=ControlState.Unknown)));
            if(!reviewIds.SetEquals(unresolved.Select(r=>r.Id)))
                throw new InvalidOperationException(unresolved.Length>0?DescribeBlocker(unresolved[0])+"。请在当前窗口核查列出的记录后一起复位":"待核查记录已改变，请刷新后重新确认");
            foreach(var original in unresolved)
                await records.ReviewAsync(original.Id,actor,reason,clock.GetUtcNow(),ct);
            var reset=new ControlRecord{Target=target,Command=null,Cycle=cycle,IsReset=true,GatewayResetExpectation=actual,State=ControlState.Queued,
                ActorId=actor.AccountId,ActorName=actor.DisplayName,CreatedAtUtc=clock.GetUtcNow(),RelatedControlId=unresolved.FirstOrDefault()?.Id,ReviewReason=reason.Trim(),Result="现场核查已保存，等待复位所选网关结果通道"};
            await records.CreateAsync(reset,ct);currentResults[(tankId,cycle)]=reset.Id;
            var cancel=CancellationTokenSource.CreateLinkedTokenSource(hostToken);var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            active[reset.Id]=(reset,cancel,completion.Task);_=Task.Run(async()=>{try{await Run(reset,cancel.Token);}finally{active.TryRemove(reset.Id,out _);cancel.Dispose();completion.TrySetResult();}});return reset;
        }finally{runtime.Gate.Release();}
    }
    public async Task<ControlRecord> ResetAfterRestoreAsync(Guid tankId,long revision,ControlCycle cycle,string reason,bool confirmed,AuditActor actor,CancellationToken ct)
    {
        if(actor.Role!=AccountRole.Administrator)throw new UnauthorizedAccessException("仅管理员可复位恢复后待核查的网关结果");if(!confirmed||string.IsNullOrWhiteSpace(reason)||reason.Length>1000||!Enum.IsDefined(cycle))throw new ArgumentException("请明确选择通道、填写核查原因并确认");await Ready.WaitAsync(ct);await evidence.Ready.WaitAsync(ct);await runtime.Gate.WaitAsync(ct);
        try
        {
            if(!evidence.RequiresReview)throw new InvalidOperationException("此入口仅用于恢复业务后的现场核查，请从原操作记录复位结果");var config=await configs.GetAsync(ct);if(config.Revision!=revision)throw new RevisionConflictException();var tank=config.Tanks.FirstOrDefault(t=>t.Id==tankId)??throw new ArgumentException("储罐不存在");var channel=config.Channels.First(c=>c.Id==tank.ChannelId);if(!channel.IsGateway||!channel.Enabled||!tank.Enabled)throw new InvalidOperationException("请选择启用的网关储罐");var live=await acquisition.CurrentAsync(ct);if(!live.Running)throw new InvalidOperationException("请先启动只读采集并核对当前绑定");var sample=live.Readings.First(r=>r.TankId==tankId);if(!sample.GatewayBootId.HasValue||!sample.GatewayBindingVersion.HasValue)throw new InvalidOperationException("当前网关身份和绑定未确认");
            var record=new ControlRecord{Target=new(channel,tank,revision,live.SessionId,sample.GatewayBootId,sample.GatewayBindingVersion),Command=null,Cycle=cycle,IsReset=true,State=ControlState.Queued,ActorId=actor.AccountId,ActorName=actor.DisplayName,CreatedAtUtc=clock.GetUtcNow(),ReviewReason=reason.Trim(),Result="恢复后的人工核查，仅复位选定结果通道；原动作记录可能不在恢复点中，不推测原命令"};await records.CreateAsync(record,ct);currentResults[(tankId,cycle)]=record.Id;
            var cancel=CancellationTokenSource.CreateLinkedTokenSource(hostToken);var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);active[record.Id]=(record,cancel,completion.Task);_=Task.Run(async()=>{try{await Run(record,cancel.Token);}finally{active.TryRemove(record.Id,out _);cancel.Dispose();completion.TrySetResult();}});return record;
        }finally{runtime.Gate.Release();}
    }
    public async Task ReviewAsync(Guid id,AuditActor actor,string reason,bool confirmed,CancellationToken ct)
    {
        if(!confirmed)throw new ArgumentException("请确认已人工核对现场运动、位置和仪表模式");await Ready.WaitAsync(ct);await runtime.Gate.WaitAsync(ct);
        try{if(active.ContainsKey(id))throw new InvalidOperationException("本次操作仍在收尾，请稍后核查");await records.ReviewAsync(id,actor,reason,clock.GetUtcNow(),ct);}finally{runtime.Gate.Release();}
    }
    public async Task HaltAcquisitionAsync(AuditActor actor,CancellationToken ct)
    {
        await runtime.Gate.WaitAsync(ct);try
        {if((await records.ListAsync(unresolvedOnly:true,ct:ct)).Count>0||(await wirelessTasks.ListAsync(activeOnly:true,ct:ct)).Count>0)throw new InvalidOperationException("仍有执行中或结果待核查的操作，请保留采集以核对设备状态");await acquisition.HaltAsync(actor,ct);}finally{runtime.Gate.Release();}
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        hostToken=stoppingToken;while(!stoppingToken.IsCancellationRequested)
        {
            try{await records.RecoverAsync(clock.GetUtcNow(),stoppingToken);ready.TrySetResult();break;}
            catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){return;}
            catch(Exception e){logger.LogCritical(e,"控制记录恢复失败，暂不受理动作");await Task.Delay(1000,stoppingToken);}
        }
        try{await Task.Delay(Timeout.Infinite,stoppingToken);}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
        await Task.WhenAll(active.Values.Select(v=>v.Task));
    }
}
