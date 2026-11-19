using System.Globalization;

namespace Naide.LevelManager.Domain;

public enum AlarmQuantity { Level, Temperature, Density, Pressure1, Pressure2, Communication, Instrument }
public enum AlarmDirection { Low, High }
public enum AlarmSeverity { General, Important }
public sealed record AlarmLimits
{
    public decimal? LowLow { get; init; }
    public decimal? Low { get; init; }
    public decimal? High { get; init; }
    public decimal? HighHigh { get; init; }
    public decimal Hysteresis { get; init; }
    public int DelaySeconds { get; init; }
}
public sealed record AlarmSettings
{
    public AlarmLimits Level { get; init; } = new();
    public AlarmLimits Temperature { get; init; } = new();
    public AlarmLimits Density { get; init; } = new();
    public AlarmLimits Pressure1 { get; init; } = new();
    public AlarmLimits Pressure2 { get; init; } = new();
    public bool CommunicationEnabled { get; init; } = true;
    public bool InstrumentEnabled { get; init; } = true;
}
public sealed record AlarmRule(Guid TankId, string TankCode, string Key, string Name, AlarmQuantity Quantity,
    AlarmDirection Direction, decimal Threshold, decimal Hysteresis, int DelaySeconds, AlarmSeverity Severity)
{
    // Tank display names do not restart a pending timer or end an existing alarm.
    public string Fingerprint => string.Join('|', (int)Quantity, (int)Direction,
        Threshold.ToString(CultureInfo.InvariantCulture), Hysteresis.ToString(CultureInfo.InvariantCulture), DelaySeconds, (int)Severity);
}
public sealed record AlarmOccurrence
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required AlarmRule Rule { get; init; }
    public required DateTimeOffset DetectedAtUtc { get; init; }
    public required DateTimeOffset ActivatedAtUtc { get; init; }
    public decimal? TriggerValue { get; init; }
    public DateTimeOffset? AcknowledgedAtUtc { get; init; }
    public string? AcknowledgedById { get; init; }
    public string? AcknowledgedByName { get; init; }
    public DateTimeOffset? RestoredAtUtc { get; init; }
    public decimal? RestoreValue { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public string? EndReason { get; init; }
    public ValueQuality LastQuality { get; init; } = ValueQuality.Good;
    public bool Active => !RestoredAtUtc.HasValue && !EndedAtUtc.HasValue;
    public string State => EndedAtUtc.HasValue ? "Ended" : Active
        ? AcknowledgedAtUtc.HasValue ? "ActiveAcknowledged" : "ActiveUnacknowledged"
        : AcknowledgedAtUtc.HasValue ? "Closed" : "RecoveredUnacknowledged";
}
public sealed record AlarmRuntime
{
    public required AlarmRule Rule { get; init; }
    public DateTimeOffset? CandidateSinceUtc { get; init; }
    public AlarmOccurrence? Current { get; init; }
}
public sealed record AlarmEvaluation(AlarmRuntime Runtime, IReadOnlyList<AlarmOccurrence> Changed, IReadOnlyList<string> Events);
public sealed record AlarmObservation(AlarmRule Rule, Measurement Value);
public static class AlarmEngine
{
    public static AlarmEvaluation Advance(AlarmRuntime? previous, AlarmRule rule, Measurement value, DateTimeOffset now)
    {
        List<AlarmOccurrence> changed = []; List<string> events = [];
        if (previous is not null && previous.Rule.Fingerprint != rule.Fingerprint)
        {
            if (previous.Current is { } old) { changed.Add(old with { EndedAtUtc = now, EndReason = "报警规则已变更" }); events.Add("alarm.ruleChanged"); }
            previous = null;
        }
        var runtime = previous ?? new AlarmRuntime { Rule = rule };
        if (runtime.Current is { } current)
        {
            // A missing/expired/fault value cannot prove recovery. It also breaks a pending delay.
            var restore = value.IsValid && (rule.Direction == AlarmDirection.High
                ? (rule.Hysteresis == 0 ? value.Value < rule.Threshold : value.Value <= rule.Threshold - rule.Hysteresis) : (rule.Hysteresis == 0 ? value.Value > rule.Threshold : value.Value >= rule.Threshold + rule.Hysteresis));
            if (restore)
            {
                changed.Add(current with { RestoredAtUtc = now, RestoreValue = value.Value, LastQuality = value.Quality }); events.Add("alarm.restored");
                return new(runtime with { CandidateSinceUtc = null, Current = null }, changed, events);
            }
            if (current.LastQuality != value.Quality)
            { current = current with { LastQuality = value.Quality }; changed.Add(current); events.Add("alarm.dataQualityChanged"); }
            return new(runtime with { Current = current, CandidateSinceUtc = null }, changed, events);
        }
        var breached = value.IsValid && (rule.Direction == AlarmDirection.High ? value.Value >= rule.Threshold : value.Value <= rule.Threshold);
        if (!breached) return new(runtime with { CandidateSinceUtc = null }, changed, events);
        var since = runtime.CandidateSinceUtc is { } candidate && candidate <= now ? candidate : now;
        if (now - since < TimeSpan.FromSeconds(rule.DelaySeconds)) return new(runtime with { CandidateSinceUtc = since }, changed, events);
        var occurrence = new AlarmOccurrence { Rule = rule, DetectedAtUtc = since, ActivatedAtUtc = now, TriggerValue = value.Value };
        changed.Add(occurrence); events.Add("alarm.activated");
        return new(runtime with { CandidateSinceUtc = null, Current = occurrence }, changed, events);
    }
    public static IReadOnlyList<AlarmRule> Rules(TankDefinition tank)
    {
        List<AlarmRule> rules = [];
        void Add(AlarmQuantity quantity, string prefix, string name, AlarmLimits limits)
        {
            if (limits.LowLow.HasValue) rules.Add(new(tank.Id, tank.Code, prefix + ".lowLow", name + "低低", quantity, AlarmDirection.Low, limits.LowLow.Value, limits.Hysteresis, limits.DelaySeconds, AlarmSeverity.Important));
            if (limits.Low.HasValue) rules.Add(new(tank.Id, tank.Code, prefix + ".low", name + "低", quantity, AlarmDirection.Low, limits.Low.Value, limits.Hysteresis, limits.DelaySeconds, AlarmSeverity.General));
            if (limits.High.HasValue) rules.Add(new(tank.Id, tank.Code, prefix + ".high", name + "高", quantity, AlarmDirection.High, limits.High.Value, limits.Hysteresis, limits.DelaySeconds, AlarmSeverity.General));
            if (limits.HighHigh.HasValue) rules.Add(new(tank.Id, tank.Code, prefix + ".highHigh", name + "高高", quantity, AlarmDirection.High, limits.HighHigh.Value, limits.Hysteresis, limits.DelaySeconds, AlarmSeverity.Important));
        }
        Add(AlarmQuantity.Level, "level", "液位", tank.Alarms.Level);
        if (tank.TemperatureMode != TemperatureMode.None) Add(AlarmQuantity.Temperature, "temperature", "瞬时温度", tank.Alarms.Temperature);
        Add(AlarmQuantity.Density, "density", "仪表密度", tank.Alarms.Density);
        if (tank.Pressure1.Enabled) Add(AlarmQuantity.Pressure1, "pressure1", "压力输入1", tank.Alarms.Pressure1);
        if (tank.Pressure2.Enabled) Add(AlarmQuantity.Pressure2, "pressure2", "压力输入2", tank.Alarms.Pressure2);
        if (tank.Alarms.CommunicationEnabled) rules.Add(new(tank.Id, tank.Code, "communication", "通讯异常", AlarmQuantity.Communication, AlarmDirection.High, 1, 0, 0, AlarmSeverity.Important));
        if (tank.Alarms.InstrumentEnabled) rules.Add(new(tank.Id, tank.Code, "instrument", "仪表故障", AlarmQuantity.Instrument, AlarmDirection.High, 1, 0, 0, AlarmSeverity.Important));
        return rules;
    }
    public static Measurement Observe(AlarmRule rule, ReadingSnapshot sample, bool acquisitionRunning) => rule.Quantity switch
    {
        AlarmQuantity.Level => sample.LevelMm, AlarmQuantity.Temperature => sample.InstantTemperatureC,
        AlarmQuantity.Density => sample.InstrumentDensityKgM3, AlarmQuantity.Pressure1 => sample.Pressure1KPa, AlarmQuantity.Pressure2 => sample.Pressure2KPa,
        AlarmQuantity.Communication => !acquisitionRunning || sample.ReceivedAtUtc == default ? Measurement.Missing("未进行通讯监测")
            : sample.LevelMm.Quality is ValueQuality.Offline or ValueQuality.Expired ? Measurement.Good(1)
            : sample.LevelMm.Quality == ValueQuality.Missing ? Measurement.Missing("通讯结果未确认") : Measurement.Good(0),
        AlarmQuantity.Instrument => sample.InstrumentFault.HasValue ? Measurement.Good(sample.InstrumentFault.Value == 0 ? 0 : 1) : Measurement.Missing("未读取故障码"),
        _ => Measurement.Missing("无报警输入")
    };
    public static IEnumerable<ValidationIssue> Validate(AlarmSettings? settings, string path)
    {
        if (settings is null) { yield return new(path, "报警配置不能为null"); yield break; }
        foreach (var limits in new[] { settings.Level, settings.Temperature, settings.Density, settings.Pressure1, settings.Pressure2 })
        {
            if (limits is null) { yield return new(path, "报警限值不能为null"); continue; }
            var values = new[] { limits.LowLow, limits.Low, limits.High, limits.HighHigh }.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            if (limits.DelaySeconds is < 0 or > 3600 || limits.Hysteresis is < 0 or > 1000000000 || values.Any(v => Math.Abs(v) > 1000000000) || values.Zip(values.Skip(1)).Any(pair => pair.First >= pair.Second))
                yield return new(path, "报警限值须从低低到高高递增；延时0～3600秒，回差为非负数");
        }
    }
}
