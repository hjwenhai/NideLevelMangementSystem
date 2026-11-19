using Naide.LevelManager.Domain;
namespace Naide.LevelManager.Application;
public enum AccountRole { Administrator, Operator, Viewer }
public sealed record AuditActor(string AccountId,string DisplayName,AccountRole Role,IReadOnlyList<AppPermission>? Permissions=null);
public sealed record UserAccount(string Id,string Login,string DisplayName,AccountRole Role,string PasswordHash,bool Enabled,long SecurityVersion=0);
public sealed record OperationEvent(long Id,long AtUtcMs,string Category,string ActorId,string ActorName,string? TankId,string? TankCode,string Content,string Result,string Details);
public sealed record EventQuery(string? Category=null,string? TankId=null,string? Actor=null,string? Keyword=null,long? StartUtcMs=null,long? EndUtcMs=null,int Limit=100);
public sealed record StorageHealth(string NativeVersion,string JournalMode,int Synchronous,int SchemaVersion);
public sealed class RevisionConflictException() : Exception("配置已被其他窗口修改，请重新载入后保存");
public interface IConfigurationStore
{
 Task<bool> WasTankDefinedAsync(Guid tankId,CancellationToken cancellationToken=default)=>Task.FromResult(false);
 Task<SystemConfiguration> GetAsync(CancellationToken cancellationToken=default);
 Task<SystemConfiguration> SaveAsync(SystemConfiguration config,AuditActor actor,CancellationToken cancellationToken=default);
 Task<IReadOnlyList<OperationEvent>> QueryEventsAsync(EventQuery query,CancellationToken cancellationToken=default);
 Task<StorageHealth> GetHealthAsync(CancellationToken cancellationToken=default);
}
public interface IUserStore
{
 Task<bool> NeedsSetupAsync(CancellationToken cancellationToken=default);
 Task<UserAccount?> FindAsync(string login,CancellationToken cancellationToken=default);
 Task CreateInitialAdministratorAsync(UserAccount account,CancellationToken cancellationToken=default);
 Task<UserAccount?> FindByIdAsync(string id,CancellationToken ct=default);
 Task<IReadOnlyList<UserAccount>> ListUsersAsync(CancellationToken ct=default);
 Task<UserAccount> SaveUserAsync(UserAccount account,bool create,AuditActor actor,CancellationToken ct=default);
 Task ChangeOwnPasswordAsync(string id,long expectedVersion,string newHash,AuditActor actor,CancellationToken ct=default);
}
public sealed class ConfigurationService(IConfigurationStore store)
{
 public Task<bool> WasTankDefinedAsync(Guid tankId,CancellationToken ct=default)=>store.WasTankDefinedAsync(tankId,ct);
 public Task<SystemConfiguration> GetAsync(CancellationToken ct=default)=>store.GetAsync(ct);
 public Task<SystemConfiguration> SaveAsync(SystemConfiguration config,AuditActor actor,CancellationToken ct=default)
 {
  actor.Require(AppPermission.ConfigurationEdit);
  ConfigurationRules.RequireValid(config);return store.SaveAsync(config,actor,ct);
 }
}

public interface ISecurityAudit
{
 Task RecordAsync(string actorId,string actorName,string content,string result,CancellationToken cancellationToken=default);
}

public sealed record EventWrite(string Category,string EventCode,string ActorId,string ActorName,
    string Content,string Result,Guid? TankId=null,string? TankCode=null,object? Parameters=null);
public interface IEventWriter
{
 Task WriteEventAsync(EventWrite entry,CancellationToken ct=default);
}
