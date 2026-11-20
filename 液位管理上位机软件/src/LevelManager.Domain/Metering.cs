namespace Naide.LevelManager.Domain;

public enum CapacityTableKind { Main, StaticPressure, Decimal }
public enum HeightUnit { Millimetre, Centimetre, Metre }
public enum VolumeUnit { CubicMetre, Litre }
public enum MeteringDensitySource { Instrument, Manual }
public enum BaseVolumeMethod { Interpolate, MainPlusDecimal }
public enum FractionalMillimetreRule { Unconfirmed, Linear, IntegerOnly }
public readonly record struct DecimalCapacityRow(decimal StartHeightMm, decimal EndHeightMm, int Number, decimal CentimetreLitres, decimal MillimetreLitres);
public readonly record struct CapacityPoint(decimal Height, decimal Volume);
public sealed record CapacityTable
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long Revision { get; init; }
    public string Number { get; init; } = "";
    public string Name { get; init; } = "";
    public CapacityTableKind Kind { get; init; }
    public HeightUnit HeightUnit { get; init; }
    public VolumeUnit VolumeUnit { get; init; }
    public decimal ReferenceDensityKgM3 { get; init; } = 1000;
    public bool Enabled { get; init; } = true;
    public string Notes { get; init; } = "";
    public IReadOnlyList<CapacityPoint> Points { get; init; } = [];
    public IReadOnlyList<DecimalCapacityRow> DecimalRows { get; init; } = [];
}
public sealed record CapacityTableSummary(Guid Id, long Revision, string Number, string Name,
    CapacityTableKind Kind, HeightUnit HeightUnit, VolumeUnit VolumeUnit, bool Enabled,
    int PointCount, decimal ReferenceDensityKgM3, IReadOnlyList<string> AssociatedTanks);
public sealed record MeteringSettings
{
    public bool Enabled { get; init; }
    public Guid? MainTableId { get; init; }
    public Guid? StaticPressureTableId { get; init; }
    public Guid? DecimalTableId { get; init; }
    public decimal WaterVolumeM3 { get; init; }
    public decimal? WaterHeightMm { get; init; }
    public decimal? StandardDensity20KgM3 { get; init; }
    public decimal? ManualOperatingDensityKgM3 { get; init; }
    public MeteringDensitySource DensitySource { get; init; }
    public bool ShowInstrumentDensity { get; init; } = true;
    public BaseVolumeMethod BaseVolumeMethod { get; init; }
    public FractionalMillimetreRule FractionalMillimetreRule { get; init; } = FractionalMillimetreRule.Linear;
}
public sealed record MeteringResult(Measurement CurrentVolumeM3, Measurement NetVolumeM3,
    Measurement MassT, decimal? MainVolumeM3, decimal? StaticCorrectionM3, IReadOnlyList<string> Notes)
{ public Measurement WaterVolumeM3 {get;init;}=new(null,ValueQuality.Unconfigured,"尚未计算水体积"); }

public static partial class CapacityRules
{
    public static decimal HeightToMm(decimal value, HeightUnit unit) => value * (unit switch { HeightUnit.Metre => 1000, HeightUnit.Centimetre => 10, _ => 1 });
    public static decimal VolumeToM3(decimal value, VolumeUnit unit) => unit == VolumeUnit.Litre ? value / 1000 : value;
    public static IReadOnlyList<ValidationIssue> Validate(CapacityTable table)
    {
        List<ValidationIssue> issues = [];
        if (table.Id == Guid.Empty || table.Revision < 0) issues.Add(new("table", "罐容表编号或版本无效"));
        if (string.IsNullOrWhiteSpace(table.Number) || table.Number.Length > 80 || string.IsNullOrWhiteSpace(table.Name) || table.Name.Length > 120)
            issues.Add(new("table", "请填写表编号和名称，且不超出长度限制"));
        if (!Enum.IsDefined(table.Kind) || !Enum.IsDefined(table.HeightUnit) || !Enum.IsDefined(table.VolumeUnit)) issues.Add(new("units", "罐容表类型或单位无效"));
        if (table.Notes is null || table.Notes.Length > 2000) issues.Add(new("notes", "备注不能超过2000字符"));
        if (table.ReferenceDensityKgM3 is <= 0 or > 1_000_000) issues.Add(new("referenceDensityKgM3", "参考密度须大于0且不超过1000000kg/m³"));
        if(table.Points is null||table.DecimalRows is null){issues.Add(new("points","罐容表数据集合不能为空"));return issues;}
        if (table.Kind == CapacityTableKind.Decimal && table.DecimalRows.Count > 0) { issues.AddRange(ValidateDecimalRows(table)); return issues; }
        if (table.Points is null || table.Points.Count is < 2 or > 100000) { issues.Add(new("points", "每表应有2～100000组数据")); return issues; }
        decimal? previous = null, previousVolume = null;
        for (var i = 0; i < table.Points.Count; i++)
        {
            var p = table.Points[i]; var height = HeightToMm(p.Height, table.HeightUnit); var volume = VolumeToM3(p.Volume, table.VolumeUnit);
            if (height is < 0 or > 100000 || volume is < 0 or > 1_000_000_000) { issues.Add(new("points/" + i, "高度或容积超出允许范围，请检查单位")); break; }
            if (previous.HasValue && height <= previous) { issues.Add(new("points/" + i, "高度应严格递增且不能重复")); break; }
            if (table.Kind == CapacityTableKind.Main && previousVolume.HasValue && volume < previousVolume) { issues.Add(new("points/" + i, "主罐容表的容量不能随高度下降")); break; }
            previous = height; previousVolume = volume;
        }
        return issues;
    }
    public static decimal Interpolate(CapacityTable table, decimal heightMm)
    {
        var points = table.Points;
        decimal H(int i) => HeightToMm(points[i].Height, table.HeightUnit);
        decimal V(int i) => VolumeToM3(points[i].Volume, table.VolumeUnit);
        if (heightMm < H(0) || heightMm > H(points.Count - 1)) throw new ArgumentOutOfRangeException(nameof(heightMm), "液位超出罐容表范围");
        var lo = 0; var hi = points.Count - 1;
        while (hi - lo > 1) { var mid = (lo + hi) / 2; if (H(mid) <= heightMm) lo = mid; else hi = mid; }
        return heightMm == H(lo) ? V(lo) : V(lo) + (V(hi) - V(lo)) * (heightMm - H(lo)) / (H(hi) - H(lo));
    }
}

public static class MeteringCalculator
{
    private static decimal BaseVolume(MeteringSettings settings, CapacityTable main, CapacityTable? small, decimal heightMm)
        => settings.BaseVolumeMethod == BaseVolumeMethod.MainPlusDecimal
            ? CapacityRules.WithDecimal(main, small, heightMm, settings.FractionalMillimetreRule)
            : CapacityRules.Interpolate(main, heightMm);

    public static Measurement CalculateManualWaterVolume(MeteringSettings settings, CapacityTable? main, CapacityTable? small)
    {
        if (!settings.Enabled) return new(null, ValueQuality.Unconfigured, "体积计算未启用");
        if (settings.WaterHeightMm is not { } height) return new(null, ValueQuality.Unconfigured, "未填写人工水位");
        if (height < 0) return new(null, ValueQuality.Fault, "水位不能小于0");
        if (main is null || !main.Enabled || main.Kind != CapacityTableKind.Main)
            return new(null, ValueQuality.Unconfigured, "未关联已启用主罐容表");
        if (height == 0) return Measurement.Good(0);
        try { return Measurement.Good(BaseVolume(settings, main, small, height)); }
        catch (ArgumentOutOfRangeException) { return new(null, ValueQuality.Fault, "水位超出罐容表范围，不进行外推"); }
        catch (InvalidOperationException e) { return new(null, ValueQuality.Unconfigured, e.Message.Replace("当前液位", "人工水位")); }
    }

    public static MeteringResult Calculate(MeteringSettings settings, Measurement levelMm,
        Measurement densityKgM3, CapacityTable? main, CapacityTable? pressure, CapacityTable? small)
    {
        var manualWater = CalculateManualWaterVolume(settings, main, small);
        MeteringResult Bad(ValueQuality quality, string reason) => new(new(null, quality, reason), new(null, quality, reason), new(null, quality, reason), null, null, [reason])
            { WaterVolumeM3 = manualWater };
        if (!settings.Enabled) return Bad(ValueQuality.Unconfigured, "体积计算未启用");
        if (!levelMm.IsValid) return Bad(levelMm.Quality, "液位无效，不能计算体积");
        if (main is null || !main.Enabled || main.Kind != CapacityTableKind.Main) return Bad(ValueQuality.Unconfigured, "未关联已启用主罐容表");
        if (settings.StaticPressureTableId.HasValue && (pressure is null || !pressure.Enabled || pressure.Kind != CapacityTableKind.StaticPressure)) return Bad(ValueQuality.Unconfigured, "静压力容积修正表未启用或类型不符");
        densityKgM3 = SelectedDensity(settings, densityKgM3);
        if (pressure is not null && (!densityKgM3.IsValid || densityKgM3.Value <= 0)) return Bad(ValueQuality.Missing, "静压力修正缺少有效所选工况密度");
        try
        {
            var baseVolume = BaseVolume(settings, main, small, levelMm.Value!.Value);
            var correction = pressure is null ? 0 : CapacityRules.Interpolate(pressure, levelMm.Value.Value) * densityKgM3.Value!.Value / pressure.ReferenceDensityKgM3;
            var current = baseVolume + correction;
            if (settings.WaterHeightMm > levelMm.Value) return Bad(ValueQuality.Fault, "水位高于当前液位");
            var water = settings.WaterHeightMm is null ? Measurement.Good(settings.WaterVolumeM3) : manualWater;
            if (!water.IsValid) return Bad(water.Quality, water.Reason!);
            var net = current - water.Value!.Value;
            List<string> notes = [];
            notes.Add(settings.WaterHeightMm is null?"沿用旧配置人工水体积；填写水位后改按罐容表自动计算":"水位为人工输入；水体积按基础罐容算法计算，不重复叠加整体静压力修正");
            notes.Add("计算密度来源：" + (settings.DensitySource == MeteringDensitySource.Manual ? "人工工况密度" : "仪表实测密度") + "；20℃标准密度独立保留。");
            if (settings.BaseVolumeMethod == BaseVolumeMethod.Interpolate && (small is not null || settings.DecimalTableId.HasValue)) notes.Add("主表插值模式：小数表未参与，避免重复叠加");
            if (settings.BaseVolumeMethod == BaseVolumeMethod.MainPlusDecimal) notes.Add("主表下节点＋分区厘米/毫米累计增量；不重复进行主表插值");
            if (net < 0) return new(Measurement.Good(current), new(null, ValueQuality.Fault, "水体积超过当前体积"), new(null, ValueQuality.Fault, "净体积无效"), baseVolume, correction, ["水体积超过当前体积"]) { WaterVolumeM3 = manualWater };
            var mass = densityKgM3.IsValid && densityKgM3.Value > 0 ? Measurement.Good(net * densityKgM3.Value!.Value / 1000m) : Measurement.Missing("质量计算缺少有效所选工况密度");
            return new(Measurement.Good(current), Measurement.Good(net), mass, baseVolume, correction, notes) { WaterVolumeM3 = water };
        }
        catch (ArgumentOutOfRangeException) { return Bad(ValueQuality.Fault, "液位超出罐容表范围，不进行外推"); }
        catch (InvalidOperationException e) { return Bad(ValueQuality.Unconfigured, e.Message); }
    }
    public static Measurement SelectedDensity(MeteringSettings settings, Measurement instrument) => settings.DensitySource == MeteringDensitySource.Manual
        ? settings.ManualOperatingDensityKgM3 is > 0 and <= 1000000 ? Measurement.Good(settings.ManualOperatingDensityKgM3.Value) : Measurement.Missing("未填写有效人工工况密度")
        : instrument;
}
