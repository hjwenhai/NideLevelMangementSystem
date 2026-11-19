from pathlib import Path
root=Path(__file__).resolve().parents[1]
def edit(name,old,new):
 p=root/name;s=p.read_text(encoding='utf-8-sig');assert old in s,(name,old[:100]);p.write_text(s.replace(old,new),encoding='utf-8')
edit('src/LevelManager.Host/WirelessTaskService.cs','IUserStore users,MotionEvidenceGuard','IUserStore users,IRolePermissionStore permissions,MotionEvidenceGuard')
edit('src/LevelManager.Host/WirelessTaskService.cs','events,users,evidence);','events,users,permissions,evidence);')
# Refresh is presentation-only: no communication sessions or archive timing changes.
edit('src/LevelManager.Domain/PresentationConfiguration.cs','public int VolumeDecimals { get; init; } = 3;','public int RefreshIntervalSeconds { get; init; } = 1;\n    public int VolumeDecimals { get; init; } = 3;')
edit('src/LevelManager.Domain/PresentationConfiguration.cs','if (display is null ||','if (display is null || display.RefreshIntervalSeconds is <1 or >30 ||')
edit('web/src/api.ts','export interface Session','import {ref} from \'vue\';\nexport const currentPermissions=ref<string[]>([]);\nexport const can=(permission:string)=>currentPermissions.value.includes(permission);\nexport interface Session')
edit('web/src/api.ts','role:string}}','role:string;permissions:string[]}}')
edit('web/src/api.ts','csrf=value.csrfToken;return value','csrf=value.csrfToken;currentPermissions.value=value.user?.permissions||[];return value')
edit('web/src/api.ts','export interface DisplaySettings {','export interface DisplaySettings {refreshIntervalSeconds:number;')
edit('web/src/Accounts.vue',"import {api} from './api';","import {api} from './api';\nimport RolePermissions from './RolePermissions.vue';")
edit('web/src/Accounts.vue','管理员维护配置、账号及系统；操作员按本罐允许的操作执行、确认报警和查询；浏览员只查看与查询。','各岗位的功能权限在下方矩阵设置。')
edit('web/src/Accounts.vue','</section><div v-if="draft||changeOwn"','</section><RolePermissions v-if="admin"/><div v-if="draft||changeOwn"')
edit('web/src/App.vue',"import {api,session as getSession,","import {api,can,session as getSession,")
edit('web/src/App.vue',"const canOperate=computed(()=>identity.value?.user&&identity.value.user.role!=='Viewer');","const canOperate=computed(()=>can('InstrumentControl'));\nconst canConfigure=computed(()=>can('ConfigurationEdit'));")
edit('web/src/App.vue',"async function refreshPresentation(){presentation.value=","async function refreshPresentation(){if(identity.value?.user)identity.value=await getSession();presentation.value=")
edit('web/src/App.vue',"if(identity.value?.user&&!busy.value)void refreshAcquisition()","if(identity.value?.user&&!busy.value&&performance.now()-lastPoll>=(config.value?.display.refreshIntervalSeconds||1)*1000)void refreshAcquisition()")
# Most admin flags in App are configuration edit controls. Specific security panels
# continue to use actual administrator, and capacity has its own functional permission.
p=root/'web/src/App.vue';s=p.read_text(encoding='utf-8')
s=s.replace('v-if="admin"','v-if="canConfigure"').replace(':disabled="!admin',':disabled="!canConfigure').replace(':admin="admin"',':admin="canConfigure"')
s=s.replace('<Accounts :admin="canConfigure"','<Accounts :admin="admin"').replace('<Authorization :admin="canConfigure"','<Authorization :admin="admin"')
s=s.replace('<ReadOnlyIntegration :admin="canConfigure"','<ReadOnlyIntegration :admin="can(\'IntegrationManage\')"')
s=s.replace('<CapacityTables :admin="canConfigure"','<CapacityTables :admin="can(\'CapacityManage\')"')
s=s.replace('v-if="canOperate" class="button" @click="startAcquisition"','v-if="can(\'AcquisitionManage\')" class="button" @click="startAcquisition"')
s=s.replace('v-if="canOperate"', 'v-if="can(\'AcquisitionManage\')"') # only acquisition toolbar (children have their own controls).
p.write_text(s,encoding='utf-8')
edit('web/src/PresentationSettings.vue','<p class="help">显示、查询和导出采用相同单位。','<label class="config-field">页面刷新间隔（秒）<input v-model.number="config.display.refreshIntervalSeconds" type="number" min="1" max="30" :disabled="!admin" aria-label="页面刷新间隔（秒）" @change="emit(\'dirty\')"></label><p class="help">页面每1～30秒刷新，与通道采集周期、历史保存周期分别设置。显示、查询和导出采用相同单位。')
print('r10 UI foundation and refresh applied')
