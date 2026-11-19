using System.Collections.Concurrent;
using Naide.LevelManager.Domain;

namespace Naide.LevelManager.Host;
public sealed class RealtimeTrends(TimeProvider clock)
{
    private const long WindowMilliseconds = 60 * 60 * 1000;
    // At 500 ms per poll, one hour has 7200 readings. Leave room for gap markers.
    private const int MaximumFramesPerTank = 8192;

    private sealed record Frame(long AtUtcMs,Measurement[] Values,bool Gap);
    private sealed class Buffer { public readonly Queue<Frame> Samples = new(); }
    private readonly ConcurrentDictionary<Guid, Buffer> buffers = new();
    public void Publish(ReadingSnapshot reading)
    {
        var b = buffers.GetOrAdd(reading.TankId, _ => new());
        lock (b)
        {
            b.Samples.Enqueue(new(reading.ReceivedAtUtc.ToUnixTimeMilliseconds(),Enum.GetValues<TrendQuantity>().Select(q=>TrendValues.Get(reading,q)).ToArray(),reading.Diagnostic=="REALTIME_GAP"));
            var cutoff = clock.GetUtcNow().ToUnixTimeMilliseconds() + 1 - WindowMilliseconds;
            while (b.Samples.TryPeek(out var first) && (first.AtUtcMs < cutoff || b.Samples.Count > MaximumFramesPerTank)) b.Samples.Dequeue();
        }
    }
    public void GapAll()
    {
        var at = clock.GetUtcNow();
        foreach (var id in buffers.Keys) Publish(new ReadingSnapshot { TankId = id, ReceivedAtUtc = at,Diagnostic="REALTIME_GAP" });
    }
    public TrendResult Query(TrendQuery query, int expirySeconds)
    {
        TrendValues.Validate(query);
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var end = now + 1; // The upper bound is exclusive. Include the current millisecond.
        query = query with { StartUtcMs = end - WindowMilliseconds, EndUtcMs = end };
        List<TrendSeries> result = [];
        foreach (var selection in query.Series)
        {
            Frame[] samples = [];
            if (buffers.TryGetValue(selection.TankId, out var b)) lock (b) samples = b.Samples.ToArray();
            var reducer = new TrendReducer(query.StartUtcMs, query.EndUtcMs, query.MaximumPoints); long? last = null;
            foreach (var sample in samples)
            {
                var at = sample.AtUtcMs;
                if (last.HasValue && at <= last.Value || at < query.StartUtcMs || at >= query.EndUtcMs) continue;
                if (last.HasValue && at - last > expirySeconds * 1000L) reducer.Add(last.Value + expirySeconds * 1000L, new(null, ValueQuality.Expired),true);
                reducer.Add(at, sample.Values[(int)selection.Quantity],sample.Gap); last = at;
            }
            if (last.HasValue && now - last > expirySeconds * 1000L && last + expirySeconds * 1000L < query.EndUtcMs) reducer.Add(last.Value + expirySeconds * 1000L, new(null, ValueQuality.Expired),true);
            var points = reducer.Finish(); result.Add(new(selection, points, reducer.SourceRows, reducer.Reduced));
        }
        return new(query.StartUtcMs,query.EndUtcMs,now, result, "实时曲线时间为软件接收时间；后台重启后从新采集数据开始。空值用于标记缺测，不计算估算值。");
    }
}
