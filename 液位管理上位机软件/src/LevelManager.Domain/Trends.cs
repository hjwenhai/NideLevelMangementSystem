namespace Naide.LevelManager.Domain;
public enum TrendQuantity { Level, InstantTemperature, AverageTemperature, Density, CurrentVolume, NetVolume, Mass, Pressure1, Pressure2 }
public sealed record TrendSelection(Guid TankId, TrendQuantity Quantity);
public sealed record TrendPoint(long AtUtcMs, decimal? Value, ValueQuality Quality, bool GapMarker = false);
public sealed record TrendSeries(TrendSelection Selection, IReadOnlyList<TrendPoint> Points, long SourceRows, bool Reduced);
public sealed record TrendQuery(long StartUtcMs, long EndUtcMs, IReadOnlyList<TrendSelection> Series, int MaximumPoints = 2000);
public sealed record TrendResult(long StartUtcMs,long EndUtcMs,long ServerAtUtcMs, IReadOnlyList<TrendSeries> Series, string TimeBasis);

// Keeps actual first/last/extrema, never averages or interpolates. Segment identities
// prevent a line being drawn across any omitted invalid sample, including short gaps.
public sealed class TrendReducer(long start, long end, int maximumPoints)
{
    private sealed record Candidate(TrendPoint Point, long Segment, TrendPoint? GapBefore);
    private readonly List<TrendPoint> output = [];
    private List<TrendPoint>? exact = [];
    public bool Reduced => exact is null;
    private readonly int buckets = Math.Max(1, maximumPoints / 10);
    private Candidate? first, last, minimum, maximum;
    private TrendPoint? firstInvalid, gap;
    private long segment, previousOutputSegment = -1, count;
    private bool inGap;
    private int currentBucket = -1;
    public long SourceRows => count;
    public void Add(long at, Measurement value,bool gapMarker=false)
    {
        if (at < start || at >= end) throw new ArgumentOutOfRangeException(nameof(at));
        var bucket = (int)((at - start) * buckets / (end - start));
        if (bucket != currentBucket) { Flush(); currentBucket = bucket; }
        if(!gapMarker)count++;
        var point = new TrendPoint(at, value.IsValid ? value.Value : null, value.Quality,gapMarker);
        if(exact is not null){exact.Add(point);if(exact.Count>maximumPoints)exact=null;}
        if (!value.IsValid)
        {
            firstInvalid ??= point;
            if (!inGap) { segment++; gap = point with { GapMarker = true }; inGap = true; }
            return;
        }
        inGap = false;
        var candidate = new Candidate(point, segment, gap);
        first ??= candidate; last = candidate;
        if (minimum is null || point.Value < minimum.Point.Value) minimum = candidate;
        if (maximum is null || point.Value > maximum.Point.Value) maximum = candidate;
    }
    private void Flush()
    {
        if (first is null)
        {
            if (firstInvalid is not null) { output.Add(firstInvalid); previousOutputSegment = -1; }
        }
        else
        {
            foreach (var c in new[] { first, minimum, maximum, last }.Where(c => c is not null).Select(c => c!).DistinctBy(c => c.Point.AtUtcMs).OrderBy(c => c.Point.AtUtcMs))
            {
                if (previousOutputSegment >= 0 && previousOutputSegment != c.Segment && c.GapBefore is { } marker) output.Add(marker);
                output.Add(c.Point); previousOutputSegment = c.Segment;
            }
        }
        first = last = minimum = maximum = null; firstInvalid = null;
    }
    public IReadOnlyList<TrendPoint> Finish() { Flush(); return exact ?? output; }
}
public static class TrendValues
{
    public static Measurement Get(ReadingSnapshot s, TrendQuantity quantity) => quantity switch
    {
        TrendQuantity.Level => s.LevelMm, TrendQuantity.InstantTemperature => s.InstantTemperatureC, TrendQuantity.AverageTemperature => s.AverageTemperatureC,
        TrendQuantity.Density => s.InstrumentDensityKgM3, TrendQuantity.CurrentVolume => s.CurrentVolumeM3, TrendQuantity.NetVolume => s.NetVolumeM3,
        TrendQuantity.Mass => s.MassT, TrendQuantity.Pressure1 => s.Pressure1KPa, TrendQuantity.Pressure2 => s.Pressure2KPa,
        _ => throw new ArgumentException("曲线数据类型无效")
    };
    public static void Validate(TrendQuery query)
    {
        if (query.Series is null || query.Series.Count is < 1 or > 5 || query.Series.Distinct().Count() != query.Series.Count || query.Series.Any(s => s is null || s.TankId == Guid.Empty || !Enum.IsDefined(s.Quantity)) || query.StartUtcMs >= query.EndUtcMs || query.EndUtcMs - query.StartUtcMs > 367L * 86400000 || query.MaximumPoints is < 100 or > 5000)
            throw new ArgumentException("请选择1～5条不同曲线，时间范围不超过一年，每条100～5000点");
        _ = DateTimeOffset.FromUnixTimeMilliseconds(query.StartUtcMs); _ = DateTimeOffset.FromUnixTimeMilliseconds(query.EndUtcMs);
    }
}

public sealed record TrendPreferences
{
 public IReadOnlyList<TrendQuantity> Quantities {get;init;}=[TrendQuantity.Level,TrendQuantity.NetVolume,TrendQuantity.Mass];
 public IReadOnlyList<string> Colors {get;init;}=["#318c9b","#b27852","#75934b"];
 public IReadOnlyDictionary<TrendQuantity,TrendAxisPreferences> Axes {get;init;}=new Dictionary<TrendQuantity,TrendAxisPreferences>();
}
public enum TrendAxisMode { Auto, Fixed }
// Canonical units: mm, degrees C, kg/m3, m3, tonnes and kPa; independent of display units.
public sealed record TrendAxisPreferences
{
 public TrendAxisMode Mode {get;init;}
 public decimal MinimumSpan {get;init;}=0;
 public decimal? Minimum {get;init;}
 public decimal? Maximum {get;init;}
}
