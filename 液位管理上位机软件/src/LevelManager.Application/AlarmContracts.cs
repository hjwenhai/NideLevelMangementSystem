using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Application;
public sealed record AlarmQuery(string State="attention", Guid? TankId=null, DateTimeOffset? Start=null,DateTimeOffset? End=null,int Limit=500);
public sealed record TankAlarmSummary(Guid TankId,int Active,int RecoveredUnacknowledged,int Unacknowledged);
public sealed record AlarmSummary(int Active,int Unacknowledged,int RecoveredUnacknowledged,IReadOnlyList<TankAlarmSummary> Tanks);
public interface IAlarmStore
{
    Task<AlarmSummary> SummaryAsync(CancellationToken ct=default);
    Task ResetPendingAsync(CancellationToken ct=default);
    Task EvaluateAsync(IReadOnlyList<AlarmObservation> observations, DateTimeOffset now,CancellationToken ct=default);
    Task<IReadOnlyList<AlarmOccurrence>> QueryAsync(AlarmQuery query,CancellationToken ct=default);
    Task<int> AcknowledgeAsync(IReadOnlyList<Guid> ids,AuditActor actor,DateTimeOffset now,CancellationToken ct=default);
}
