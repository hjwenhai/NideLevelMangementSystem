namespace Naide.LevelManager.Domain;
public enum DataJobKind { HistoryExport, Snapshot, DatabaseBackup, AverageTemperatureDispatch }
public enum DataJobState { Queued, Running, Completed, Failed, Cancelled, Interrupted }
public sealed record DataJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required DataJobKind Kind { get; init; }
    public DataJobState State { get; init; }
    public required string ActorId { get; init; }
    public required string ActorName { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? FinishedAtUtc { get; init; }
    public required string RequestJson { get; init; }
    public long Rows { get; init; }
    public string? FileName { get; init; }
    public string? Error { get; init; }
    public Guid? SnapshotId { get; init; }
}
public enum HistoryDataItem { Level, Interface, InstantTemperature, AverageTemperature, Density, CurrentVolume, NetVolume, Mass, Pressure1, Pressure2 }
public sealed record HistoryExportRequest(long StartUtcMs,long EndUtcMs,Guid? TankId=null,bool IncludeAtsPoints=false,TrendQuantity QualityQuantity=TrendQuantity.Level,HistoryQualityFilter Quality=HistoryQualityFilter.All,IReadOnlyList<HistoryDataItem>? SelectedQuantities=null);
public static class HistoryExportPolicy
{
    public static void Validate(IReadOnlyList<HistoryDataItem>? selected)
    {
        if(selected is not null&&(selected.Count is <1 or >10||selected.Distinct().Count()!=selected.Count||selected.Any(q=>!Enum.IsDefined(q))))
            throw new ArgumentException("请选择至少一个有效且不重复的历史数据项");
    }
}
public sealed record SnapshotRequest(string Name,IReadOnlyList<Guid>? TankIds=null);
public sealed record DatabaseBackupManifest(string ProductId,int SchemaVersion,long ConfigurationRevision,DateTimeOffset SnapshotAtUtc,long Bytes,string Sha256,bool AuthorizationIncluded=false);
public sealed record DataSnapshot(Guid Id,string Name,DateTimeOffset AtUtc,long ConfigurationRevision,
    string ActorName,IReadOnlyList<ReadingSnapshot> Readings);

public sealed record DataSnapshotSummary(Guid Id,string Name,DateTimeOffset AtUtc,long ConfigurationRevision,string ActorName,int Rows,int ValidLevels);
