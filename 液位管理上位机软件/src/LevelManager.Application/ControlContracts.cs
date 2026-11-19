using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Application;
public interface IControlStore
{
    Task RecoverAsync(DateTimeOffset now,CancellationToken ct=default);
    Task CreateAsync(ControlRecord record,CancellationToken ct=default);
    Task UpdateAsync(ControlRecord record,CancellationToken ct=default);
    Task<ControlRecord?> GetAsync(Guid id,CancellationToken ct=default);
    Task<IReadOnlyList<ControlRecord>> ListAsync(Guid? tankId=null,bool unresolvedOnly=false,CancellationToken ct=default);
    Task ReviewAsync(Guid id,AuditActor reviewer,string reason,DateTimeOffset now,CancellationToken ct=default);
}
