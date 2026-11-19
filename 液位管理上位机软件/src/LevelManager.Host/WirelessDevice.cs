using Naide.LevelManager.Application;
using Naide.LevelManager.Communication;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Host;
public sealed class WirelessDevice(ControlTarget target,ChannelAccess access,IControlStore records,IConfigurationStore configs,RuntimeOperationGate runtime,TimeProvider clock,ProductAuthorization authorization,IWirelessTaskStore tasks,IEventWriter audit,IUserStore users,IRolePermissionStore permissions,MotionEvidenceGuard evidence):IWirelessMeasurementDevice
{
    private bool authorized,dispatchAdmitted;
    public string? UnsupportedReason=>SupportReason(target.Channel.Protocol);
    public static string? SupportReason(AcquisitionProtocol protocol)=>protocol switch
    {
        AcquisitionProtocol.DirectRtu=>null,
        AcquisitionProtocol.IfxL25=>"IFX本点表尚不能确认位置缓存的更新关系，平均测温暂未开放；监视和已配置单次操作可使用",
        _=>"当前网关尚缺少整任务一次认证及停留后新读I6的北向接口，平均测温暂未开放；IR26瞬时温度采集可使用"
    };
    private async Task Revalidate(CancellationToken ct)
    {
        var config=await configs.GetAsync(ct);var tank=config.Tanks.FirstOrDefault(t=>t.Id==target.Tank.Id);var channel=tank is null?null:config.Channels.FirstOrDefault(c=>c.Id==tank.ChannelId);
        if(tank is null||channel is null||!tank.Enabled||!channel.Enabled||tank.TemperatureMode!=TemperatureMode.Wireless||!tank.Controls.WirelessMeasurementEnabled||tank.Address!=target.Tank.Address||channel with{Name=""}!=target.Channel with{Name=""})throw new InvalidOperationException("原测温设备或配置已改变，未发送新动作");
    }
    public async Task AuthorizeOnceAsync(WirelessTaskRecord task,CancellationToken ct)
    {
        if(UnsupportedReason is {} why)throw new InvalidOperationException(why);if(authorized)throw new InvalidOperationException("本任务已经认证，禁止再次认证");
        // Acquire runtime admission inside the channel's first-write guard. Never hold
        // the runtime gate while waiting for a queued physical write to reach this guard.
        using var dispatchGuard=new RegisterWriteGuardScope(async token=>
        {
            if(dispatchAdmitted)return;await runtime.Gate.WaitAsync(token);try
            {
                await Revalidate(token);var persisted=await tasks.GetAsync(task.Id,token);
                if(persisted is null||persisted.Version!=task.Version||persisted.Stage!=WirelessStage.Authorizing||!persisted.WriteIntentRecorded)throw new InvalidOperationException("缺少已持久保存的本任务认证意图");
                evidence.RequireNewActionAllowed();authorization.RequireAverageModule();authorization.RequireLicensed("排队等待的平均测温任务");
                var user=await users.FindByIdAsync(task.ActorId,token);PermissionPolicy.RequireScheduledAuthority(user,(await permissions.GetPermissionsAsync(token)).Revision,task.ScheduledPermissionRevision,task.ScheduledSecurityVersion);
                if(user is null||!user.Enabled||!(await permissions.ResolveAsync(user,token)).Allows(AppPermission.AverageTemperatureStart))throw new UnauthorizedAccessException("原测温账号权限已失效");
                await evidence.CommitAsync(new(Guid.NewGuid(),task.Id,"task-authorization",clock.GetUtcNow(),target,task.ActorId,task.ActorName),()=>audit.WriteEventAsync(new("操作日志","wireless.first-dispatch",task.ActorId,task.ActorName,"本任务进入首次认证写入","已核准",target.Tank.Id,target.Tank.Code,new{TaskId=task.Id}),token),token);dispatchAdmitted=true;
            }finally{runtime.Gate.Release();}
        });
        await access.UseAsync(target,RegisterPriority.Control,async(client,token)=>{await client.WriteMultipleRegistersAsync(checked((byte)target.Tank.Address),129,new ushort[]{0x10d9},token);return true;},ct);authorized=true;
    }
    public Task<ReadingSnapshot> ReadPositionAsync(CancellationToken ct)=>access.UseAsync(target,RegisterPriority.Control,async(client,token)=>await new DeviceReader(target.Channel.Protocol,clock).ReadAsync(client,new(target.Channel,target.Tank,target.ConfigurationRevision,target.AcquisitionSessionId),token),ct);
    public Task<WirelessTemperatureSample> ReadTemperatureOnceAsync(CancellationToken ct)=>access.UseAsync(target,RegisterPriority.Control,async(client,token)=>
    {
        if(UnsupportedReason is {} why)throw new InvalidOperationException(why);var words=await client.ReadInputRegistersAsync(checked((byte)target.Tank.Address),6,1,token);if(words.Length!=1)throw new InvalidDataException("I6读取长度不符");var raw=words[0];return new WirelessTemperatureSample(raw==0x8000?Measurement.Missing("I6协议0x8000缺测"):Measurement.Good(ModbusReadCodec.TemperatureC(raw)),raw);
    },ct);
    public async Task CommandAsync(WirelessTaskRecord task,InstrumentCommand command,CancellationToken ct)
    {
        if(!authorized)throw new InvalidOperationException("本任务认证未确认，未继续发出动作");
        var record=new ControlRecord{Target=target,Command=command,Cycle=command==InstrumentCommand.Stop?ControlCycle.IndependentStop:ControlCycle.Normal,State=ControlState.Queued,ActorId=task.ActorId,ActorName=task.ActorName,CreatedAtUtc=clock.GetUtcNow(),MeasurementTaskId=task.Id,Result="平均测温内部操作已受理"};await records.CreateAsync(record,ct);
        try
        {
            record=record with{State=ControlState.Checking,Result="核验平均测温内部操作"};await records.UpdateAsync(record,ct);
            async Task Checkpoint(ControlCheckpoint phase)
            {
                await runtime.Gate.WaitAsync(ct);try
                {
                    await Revalidate(ct);record=phase==ControlCheckpoint.WriteAcknowledged?record with{State=ControlState.Submitted,Result="取得本任务操作写回执，等待实际模式回读"}:record with{State=ControlState.WriteIntent,WriteIntentAtUtc=record.WriteIntentAtUtc??clock.GetUtcNow(),Result="已记录本任务动作写入意图"};await records.UpdateAsync(record,ct);
                }finally{runtime.Gate.Release();}
            }
            using var writeGuard=new RegisterWriteGuardScope(async token=>{if(record.FirstDispatchAtUtc.HasValue)return;var next=record with{FirstDispatchAtUtc=clock.GetUtcNow()};await evidence.CommitAsync(new(Guid.NewGuid(),record.Id,"measurement-command",clock.GetUtcNow(),target,record.ActorId,record.ActorName),async()=>{await records.UpdateAsync(next,token);record=next;},token);});
            var result=await access.UseAsync(target,command==InstrumentCommand.Stop?RegisterPriority.Stop:RegisterPriority.Control,(client,token)=>new ControlProtocolDriver(clock).ExecuteAsync(client,target,command,Checkpoint,true,token),ct);
            record=record with{State=result.State,FinishedAtUtc=clock.GetUtcNow(),Result=result.Message};await records.UpdateAsync(record,CancellationToken.None);
            if(result.State!=ControlState.ReadbackConfirmed)throw new InvalidOperationException(result.Message);
        }
        catch(Exception e)
        {
            if(record.State is ControlState.Checking or ControlState.Queued or ControlState.WriteIntent or ControlState.Submitted)
            {
                record=record with{State=record.WriteIntentAtUtc.HasValue?ControlState.Unknown:ControlState.Rejected,FinishedAtUtc=clock.GetUtcNow(),Result="本任务操作中断，未重发："+e.Message};await records.UpdateAsync(record,CancellationToken.None);
            }
            throw;
        }
    }
}
