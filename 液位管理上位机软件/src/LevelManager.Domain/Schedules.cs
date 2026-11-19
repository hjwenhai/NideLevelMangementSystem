namespace Naide.LevelManager.Domain;
public enum ScheduledTaskKind { Snapshot, AverageTemperature }
public sealed record SnapshotSchedule
{
    public Guid Id {get;init;}=Guid.NewGuid();
    public ScheduledTaskKind Kind {get;init;}
    public int Points {get;init;}=5;
    public bool AutomaticOperationConfirmed {get;init;}
    public string? AuthorizedById {get;init;}
    public string? AuthorizedByName {get;init;}
    public long AuthorizedSecurityVersion {get;init;}
    public long AuthorizedPermissionRevision {get;init;}
    public string Name {get;init;}="";
    public string Time {get;init;}="23:55";
    public bool Enabled {get;init;}=true;
    public IReadOnlyList<Guid>? TankIds {get;init;}
}
public static class SnapshotSchedulePolicy
{
    public static string? DueDate(SnapshotSchedule schedule,DateTimeOffset now,TimeZoneInfo? zone=null)
    {
        if(!schedule.Enabled||!TimeOnly.TryParseExact(schedule.Time,"HH:mm",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var time))return null;
        var local=TimeZoneInfo.ConvertTime(now,zone??TimeZoneInfo.Local);var elapsed=local.TimeOfDay-time.ToTimeSpan();
        return elapsed>=TimeSpan.Zero&&elapsed<TimeSpan.FromMinutes(1)?local.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture):null;
    }
    public static IEnumerable<ValidationIssue> Validate(IReadOnlyList<SnapshotSchedule>? schedules,IReadOnlyList<TankDefinition> tanks)
    {
        if(schedules is null||schedules.Count>64){yield return new("schedules","定时任务最多64项，列表不能为null");yield break;}
        HashSet<Guid> ids=[];HashSet<string> names=new(StringComparer.OrdinalIgnoreCase);
        foreach(var schedule in schedules)
        {
            if(schedule is null){yield return new("schedules","定时任务不能为null");continue;}
            if(!Enum.IsDefined(schedule.Kind)||schedule.Points is <1 or >16)yield return new("schedules","定时任务类型或测温点数无效");
            if(schedule.Kind==ScheduledTaskKind.AverageTemperature&&(schedule.TankIds is null||schedule.TankIds.Any(id=>!tanks.Any(t=>t.Id==id&&t.TemperatureMode==TemperatureMode.Wireless&&t.Controls.WirelessMeasurementEnabled))))yield return new("schedules","定时测温需明确选择已开启测温的无线浮子储罐");
            if(schedule.Id==Guid.Empty||!ids.Add(schedule.Id)||string.IsNullOrWhiteSpace(schedule.Name)||schedule.Name.Length>80||!names.Add(schedule.Name.Trim())||!TimeOnly.TryParseExact(schedule.Time,"HH:mm",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out _))yield return new("schedules","定时任务编号、名称或每日时间无效");
            if(schedule.TankIds is {} selected&&(selected.Count is <1 or >128||selected.Distinct().Count()!=selected.Count||selected.Any(id=>!tanks.Any(t=>t.Id==id))))yield return new("schedules","定时任务选择了重复或不存在的储罐");
        }
    }
}
