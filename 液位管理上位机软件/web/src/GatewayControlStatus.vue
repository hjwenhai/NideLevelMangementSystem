<script setup lang="ts">
import {computed,onMounted,onUnmounted,ref,watch} from 'vue';
import {api,can,type Configuration,type Tank,type ControlRecord,type GatewayControlSnapshot,type GatewayControlChannel} from './api';
const props=defineProps<{tank:Tank;config:Configuration;dirty:boolean;records:ControlRecord[]}>();
const emit=defineEmits<{changed:[];'view-record':[id:string]}>();
type Cycle='Normal'|'IndependentStop';
const status=ref<GatewayControlSnapshot|null>(null),error=ref(''),message=ref(''),loading=ref(false),busy=ref(false),now=ref(Date.now()),received=ref(0);
const selected=ref<Cycle|null>(null),frozen=ref<GatewayControlSnapshot|null>(null),reason=ref(''),confirmed=ref(false),dialogError=ref('');
const frozenRecords=ref<ControlRecord[]>([]);
let timer:ReturnType<typeof setInterval>,alive=true,epoch=0;
const pendingStates=['Prepared','Queued','Checking','WriteIntent','Submitted','Unknown'];
const fresh=computed(()=>!!status.value&&!error.value&&now.value-received.value<10000&&status.value.configurationRevision===props.config.revision&&!props.dirty);
const rows=computed(()=>[{key:'Normal' as const,name:'正常动作',row:status.value?.normal},{key:'IndependentStop' as const,name:'独立停止',row:status.value?.independentStop}]);
const selectedName=computed(()=>selected.value==='Normal'?'正常动作':'独立停止');
const stateNames=['空闲','已受理 · 排队中','执行中','回读已确认 · 待复位','失败或取消 · 待复位','结果未知 · 需核查'];
const commands:Record<string,string>={MeasureI:'测量Ⅰ',MeasureII:'测量Ⅱ（界面）',MeasureIII:'测量Ⅲ（罐底）',Stop:'停止',Hoist:'上升',Lower:'下降',Density:'远程密度',Repeatability:'重复性检查',OscillationAvoidance:'液体震荡回避',RemoteLevel:'远程液位修改'};
const recordName=(r:ControlRecord)=>r.isReset?'结果复位':commands[r.command||'']||'原命令未记录';
const date=(value:string)=>new Date(value).toLocaleString('zh-CN',{hour12:false});
function pending(cycle:Cycle){return props.records.filter(r=>r.cycle===cycle&&pendingStates.includes(r.state))}
function elapsed(r:ControlRecord){
 const minutes=Math.max(0,Math.floor((now.value-Date.parse(r.createdAtUtc))/60000));
 return minutes<1?'不足1分钟':minutes<60?minutes+'分钟':Math.floor(minutes/60)+'小时'+minutes%60+'分钟';
}
function automaticReset(cycle:Cycle){return props.records.find(r=>r.cycle===cycle&&r.isAutomaticReset&&r.state==='ReadbackConfirmed')}
async function refresh(){
 if(loading.value)return;
 loading.value=true;const token=epoch;
 try{
  const data=await api<GatewayControlSnapshot>('/api/tanks/'+props.tank.id+'/gateway-controls');
  if(!alive||token!==epoch)return;
  if(data.configurationRevision!==props.config.revision)throw Error('配置已改变，请重新载入配置');
  status.value=data;received.value=Date.now();now.value=Date.now();error.value='';return data;
 }catch(e){if(alive&&token===epoch){status.value=null;error.value=String(e)}}
 finally{loading.value=false}
}
function open(cycle:Cycle){
 if(!fresh.value||!status.value||!can('ControlReview'))return;
 selected.value=cycle;frozen.value=JSON.parse(JSON.stringify(status.value)) as GatewayControlSnapshot;
 frozenRecords.value=pending(cycle).map(r=>({...r}));
 reason.value='';confirmed.value=false;dialogError.value='';
}
function same(a:GatewayControlSnapshot,b:GatewayControlSnapshot,cycle:Cycle){
 const key=cycle==='Normal'?'normal':'independentStop';
 return a.configurationRevision===b.configurationRevision&&a.acquisitionSessionId===b.acquisitionSessionId&&a.gatewayBootId===b.gatewayBootId&&a.gatewayBindingVersion===b.gatewayBindingVersion&&JSON.stringify(a[key])===JSON.stringify(b[key]);
}
async function reset(){
 if(busy.value||!selected.value||!frozen.value||!confirmed.value||!reason.value.trim())return;
 if(props.dirty){dialogError.value='请先保存或重新载入配置';return}
 busy.value=true;
 try{
  if(loading.value)throw Error('正在刷新状态，请稍后再确认');
  const current=await refresh();if(!current)throw Error('网关状态读取失败，未提交复位');
  if(!same(current,frozen.value,selected.value))throw Error('通道状态或周期已改变，请点击“重新读取并核对”后确认');
  await api('/api/tanks/'+props.tank.id+'/gateway-controls/reset','POST',{
   expected:current,cycle:selected.value,reason:reason.value.trim(),confirmed:true,
   reviewedControlIds:frozenRecords.value.map(r=>r.id)
  });
  const name=selectedName.value;selected.value=null;message.value=name+'结果复位已受理，正在等待空闲回读。';emit('changed');await refresh();
 }catch(e){dialogError.value=String(e);emit('changed')}
 finally{busy.value=false}
}
async function reloadConfirmation(){
 const current=await refresh();
 if(current&&selected.value){frozen.value=JSON.parse(JSON.stringify(current));frozenRecords.value=pending(selected.value).map(r=>({...r}));confirmed.value=false;dialogError.value='已重新读取，请核对当前状态及记录后再次确认';}
}
const canReset=(r:GatewayControlChannel|undefined,cycle:Cycle)=>fresh.value&&!!r&&[3,4,5].includes(r.state)&&can('ControlReview')&&!busy.value&&pending(cycle).every(r=>r.state==='Unknown');
watch(()=>[props.tank.id,props.config.revision],()=>{epoch++;status.value=null;error.value='';message.value='';selected.value=null;void refresh()});
onMounted(()=>{void refresh();let ticks=0;timer=setInterval(()=>{now.value=Date.now();if(++ticks%4===0&&document.visibilityState==='visible')void refresh()},1000)});
onUnmounted(()=>{alive=false;epoch++;clearInterval(timer)});
defineExpose({refresh});
</script>
<template>
<section class="gateway-live" aria-label="网关控制通道实时状态">
 <div class="gateway-live-head"><strong>网关通道实时状态</strong><button type="button" class="text-button" :disabled="loading||busy" @click="refresh">{{loading?'读取中…':'刷新'}}</button></div>
 <div v-for="item in rows" :key="item.key" class="gateway-channel">
  <div class="gateway-live-row">
   <span>{{item.name}}</span>
   <strong class="gateway-state" :class="{'needs-review':fresh&&item.row?.state===5,'needs-reset':fresh&&[3,4].includes(item.row?.state??-1)}">{{fresh&&item.row?stateNames[item.row.state]||'状态未知':'状态未知'}}</strong>
   <small v-if="fresh&&item.row?.error">网关结果错误码 {{item.row.error}}</small>
  </div>
  <div v-if="pending(item.key).length" class="gateway-blocker" role="status">
   <strong>{{tank.code}} · {{item.name}}通道有{{pending(item.key).length}}条待处理记录</strong>
   <div v-for="record in pending(item.key)" :key="record.id" class="gateway-blocker-record">
    <span>{{date(record.createdAtUtc)}} · {{recordName(record)}}</span>
    <span>{{record.state==='Unknown'?'结果未知 · 需现场核查':'执行或收尾中 · 请等待'}} · 已持续{{elapsed(record)}}</span>
    <p>{{record.result}}</p>
    <button type="button" class="text-button" @click="emit('view-record',record.id)">查看这条记录 →</button>
   </div>
  </div>
  <p v-else-if="fresh&&item.row?.state===0&&automaticReset(item.key)" class="gateway-note">最近结果已自动复位 · {{date(automaticReset(item.key)!.createdAtUtc)}}</p>
  <p v-if="fresh&&item.row&&[1,2].includes(item.row.state)" class="gateway-note">通道正在排队或执行，请等待状态更新。</p>
  <p v-if="fresh&&item.row&&[3,4,5].includes(item.row.state)&&!pending(item.key).length" class="gateway-note">网关保留着结果，当前没有对应的待处理记录。请核对现场及原操作。</p>
  <button v-if="canReset(item.row,item.key)" type="button" class="button gateway-resolve" @click="open(item.key)">核查现场并复位</button>
 </div>
 <p v-if="!fresh" class="gateway-note">{{props.dirty?'有待保存配置，请先保存或重新载入。':error||'正在读取网关通道状态…'}}</p>
 <p v-else class="gateway-note">更新 {{new Date(received).toLocaleTimeString('zh-CN',{hour12:false})}}</p>
 <p v-if="message" class="gateway-note" role="status">{{status?.normal.state===0&&status?.independentStop.state===0?'最新实时读数显示两通道均空闲。':message}}</p>
</section>
<div v-if="selected" class="modal-backdrop">
 <section class="modal" role="dialog" aria-label="核查现场并复位">
  <div class="modal-head"><h2>{{tank.code}} · 核查{{selectedName}}通道并复位</h2><button class="button" :disabled="busy" @click="selected=null">关闭</button></div>
  <form class="modal-body" @submit.prevent="reset">
   <p class="notice">现场核查结论将保存到下列记录，随后复位当前“{{selectedName}}”结果通道。</p>
   <div v-for="record in frozenRecords" :key="record.id" class="control-result">
    <strong>{{recordName(record)}} · {{record.state==='Unknown'?'结果未知':'仍在处理中'}}</strong>
    <small>{{date(record.createdAtUtc)}} · {{record.actorName}} · 记录 {{record.id}}</small><p>{{record.result}}</p>
   </div>
   <p v-if="!frozenRecords.length" class="help">当前无待核查记录，现场核查结论将随本次复位记录保存。</p>
   <p class="help">请核对浮子运动、实际位置、仪表模式及原操作。复位只清除网关结果锁存，不会停止浮子、恢复测量或重发原操作。</p>
   <p v-if="frozenRecords.some(r=>r.command==='RemoteLevel')" class="help">涉及远程液位修改，请同时核对仪表实际偏移和认证状态。</p>
   <p v-if="dialogError" class="error-message" role="alert">{{dialogError}}</p>
   <button type="button" class="text-button" :disabled="busy||loading" @click="reloadConfirmation">重新读取并核对</button>
   <label class="config-field">现场核查结论<textarea v-model="reason" rows="3" maxlength="1000" required placeholder="填写浮子实际状态、仪表模式及核查结果"></textarea></label>
   <label class="checkbox"><input v-model="confirmed" type="checkbox" required>已核对列出的记录和现场状态，确认保存结论并复位本通道</label>
   <button class="button primary" :disabled="busy||!confirmed||!reason.trim()||dirty">{{busy?'正在核验并提交…':'保存核查并复位'}}</button>
  </form>
 </section>
</div>
</template>
<style scoped>
.gateway-live{border-top:1px solid #e1ebed;margin-top:10px;padding-top:10px}
.gateway-live-head,.gateway-live-row{display:flex;align-items:center;gap:7px}
.gateway-live-head{justify-content:space-between;margin-bottom:5px;font-size:12px}
.gateway-channel{padding:5px 0}
.gateway-live-row{flex-wrap:wrap;min-height:27px;font-size:12px}
.gateway-live-row>span:first-child{min-width:56px;color:#647d86}
.gateway-state{font-weight:500;color:#36656a}
.gateway-state.needs-reset{color:#926e32}.gateway-state.needs-review{color:#b9574f}
.gateway-live .text-button{font-size:12px;padding:3px}
.gateway-live-row small{color:#956e44}
.gateway-note{font-size:11px;color:#748d96;line-height:1.5;margin:4px 0}
.gateway-blocker{background:#fff8f5;border:1px solid #edcfca;border-radius:8px;padding:9px;margin:5px 0;font-size:12px;line-height:1.65;color:#965149;overflow-wrap:anywhere}
.gateway-blocker-record{margin-top:6px}.gateway-blocker-record>span{display:block}.gateway-blocker-record p{margin:4px 0}
.gateway-resolve{font-size:12px;padding:6px 10px}
.control-result small{display:block;overflow-wrap:anywhere}
</style>
