using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
namespace Naide.LevelManager.Infrastructure;
public sealed class SqliteMotionEvidence(string path):IMotionEvidenceStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new JsonStringEnumConverter()}};
    private async Task<SqliteConnection> Open(CancellationToken ct){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,ForeignKeys=true,DefaultTimeout=5}.ToString());try{await c.OpenAsync(ct);await using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA synchronous=FULL";await cmd.ExecuteNonQueryAsync(ct);return c;}catch{await c.DisposeAsync();throw;}}
    public async Task<bool> ExistsAsync(Guid id,CancellationToken ct=default){await using var c=await Open(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT EXISTS(SELECT 1 FROM motion_markers WHERE Id=$id)";cmd.Parameters.AddWithValue("$id",id.ToString());return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct))==1;}
    public async Task<long> CountAsync(CancellationToken ct=default){await using var c=await Open(ct);await using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM motion_markers";return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));}
    public async Task CreateAsync(MotionEvidence e,CancellationToken ct=default)
    {
        if(e.Id==Guid.Empty||e.OwnerId==Guid.Empty||string.IsNullOrWhiteSpace(e.Kind))throw new ArgumentException("写入证据参数无效");await using var c=await Open(ct);using var tx=c.BeginTransaction();await using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="INSERT INTO motion_markers(Id,AtUtcMs,OwnerId,Kind,Payload) VALUES($id,$at,$owner,$kind,$payload)";cmd.Parameters.AddWithValue("$id",e.Id.ToString());cmd.Parameters.AddWithValue("$at",e.AtUtc.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$owner",e.OwnerId.ToString());cmd.Parameters.AddWithValue("$kind",e.Kind);cmd.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(e,Json));await cmd.ExecuteNonQueryAsync(ct);
        cmd.Parameters.Clear();cmd.CommandText="INSERT INTO operation_events(AtUtcMs,Category,ActorId,ActorName,TankId,TankCode,Content,Result,Details) VALUES($at,'操作日志',$actor,$name,$tank,$code,$content,'已保存',$details)";cmd.Parameters.AddWithValue("$at",e.AtUtc.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$actor",e.ActorId);cmd.Parameters.AddWithValue("$name",e.ActorName);cmd.Parameters.AddWithValue("$tank",(object?)e.Target?.Tank.Id.ToString()??DBNull.Value);cmd.Parameters.AddWithValue("$code",(object?)e.Target?.Tank.Code??DBNull.Value);cmd.Parameters.AddWithValue("$content",e.Kind=="recovery-review"?"恢复业务数据后人工核查":"记录物理写入前的独立校验证据");cmd.Parameters.AddWithValue("$details",JsonSerializer.Serialize(e,Json));await cmd.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);
    }
}
