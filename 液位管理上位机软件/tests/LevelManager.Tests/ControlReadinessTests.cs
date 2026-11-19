using Naide.LevelManager.Domain;
using Xunit;
namespace Naide.LevelManager.Tests;

public class ControlReadinessTests
{
    [Fact]public void MissingStatusNamesUnavailableFieldsInsteadOfGenericError()
    {
        var sample=new ReadingSnapshot{FloatPositionMm=Measurement.Good(19372.1m)};
        var error=Assert.Throws<InvalidOperationException>(()=>ControlReadiness.RequireCurrentData(sample,InstrumentCommand.RemoteLevel));
        Assert.Contains("仪表状态",error.Message);Assert.Contains("仪表故障码",error.Message);
        Assert.DoesNotContain("当前液位/浮子位置无效",error.Message);
    }
    [Fact]public void InvalidDisplayedPositionDoesNotBlockDispatchButMissingStatusDoes()
    {
        var sample=new ReadingSnapshot{FloatPositionMm=Measurement.Missing("网关数据组过期"),InstrumentStatus=1,InstrumentFault=0};
        ControlReadiness.RequireCurrentData(sample,InstrumentCommand.RemoteLevel);
        Assert.Throws<InvalidOperationException>(()=>ControlReadiness.RequireCurrentData(sample with{InstrumentStatus=null},InstrumentCommand.Hoist));
        ControlReadiness.RequireCurrentData(sample,InstrumentCommand.Stop);
        ControlReadiness.RequireCurrentData(sample with{FloatPositionMm=Measurement.Good(0)},InstrumentCommand.RemoteLevel);
    }
}
