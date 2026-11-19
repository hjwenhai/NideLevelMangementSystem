using System.Collections.Concurrent;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Application;
public sealed class WirelessRuntime
{
    private readonly ConcurrentDictionary<Guid,WirelessTaskRecord> active=new(),successful=new();
    public void Publish(WirelessTaskRecord r)
    {
        if(r.HoldsTank)active[r.Target.Tank.Id]=r;else active.TryRemove(r.Target.Tank.Id,out _);
        if(r.HasSuccessfulResult)successful[r.Target.Tank.Id]=r;
    }
    private readonly ConcurrentDictionary<Guid,(Guid Session,Guid Channel,int Address,decimal Value,DateTimeOffset At)> lastSurfaces=new();
    public ReadingSnapshot Apply(ReadingSnapshot reading,TankDefinition tank,ChannelDefinition channel,bool derivePosition=true)
    {
        var result=derivePosition?reading with{FloatPositionMm=reading.LevelMm}:reading;active.TryGetValue(tank.Id,out var task);
        if(derivePosition&&task is not null&&task.Plan is not null&&task.Target.AcquisitionSessionId==reading.AcquisitionSessionId&&reading.LevelMm.IsValid)
            result=result with{LevelMm=Measurement.Good(task.Plan.SurfaceMm),FrozenSurfaceAtUtc=task.PlanFrozenAtUtc,SurfaceBasis="平均测温液面基准X（冻结于"+task.PlanFrozenAtUtc?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss")+"）；实际位置P单列显示"};
        else if(derivePosition&&reading.InstrumentStatus.HasValue&&!CommandEncoding.ModeMatches(reading.InstrumentStatus.Value,InstrumentCommand.MeasureI,channel.Protocol))
            result=result with{LevelMm=Measurement.Missing("仪表当前高度是浮子位置，尚未确认测量Ⅰ液面"),SurfaceBasis="浮子实际位置P不能代替液面；体积和质量暂不计算"};
        if(tank.TemperatureMode==TemperatureMode.Wireless&&successful.TryGetValue(tank.Id,out var last)&&last.Target.Tank.Address==tank.Address&&last.Target.Channel with{Name=""}==channel with{Name=""})
            result=result with{AverageTemperatureC=last.AverageTemperature,AverageCompletedAtUtc=last.FinishedAtUtc,AverageTaskId=last.Id};
        if(result.LevelMm.IsValid&&result.FrozenSurfaceAtUtc is null&&result.InstrumentStatus.HasValue&&CommandEncoding.ModeMatches(result.InstrumentStatus.Value,InstrumentCommand.MeasureI,channel.Protocol))
            lastSurfaces[tank.Id]=(reading.AcquisitionSessionId,channel.Id,tank.Address,result.LevelMm.Value!.Value,reading.ReceivedAtUtc);
        if(lastSurfaces.TryGetValue(tank.Id,out var surface)&&surface.Session==reading.AcquisitionSessionId&&surface.Channel==channel.Id&&surface.Address==tank.Address)
            result=result with{LastMeasuredLevelMm=surface.Value,LastMeasuredLevelAtUtc=surface.At};
        return result;
    }
}
