using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Application;

public sealed record RecordTableBasis(Guid Id,long Revision,string Number,string Name,CapacityTableKind Kind,HeightUnit HeightUnit,VolumeUnit VolumeUnit,int PointCount,decimal ReferenceDensityKgM3);
public sealed record RecordBasis(long ConfigurationRevision,DateTimeOffset ConfigurationSavedAtUtc,TankDefinition Tank,ChannelDefinition Channel,
    int HistoryIntervalSeconds,int ReadingExpirySeconds,long CalculationRevision,DateTimeOffset? CalculationSavedAtUtc,
    MeteringSettings? Metering,PressureCalibration? Pressure1,PressureCalibration? Pressure2,IReadOnlyList<RecordTableBasis> Tables,string Formula);
public interface IRecordBasisStore
{
    Task<RecordBasis> GetRecordBasisAsync(Guid tankId,long configurationRevision,long calculationRevision,CancellationToken ct=default);
}
