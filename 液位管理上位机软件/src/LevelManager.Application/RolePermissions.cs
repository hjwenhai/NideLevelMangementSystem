namespace Naide.LevelManager.Application;

public enum AppPermission
{
    ConfigurationEdit, AcquisitionManage, AlarmAcknowledge, InstrumentControl, InstrumentStop,
    ControlReview, AverageTemperatureStart, HistoryRead, DataExport, SnapshotCreate,
    CapacityManage, BackupManage, IntegrationManage, AccountsManage, PermissionsManage,
    LicenseManage, RecoveryManage, RemoteLevelAdjust
}
public sealed record PermissionDefinition(AppPermission Id,string Name,string Group,bool AdministratorOnly=false);
public sealed record RoleGrant(AccountRole Role,IReadOnlyList<AppPermission> Permissions);
public sealed record RolePermissionMatrix(long Revision,IReadOnlyList<RoleGrant> Roles);
public interface IRolePermissionStore
{
    Task<RolePermissionMatrix> GetPermissionsAsync(CancellationToken ct=default);
    Task<RolePermissionMatrix> SavePermissionsAsync(RolePermissionMatrix matrix,AuditActor actor,CancellationToken ct=default);
}
public static class PermissionPolicy
{
    public static readonly IReadOnlyList<PermissionDefinition> Definitions = [
        new(AppPermission.ConfigurationEdit,"储罐、通讯与基础配置","配置管理"),
        new(AppPermission.CapacityManage,"罐容表维护","配置管理"),
        new(AppPermission.AcquisitionManage,"启动、停止采集","运行操作"),
        new(AppPermission.AlarmAcknowledge,"确认报警","运行操作"),
        new(AppPermission.InstrumentControl,"执行本罐允许的仪表操作","运行操作"),
        new(AppPermission.RemoteLevelAdjust,"远程液位修改（校准）","运行操作"),
        new(AppPermission.InstrumentStop,"停止仪表、取消测温","运行操作"),
        new(AppPermission.ControlReview,"操作结果核查、网关结果复位","运行操作"),
        new(AppPermission.AverageTemperatureStart,"发起平均温度测量","运行操作"),
        new(AppPermission.SnapshotCreate,"创建数据快照","数据与接口"),
        new(AppPermission.HistoryRead,"查询历史、事件及数据任务","数据与接口"),
        new(AppPermission.DataExport,"导出与下载数据","数据与接口"),
        new(AppPermission.BackupManage,"创建、下载业务备份","数据与接口"),
        new(AppPermission.IntegrationManage,"配置对外只读接口","数据与接口"),
        new(AppPermission.AccountsManage,"账号管理","管理员保留",true),
        new(AppPermission.PermissionsManage,"岗位权限管理","管理员保留",true),
        new(AppPermission.LicenseManage,"导入供方授权","管理员保留",true),
        new(AppPermission.RecoveryManage,"业务恢复现场核查","管理员保留",true)
    ];
    public static IReadOnlyList<AppPermission> Defaults(AccountRole role)=>Definitions.Where(d=>role==AccountRole.Administrator||
        role==AccountRole.Operator&&!d.AdministratorOnly&&d.Id is not (AppPermission.ConfigurationEdit or AppPermission.CapacityManage or AppPermission.BackupManage or AppPermission.IntegrationManage or AppPermission.RemoteLevelAdjust)||
        role==AccountRole.Viewer&&d.Id is AppPermission.HistoryRead or AppPermission.DataExport).Select(d=>d.Id).ToArray();
    public static RolePermissionMatrix DefaultMatrix()=>new(0,Enum.GetValues<AccountRole>().Select(r=>new RoleGrant(r,Defaults(r))).ToArray());
    public static bool Allows(this AuditActor actor,AppPermission permission)=>
        Definitions.First(d=>d.Id==permission).AdministratorOnly?actor.Role==AccountRole.Administrator:
        (actor.Permissions??Defaults(actor.Role)).Contains(permission);
    public static void Require(this AuditActor actor,AppPermission permission)
    {if(!actor.Allows(permission))throw new UnauthorizedAccessException("当前岗位没有“"+Definitions.First(d=>d.Id==permission).Name+"”权限");}
    public static void Validate(RolePermissionMatrix matrix)
    {
        if(matrix.Revision<0||matrix.Roles is null||matrix.Roles.Count!=3||matrix.Roles.Any(g=>g is null||!Enum.IsDefined(g.Role)||g.Permissions is null||g.Permissions.Count>Definitions.Count||g.Permissions.Distinct().Count()!=g.Permissions.Count||g.Permissions.Any(p=>!Enum.IsDefined(p)))||matrix.Roles.Select(g=>g.Role).Distinct().Count()!=3)
            throw new ArgumentException("岗位权限矩阵格式无效，应包含三个岗位及不重复的有效权限");
        foreach(var grant in matrix.Roles)
        {
            if(Definitions.Where(d=>d.AdministratorOnly).Any(d=>grant.Permissions.Contains(d.Id)!=(grant.Role==AccountRole.Administrator)))throw new ArgumentException("账号、岗位、供方授权及恢复核查权限仅由管理员保留");
            if((grant.Permissions.Contains(AppPermission.InstrumentControl)||grant.Permissions.Contains(AppPermission.RemoteLevelAdjust))&&!grant.Permissions.Contains(AppPermission.InstrumentStop)||grant.Permissions.Contains(AppPermission.AverageTemperatureStart)&&!grant.Permissions.Contains(AppPermission.InstrumentStop))throw new ArgumentException("允许仪表操作或平均测温时，必须同时保留停止权限");
        }
    }
    public static async Task<AuditActor> ResolveAsync(this IRolePermissionStore store,UserAccount user,CancellationToken ct=default)
    {var matrix=await store.GetPermissionsAsync(ct);return new(user.Id,user.DisplayName,user.Role,matrix.Roles.Single(r=>r.Role==user.Role).Permissions);}
    public static void RequireScheduledAuthority(UserAccount? user,long currentRevision,long? expectedRevision,long? expectedSecurityVersion)
    {
        if(expectedRevision is null&&expectedSecurityVersion is null)return;
        if(expectedRevision is null||expectedSecurityVersion is null||user is null||!user.Enabled||user.SecurityVersion!=expectedSecurityVersion||currentRevision!=expectedRevision)
            throw new UnauthorizedAccessException("原定时测温授权账号或岗位权限已改变，请重新编辑确认定时任务");
    }
}
