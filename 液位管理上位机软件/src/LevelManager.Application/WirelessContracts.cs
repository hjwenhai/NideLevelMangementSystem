using System.Diagnostics;
using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Application;
public interface IWirelessTaskStore
{
    Task CreateAsync(WirelessTaskRecord record,CancellationToken ct=default);
    Task SaveAsync(WirelessTaskRecord record,CancellationToken ct=default);
    Task<WirelessTaskRecord?> GetAsync(Guid id,CancellationToken ct=default);
    Task<IReadOnlyList<WirelessTaskRecord>> ListAsync(Guid? tankId=null,bool activeOnly=false,CancellationToken ct=default);
    Task<IReadOnlyList<WirelessTaskRecord>> LastSuccessfulAsync(CancellationToken ct=default);
    Task RecoverAsync(DateTimeOffset now,CancellationToken ct=default);
    Task ReviewAsync(Guid id,AuditActor actor,string reason,DateTimeOffset now,CancellationToken ct=default);
}
public sealed record WirelessTemperatureSample(Measurement Temperature,ushort? RawValue);
public interface IWirelessMeasurementDevice
{
    string? UnsupportedReason {get;}
    Task AuthorizeOnceAsync(WirelessTaskRecord task,CancellationToken ct);
    Task CommandAsync(WirelessTaskRecord task,InstrumentCommand command,CancellationToken ct);
    Task<ReadingSnapshot> ReadPositionAsync(CancellationToken ct);
    Task<WirelessTemperatureSample> ReadTemperatureOnceAsync(CancellationToken ct);
}
public interface IWorkflowTime
{
    DateTimeOffset UtcNow {get;}
    long Timestamp {get;}
    TimeSpan Elapsed(long start);
    Task Delay(TimeSpan duration,CancellationToken ct);
}
public sealed class WorkflowTime(TimeProvider clock):IWorkflowTime
{
    public DateTimeOffset UtcNow=>clock.GetUtcNow();
    public long Timestamp=>clock.GetTimestamp();
    public TimeSpan Elapsed(long start)=>clock.GetElapsedTime(start);
    public Task Delay(TimeSpan duration,CancellationToken ct)=>Task.Delay(duration,clock,ct);
}
