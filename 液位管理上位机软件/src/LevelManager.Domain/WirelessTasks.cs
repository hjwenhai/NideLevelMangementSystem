namespace Naide.LevelManager.Domain;
public enum WirelessStage { Queued,Checking,Authorizing,EnteringMeasureI,InitialStability,InitialDwell,Planning,Descending,Stopping,Settling,PointDwell,ReadingTemperature,RestoringMeasureI,Completed,Skipped,Failed,Cancelled,RequiresReview,Reviewed }
public sealed record WirelessPoint(int Number,decimal TargetMm,decimal? StoppedPositionMm,DateTimeOffset? DwellStartedAtUtc,DateTimeOffset? SampledAtUtc,Measurement Temperature,int Attempts,ushort? RawTemperature=null,double DwellElapsedSeconds=0);
public sealed record WirelessTaskRecord
{
    public long? ScheduledPermissionRevision {get;init;}
    public long? ScheduledSecurityVersion {get;init;}
    public Guid Id {get;init;}=Guid.NewGuid();
    public long Version {get;init;}
    public required ControlTarget Target {get;init;}
    public required string ActorId {get;init;}
    public required string ActorName {get;init;}
    public required DateTimeOffset CreatedAtUtc {get;init;}
    public DateTimeOffset? FinishedAtUtc {get;init;}
    public WirelessStage Stage {get;init;}=WirelessStage.Queued;
    public DateTimeOffset? StageStartedAtUtc {get;init;}
    public DateTimeOffset? DisplayDueAtUtc {get;init;}
    public int RequestedPoints {get;init;}=5;
    public string RuleVersion {get;init;}="V1.3 + C01-20261013";
    public MeasurementPlan? Plan {get;init;}
    public DateTimeOffset? PlanFrozenAtUtc {get;init;}
    public IReadOnlyList<WirelessPoint> Points {get;init;}=[];
    public int CurrentPoint {get;init;}
    public Measurement BaselineTemperature {get;init;}=Measurement.Missing("尚未读取基准温度");
    public Measurement AverageTemperature {get;init;}=Measurement.Missing("尚未完成本次测温");
    public bool MeasureIRestored {get;init;}
    public bool WriteIntentRecorded {get;init;}
    public string Message {get;init;}="等待执行";
    public string? ReviewReason {get;init;}
    public string? ReviewerId {get;init;}
    public string? ReviewerName {get;init;}
    public int ValidPoints=>Points.Count(p=>p.Temperature.IsValid);
    public bool HasSuccessfulResult=>Stage==WirelessStage.Completed&&MeasureIRestored&&AverageTemperature.IsValid;
    public bool HoldsTank=>Stage is not(WirelessStage.Completed or WirelessStage.Skipped or WirelessStage.Failed or WirelessStage.Cancelled or WirelessStage.Reviewed);
}
