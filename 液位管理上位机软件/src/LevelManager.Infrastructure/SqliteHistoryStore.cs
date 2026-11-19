using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Naide.LevelManager.Application;
using Naide.LevelManager.Domain;

namespace Naide.LevelManager.Infrastructure;

public sealed partial class SqliteHistoryStore
    : IHistoryStore
{
    private readonly string connectionString;
    public SqliteHistoryStore(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("历史库必须为绝对路径");
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = true, DefaultTimeout = 5 }.ToString();
    }

    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        var c = new SqliteConnection(connectionString);
        try
        {
            await c.OpenAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
            await cmd.ExecuteNonQueryAsync(ct);
            return c;
        }
        catch { await c.DisposeAsync(); throw; }
    }

    public async Task<int> AppendAsync(IReadOnlyList<HistorySample> samples, CancellationToken ct = default)
    {
        if (samples.Count > 4096) throw new ArgumentException("历史批次不能超过4096条");
        if (samples.Count == 0) return 0;
        foreach (var s in samples)
        {
            if (s.TankId == Guid.Empty || s.AcquisitionSessionId == Guid.Empty || s.ConfigurationRevision < 0 || s.CalculationRevision < 0 || s.DiagnosticCode.Length > 128)
                throw new ArgumentException("历史记录标识、版本或诊断码无效");
            _ = DateTimeOffset.FromUnixTimeMilliseconds(s.AtUtcMs);
            _ = HistoryEncoding.QualityBits(s);
            _ = HistoryEncoding.EncodePoints(s.AtsPoints);
            foreach(var m in new[]{s.Pressure1KPa,s.Pressure2KPa,s.Analog1Ma,s.Analog2Ma})_=HistoryEncoding.Encode(m,0);
        }
        await using var c = await Open(ct);
        using var tx = c.BeginTransaction();
        var versions = new Dictionary<long, HashSet<Guid>>();
        foreach (var sample in samples)
        {
            if (!versions.TryGetValue(sample.ConfigurationRevision, out var ids))
            {
                await using var version = c.CreateCommand(); version.Transaction = tx;
                version.CommandText = "SELECT Payload FROM configuration_versions WHERE Revision=$revision";
                version.Parameters.AddWithValue("$revision", sample.ConfigurationRevision);
                var payload = Convert.ToString(await version.ExecuteScalarAsync(ct));
                if (string.IsNullOrEmpty(payload)) throw new ArgumentException("历史记录引用的配置版本不存在");
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
                var config = JsonSerializer.Deserialize<SystemConfiguration>(payload, options)!;
                ids = config.Tanks.Select(t => t.Id).ToHashSet(); versions.Add(sample.ConfigurationRevision, ids);
            }
            if (!ids.Contains(sample.TankId)) throw new ArgumentException("储罐不属于所引用的历史配置版本");
        }
        var tanks = new Dictionary<Guid, long>();
        var sessions = new Dictionary<Guid, long>();
        async Task<long> Dimension(string table, string column, Guid id, Dictionary<Guid, long> cache)
        {
            if (cache.TryGetValue(id, out var key)) return key;
            await using var d = c.CreateCommand();
            d.Transaction = tx;
            d.CommandText = $"INSERT INTO {table}({column}) VALUES($id) ON CONFLICT({column}) DO NOTHING; SELECT Id FROM {table} WHERE {column}=$id;";
            d.Parameters.AddWithValue("$id", id.ToString());
            key = Convert.ToInt64(await d.ExecuteScalarAsync(ct));
            cache.Add(id, key);
            return key;
        }
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO history_samples(TankKey,AtUtcMs,ConfigurationRevision,CalculationRevision,SessionKey,ReceivedAtUtcMs,SourceAtUtcMs,SourceTimeConfirmed,DeviceDataAgeMs,LevelMicrometres,InterfaceMicrometres,InstantMilliC,AverageMilliC,AverageCompletedAtUtcMs,DensityMilliKgM3,CurrentVolumeMicroM3,NetVolumeMicroM3,MassMicroT,QualityBits,InstrumentStatus,InstrumentFault,RawQuality,RawValidity,DiagnosticCode,AtsPoints,Pressure1Pa,Pressure2Pa,PressureQualityBits,Analog1MicroAmp,Analog2MicroAmp,AnalogQualityBits) VALUES(
              $tank,$at,$config,$calc,$session,$received,$source,$confirmed,$age,
              $v0,$v1,$v2,$v3,$averageAt,$v4,$v5,$v6,$v7,$quality,$status,$fault,$rawq,$rawv,$diagnostic,$points,$pressure1,$pressure2,$pressureQ,$analog1,$analog2,$analogQ)
            ON CONFLICT(TankKey,AtUtcMs) DO NOTHING;
            """;
        foreach (var name in new[] { "$tank", "$at", "$config", "$calc", "$session", "$received", "$source", "$confirmed", "$age", "$v0", "$v1", "$v2", "$v3", "$averageAt", "$v4", "$v5", "$v6", "$v7", "$quality", "$status", "$fault", "$rawq", "$rawv", "$diagnostic", "$points", "$pressure1", "$pressure2", "$pressureQ", "$analog1", "$analog2", "$analogQ" })
            cmd.Parameters.Add(new SqliteParameter(name, name == "$points" ? SqliteType.Blob : name == "$diagnostic" ? SqliteType.Text : SqliteType.Integer));
        cmd.Prepare();
        void Set(string name, object? value) => cmd.Parameters[name].Value = value ?? DBNull.Value;
        var inserted = 0;
        foreach (var s in samples)
        {
            ct.ThrowIfCancellationRequested();
            Set("$tank", await Dimension("history_tanks", "TankId", s.TankId, tanks));
            Set("$session", await Dimension("history_sessions", "SessionId", s.AcquisitionSessionId, sessions));
            Set("$at", s.AtUtcMs); Set("$config", s.ConfigurationRevision); Set("$calc", s.CalculationRevision);
            Set("$received", s.ReceivedAtUtcMs); Set("$source", s.SourceAtUtcMs); Set("$confirmed", s.SourceTimeConfirmed ? 1 : 0); Set("$age", s.DeviceDataAgeMs);
            var values = HistoryEncoding.Fields(s);
            for (var i = 0; i < values.Length; i++) Set("$v" + i, HistoryEncoding.Encode(values[i], i));
            Set("$averageAt", s.AverageCompletedAtUtcMs); Set("$quality", HistoryEncoding.QualityBits(s));
            Set("$status", s.InstrumentStatus); Set("$fault", s.InstrumentFault); Set("$rawq", s.RawQuality); Set("$rawv", s.RawValidity);
            Set("$diagnostic", s.DiagnosticCode); Set("$points", HistoryEncoding.EncodePoints(s.AtsPoints));
            Set("$pressure1",HistoryEncoding.Encode(s.Pressure1KPa,0));Set("$pressure2",HistoryEncoding.Encode(s.Pressure2KPa,1));Set("$pressureQ",(long)s.Pressure1KPa.Quality|((long)s.Pressure2KPa.Quality<<3));
            Set("$analog1",HistoryEncoding.Encode(s.Analog1Ma,0));Set("$analog2",HistoryEncoding.Encode(s.Analog2Ma,1));Set("$analogQ",(long)s.Analog1Ma.Quality|((long)s.Analog2Ma.Quality<<3));
            inserted += await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return inserted;
    }

    public async Task<HistoryPage> QueryAsync(HistoryQuery query, CancellationToken ct = default)
    {
        if (query.StartUtcMs >= query.EndUtcMs || query.Limit is < 1 or > 2000 || query.TankId == Guid.Empty)
            throw new ArgumentException("历史查询范围、罐号或数量无效");
        await using var c = await Open(ct);
        await using var cmd = c.CreateCommand();
        var tankPredicate = query.TankId.HasValue ? " AND h.TankKey=(SELECT Id FROM history_tanks WHERE TankId=$tank)" : "";
        var cursorPredicate = query.After is null ? "" : " AND (h.AtUtcMs,h.TankKey)>($afterAt,$afterTank)";
        cmd.CommandText = """
            SELECT h.*,t.TankId,s.SessionId FROM history_samples h
            JOIN history_tanks t ON t.Id=h.TankKey JOIN history_sessions s ON s.Id=h.SessionKey
            WHERE h.AtUtcMs >= $start AND h.AtUtcMs < $end
            """ + tankPredicate + cursorPredicate + QualityPredicate(query,cmd) + " ORDER BY h.AtUtcMs,h.TankKey LIMIT $limit";
        cmd.Parameters.AddWithValue("$start", query.StartUtcMs); cmd.Parameters.AddWithValue("$end", query.EndUtcMs); cmd.Parameters.AddWithValue("$limit", query.Limit + 1);
        if (query.TankId.HasValue) cmd.Parameters.AddWithValue("$tank", query.TankId.Value.ToString());
        if (query.After is { } after) { cmd.Parameters.AddWithValue("$afterAt", after.AtUtcMs); cmd.Parameters.AddWithValue("$afterTank", after.TankKey); }
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<HistorySample>();
        HistoryCursor? last = null;
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == query.Limit) return new(rows, last);
            rows.Add(ReadHistoryRow(reader));
            last = new(reader.GetInt64(reader.GetOrdinal("AtUtcMs")), reader.GetInt64(reader.GetOrdinal("TankKey")));
        }
        return new(rows, null);
    }

    private static HistorySample ReadHistoryRow(SqliteDataReader reader)
    {
            long? N(string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt64(reader.GetOrdinal(name));
            var bits = N("QualityBits")!.Value;
            Measurement M(string name, int field) => HistoryEncoding.Decode(N(name), bits, field);
            return new()
            {
                TankId = Guid.Parse(reader.GetString(reader.GetOrdinal("TankId"))), AtUtcMs = N("AtUtcMs")!.Value,
                ConfigurationRevision = N("ConfigurationRevision")!.Value, CalculationRevision = N("CalculationRevision")!.Value,
                AcquisitionSessionId = Guid.Parse(reader.GetString(reader.GetOrdinal("SessionId"))),
                ReceivedAtUtcMs = N("ReceivedAtUtcMs"), SourceAtUtcMs = N("SourceAtUtcMs"), SourceTimeConfirmed = N("SourceTimeConfirmed") == 1,
                DeviceDataAgeMs = (uint?)N("DeviceDataAgeMs"), LevelMm = M("LevelMicrometres", 0), InterfaceMm = M("InterfaceMicrometres", 1),
                InstantTemperatureC = M("InstantMilliC", 2), AverageTemperatureC = M("AverageMilliC", 3), AverageCompletedAtUtcMs = N("AverageCompletedAtUtcMs"),
                InstrumentDensityKgM3 = M("DensityMilliKgM3", 4), CurrentVolumeM3 = M("CurrentVolumeMicroM3", 5), NetVolumeM3 = M("NetVolumeMicroM3", 6), MassT = M("MassMicroT", 7),
                Analog1Ma=HistoryEncoding.Decode(N("Analog1MicroAmp"),N("AnalogQualityBits")!.Value,0),Analog2Ma=HistoryEncoding.Decode(N("Analog2MicroAmp"),N("AnalogQualityBits")!.Value,1),
                Pressure1KPa=HistoryEncoding.Decode(N("Pressure1Pa"),N("PressureQualityBits")!.Value,0),Pressure2KPa=HistoryEncoding.Decode(N("Pressure2Pa"),N("PressureQualityBits")!.Value,1),
                InstrumentStatus = (ushort?)N("InstrumentStatus"), InstrumentFault = (ushort?)N("InstrumentFault"), RawQuality = (ushort)N("RawQuality")!.Value, RawValidity = (ushort)N("RawValidity")!.Value,
                DiagnosticCode = reader.GetString(reader.GetOrdinal("DiagnosticCode")),
                AtsPoints = HistoryEncoding.DecodePoints(reader.IsDBNull(reader.GetOrdinal("AtsPoints")) ? null : (byte[])reader["AtsPoints"])
            };
    }

    public async Task<HistoryStatistics> StatisticsAsync(CancellationToken ct = default)
    {
        await using var c = await Open(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*),min(AtUtcMs),max(AtUtcMs) FROM history_samples";
        await using var reader = await cmd.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
        return new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    public async Task<int> DeleteExpiredBatchAsync(long beforeUtcMs, int maximumRows = 4096, CancellationToken ct = default)
    {
        if (maximumRows is < 1 or > 10000) throw new ArgumentException("清理批次大小无效");
        await using var c = await Open(ct); await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM history_samples WHERE (TankKey,AtUtcMs) IN (SELECT TankKey,AtUtcMs FROM history_samples WHERE AtUtcMs<$before AND NOT EXISTS(SELECT 1 FROM history_record_pins p WHERE p.TankKey=history_samples.TankKey AND p.AtUtcMs=history_samples.AtUtcMs) ORDER BY AtUtcMs,TankKey LIMIT $limit)";
        cmd.Parameters.AddWithValue("$before", beforeUtcMs); cmd.Parameters.AddWithValue("$limit", maximumRows);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
