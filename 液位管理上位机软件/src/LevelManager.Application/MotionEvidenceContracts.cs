using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Application;
public sealed record MotionEvidence(Guid Id,Guid OwnerId,string Kind,DateTimeOffset AtUtc,ControlTarget? Target,string ActorId,string ActorName,string? Note=null);
public interface IMotionEvidenceStore
{
    Task<bool> ExistsAsync(Guid id,CancellationToken ct=default);
    Task<long> CountAsync(CancellationToken ct=default);
    Task CreateAsync(MotionEvidence evidence,CancellationToken ct=default);
}
