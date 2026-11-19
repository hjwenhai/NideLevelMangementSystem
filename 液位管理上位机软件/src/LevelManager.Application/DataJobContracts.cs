using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Application;
public interface IDataJobStore
{
    Task<bool> CreateScheduledAsync(Guid scheduleId,string date,DataJob job,CancellationToken ct=default);
    Task RecoverInterruptedAsync(DateTimeOffset now,CancellationToken ct=default);
    Task CreateAsync(DataJob job,CancellationToken ct=default);
    Task<DataJob?> ClaimAsync(DateTimeOffset now,CancellationToken ct=default);
    Task UpdateAsync(DataJob job,CancellationToken ct=default);
    Task<IReadOnlyList<DataJob>> ListAsync(CancellationToken ct=default);
    Task<DataJob?> GetAsync(Guid id,CancellationToken ct=default);
    Task CompleteSnapshotAsync(DataJob job,DataSnapshot snapshot,CancellationToken ct=default);
    Task<IReadOnlyList<DataSnapshotSummary>> SnapshotsAsync(CancellationToken ct=default);
    Task<DataSnapshot?> SnapshotAsync(Guid id,CancellationToken ct=default);
}
