namespace Naide.LevelManager.Domain;
public sealed record MeasurementPlan(decimal SurfaceMm,int RequestedPoints,IReadOnlyList<decimal> TargetsMm)
{
    public int PlannedPoints=>TargetsMm.Count;
    public int RequiredValidPoints=>WirelessMeasurementRules.RequiredValidPoints(PlannedPoints);
}
public static class WirelessMeasurementRules
{
    public static readonly TimeSpan Dwell=TimeSpan.FromMinutes(10);
    public static readonly TimeSpan InitialStableDuration=TimeSpan.FromSeconds(20);
    public static readonly TimeSpan InitialStableTimeout=TimeSpan.FromMinutes(10);
    public const decimal InitialRangeMm=5;
    public const decimal PositionToleranceMm=100;
    public static MeasurementPlan Plan(decimal surfaceMm,int requestedPoints)
    {
        if(requestedPoints is <1 or >16||surfaceMm is < -9999.9m or >99999.9m)throw new ArgumentException("计划点数或液面基准无效");
        var count=surfaceMm<500?0:surfaceMm<2000?1:Math.Min(requestedPoints,surfaceMm<4000?3:16);
        if(count==0)return new(surfaceMm,requestedPoints,[]);
        if(count==1)return new(surfaceMm,requestedPoints,[decimal.Round(surfaceMm/2,1,MidpointRounding.AwayFromZero)]);
        var step=(surfaceMm-1000)/(count-1);
        return new(surfaceMm,requestedPoints,Enumerable.Range(0,count).Select(i=>decimal.Round(surfaceMm-500-i*step,1,MidpointRounding.AwayFromZero)).ToArray());
    }
    public static int RequiredValidPoints(int planned)=>planned switch{0=>0,1 or 2=>1,3 or 4=>2,>=5 and <=7=>3,>=8 and <=10=>4,>=11 and <=16=>5,_=>throw new ArgumentOutOfRangeException(nameof(planned))};
    public static bool ShouldStop(decimal positionMm,decimal targetMm)=>positionMm-targetMm<0;
    public static bool WithinTolerance(decimal positionMm,decimal targetMm)=>Math.Abs(positionMm-targetMm)<=PositionToleranceMm;
    public static Measurement Average(MeasurementPlan plan,IReadOnlyList<Measurement> attempted,bool restoredMeasureI,bool terminalFailure)
    {
        if(terminalFailure||!restoredMeasureI)return new(null,ValueQuality.Fault,"测量中断或尚未确认恢复测量Ⅰ，不发布本次平均温度");
        if(plan.PlannedPoints==0)return new(null,ValueQuality.Unconfigured,"液面低于500mm，本次不测平均温度");
        if(attempted.Count!=plan.PlannedPoints)throw new ArgumentException("本次每个计划测点都须保留温度或缺测记录");
        var values=attempted.Where(v=>v.IsValid).Select(v=>v.Value!.Value).ToArray();
        return values.Length<plan.RequiredValidPoints?Measurement.Missing("有效测点不足（"+values.Length+"/"+plan.PlannedPoints+"）"):Measurement.Good(values.Average());
    }
}
public sealed class InitialStabilityWindow(TimeSpan? maximumSampleGap=null)
{
    private readonly Queue<(DateTimeOffset At,decimal Value)> points=new();
    private string? previousEvidence;
    private DateTimeOffset? lastTime;
    public void Reset(){points.Clear();previousEvidence=null;lastTime=null;}
    public bool Observe(Measurement height,DateTimeOffset at,string freshEvidence,bool freshnessConfirmed)
    {
        if(!height.IsValid||!freshnessConfirmed||string.IsNullOrWhiteSpace(freshEvidence)||lastTime.HasValue&&at<=lastTime.Value){Reset();return false;}
        if(freshEvidence==previousEvidence){if(lastTime.HasValue&&at-lastTime.Value>(maximumSampleGap??TimeSpan.FromSeconds(5)))points.Clear();return false;} // A repeated cache sequence never advances the window.
        if(lastTime.HasValue&&at-lastTime.Value>(maximumSampleGap??TimeSpan.FromSeconds(5)))points.Clear();
        previousEvidence=freshEvidence;lastTime=at;points.Enqueue((at,height.Value!.Value));
        while(points.Count>1&&points.Max(p=>p.Value)-points.Min(p=>p.Value)>WirelessMeasurementRules.InitialRangeMm)points.Dequeue();
        // Keep the full continuous stable interval until the caller enters its separate 600-second dwell.
        while(points.Count>256)points.Dequeue();
        return points.Count>=2&&at-points.Peek().At>=WirelessMeasurementRules.InitialStableDuration;
    }
}
