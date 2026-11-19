from pathlib import Path
root=Path(__file__).resolve().parents[1]
def edit(name,old,new):
 p=root/name;s=p.read_text(encoding='utf-8-sig');assert old in s,(name,old[:100]);p.write_text(s.replace(old,new),encoding='utf-8')
edit('tests/LevelManager.Tests/WirelessStoreTests.cs','DROP TABLE instrument_fault_state;ALTER TABLE users','DROP TABLE instrument_fault_state;DROP TABLE role_permissions;ALTER TABLE users')
edit('src/LevelManager.Host/ControlService.cs','if(current.FirstDispatchAtUtc.HasValue)return;\n                if(current.Command','if(current.FirstDispatchAtUtc.HasValue)return;\n                var dispatchUser=await users.FindByIdAsync(current.ActorId,token);if(dispatchUser is null||!dispatchUser.Enabled)throw new UnauthorizedAccessException("原操作账号已失效，未发送新的写入");\n                var dispatchActor=await permissions.ResolveAsync(dispatchUser,token);dispatchActor.Require(current.IsReset?(current.RelatedControlId.HasValue?AppPermission.ControlReview:AppPermission.RecoveryManage):current.Command==InstrumentCommand.Stop?AppPermission.InstrumentStop:AppPermission.InstrumentControl);\n                if(current.Command')
# Every role-policy edit invalidates prior unattended motion authorizations until an explicit re-confirmation.
p=next(p for p in (root/'src/LevelManager.Domain').glob('*.cs') if 'AuthorizedSecurityVersion {get;init;}' in p.read_text(encoding='utf-8-sig'))
s=p.read_text(encoding='utf-8-sig');s=s.replace('public long AuthorizedSecurityVersion {get;init;}','public long AuthorizedSecurityVersion {get;init;}\n    public long AuthorizedPermissionRevision {get;init;}');p.write_text(s,encoding='utf-8')
edit('src/LevelManager.Host/ConfigurationCoordinator.cs','AuthorizedSecurityVersion=0});continue;','AuthorizedSecurityVersion=0,AuthorizedPermissionRevision=0});continue;')
edit('src/LevelManager.Host/ConfigurationCoordinator.cs','AuthorizedSecurityVersion=user.SecurityVersion,AutomaticOperationConfirmed','AuthorizedSecurityVersion=user.SecurityVersion,AuthorizedPermissionRevision=(await permissions.GetPermissionsAsync(ct)).Revision,AutomaticOperationConfirmed')
edit('src/LevelManager.Host/ConfigurationCoordinator.cs','AuthorizedSecurityVersion=old?.AuthorizedSecurityVersion??0,AutomaticOperationConfirmed','AuthorizedSecurityVersion=old?.AuthorizedSecurityVersion??0,AuthorizedPermissionRevision=old?.AuthorizedPermissionRevision??0,AutomaticOperationConfirmed')
edit('src/LevelManager.Host/SnapshotScheduler.cs','user.SecurityVersion!=schedule.AuthorizedSecurityVersion','user.SecurityVersion!=schedule.AuthorizedSecurityVersion||(await permissions.GetPermissionsAsync(stoppingToken)).Revision!=schedule.AuthorizedPermissionRevision')
edit('web/src/Schedules.vue','岗位或密码变化后','岗位、权限矩阵或密码变化后')
edit('web/src/RolePermissions.vue','所有已登录账号的后续请求立即按新权限校验。','所有已登录账号的后续请求立即按新权限校验；原定时测温授权须重新编辑确认。')
edit('web/src/RolePermissions.vue','岗位权限不能开放未交付模块','修改权限后，原定时测温任务须重新编辑确认授权。岗位权限不能开放未交付模块')
# Retain backend definitions for real grants; hide unopened optional delivery in matrix UI.
edit('web/src/HistoryRecords.vue',"'液位状态','配置版本'","'液位状态','筛选数据项状态','配置版本'")
edit('web/src/HistoryRecords.vue','qualityNames[r.levelMm.quality],r.configurationRevision','qualityNames[r.levelMm.quality],qualityNames[matchedMetric(r).quality],r.configurationRevision')
edit('web/src/Snapshots.vue','function exportSnapshot(){if(!details.value)return','function exportSnapshot(){if(!details.value||!can(\'DataExport\'))return')
edit('web/src/Snapshots.vue','class="button" @click="exportSnapshot"','class="button" :disabled="!can(\'DataExport\')" @click="exportSnapshot"')
edit('web/src/AlarmCenter.vue','<option value="history" :disabled="!licensed">','<option value="history" :disabled="!licensed||!can(\'HistoryRead\')">')
edit('web/src/AlarmCenter.vue','<option value="all" :disabled="!licensed">','<option value="all" :disabled="!licensed||!can(\'HistoryRead\')">')
edit('web/src/AlarmCenter.vue',':disabled="!licensed||!records.length"',':disabled="!licensed||!can(\'DataExport\')||!records.length"')
edit('web/src/TrendBoard.vue',':disabled="!licensed||!hasData"',':disabled="!licensed||!can(\'DataExport\')||!hasData"')
edit('web/src/TrendBoard.vue',':disabled="!licensed||!data"',':disabled="!licensed||!can(\'DataExport\')||!data"')
edit('web/src/CurrentReadings.vue',':disabled="!licensed||!rows.length"',':disabled="!licensed||!can(\'DataExport\')||!rows.length"')
p=root/'web/src/workspace-ui.css';s=p.read_text(encoding='utf-8-sig');s+='\n/* Functional additions follow the existing workspace visual language. */\n.role-permissions{margin-top:20px}.role-permissions table th:not(:first-child),.role-permissions table td:not(:first-child){text-align:center;width:140px}.permission-check{display:inline-flex;align-items:center;gap:8px;justify-content:center;min-height:36px}.permission-check input{width:18px;height:18px;accent-color:#168d83}.configuration-transfer{margin-bottom:18px}.configuration-transfer>.inline-actions{flex-wrap:wrap}.configuration-transfer .file-picker{position:relative;overflow:hidden;cursor:pointer;display:inline-flex;align-items:center}.configuration-transfer .file-picker input{position:absolute;width:1px;height:1px;opacity:0;padding:0}.configuration-transfer .disabled{opacity:.5;cursor:not-allowed}.role-permissions .toolbar{align-items:flex-start}.role-permissions .toolbar h2{margin-bottom:6px}.import-issues{max-height:50vh;overflow:auto}.ats-marker-legend{font-size:12px;line-height:1.7}.configuration-transfer .modal-body .checkbox{margin:20px 0}.role-permissions table small{display:block;color:var(--muted,#839ca7);font-weight:400;margin-top:4px}\n';p.write_text(s,encoding='utf-8')
print('r10 safety polish applied')
