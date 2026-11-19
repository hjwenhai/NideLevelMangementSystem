from pathlib import Path
root=Path(__file__).resolve().parents[1]
def edit(name,old,new):
 p=root/name;s=p.read_text(encoding='utf-8-sig');assert old in s,(name,old[:100]);p.write_text(s.replace(old,new),encoding='utf-8')
edit('src/LevelManager.Application/Contracts.cs','public sealed record AuditActor(string AccountId,string DisplayName,AccountRole Role);','public sealed record AuditActor(string AccountId,string DisplayName,AccountRole Role,IReadOnlyList<AppPermission>? Permissions=null);')
for f in ['src/LevelManager.Application/Contracts.cs','src/LevelManager.Infrastructure/SqliteConfigurationStore.cs']:
 edit(f,'if(actor.Role!=AccountRole.Administrator)throw new UnauthorizedAccessException("当前账号没有配置权限");','actor.Require(AppPermission.ConfigurationEdit);')
edit('src/LevelManager.Infrastructure/SqliteConfigurationStore.cs','CurrentSchema=14','CurrentSchema=15')
edit('src/LevelManager.Infrastructure/SqliteConfigurationStore.cs','  cmd.Parameters.Clear();cmd.CommandText="SELECT Value FROM database_properties', '  if(version<15){cmd.Parameters.Clear();cmd.CommandText="CREATE TABLE role_permissions(Id INTEGER PRIMARY KEY CHECK(Id=1),Revision INTEGER NOT NULL,Payload TEXT NOT NULL);PRAGMA user_version=15";await cmd.ExecuteNonQueryAsync(ct);}\n  cmd.Parameters.Clear();cmd.CommandText="SELECT Value FROM database_properties')
edit('src/LevelManager.Infrastructure/SqliteCapacityTableStore.cs','if (actor.Role != AccountRole.Administrator) throw new UnauthorizedAccessException("无罐容表维护权限");','actor.Require(AppPermission.CapacityManage);')
edit('src/LevelManager.Infrastructure/SqliteAlarmStore.cs','if (actor.Role == AccountRole.Viewer) throw new UnauthorizedAccessException("当前账号没有报警确认权限");','actor.Require(AppPermission.AlarmAcknowledge);')
edit('src/LevelManager.Infrastructure/SqliteControlStore.cs','if(reviewer.Role==AccountRole.Viewer)throw new UnauthorizedAccessException("无人工核查权限");','reviewer.Require(AppPermission.ControlReview);')
edit('src/LevelManager.Infrastructure/SqliteWirelessTaskStore.cs','if(actor.Role==AccountRole.Viewer)throw new UnauthorizedAccessException("无测温核查权限");','actor.Require(AppPermission.ControlReview);')
edit('src/LevelManager.Host/AcquisitionService.cs','if (actor.Role == AccountRole.Viewer) throw new UnauthorizedAccessException("当前账号无采集管理权限");','actor.Require(AppPermission.AcquisitionManage);')
edit('src/LevelManager.Host/ReadOnlyIntegration.cs','if(actor.Role!=AccountRole.Administrator)throw new UnauthorizedAccessException("仅管理员可设置对外只读接口");','actor.Require(AppPermission.IntegrationManage);')
edit('src/LevelManager.Host/DataJobService.cs','if(actor.Role!=AccountRole.Administrator)throw new UnauthorizedAccessException("仅管理员可创建业务备份");','actor.Require(AppPermission.BackupManage);')
edit('src/LevelManager.Host/ControlService.cs','if(actor.Role==AccountRole.Viewer)throw new UnauthorizedAccessException("当前账号没有仪表操作权限");','actor.Require(command==InstrumentCommand.Stop?AppPermission.InstrumentStop:AppPermission.InstrumentControl);')
edit('src/LevelManager.Host/ControlService.cs','if(actor.Role==AccountRole.Viewer)throw new UnauthorizedAccessException("无复位权限");','actor.Require(AppPermission.ControlReview);')
edit('src/LevelManager.Host/WirelessTaskService.cs','if(actor.Role==AccountRole.Viewer)throw new UnauthorizedAccessException("无平均测温操作权限");','actor.Require(AppPermission.AverageTemperatureStart);')
edit('src/LevelManager.Host/WirelessTaskService.cs','if(actor.Role==AccountRole.Viewer)throw new UnauthorizedAccessException("无停止测温权限");','actor.Require(AppPermission.InstrumentStop);')
edit('src/LevelManager.Host/Program.cs','builder.Services.AddSingleton<IUserStore>(sp=>sp.GetRequiredService<SqliteConfigurationStore>());','builder.Services.AddSingleton<IUserStore>(sp=>sp.GetRequiredService<SqliteConfigurationStore>());\nbuilder.Services.AddSingleton<IRolePermissionStore>(sp=>sp.GetRequiredService<SqliteConfigurationStore>());')
edit('src/LevelManager.Host/Program.cs','await ctx.HttpContext.SignOutAsync();}};','await ctx.HttpContext.SignOutAsync();}\n else if(ctx.Principal!.Identity is ClaimsIdentity identity){foreach(var claim in identity.FindAll(PermissionEndpoints.ClaimType).ToArray())identity.RemoveClaim(claim);var actor=await ctx.HttpContext.RequestServices.GetRequiredService<IRolePermissionStore>().ResolveAsync(user,ctx.HttpContext.RequestAborted);identity.AddClaims(actor.Permissions!.Select(p=>new Claim(PermissionEndpoints.ClaimType,p.ToString())));}};')
edit('src/LevelManager.Host/Program.cs','o.AddPolicy("operator",p=>p.RequireRole(AccountRole.Administrator.ToString(),AccountRole.Operator.ToString()));','o.AddPolicy("operator",p=>p.RequireRole(AccountRole.Administrator.ToString(),AccountRole.Operator.ToString()));foreach(var permission in Enum.GetValues<AppPermission>()){var key=permission.ToString();o.AddPolicy(key,p=>p.RequireAuthenticatedUser().RequireClaim(PermissionEndpoints.ClaimType,key));}')
edit('src/LevelManager.Host/Program.cs','role=ctx.User.FindFirstValue(ClaimTypes.Role)}:null','role=ctx.User.FindFirstValue(ClaimTypes.Role),permissions=ctx.User.FindAll(PermissionEndpoints.ClaimType).Select(c=>c.Value).ToArray()}:null')
edit('src/LevelManager.Host/Program.cs','app.MapUserEndpoints();','app.MapUserEndpoints();app.MapPermissionEndpoints();')
old='new AuditActor(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!,ctx.User.FindFirstValue(ClaimTypes.Name)!,Enum.Parse<AccountRole>(ctx.User.FindFirstValue(ClaimTypes.Role)!))'
edit('src/LevelManager.Host/Program.cs',old,'ctx.CurrentActor()')
edit('src/LevelManager.Host/Program.cs','static AuditActor Actor(HttpContext ctx)=>new(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!,ctx.User.FindFirstValue(ClaimTypes.Name)!,Enum.Parse<AccountRole>(ctx.User.FindFirstValue(ClaimTypes.Role)!));','static AuditActor Actor(HttpContext ctx)=>ctx.CurrentActor();')
# Replace endpoint authorization in-place, including multiline endpoint bodies.
p=root/'src/LevelManager.Host/Program.cs';s=p.read_text(encoding='utf-8');import re
route_permissions={
 '/api/integration/access':'IntegrationManage','/api/config':'ConfigurationEdit','/api/events':'HistoryRead',
 '/api/capacity-tables':'CapacityManage','/api/capacity-tables/{id:guid}/associations':'CapacityManage',
 '/api/capacity-tables/import-preview':'CapacityManage','/api/capacity-tables/{id:guid}/export':'DataExport',
 '/api/tanks/{id:guid}/display':'ConfigurationEdit','/api/storage/retention-change':'ConfigurationEdit',
 '/api/storage/backup':'BackupManage','/api/history/export':'DataExport', '/api/history':'HistoryRead',
 '/api/snapshots':'SnapshotCreate','/api/trends/history':'HistoryRead', '/api/alarms/acknowledge':'AlarmAcknowledge',
 '/api/tanks/{id:guid}/wireless-tasks':'AverageTemperatureStart','/api/wireless-tasks/{id:guid}/cancel':'InstrumentStop',
 '/api/wireless-tasks/{id:guid}/review':'ControlReview','/api/controls/{id:guid}/reset':'ControlReview',
 '/api/controls/{id:guid}/review':'ControlReview','/api/acquisition/start':'AcquisitionManage','/api/acquisition/stop':'AcquisitionManage'}
pattern=r'(app\.Map(Get|Post|Put)\("([^"]+)"[\s\S]*?)(\.RequireAuthorization\([^;]*?\);)'
def change(m):
 body,method,route,auth=m.groups();perm=route_permissions.get(route)
 if route=='/api/config' and method=='Get':return m.group(0)
 if route=='/api/capacity-tables' and method=='Get':return m.group(0)
 if route=='/api/snapshots' and method=='Get':perm='HistoryRead'
 if perm:return body+'.RequireAuthorization("'+perm+'");'
 if route=='/api/tanks/{id:guid}/controls':return body+'.RequireAuthorization();' # discriminated command; service enforces stop vs ordinary.
 return m.group(0)
s=re.sub(pattern,change,s);p.write_text(s,encoding='utf-8')
# Services executing queued instrument actions must load CURRENT matrix, not stale cookie roles.
for f,needle in [('src/LevelManager.Host/ControlService.cs','ProductAuthorization authorization,IUserStore users,'),('src/LevelManager.Host/WirelessDevice.cs','IUserStore users,'),('src/LevelManager.Host/SnapshotScheduler.cs','IUserStore users)')]:
 edit(f,needle,needle.replace('IUserStore users','IUserStore users,IRolePermissionStore permissions'))
edit('src/LevelManager.Host/ControlService.cs','user is null||!user.Enabled||user.Role==AccountRole.Viewer','user is null||!user.Enabled||!(await permissions.ResolveAsync(user,token)).Allows(AppPermission.InstrumentControl)')
edit('src/LevelManager.Host/WirelessDevice.cs','user is null||!user.Enabled||user.Role==AccountRole.Viewer','user is null||!user.Enabled||!(await permissions.ResolveAsync(user,token)).Allows(AppPermission.AverageTemperatureStart)')
edit('src/LevelManager.Host/SnapshotScheduler.cs','user is null||!user.Enabled||user.Role==AccountRole.Viewer','user is null||!user.Enabled||!(await permissions.ResolveAsync(user,stoppingToken)).Allows(AppPermission.AverageTemperatureStart)')
edit('src/LevelManager.Host/SnapshotScheduler.cs','new(user.Id,user.DisplayName,user.Role),stoppingToken','await permissions.ResolveAsync(user,stoppingToken),stoppingToken')
edit('src/LevelManager.Host/ConfigurationCoordinator.cs','if(schedule.Enabled&&changed){if(!schedule.AutomaticOperationConfirmed)','if(schedule.Enabled&&changed){actor.Require(AppPermission.AverageTemperatureStart);if(!schedule.AutomaticOperationConfirmed)')
print('r10 permission foundation applied')
