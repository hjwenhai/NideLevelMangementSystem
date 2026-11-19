<script setup lang="ts">
import type {AlarmSettings,AlarmLimits} from './api';
defineProps<{settings:AlarmSettings}>();
const fields=[['lowLow','低低限'],['low','低限'],['high','高限'],['highHigh','高高限']] as const;
function setValue(limits:AlarmLimits,key:'lowLow'|'low'|'high'|'highHigh',event:Event){const value=(event.target as HTMLInputElement).value;limits[key]=value===''?null:Number(value)}
</script>
<template>
 <section class="alarm-settings wide-field">
  <h3>报警设置</h3>
  <p class="help">限值留空表示不启用。延时要求连续超限；回差用于恢复判断。确认报警表示已知悉，恢复由有效读数决定。</p>
  <div class="table-wrap alarm-settings-table"><table>
   <thead><tr><th scope="col">监测项目 / 单位</th><th v-for="[,name] in fields" :key="name" scope="col">{{name}}</th><th scope="col">回差</th><th scope="col">延时（秒）</th></tr></thead>
   <tbody><tr v-for="(label,key) in {level:'液位 mm',temperature:'温度 ℃',density:'仪表密度 kg/m³',pressure1:'压力输入1 kPa',pressure2:'压力输入2 kPa'}" :key="key">
    <th scope="row">{{label}}</th>
    <td v-for="[field,name] in fields" :key="field"><input :value="settings[key][field]??''" type="number" step="any" placeholder="不启用" :aria-label="label+' '+name" @input="setValue(settings[key],field,$event)"></td>
    <td><input v-model.number="settings[key].hysteresis" type="number" step="any" min="0" required :aria-label="label+' 回差'"></td>
    <td><input v-model.number="settings[key].delaySeconds" type="number" min="0" max="3600" required :aria-label="label+' 延时'"></td>
   </tr></tbody>
  </table></div>
  <div class="alarm-monitor-options"><label class="checkbox"><input v-model="settings.communicationEnabled" type="checkbox">监测通讯异常</label><label class="checkbox"><input v-model="settings.instrumentEnabled" type="checkbox">监测仪表故障</label></div>
 </section>
</template>
