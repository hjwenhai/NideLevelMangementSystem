namespace Naide.LevelManager.Domain;

public sealed record TemperaturePoint(int Index, Measurement TemperatureC);

// A history record contains the results and inputs at archive time. Later edits never
// recalculate this record. Configuration and calculation documents are version references.
public sealed record HistorySample
{
    public Guid TankId { get; init; }
    public long AtUtcMs { get; init; }
    public long ConfigurationRevision { get; init; }
    public long CalculationRevision { get; init; }
    public Guid AcquisitionSessionId { get; init; }
    public long? ReceivedAtUtcMs { get; init; }
    public long? SourceAtUtcMs { get; init; }
    public bool SourceTimeConfirmed { get; init; }
    public uint? DeviceDataAgeMs { get; init; }
    public Measurement LevelMm { get; init; } = Measurement.Missing("尚未采集");
    public Measurement InterfaceMm { get; init; } = new(null, ValueQuality.Unconfigured);
    public Measurement InstantTemperatureC { get; init; } = Measurement.Missing("尚未采集");
    public Measurement AverageTemperatureC { get; init; } = new(null, ValueQuality.Unconfigured);
    public long? AverageCompletedAtUtcMs { get; init; }
    public Measurement InstrumentDensityKgM3 { get; init; } = Measurement.Missing("尚未采集");
    public Measurement CurrentVolumeM3 { get; init; } = new(null, ValueQuality.Unconfigured);
    public Measurement NetVolumeM3 { get; init; } = new(null, ValueQuality.Unconfigured);
    public Measurement Analog1Ma {get;init;}=new(null,ValueQuality.Unconfigured);
    public Measurement Analog2Ma {get;init;}=new(null,ValueQuality.Unconfigured);
    public Measurement Pressure1KPa { get; init; } = new(null,ValueQuality.Unconfigured);
    public Measurement Pressure2KPa { get; init; } = new(null,ValueQuality.Unconfigured);
    public Measurement MassT { get; init; } = new(null, ValueQuality.Unconfigured);
    public ushort? InstrumentStatus { get; init; }
    public ushort? InstrumentFault { get; init; }
    public ushort RawQuality { get; init; }
    public ushort RawValidity { get; init; }
    public string DiagnosticCode { get; init; } = "";
    public IReadOnlyList<TemperaturePoint> AtsPoints { get; init; } = [];
}

public sealed record HistoryCursor(long AtUtcMs, long TankKey);
public sealed record HistoryQuery(long StartUtcMs, long EndUtcMs, Guid? TankId = null,
    int Limit = 500, HistoryCursor? After = null, TrendQuantity QualityQuantity=TrendQuantity.Level, HistoryQualityFilter Quality=HistoryQualityFilter.All);
public enum HistoryQualityFilter { All, Good, Invalid, Missing, Offline, Expired, Fault, Unconfigured }
public static class HistoryQualityPolicy
{
    public static void Validate(TrendQuantity quantity,HistoryQualityFilter quality){if(!Enum.IsDefined(quantity)||!Enum.IsDefined(quality))throw new ArgumentException("历史数据项或状态筛选无效");}
}
public sealed record HistoryPage(IReadOnlyList<HistorySample> Rows, HistoryCursor? Next);
public sealed record HistoryStatistics(long Rows, long? FirstUtcMs, long? LastUtcMs);

public static class HistoryEncoding
{
    public const int FieldCount = 8;
    public static Measurement[] Fields(HistorySample s) => [s.LevelMm, s.InterfaceMm,
        s.InstantTemperatureC, s.AverageTemperatureC, s.InstrumentDensityKgM3,
        s.CurrentVolumeM3, s.NetVolumeM3, s.MassT];
    public static readonly decimal[] Scales = [1000m, 1000m, 1000m, 1000m, 1000m, 1_000_000m, 1_000_000m, 1_000_000m];

    public static long? Encode(Measurement value, int field)
    {
        if (!Enum.IsDefined(value.Quality) || value.Quality == ValueQuality.Good && !value.Value.HasValue)
            throw new ArgumentException("历史数据质量与数值不一致");
        return value.IsValid ? checked((long)decimal.Round(value.Value!.Value * Scales[field], 0, MidpointRounding.AwayFromZero)) : null;
    }

    public static long QualityBits(HistorySample sample)
    {
        long bits = 0;
        var fields = Fields(sample);
        for (var i = 0; i < fields.Length; i++)
        {
            _ = Encode(fields[i], i);
            bits |= (long)fields[i].Quality << (i * 3);
        }
        return bits;
    }

    public static Measurement Decode(long? value, long bits, int field)
    {
        var q = (ValueQuality)((bits >> (field * 3)) & 7);
        if (!Enum.IsDefined(q) || q == ValueQuality.Good != value.HasValue)
            throw new InvalidDataException("历史数据的质量编码无效");
        return new(value.HasValue ? value / Scales[field] : null, q);
    }

    // Version, count, then fixed-size [index, milli-Celsius int32 little endian, quality].
    public static byte[]? EncodePoints(IReadOnlyList<TemperaturePoint> points)
    {
        if (points.Count == 0) return null;
        if (points.Count > 16 || points.Any(p => p.Index is < 1 or > 16) || points.Select(p => p.Index).Distinct().Count() != points.Count)
            throw new ArgumentException("ATS历史测点编号或数量无效");
        var bytes = new byte[2 + points.Count * 6];
        bytes[0] = 1;
        bytes[1] = (byte)points.Count;
        var at = 2;
        foreach (var point in points.OrderBy(p => p.Index))
        {
            var value = Encode(point.TemperatureC, 2);
            bytes[at] = (byte)point.Index;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 1, 4), checked((int)(value ?? 0)));
            bytes[at + 5] = (byte)point.TemperatureC.Quality;
            at += 6;
        }
        return bytes;
    }

    public static IReadOnlyList<TemperaturePoint> DecodePoints(byte[]? bytes)
    {
        if (bytes is null) return [];
        if (bytes.Length < 2 || bytes[0] != 1 || bytes[1] is < 1 or > 16 || bytes.Length != 2 + bytes[1] * 6)
            throw new InvalidDataException("ATS历史数据格式无效");
        List<TemperaturePoint> points = [];
        for (var i = 2; i < bytes.Length; i += 6)
        {
            var q = (ValueQuality)bytes[i + 5];
            if (!Enum.IsDefined(q) || bytes[i] is < 1 or > 16 || points.Any(p => p.Index == bytes[i]))
                throw new InvalidDataException("ATS历史测点编码无效");
            var raw = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i + 1, 4));
            points.Add(new(bytes[i], new(q == ValueQuality.Good ? raw / 1000m : null, q)));
        }
        return points;
    }
}
