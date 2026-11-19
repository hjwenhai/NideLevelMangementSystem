namespace Naide.LevelManager.Domain;

public enum PressureUnit { KPa, MPa, Bar }
public enum DensityUnit { KilogramPerCubicMetre, GramPerCubicCentimetre }
public enum MassUnit { Tonne, Kilogram }
public enum GroupKind { Zone, TankGroup, Medium, Batch }
public sealed record PressureCalibration
{
    public bool Enabled { get; init; }
    public decimal At4Ma { get; init; }
    public decimal At20Ma { get; init; } = 100;
    public PressureUnit Unit { get; init; }
}
public sealed record DisplaySettings
{
    public HeightUnit HeightUnit { get; init; }
    public VolumeUnit VolumeUnit { get; init; }
    public DensityUnit DensityUnit { get; init; }
    public MassUnit MassUnit { get; init; }
    public int RefreshIntervalSeconds { get; init; } = 1;
    public int VolumeDecimals { get; init; } = 3;
    public int MassDecimals { get; init; } = 3;
}
public sealed record TankGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public GroupKind Kind { get; init; }
    public Guid? ParentId { get; init; }
    public IReadOnlyList<Guid> TankIds { get; init; } = [];
}
public sealed record MediumDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string Color { get; init; } = "#659da0";
    public decimal? ReferenceTemperatureC { get; init; }
    public decimal? StandardDensityKgM3 { get; init; }
}
public static class PressureCalculator
{
    public static decimal UnitToKPa(decimal value, PressureUnit unit) => value * (unit switch { PressureUnit.MPa => 1000, PressureUnit.Bar => 100, _ => 1 });
    public static Measurement Calculate(PressureCalibration settings, Measurement currentMa)
    {
        if (!settings.Enabled) return new(null, ValueQuality.Unconfigured, "未配置压力变送器");
        if (!currentMa.IsValid) return new(null, currentMa.Quality, "压力输入电流无效");
        if (currentMa.Value <= 3.2m) return new(null, ValueQuality.Fault, "压力输入电流≤3.2mA，按断线处理");
        var ma = Math.Clamp(currentMa.Value!.Value, 4m, 20m);
        var engineering = settings.At4Ma + (ma - 4) / 16 * (settings.At20Ma - settings.At4Ma);
        return new(UnitToKPa(engineering, settings.Unit),ValueQuality.Good,currentMa.Value<4?"输入低于4mA，按工程下限显示":currentMa.Value>20?"输入高于20mA，按工程上限显示":null);
    }
}
public static class PresentationRules
{
    public static IReadOnlyList<ValidationIssue> Validate(SystemConfiguration config)
    {
        List<ValidationIssue> issues = [];
        issues.AddRange(SnapshotSchedulePolicy.Validate(config.Schedules,config.Tanks));
        var display = config.Display;
        if (display is null || display.RefreshIntervalSeconds is <1 or >30 || !Enum.IsDefined(display.HeightUnit) || !Enum.IsDefined(display.VolumeUnit) || !Enum.IsDefined(display.DensityUnit) || !Enum.IsDefined(display.MassUnit) || display.VolumeDecimals is < 0 or > 6 || display.MassDecimals is < 0 or > 6)
            issues.Add(new("display", "显示单位或小数位设置无效"));
        if (config.Groups is null || config.Media is null) { issues.Add(new("groups", "分组和介质列表不能为null")); return issues; }
        if (config.Groups.Count > 512 || config.Media.Count > 512) issues.Add(new("groups", "分组或介质数量超过512"));
        var ids = config.Groups.Where(g => g is not null).Select(g => g.Id).ToHashSet();
        var tankIds = config.Tanks.Select(t => t.Id).ToHashSet();
        var siblingNames = new HashSet<(Guid?, string)>();
        foreach (var group in config.Groups)
        {
            if (group is null) { issues.Add(new("groups", "分组不能为null")); continue; }
            if (group.Id == Guid.Empty || config.Groups.Count(g => g?.Id == group.Id) != 1 || string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 100 || !Enum.IsDefined(group.Kind) || !siblingNames.Add((group.ParentId, group.Name.Trim().ToUpperInvariant())))
                issues.Add(new("groups", "分组编号、名称或类型无效，同级名称不能重复"));
            if (group.ParentId.HasValue && (!ids.Contains(group.ParentId.Value) || group.ParentId == group.Id)) issues.Add(new("groups", "上级分组不存在或指向自己"));
            var visited = new HashSet<Guid> { group.Id }; var parent = group.ParentId;
            while (parent.HasValue)
            {
                if (!visited.Add(parent.Value)) { issues.Add(new("groups", "分组上下级关系存在循环")); break; }
                parent = config.Groups.FirstOrDefault(g => g?.Id == parent.Value)?.ParentId;
            }
            if (group.TankIds is null || group.TankIds.Count != group.TankIds.Distinct().Count() || group.TankIds.Any(t => !tankIds.Contains(t))) issues.Add(new("groups", "分组内有重复或不存在的储罐"));
        }
        HashSet<Guid> mediumIds = []; HashSet<string> codes = new(StringComparer.OrdinalIgnoreCase), names = new(StringComparer.OrdinalIgnoreCase);
        foreach (var medium in config.Media)
        {
            if (medium is null) { issues.Add(new("media", "介质不能为null")); continue; }
            if (medium.Id == Guid.Empty || !mediumIds.Add(medium.Id) || string.IsNullOrWhiteSpace(medium.Code) || medium.Code.Length > 80 || !codes.Add(medium.Code.Trim()) || string.IsNullOrWhiteSpace(medium.Name) || medium.Name.Length > 100 || !names.Add(medium.Name.Trim()) || medium.Category is null || medium.Category.Length > 100)
                issues.Add(new("media", "介质编号、名称或类别无效，编号和名称不能重复"));
            if (medium.Color is null || !System.Text.RegularExpressions.Regex.IsMatch(medium.Color, "^#[0-9a-fA-F]{6}$") || medium.ReferenceTemperatureC is < -273.15m or > 1000 || medium.StandardDensityKgM3 is <= 0 or > 1000000)
                issues.Add(new("media", "介质颜色、参考温度或标准密度无效"));
        }
        foreach (var tank in config.Tanks)
        {
            if(tank.Controls is null||tank.Controls.AllowedCommands is null||tank.Controls.AllowedCommands.Count>10||tank.Controls.AllowedCommands.Distinct().Count()!=tank.Controls.AllowedCommands.Count||tank.Controls.AllowedCommands.Any(c=>!Enum.IsDefined(c))||tank.Controls.ConfirmationTimeoutSeconds is <3 or >300||tank.Controls.MovementTimeoutSeconds is <30 or >3600)issues.Add(new("tanks/"+tank.Id,"开放操作或控制超时设置无效"));
            else if(config.Channels.FirstOrDefault(c=>c.Id==tank.ChannelId)?.Protocol==AcquisitionProtocol.IfxL25&&tank.Controls.AllowedCommands.Any(c=>(int)c>7))issues.Add(new("tanks/"+tank.Id,"IFX未提供所选扩展操作（含远程液位修改）"));
            if(tank.Controls?.WirelessMeasurementEnabled==true&&tank.TemperatureMode!=TemperatureMode.Wireless)issues.Add(new("tanks/"+tank.Id,"仅无线浮子罐可启用任务平均测温"));
            if(tank.Trends is null||tank.Trends.Quantities is null||tank.Trends.Colors is null||tank.Trends.Quantities.Count is <1 or >5||tank.Trends.Quantities.Distinct().Count()!=tank.Trends.Quantities.Count||tank.Trends.Quantities.Any(q=>!Enum.IsDefined(q))||tank.Trends.Colors.Count!=tank.Trends.Quantities.Count||tank.Trends.Colors.Any(c=>c is null||!System.Text.RegularExpressions.Regex.IsMatch(c,"^#[0-9a-fA-F]{6}$")))issues.Add(new("tanks/"+tank.Id,"共用曲线设置或颜色无效"));
            if(tank.Trends?.Axes is null||tank.Trends.Axes.Count>9||tank.Trends.Axes.Any(a=>!Enum.IsDefined(a.Key)||a.Value is null||!Enum.IsDefined(a.Value.Mode)||a.Value.MinimumSpan<0||a.Value.MinimumSpan>1000000000||a.Value.Mode==TrendAxisMode.Fixed&&(!a.Value.Minimum.HasValue||!a.Value.Maximum.HasValue||a.Value.Maximum<=a.Value.Minimum)||a.Value.Minimum is < -1000000000 or > 1000000000||a.Value.Maximum is < -1000000000 or > 1000000000))issues.Add(new("tanks/"+tank.Id,"曲线纵轴设置无效；固定上限须大于下限"));
            issues.AddRange(AlarmEngine.Validate(tank.Alarms,"tanks/"+tank.Id+"/alarms"));
            if (tank.MediumId.HasValue && !mediumIds.Contains(tank.MediumId.Value)) issues.Add(new("tanks/" + tank.Id, "关联介质不存在"));
            if (tank.AtsHeightsMm is null || tank.AtsHeightsMm.Count > 16 || tank.AtsHeightsMm.Any(h => h is < 0 or > 100000)) issues.Add(new("tanks/" + tank.Id, "ATS高度配置无效"));
            foreach (var pressure in new[] { tank.Pressure1, tank.Pressure2 })
            {
                if (pressure is null || !Enum.IsDefined(pressure.Unit) || pressure.Enabled && (pressure.At4Ma >= pressure.At20Ma || Math.Abs(pressure.At4Ma) > 1000000000m / PressureCalculator.UnitToKPa(1, pressure.Unit) || Math.Abs(pressure.At20Ma) > 1000000000m / PressureCalculator.UnitToKPa(1, pressure.Unit)))
                    issues.Add(new("tanks/" + tank.Id, "压力单位或4mA/20mA对应工程量无效"));
                if (pressure?.Enabled == true && config.Channels.FirstOrDefault(c => c.Id == tank.ChannelId)?.Protocol == AcquisitionProtocol.IfxL25)
                    issues.Add(new("tanks/" + tank.Id, "IFX30000协议不提供压力数据，不能启用压力换算"));
            }
        }
        return issues;
    }
}
