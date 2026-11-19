using System.Text.Json;
using Naide.LevelManager.Application;
namespace Naide.LevelManager.Infrastructure;

public sealed partial class SqliteConfigurationStore : IRolePermissionStore
{
    public async Task<RolePermissionMatrix> GetPermissionsAsync(CancellationToken ct=default)
    {
        await using var c=await OpenAsync(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT Payload FROM role_permissions WHERE Id=1";
        var payload=await cmd.ExecuteScalarAsync(ct) as string;
        var matrix=payload is null?PermissionPolicy.DefaultMatrix():JsonSerializer.Deserialize<RolePermissionMatrix>(payload,Json)??throw new InvalidDataException("岗位权限配置无法解析");
        PermissionPolicy.Validate(matrix);return matrix;
    }
    public async Task<RolePermissionMatrix> SavePermissionsAsync(RolePermissionMatrix matrix,AuditActor actor,CancellationToken ct=default)
    {
        actor.Require(AppPermission.PermissionsManage);PermissionPolicy.Validate(matrix);
        await using var c=await OpenAsync(ct);using var tx=c.BeginTransaction();await using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT Revision FROM role_permissions WHERE Id=1";var previous=Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)??0L);
        if(previous!=matrix.Revision)throw new RevisionConflictException();var next=matrix with{Revision=checked(previous+1)};var payload=JsonSerializer.Serialize(next,Json);
        cmd.CommandText="INSERT INTO role_permissions(Id,Revision,Payload) VALUES(1,$revision,$payload) ON CONFLICT(Id) DO UPDATE SET Revision=$revision,Payload=$payload";
        cmd.Parameters.AddWithValue("$revision",next.Revision);cmd.Parameters.AddWithValue("$payload",payload);await cmd.ExecuteNonQueryAsync(ct);
        cmd.Parameters.Clear();cmd.CommandText="INSERT INTO operation_events(AtUtcMs,Category,ActorId,ActorName,Content,Result,Details) VALUES($at,'操作日志',$actor,$name,'修改岗位权限矩阵','已保存，后续请求按新权限校验',$details)";
        cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$actor",actor.AccountId);cmd.Parameters.AddWithValue("$name",actor.DisplayName);cmd.Parameters.AddWithValue("$details",payload);await cmd.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);return next;
    }
}
