from pathlib import Path
root=Path(__file__).resolve().parents[1]
def edit(name,old,new):
 p=root/name;s=p.read_text(encoding='utf-8-sig');assert old in s,(name,old[:100]);p.write_text(s.replace(old,new),encoding='utf-8')
edit('web/src/InstrumentControls.vue',':disabled="!canOperate||busy||',':disabled="!can(c===\'Stop\'?\'InstrumentStop\':\'InstrumentControl\')||busy||')
edit('web/src/InstrumentControls.vue',"r.state==='Unknown'&&canOperate","r.state==='Unknown'&&can('ControlReview')")
edit('web/src/InstrumentControls.vue',"(can('InstrumentControl')||can('InstrumentStop')||can('ControlReview'))&&['GatewayTcp'","can('ControlReview')&&['GatewayTcp'")
edit('web/src/HistoryRecords.vue',':disabled="!can(\'DataExport\')||busy||!page?.next"',':disabled="busy||!page?.next"')
edit('web/src/TrendBoard.vue','<option value="history" :disabled="!licensed">','<option value="history" :disabled="!licensed||!can(\'HistoryRead\')">')
edit('web/src/CurrentReadings.vue','function exportRows(){if(!props.licensed)return','function exportRows(){if(!props.licensed||!can(\'DataExport\'))return')
edit('web/src/CurrentReadings.vue','function print(){if(props.licensed)window.print()}','function print(){if(props.licensed&&can(\'DataExport\'))window.print()}')
edit('web/src/TrendBoard.vue','function exportRows(){if(!props.licensed||!data.value)return','function exportRows(){if(!props.licensed||!can(\'DataExport\')||!data.value)return')
edit('web/src/TrendBoard.vue','function print(){if(props.licensed)window.print()}','function print(){if(props.licensed&&can(\'DataExport\'))window.print()}')
edit('web/src/CapacityTables.vue','<a v-if="licensed" class="text-button"','<a v-if="licensed&&can(\'DataExport\')" class="text-button"')
edit('src/LevelManager.Host/PermissionEndpoints.cs','async(IRolePermissionStore store,CancellationToken ct)=>Results.Ok(new{definitions=PermissionPolicy.Definitions,matrix=await store.GetPermissionsAsync(ct)})','async(IRolePermissionStore store,ProductAuthorization authorization,CancellationToken ct)=>Results.Ok(new{definitions=PermissionPolicy.Definitions.Where(d=>d.Id!=AppPermission.AverageTemperatureStart||authorization.HasAverageModule),matrix=await store.GetPermissionsAsync(ct)})')
# Recheck functional authority after waiting for a configuration/permission commit gate.
edit('src/LevelManager.Host/ConfigurationCoordinator.cs','DataJobService dataJobs,IUserStore users)','DataJobService dataJobs,IUserStore users,IRolePermissionStore permissions)')
edit('src/LevelManager.Host/ConfigurationCoordinator.cs','var before=await configurations.GetAsync(ct);','var account=await users.FindByIdAsync(actor.AccountId,ct);if(account is null||!account.Enabled)throw new UnauthorizedAccessException("当前账号已失效");actor=await permissions.ResolveAsync(account,ct);actor.Require(AppPermission.ConfigurationEdit);\n            var before=await configurations.GetAsync(ct);')
# Native 6h runs must exercise a restart INSIDE their window, not only at its endpoint.
edit('tests/native-soak-check.cjs','nextRestart=duration<600?120000:21600000','nextRestart=duration<600?120000:duration<=21600?Math.floor(duration*1000/2):21600000')
edit('tests/native-soak-check.cjs','nextRestart+=21600000;','nextRestart+=duration<600?120000:duration<=21600?Math.floor(duration*1000/2):21600000;')
edit('tests/LevelManager.Tests/HistoryQualityTests.cs','"2026-01-01Z"','"2026-01-01T00:00:00Z"')
print('r10 polish applied')
