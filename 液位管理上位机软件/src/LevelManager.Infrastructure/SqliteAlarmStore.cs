using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;

namespace Naide.LevelManager.Infrastructure;

public sealed class SqliteAlarmStore(string databasePath) : IAlarmStore
{
    private static readonly JsonSerializerOptions Json = CreateJson();
    private static JsonSerializerOptions CreateJson() { var o = new JsonSerializerOptions(JsonSerializerDefaults.Web); o.Converters.Add(new JsonStringEnumConverter()); return o; }
    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true, DefaultTimeout = 10 }.ToString());
        await c.OpenAsync(ct); await using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA synchronous=FULL"; await cmd.ExecuteNonQueryAsync(ct); return c;
    }
    private static async Task<Dictionary<(Guid, string), (AlarmRuntime State, string Payload)>> Runtimes(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT Payload FROM alarm_runtime";
        Dictionary<(Guid, string), (AlarmRuntime, string)> result = []; await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) { var payload = reader.GetString(0); var state = JsonSerializer.Deserialize<AlarmRuntime>(payload, Json)!; result.Add((state.Rule.TankId, state.Rule.Key), (state, payload)); }
        return result;
    }
    private static async Task SaveRuntime(SqliteConnection c, SqliteTransaction tx, AlarmRuntime runtime, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO alarm_runtime(TankId,RuleKey,CurrentId,Payload) VALUES($tank,$key,$current,$payload) ON CONFLICT(TankId,RuleKey) DO UPDATE SET CurrentId=excluded.CurrentId,Payload=excluded.Payload";
        cmd.Parameters.AddWithValue("$tank", runtime.Rule.TankId.ToString()); cmd.Parameters.AddWithValue("$key", runtime.Rule.Key);
        cmd.Parameters.AddWithValue("$current", (object?)runtime.Current?.Id.ToString() ?? DBNull.Value); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(runtime, Json)); await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task SaveOccurrence(SqliteConnection c, SqliteTransaction tx, AlarmOccurrence record, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO alarm_occurrences(Id,TankId,RuleKey,ActivatedAtUtcMs,State,Payload) VALUES($id,$tank,$key,$at,$state,$payload) ON CONFLICT(Id) DO UPDATE SET State=excluded.State,Payload=excluded.Payload";
        cmd.Parameters.AddWithValue("$id", record.Id.ToString()); cmd.Parameters.AddWithValue("$tank", record.Rule.TankId.ToString()); cmd.Parameters.AddWithValue("$key", record.Rule.Key);
        cmd.Parameters.AddWithValue("$at", record.ActivatedAtUtc.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$state", record.State); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record, Json)); await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task Event(SqliteConnection c, SqliteTransaction tx, AlarmOccurrence record, string code, DateTimeOffset now, AuditActor? actor, CancellationToken ct)
    {
        var action = code switch { "alarm.activated" => "发生", "alarm.restored" => "恢复", "alarm.acknowledged" => "确认", "alarm.dataQualityChanged" => "数据质量变化，已有报警保留", _ => record.EndReason ?? "监测终止" };
        await using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO operation_events(AtUtcMs,Category,ActorId,ActorName,TankId,TankCode,Content,Result,Details) VALUES($at,'报警事件',$actor,$name,$tank,$tankCode,$content,$result,$details)";
        cmd.Parameters.AddWithValue("$at", now.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$actor", actor?.AccountId ?? "system"); cmd.Parameters.AddWithValue("$name", actor?.DisplayName ?? "系统");
        cmd.Parameters.AddWithValue("$tank", record.Rule.TankId.ToString()); cmd.Parameters.AddWithValue("$tankCode", record.Rule.TankCode);
        cmd.Parameters.AddWithValue("$content", record.Rule.Name + "报警" + action); cmd.Parameters.AddWithValue("$result", action);
        cmd.Parameters.AddWithValue("$details", JsonSerializer.Serialize(new { EventCode = code, AlarmId = record.Id, record.Rule, record.LastQuality, record.RestoreValue }, Json)); await cmd.ExecuteNonQueryAsync(ct);
    }
    public async Task<AlarmSummary> SummaryAsync(CancellationToken ct=default)
    {
        await using var c=await Open(ct);await using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT TankId,SUM(CASE WHEN State IN ('ActiveUnacknowledged','ActiveAcknowledged') THEN 1 ELSE 0 END),SUM(CASE WHEN State='RecoveredUnacknowledged' THEN 1 ELSE 0 END),SUM(CASE WHEN State IN ('ActiveUnacknowledged','RecoveredUnacknowledged') THEN 1 ELSE 0 END) FROM alarm_occurrences WHERE State IN ('ActiveUnacknowledged','ActiveAcknowledged','RecoveredUnacknowledged') GROUP BY TankId";
        List<TankAlarmSummary> rows=[];await using var reader=await cmd.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))rows.Add(new(Guid.Parse(reader.GetString(0)),reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3)));
        return new(rows.Sum(r=>r.Active),rows.Sum(r=>r.Unacknowledged),rows.Sum(r=>r.RecoveredUnacknowledged),rows);
    }
    public async Task ResetPendingAsync(CancellationToken ct = default)
    {
        await using var c = await Open(ct); using var tx = c.BeginTransaction();
        foreach (var (_, row) in await Runtimes(c, tx, ct)) if (row.State.CandidateSinceUtc.HasValue) await SaveRuntime(c, tx, row.State with { CandidateSinceUtc = null }, ct);
        await tx.CommitAsync(ct);
    }
    public async Task EvaluateAsync(IReadOnlyList<AlarmObservation> observations, DateTimeOffset now, CancellationToken ct = default)
    {
        if (observations.Count > 4096 || observations.Select(x => (x.Rule.TankId, x.Rule.Key)).Distinct().Count() != observations.Count) throw new ArgumentException("报警输入数量或规则键无效");
        await using var c = await Open(ct); using var tx = c.BeginTransaction(); var states = await Runtimes(c, tx, ct);
        foreach (var input in observations)
        {
            var key = (input.Rule.TankId, input.Rule.Key); states.Remove(key, out var previous);
            var result = AlarmEngine.Advance(previous.State, input.Rule, input.Value, now);
            for (var i = 0; i < result.Changed.Count; i++) { await SaveOccurrence(c, tx, result.Changed[i], ct); await Event(c, tx, result.Changed[i], result.Events[i], now, null, ct); }
            if (JsonSerializer.Serialize(result.Runtime, Json) != previous.Payload) await SaveRuntime(c, tx, result.Runtime, ct);
        }
        // A disabled/deleted tank or disabled rule ends monitoring with an explicit reason, never "recovered".
        foreach (var (_, previous) in states)
        {
            if (previous.State.Current is { } current) { var ended = current with { EndedAtUtc = now, EndReason = "储罐、通道或报警规则已停用/删除" }; await SaveOccurrence(c, tx, ended, ct); await Event(c, tx, ended, "alarm.monitoringEnded", now, null, ct); }
            await using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "DELETE FROM alarm_runtime WHERE TankId=$tank AND RuleKey=$key";
            cmd.Parameters.AddWithValue("$tank", previous.State.Rule.TankId.ToString()); cmd.Parameters.AddWithValue("$key", previous.State.Rule.Key); await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<AlarmOccurrence>> QueryAsync(AlarmQuery query, CancellationToken ct = default)
    {
        if (query.Limit is < 1 or > 1000 || query.Start > query.End || query.State is not ("attention" or "active" or "unacknowledged" or "history" or "all")) throw new ArgumentException("报警查询条件无效");
        await using var c = await Open(ct); await using var cmd = c.CreateCommand();
        var state = query.State switch { "attention" => "AND State IN ('ActiveAcknowledged','ActiveUnacknowledged','RecoveredUnacknowledged')", "active" => "AND State IN ('ActiveAcknowledged','ActiveUnacknowledged')", "unacknowledged" => "AND State IN ('ActiveUnacknowledged','RecoveredUnacknowledged')", "history" => "AND State IN ('Closed','Ended')", _ => "" };
        cmd.CommandText = "SELECT Payload FROM alarm_occurrences WHERE ($tank IS NULL OR TankId=$tank) AND ($start IS NULL OR ActivatedAtUtcMs >= $start) AND ($end IS NULL OR ActivatedAtUtcMs <= $end) " + state + " ORDER BY ActivatedAtUtcMs DESC,Id LIMIT $limit";
        cmd.Parameters.AddWithValue("$tank", (object?)query.TankId?.ToString() ?? DBNull.Value); cmd.Parameters.AddWithValue("$start", (object?)query.Start?.ToUnixTimeMilliseconds() ?? DBNull.Value); cmd.Parameters.AddWithValue("$end", (object?)query.End?.ToUnixTimeMilliseconds() ?? DBNull.Value); cmd.Parameters.AddWithValue("$limit", query.Limit);
        List<AlarmOccurrence> result = []; await using var reader = await cmd.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) result.Add(JsonSerializer.Deserialize<AlarmOccurrence>(reader.GetString(0), Json)!); return result;
    }
    public async Task<int> AcknowledgeAsync(IReadOnlyList<Guid> ids, AuditActor actor, DateTimeOffset now, CancellationToken ct = default)
    {
        actor.Require(AppPermission.AlarmAcknowledge);
        if (ids.Count is < 1 or > 500 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Count) throw new ArgumentException("请选择1～500条不同报警");
        await using var c = await Open(ct); using var tx = c.BeginTransaction(); var runtimes = await Runtimes(c, tx, ct); var count = 0;
        foreach (var id in ids)
        {
            await using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT Payload FROM alarm_occurrences WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString());
            if (await cmd.ExecuteScalarAsync(ct) is not string payload) throw new ArgumentException("选中的报警不存在，未保存本次确认");
            var record = JsonSerializer.Deserialize<AlarmOccurrence>(payload, Json)!;
            if (record.AcknowledgedAtUtc.HasValue || record.EndedAtUtc.HasValue) continue;
            record = record with { AcknowledgedAtUtc = now < record.ActivatedAtUtc ? record.ActivatedAtUtc : now, AcknowledgedById = actor.AccountId, AcknowledgedByName = actor.DisplayName };
            await SaveOccurrence(c, tx, record, ct); await Event(c, tx, record, "alarm.acknowledged", now, actor, ct);
            if (runtimes.TryGetValue((record.Rule.TankId, record.Rule.Key), out var runtime) && runtime.State.Current?.Id == id) await SaveRuntime(c, tx, runtime.State with { Current = record }, ct);
            count++;
        }
        await tx.CommitAsync(ct); return count;
    }
}
