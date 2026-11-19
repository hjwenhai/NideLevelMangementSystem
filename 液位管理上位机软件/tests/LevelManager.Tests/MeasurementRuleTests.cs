using Naide.LevelManager.Domain;
using Xunit;
namespace Naide.LevelManager.Tests;
public class MeasurementRuleTests
{
    [Theory][InlineData(499.9,16,0)][InlineData(500,16,1)][InlineData(1999.9,16,1)][InlineData(2000,16,3)][InlineData(3999.9,16,3)][InlineData(4000,16,16)][InlineData(5000,5,5)]
    public void BoundariesUseTheConfirmedPointCaps(double surface,int requested,int expected)=>Assert.Equal(expected,WirelessMeasurementRules.Plan((decimal)surface,requested).PlannedPoints);
    [Fact]public void TargetsDescendAndSurfaceIsFrozenIndependentlyFromCurrentPosition()
    {
        var plan=WirelessMeasurementRules.Plan(5000,5);Assert.Equal(new decimal[]{4500,3500,2500,1500,500},plan.TargetsMm);Assert.Equal(5000,plan.SurfaceMm);Assert.False(WirelessMeasurementRules.ShouldStop(4500,4500));Assert.True(WirelessMeasurementRules.ShouldStop(4499.9m,4500));Assert.True(WirelessMeasurementRules.WithinTolerance(4400,4500));Assert.False(WirelessMeasurementRules.WithinTolerance(4399.9m,4500));
    }
    [Theory][InlineData(1,1)][InlineData(2,1)][InlineData(3,2)][InlineData(4,2)][InlineData(5,3)][InlineData(7,3)][InlineData(8,4)][InlineData(10,4)][InlineData(11,5)][InlineData(16,5)]
    public void MinimumCountUsesActualPlannedN(int n,int m)=>Assert.Equal(m,WirelessMeasurementRules.RequiredValidPoints(n));
    [Fact]public void MissingPointsDoNotReduceNAndRealZeroParticipates()
    {
        var plan=WirelessMeasurementRules.Plan(5000,5);Measurement[] points=[Measurement.Good(0),Measurement.Good(10),Measurement.Good(20),Measurement.Missing("缺测"),Measurement.Missing("缺测")];Assert.Equal(10,WirelessMeasurementRules.Average(plan,points,true,false).Value);Assert.Null(WirelessMeasurementRules.Average(plan,points,false,false).Value);points[2]=Measurement.Missing("缺测");Assert.Null(WirelessMeasurementRules.Average(plan,points,true,false).Value);Assert.Equal(5,plan.PlannedPoints);
    }
    [Fact]public void RepeatedCacheEvidenceAndInvalidValuesNeverProveStability()
    {
        var at=DateTimeOffset.UtcNow;var window=new InitialStabilityWindow();Assert.False(window.Observe(Measurement.Good(5000),at,"a",true));Assert.False(window.Observe(Measurement.Good(5000),at.AddSeconds(20),"a",true));Assert.False(window.Observe(Measurement.Good(5001),at.AddSeconds(21),"b",true));for(var i=22;i<41;i++)Assert.False(window.Observe(Measurement.Good(5001),at.AddSeconds(i),"fresh"+i,true));Assert.True(window.Observe(Measurement.Good(5001),at.AddSeconds(41),"fresh41",true));Assert.False(window.Observe(Measurement.Missing("离线"),at.AddSeconds(42),"c",true));Assert.False(window.Observe(Measurement.Good(5000),at.AddSeconds(50),"d",false));
    }
    [Fact]public void WriteValuesAndReturnedModeEnumsAreDifferent()
    {
        Assert.Equal(3,CommandEncoding.IfxWord(InstrumentCommand.MeasureIII));Assert.Equal(4,CommandEncoding.IfxWord(InstrumentCommand.Stop));Assert.True(CommandEncoding.ModeMatches(6,InstrumentCommand.MeasureIII,AcquisitionProtocol.IfxL25));Assert.True(CommandEncoding.ModeMatches(5,InstrumentCommand.Stop,AcquisitionProtocol.IfxL25));Assert.Equal(32,CommandEncoding.DirectWord(InstrumentCommand.MeasureIII));Assert.Equal(64,CommandEncoding.DirectWord(InstrumentCommand.Density));Assert.False(CommandEncoding.ModeMatches(3,InstrumentCommand.MeasureIII,AcquisitionProtocol.IfxL25));
    }
}
