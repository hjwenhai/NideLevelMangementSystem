using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;

namespace Naide.LevelManager.Infrastructure;

public sealed partial class SqliteConfigurationStore : ICapacityTableStore
{
    private readonly ConcurrentDictionary<Guid, CapacityTable> capacityCache = new();
    private readonly ConcurrentDictionary<Guid, byte> deletedCapacity = new();
    private readonly ConcurrentDictionary<string, long> calculationCache = new();
    private static CapacityTable Freeze(CapacityTable table) => table with { Points = Array.AsReadOnly(table.Points.ToArray()), DecimalRows = Array.AsReadOnly(table.DecimalRows.ToArray()) };

    public async Task<CapacityTable?> GetTableAsync(Guid id, long? revision = null, CancellationToken ct = default)
    {
        if(revision is null&&deletedCapacity.ContainsKey(id))return null;
        if (revision is null && capacityCache.TryGetValue(id, out var cached)) return cached;
        await using var c = await OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = revision.HasValue ? "SELECT Payload FROM capacity_table_versions WHERE TableId=$id AND Revision=$revision" : "SELECT v.Payload FROM capacity_tables t JOIN capacity_table_versions v ON t.Id=v.TableId AND t.Revision=v.Revision WHERE t.Id=$id AND NOT EXISTS(SELECT 1 FROM deleted_capacity_tables d WHERE d.TableId=t.Id)";
        cmd.Parameters.AddWithValue("$id", id.ToString()); if (revision.HasValue) cmd.Parameters.AddWithValue("$revision", revision.Value);
        var payload = Convert.ToString(await cmd.ExecuteScalarAsync(ct));
        if (string.IsNullOrEmpty(payload)) return null;
        var table = Freeze(JsonSerializer.Deserialize<CapacityTable>(payload, Json)!);
        // Never replace a newer in-process save with an older racing read.
        if (!revision.HasValue) capacityCache.AddOrUpdate(id, table, (_, old) => old.Revision > table.Revision ? old : table);
        return revision is null&&deletedCapacity.ContainsKey(id)?null:table;
    }
    public async Task<IReadOnlyList<CapacityTableSummary>> ListTablesAsync(CancellationToken ct = default)
    {
        var config = await GetAsync(ct);
        await using var c = await OpenAsync(ct); await using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT * FROM capacity_tables WHERE Id NOT IN (SELECT TableId FROM deleted_capacity_tables) ORDER BY Number";
        await using var reader = await cmd.ExecuteReaderAsync(ct); List<CapacityTableSummary> list = [];
        while (await reader.ReadAsync(ct))
        {
            var id = Guid.Parse(reader.GetString(0)); var kind = (CapacityTableKind)reader.GetInt32(4);
            var associated = config.Tanks.Where(t => kind switch
            {
                CapacityTableKind.Main => t.Metering.MainTableId == id,
                CapacityTableKind.StaticPressure => t.Metering.StaticPressureTableId == id,
                _ => t.Metering.DecimalTableId == id
            }).Select(t => t.Code).ToArray();
            list.Add(new(id, reader.GetInt64(1), reader.GetString(2), reader.GetString(3), kind,
                (HeightUnit)reader.GetInt32(5), (VolumeUnit)reader.GetInt32(6), reader.GetInt64(7) == 1,
                reader.GetInt32(8), decimal.Parse(reader.GetString(9), CultureInfo.InvariantCulture), associated));
        }
        return list;
    }
    public async Task<CapacityTable> SaveTableAsync(CapacityTable table, AuditActor actor, CancellationToken ct = default)
    {
        actor.Require(AppPermission.CapacityManage);
        var issues = CapacityRules.Validate(table); if (issues.Count > 0) throw new ConfigurationException(issues);
        await using var c = await OpenAsync(ct); using var tx = c.BeginTransaction();
        await using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT Revision FROM capacity_tables WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", table.Id.ToString());
        cmd.CommandText="SELECT 1 FROM deleted_capacity_tables WHERE TableId=$id";if(await cmd.ExecuteScalarAsync(ct) is not null)throw new InvalidOperationException("已删除的罐容表不能修改或重新启用");
        cmd.CommandText="SELECT Revision FROM capacity_tables WHERE Id=$id";
        var existing = await cmd.ExecuteScalarAsync(ct); var current = existing is null ? 0L : Convert.ToInt64(existing);
        if (current != table.Revision) throw new RevisionConflictException();
        cmd.Parameters.AddWithValue("$number", table.Number.Trim()); cmd.CommandText = "SELECT Id FROM capacity_tables WHERE Number=$number AND Id<>$id";
        if (await cmd.ExecuteScalarAsync(ct) is not null) throw new ArgumentException("罐容表编号已存在");
        cmd.CommandText = "SELECT Payload FROM configuration WHERE Id=1"; var config = JsonSerializer.Deserialize<SystemConfiguration>((string)(await cmd.ExecuteScalarAsync(ct))!, Json)!;
        var bound = config.Tanks.Any(t => t.Metering.MainTableId == table.Id || t.Metering.StaticPressureTableId == table.Id || t.Metering.DecimalTableId == table.Id);
        if (bound)
        {
            cmd.CommandText = "SELECT Kind FROM capacity_tables WHERE Id=$id"; var kind = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            if (!table.Enabled || kind != (int)table.Kind) throw new InvalidOperationException("本表仍有关联储罐，请先取消关联再停用或修改类型");
        }
        var next = Freeze(table with { Revision = checked(current + 1), Number = table.Number.Trim(), Name = table.Name.Trim() });
        cmd.Parameters.Clear(); cmd.CommandText = """
            INSERT INTO capacity_tables VALUES($id,$revision,$number,$name,$kind,$height,$volume,$enabled,$count,$density)
            ON CONFLICT(Id) DO UPDATE SET Revision=$revision,Number=$number,Name=$name,Kind=$kind,HeightUnit=$height,VolumeUnit=$volume,Enabled=$enabled,PointCount=$count,ReferenceDensity=$density;
            INSERT INTO capacity_table_versions VALUES($id,$revision,$payload,$at);
            INSERT INTO operation_events(AtUtcMs,Category,ActorId,ActorName,Content,Result,Details)
            VALUES($at,'操作日志',$actor,$actorName,$content,'已校核并保存',$details);
            """;
        cmd.Parameters.AddWithValue("$id", next.Id.ToString()); cmd.Parameters.AddWithValue("$revision", next.Revision); cmd.Parameters.AddWithValue("$number", next.Number);
        cmd.Parameters.AddWithValue("$name", next.Name); cmd.Parameters.AddWithValue("$kind", (int)next.Kind); cmd.Parameters.AddWithValue("$height", (int)next.HeightUnit);
        cmd.Parameters.AddWithValue("$volume", (int)next.VolumeUnit); cmd.Parameters.AddWithValue("$enabled", next.Enabled ? 1 : 0); cmd.Parameters.AddWithValue("$count", next.Points.Count + next.DecimalRows.Count);
        cmd.Parameters.AddWithValue("$density", next.ReferenceDensityKgM3.ToString(CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(next, Json));
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$actor", actor.AccountId); cmd.Parameters.AddWithValue("$actorName", actor.DisplayName);
        cmd.Parameters.AddWithValue("$content", "保存罐容表 " + next.Number + " 版本 " + next.Revision);
        cmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new { eventCode = "capacity.saved", next.Id, next.Revision, previousRevision = current, next.Kind, next.ReferenceDensityKgM3, pointCount = next.Points.Count + next.DecimalRows.Count }, Json));
        await cmd.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct); capacityCache[next.Id] = next;
        return next;
    }
    public async Task DeleteTableAsync(Guid id,long revision,AuditActor actor,CancellationToken ct=default)
    {
        actor.Require(AppPermission.CapacityManage);await using var c=await OpenAsync(ct);using var tx=c.BeginTransaction();await using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT Revision,Number FROM capacity_tables WHERE Id=$id AND Id NOT IN(SELECT TableId FROM deleted_capacity_tables)";cmd.Parameters.AddWithValue("$id",id.ToString());
        string number;await using(var r=await cmd.ExecuteReaderAsync(ct)){if(!await r.ReadAsync(ct))throw new ArgumentException("罐容表不存在或已删除");if(r.GetInt64(0)!=revision)throw new RevisionConflictException();number=r.GetString(1);}
        cmd.CommandText="SELECT Payload FROM configuration WHERE Id=1";var config=JsonSerializer.Deserialize<SystemConfiguration>((string)(await cmd.ExecuteScalarAsync(ct))!,Json)!;
        var bound=config.Tanks.Where(t=>t.Metering.MainTableId==id||t.Metering.DecimalTableId==id||t.Metering.StaticPressureTableId==id).Select(t=>t.Code).ToArray();
        if(bound.Length>0)throw new InvalidOperationException("本表仍关联储罐："+string.Join("、",bound)+"；请先取消关联再删除");
        cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());cmd.Parameters.AddWithValue("$actor",actor.AccountId);cmd.Parameters.AddWithValue("$name",actor.DisplayName);cmd.Parameters.AddWithValue("$content","删除罐容表 "+number+"；历史版本保留");cmd.Parameters.AddWithValue("$details",JsonSerializer.Serialize(new{eventCode="capacity.deleted",id,revision},Json));
        cmd.CommandText="INSERT INTO deleted_capacity_tables VALUES($id,$at,$actor);INSERT INTO operation_events(AtUtcMs,Category,ActorId,ActorName,Content,Result,Details) VALUES($at,'操作日志',$actor,$name,$content,'已从当前列表移除，历史依据保留',$details)";await cmd.ExecuteNonQueryAsync(ct);await tx.CommitAsync(ct);deletedCapacity[id]=0;capacityCache.TryRemove(id,out _);
    }
    public async Task<long> GetCalculationRevisionAsync(string payload, CancellationToken ct = default)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        if (calculationCache.TryGetValue(fingerprint, out var cached)) return cached;
        await using var c = await OpenAsync(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO calculation_versions(Payload,AtUtcMs,Fingerprint) VALUES($payload,$at,$hash) ON CONFLICT(Fingerprint) DO NOTHING; SELECT Id FROM calculation_versions WHERE Fingerprint=$hash";
        cmd.Parameters.AddWithValue("$payload", payload); cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$hash", fingerprint);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)); calculationCache[fingerprint] = id; return id;
    }

    private static async Task ValidateCapacityBindings(SqliteConnection c, SqliteTransaction tx, SystemConfiguration config, CancellationToken ct)
    {
        foreach (var tank in config.Tanks)
        {
            var bindings = new (Guid? Id, CapacityTableKind Kind)[] { (tank.Metering.MainTableId, CapacityTableKind.Main), (tank.Metering.StaticPressureTableId, CapacityTableKind.StaticPressure), (tank.Metering.DecimalTableId, CapacityTableKind.Decimal) };
            foreach (var b in bindings.Where(b => b.Id.HasValue))
            {
                await using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT Kind,Enabled FROM capacity_tables WHERE Id=$id AND Id NOT IN(SELECT TableId FROM deleted_capacity_tables)"; cmd.Parameters.AddWithValue("$id", b.Id!.Value.ToString());
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct) || reader.GetInt32(0) != (int)b.Kind || reader.GetInt64(1) != 1)
                    throw new ConfigurationException([new("tanks/" + tank.Id + "/metering", "关联罐容表不存在、未启用或类型不符")]);
            }
        }
    }
}
