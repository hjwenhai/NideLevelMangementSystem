using Microsoft.Data.Sqlite;
using System.Runtime.CompilerServices;
using Naide.LevelManager.Domain;

namespace Naide.LevelManager.Infrastructure;
public sealed partial class SqliteHistoryStore
{
    public async IAsyncEnumerable<HistorySample> ReadForExportAsync(HistoryQuery query,[EnumeratorCancellation] CancellationToken ct=default)
    {
        if(query.StartUtcMs>=query.EndUtcMs||query.EndUtcMs-query.StartUtcMs>367L*86400000||query.TankId==Guid.Empty)throw new ArgumentException("导出时间范围或罐号无效");
        await using var c=await Open(ct);using var tx=c.BeginTransaction(deferred:true);await using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT h.*,t.TankId,s.SessionId FROM history_samples h JOIN history_tanks t ON t.Id=h.TankKey JOIN history_sessions s ON s.Id=h.SessionKey WHERE h.AtUtcMs >= $start AND h.AtUtcMs < $end"+(query.TankId.HasValue?" AND h.TankKey=(SELECT Id FROM history_tanks WHERE TankId=$tank)":"")+QualityPredicate(query,cmd)+" ORDER BY h.AtUtcMs,h.TankKey";
        cmd.Parameters.AddWithValue("$start",query.StartUtcMs);cmd.Parameters.AddWithValue("$end",query.EndUtcMs);if(query.TankId.HasValue)cmd.Parameters.AddWithValue("$tank",query.TankId.Value.ToString());
        await using var reader=await cmd.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct)){ct.ThrowIfCancellationRequested();yield return ReadHistoryRow(reader);}await reader.DisposeAsync();await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<TrendSeries>> QueryTrendAsync(TrendQuery query, CancellationToken ct = default)
    {
        TrendValues.Validate(query); List<TrendSeries> result = [];
        await using var c = await Open(ct); using var tx = c.BeginTransaction(deferred: true);
        foreach (var selection in query.Series)
        {
            var (column, qualityColumn, field) = selection.Quantity switch
            {
                TrendQuantity.Level => ("LevelMicrometres", "QualityBits", 0), TrendQuantity.InstantTemperature => ("InstantMilliC", "QualityBits", 2),
                TrendQuantity.AverageTemperature => ("AverageMilliC", "QualityBits", 3), TrendQuantity.Density => ("DensityMilliKgM3", "QualityBits", 4),
                TrendQuantity.CurrentVolume => ("CurrentVolumeMicroM3", "QualityBits", 5), TrendQuantity.NetVolume => ("NetVolumeMicroM3", "QualityBits", 6),
                TrendQuantity.Mass => ("MassMicroT", "QualityBits", 7), TrendQuantity.Pressure1 => ("Pressure1Pa", "PressureQualityBits", 0),
                TrendQuantity.Pressure2 => ("Pressure2Pa", "PressureQualityBits", 1), _ => throw new ArgumentException("曲线数据类型无效")
            };
            await using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = $"SELECT AtUtcMs,{column},{qualityColumn} FROM history_samples WHERE TankKey=(SELECT Id FROM history_tanks WHERE TankId=$tank) AND AtUtcMs >= $start AND AtUtcMs < $end ORDER BY AtUtcMs";
            cmd.Parameters.AddWithValue("$tank", selection.TankId.ToString()); cmd.Parameters.AddWithValue("$start", query.StartUtcMs); cmd.Parameters.AddWithValue("$end", query.EndUtcMs);
            var reducer = new TrendReducer(query.StartUtcMs, query.EndUtcMs, query.MaximumPoints);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            { ct.ThrowIfCancellationRequested(); reducer.Add(reader.GetInt64(0), HistoryEncoding.Decode(reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt64(2), field)); }
            var points = reducer.Finish(); result.Add(new(selection, points, reducer.SourceRows, reducer.Reduced));
        }
        await tx.CommitAsync(ct); return result;
    }
}
