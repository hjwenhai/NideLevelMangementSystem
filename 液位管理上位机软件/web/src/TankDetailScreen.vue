<script setup lang="ts">
import {computed,ref,watch,defineAsyncComponent} from 'vue';
import {can,operationAvailable,ifxTableAddress,type Configuration,type Tank,type LiveReading,type Metric} from './api';
import {displayMetric,converted,unit,qualityNames,type Quantity} from './units';
import TankGraphic from './TankGraphic.vue';
import {hasFrozenSurface} from './surfaceDisplay';
import InstrumentControls from './InstrumentControls.vue';
import {instrumentMode} from './instrumentMode';
import {previewPrint} from './printPreview';
const TankRealtimeTrend=defineAsyncComponent(()=>import('./TankRealtimeTrend.vue'));
const RecordBasisDialog=defineAsyncComponent(()=>import('./RecordBasisDialog.vue'));
const props=defineProps<{licensed:boolean;config:Configuration;tank:Tank;reading:LiveReading|undefined;valid:boolean;admin:boolean;canOperate:boolean;averageAllowed:boolean;dirty:boolean;alarmColor:string;alarmText:string}>();
const emit=defineEmits<{updated:[Configuration];edit:[Tank];trends:[string];average:[string]}>();
const tab=ref('data'),graphic=ref<HTMLElement|null>(null),controls=ref<InstanceType<typeof InstrumentControls>|null>(null);
const selectedBasis=ref<{tankId:string;configurationRevision:number;calculationRevision:number;initialTab:string}|null>(null);
const canAdjustLevel=computed(()=>operationAvailable('RemoteLevel')&&props.tank.controls.allowedCommands.includes('RemoteLevel')&&can('RemoteLevelAdjust')&&props.config.channels.find(c=>c.id===props.tank.channelId)?.protocol!=='IfxL25');
const frozenDisplay=computed(()=>hasFrozenSurface(props.reading,props.valid,sourceChannel.value?.protocol));
const displayLevel=computed(()=>frozenDisplay.value?props.reading!.lastMeasuredLevelMm!:props.reading?.levelMm.value??null);
const detailTabs=computed(()=>[['data','测量与计量'],...(props.tank.temperatureMode==='Ats'?[['ats','温度计']]:[]),['diagnostics','故障诊断'],['archive','罐体档案']]);
watch(()=>props.tank.id,()=>{tab.value='data';selectedBasis.value=null});
watch(()=>props.tank.temperatureMode,()=>{if(tab.value==='ats'&&props.tank.temperatureMode!=='Ats')tab.value='data'});
function openBasis(initialTab:string){if(props.reading)selectedBasis.value={tankId:props.tank.id,configurationRevision:props.reading.configurationRevision,calculationRevision:props.reading.calculationRevision,initialTab}}
const sourceChannel=computed(()=>props.config.channels.find(c=>c.id===props.tank.channelId));
const sourceProtocol=computed(()=>({DirectRtu:'直连RS-485 Modbus RTU',IfxL25:'IFX30000',GatewayRtu:'GT6757X Modbus RTU',GatewayTcp:'GT6757X Modbus TCP'}[sourceChannel.value?.protocol||'DirectRtu']));
const sourceNotes=computed(()=>[...new Set([props.reading?.surfaceBasis,props.reading?.diagnostic,...(props.reading?.calculationNotes||[])].map(x=>x?.trim()).filter((x):x is string=>!!x))]);
const shapes:Record<string,string>={Vertical:'立式罐',Sphere:'球罐',FloatingRoof:'浮顶罐'};
const value=(m:Metric|undefined,q:Quantity)=>props.valid?displayMetric(m,q,props.config.display):'—';
const points=computed(()=>Array.from({length:props.tank.atsPointCount},(_,i)=>({index:i+1,temperatureC:props.reading?.atsPoints.find(p=>p.index===i+1)?.temperatureC||{value:null,quality:'Missing' as const,isValid:false,reason:'等待采集'}})));
const normalPoints=computed(()=>props.valid?points.value.filter(p=>p.temperatureC.quality==='Good').length:0);
const graphicPoints=computed(()=>props.tank.temperatureMode!=='Ats'?[]:points.value.map(p=>({index:p.index,heightMm:props.tank.atsHeightsMm[p.index-1]??null,quality:props.valid?p.temperatureC.quality:'Missing',value:props.valid?p.temperatureC.value:null})));
function quality(m:Metric|undefined){return !props.valid?'无有效数据':m?.reason||qualityNames[m?.quality||'Missing']}
function date(t:string|null|undefined){return t?new Date(t).toLocaleString('zh-CN',{hour12:false}):'—'}
function pressure(i:number){const p=props.tank[i===1?'pressure1':'pressure2'],m=props.reading?.[i===1?'pressure1KPa':'pressure2KPa'];if(!p.enabled)return '未配置';if(!props.valid||m?.quality!=='Good'||m.value===null)return '—';return (m.value/(p.unit==='MPa'?1000:p.unit==='Bar'?100:1)).toLocaleString('zh-CN',{minimumFractionDigits:3,maximumFractionDigits:3})}
const pressureUnit=(i:number)=>{const u=props.tank[i===1?'pressure1':'pressure2'].unit;return u==='KPa'?'kPa':u==='MPa'?'MPa':'bar'};
const keyMetrics=computed(()=>[
 {label:frozenDisplay.value?'液位（冻结）':props.reading?.surfaceBasis.includes('冻结')?'液位基准 X（冻结）':'液位',value:frozenDisplay.value?displayMetric({value:displayLevel.value,quality:'Good',isValid:true},'level',props.config.display):value(props.reading?.levelMm,'level'),unit:unit('level',props.config.display),status:instrumentMode(props.reading,props.valid,sourceChannel.value?.protocol)+(frozenDisplay.value?' · 冻结于 '+date(props.reading?.lastMeasuredLevelAtUtc):'')},
 {label:'温度',value:value(props.reading?.instantTemperatureC,'temperature'),unit:'℃',status:quality(props.reading?.instantTemperatureC)},
 ...(props.tank.metering.showInstrumentDensity===false?[]:[{label:'仪表实测密度',value:value(props.reading?.instrumentDensityKgM3,'density'),unit:unit('density',props.config.display),status:quality(props.reading?.instrumentDensityKgM3)}]),
 {label:'人工工况密度',value:props.tank.metering.manualOperatingDensityKgM3?converted(props.tank.metering.manualOperatingDensityKgM3,'density',props.config.display).toLocaleString('zh-CN',{maximumFractionDigits:3}):'—',unit:unit('density',props.config.display),status:props.tank.metering.densitySource==='Manual'?'用于计算':'独立输入'},
 {label:'当前净体积',value:value(props.reading?.netVolumeM3,'volume'),unit:unit('volume',props.config.display),status:quality(props.reading?.netVolumeM3)},
 {label:'产品质量',value:value(props.reading?.massT,'mass'),unit:unit('mass',props.config.display),status:quality(props.reading?.massT)}
]);
const extraMetrics=computed(()=>[
 {label:'修正后当前体积',value:value(props.reading?.currentVolumeM3,'volume'),unit:unit('volume',props.config.display),note:'扣水前体积 · '+quality(props.reading?.currentVolumeM3)},
 {label:'水位',value:props.tank.metering.waterHeightMm==null?'未填写':props.tank.metering.waterHeightMm.toLocaleString('zh-CN',{maximumFractionDigits:1}),unit:'mm',note:props.tank.metering.waterHeightMm==null?'旧配置沿用人工水体积 '+props.tank.metering.waterVolumeM3+' m³':'人工水位 · 计算水体积 '+value(props.reading?.waterVolumeM3,'volume')+' '+unit('volume',props.config.display)},
 {label:'20℃标准密度',value:String(props.tank.metering.standardDensity20KgM3??'—'),unit:'kg/m³',note:'人工配置，独立于实测密度'},
 ...[1,2].filter(i=>props.tank[i===1?'pressure1':'pressure2'].enabled).map(i=>({label:'压力输入'+i,value:pressure(i),unit:pressureUnit(i),note:props.tank[i===1?'pressure1':'pressure2'].enabled?quality(props.reading?.[i===1?'pressure1KPa':'pressure2KPa']):'未配置压力输入'})),
 {label:'浮子实际位置 P',value:value(props.reading?.floatPositionMm,'level'),unit:unit('level',props.config.display),note:props.reading?.surfaceBasis||quality(props.reading?.floatPositionMm)}
]);
const archive=computed(()=>[
 ['储罐名称',props.tank.name||'—'],['仪表位号',props.tank.instrumentTag||'—'],['产品编号',props.tank.productNumber||'—'],['设备批次',props.tank.batch||'—'],
 ['罐型',shapes[props.tank.shape]],['罐高 mm',props.tank.heightMm],['罐径 mm',props.tank.diameterMm??'—'],['公称容量 m³',props.tank.nominalCapacityM3??'—'],
 ['所属罐组',props.config.groups.filter(g=>g.tankIds.includes(props.tank.id)).map(g=>g.name).join('、')||'—'],['采集通道',props.config.channels.find(c=>c.id===props.tank.channelId)?.name||'—'],
 [sourceChannel.value?.protocol==='IfxL25'?'表地址':'仪表地址',sourceChannel.value?.protocol==='IfxL25'?ifxTableAddress(props.tank.address,sourceChannel.value.unitId):props.tank.address],['备注',props.tank.notes||'—']
]);
const faultCode=computed(()=>props.valid&&props.reading?.instrumentFault!=null?'0x'+props.reading.instrumentFault.toString(16).toUpperCase().padStart(4,'0'):'—');
const faultCount=computed(()=>props.valid?props.reading?.instrumentFaults.length||0:0);
const averageValue=computed(()=>props.tank.temperatureMode==='Wireless'&&props.reading?.averageTaskId?displayMetric(props.reading.averageTemperatureC,'temperature',props.config.display):value(props.reading?.averageTemperatureC,'temperature'));
function printDetail(){
 if(!props.licensed||!can('DataExport'))return;
 const svg=graphic.value?.querySelector('svg');
 previewPrint({title:props.tank.code+' · 单罐详情',scope:[(props.tank.name||'')+' · '+props.tank.medium+' · '+shapes[props.tank.shape]+' · '+props.tank.zone,'最近接收：'+date(props.reading?.receivedAtUtc)+' · '+props.alarmText],image:svg?'data:image/svg+xml;charset=utf-8,'+encodeURIComponent(new XMLSerializer().serializeToString(svg)):undefined,imageTitle:'液位与已知测点位置示意',sections:[
 {title:'测量与计量',headers:['数据项','数值','单位','状态 / 依据'],rows:[...keyMetrics.value.map(m=>[m.label,m.value,m.unit,m.status]),...extraMetrics.value.map(m=>[m.label,m.value,m.unit,m.note]),...(props.tank.temperatureMode==='Ats'||props.averageAllowed&&props.tank.temperatureMode==='Wireless'?[[props.tank.temperatureMode==='Ats'?'实时平均温度':'最近任务平均温度',averageValue.value,'℃',props.tank.temperatureMode==='Ats'?'正常点 '+normalPoints.value+' / '+props.tank.atsPointCount:'完成时间 '+date(props.reading?.averageCompletedAtUtc)]]:[])]},
 ...(props.tank.temperatureMode==='Ats'?[{title:'温度计',headers:['点号','高度 mm','温度 ℃','状态'],rows:points.value.map(p=>[p.index,props.tank.atsHeightsMm[p.index-1]??'—',value(p.temperatureC,'temperature'),quality(p.temperatureC)]),note:'所有正常读取点均参与平均；缺测不补零，未填高度不推定位置。'}]:[]),
 {title:'仪表状态与故障',headers:['表头故障码','故障编码','含义','软件首次检测'],rows:props.valid?props.reading?.instrumentFaults.map(f=>[f.displayCode||'—',f.code,f.description,date(f.firstDetectedAtUtc)])||[]:[],note:'状态码 '+(props.valid?props.reading?.instrumentStatus??'—':'—')+' · 故障码 '+faultCode.value+(props.reading?.diagnostic?' · '+props.reading.diagnostic:'')},
 {title:'罐体档案',headers:['项目','配置值'],rows:archive.value}
 ]});
}
defineExpose({printDetail});
</script>
<template><section class="single-tank-screen">
 <div class="single-primary-metrics" :style="{'--metric-count':keyMetrics.length}"><article v-for="(m,index) in keyMetrics" :key="m.label" class="panel single-primary-metric" :class="{'frozen-reading':index===0&&(frozenDisplay||reading?.frozenSurfaceAtUtc)}"><small>{{m.label}} <span>{{m.unit}}</span></small><button v-if="index===0&&canAdjustLevel" class="level-adjust-icon" aria-label="远程液位修改" title="远程液位修改" :disabled="!licensed||dirty" @click="controls?.openRemoteLevel()"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 19h4L20 7l-4-4L4 15v4ZM13 6l4 4M4 22h16"/></svg></button><strong>{{m.value}}</strong><span class="metric-quality" :title="m.status">{{m.status}}</span></article></div>
 <section class="single-detail-grid">
 <article class="panel single-vessel-panel detail-visual"><header><h2>储罐状态</h2><span class="single-alarm" :style="{color:alarmColor}" :title="alarmText">{{alarmText}}</span></header><div ref="graphic" class="single-vessel-graphic"><TankGraphic :shape="tank.shape" :height="tank.heightMm" :level="displayLevel" :good="valid&&(reading?.levelMm.isValid||frozenDisplay)" :color="frozenDisplay?'#bbc7cc':alarmColor" :alarm-limits="tank.alarms.level" :ats-points="graphicPoints"/></div><p class="single-vessel-caption">{{tank.medium||'介质未填'}} · {{shapes[tank.shape]}} · 罐高 {{tank.heightMm}} mm</p><p v-if="tank.temperatureMode==='Ats'" class="single-vessel-caption ats-marker-legend">编号对应测点；未填高度不推定位置。</p><InstrumentControls :reading="reading" :reading-valid="valid" ref="controls" compact hide-remote-level :licensed="licensed" :key="tank.id" :config="config" :tank="tank" :can-operate="canOperate" :dirty="dirty"/><button v-if="averageAllowed&&tank.temperatureMode==='Wireless'" class="button single-average-link" @click="emit('average',tank.id)">平均温度测量任务 →</button></article>
 <div class="single-detail-workspace">
 <article class="panel single-data-panel"><div class="single-detail-tabs" role="tablist" aria-label="单罐详情内容"><button v-for="[key,label] in detailTabs" :key="key" role="tab" :aria-selected="tab===key" :class="{active:tab===key}" @click="tab=key">{{label}}<span v-if="key==='diagnostics'&&faultCount">{{faultCount}}</span></button><button v-if="admin" class="text-button single-edit" @click="emit('edit',tank)">编辑配置</button></div>
 <div class="single-tab-content"><template v-if="tab==='data'"><div v-if="tank.temperatureMode==='Wireless'&&averageAllowed" class="single-wireless-summary"><span>最近一次平均温度 <strong>{{averageValue}} ℃</strong></span><span>测量完成 {{date(reading?.averageCompletedAtUtc)}}</span></div><div class="single-extra-metrics"><div v-for="m in extraMetrics" :key="m.label" class="single-extra-metric"><small>{{m.label}} <span>{{m.unit}}</span></small><strong>{{m.value}}</strong><p :title="m.note">{{m.note}}</p></div></div></template>
 <section v-else-if="tab==='ats'&&tank.temperatureMode==='Ats'" class="single-ats-panel"><header><span>正常点 {{normalPoints}} / {{tank.atsPointCount}} · 点号从下至上</span><div class="single-ats-average"><span>实时平均</span><strong>{{averageValue}} <small>℃</small></strong></div></header><div class="single-ats-grid"><div v-for="point in points" :key="point.index" class="single-temperature-point" :data-point="point.index" :class="{invalid:!valid||point.temperatureC.quality!=='Good'}" :title="quality(point.temperatureC)"><div><b>{{String(point.index).padStart(2,'0')}}</b><span>{{tank.atsHeightsMm[point.index-1]===null||tank.atsHeightsMm[point.index-1]===undefined?'高度未设置':tank.atsHeightsMm[point.index-1]+' mm'}}</span></div><strong>{{value(point.temperatureC,'temperature')}} <small>℃</small></strong><span>{{valid?qualityNames[point.temperatureC.quality]:'无效'}}</span></div></div><p class="single-ats-rule">所有正常读取点均参与平均。缺测、故障不补零。</p></section>
 <div v-else-if="tab==='diagnostics'" class="single-diagnostics"><div class="single-status-codes"><span>仪表状态码 <strong>{{valid?reading?.instrumentStatus??'—':'—'}}</strong></span><span>仪表故障码 <strong>{{faultCode}}</strong></span></div><p v-if="!valid" class="error-message">当前无有效数据，不能据此判断仪表无故障。</p><template v-else><p v-if="!faultCount" class="help">当前未检测到仪表故障。</p><div v-else class="single-fault-list"><table><thead><tr><th>表头故障码</th><th>故障编码</th><th>含义</th><th>软件首次检测</th></tr></thead><tbody><tr v-for="fault in reading?.instrumentFaults" :key="fault.code"><td>{{fault.displayCode||'—'}}</td><td>{{fault.code}}</td><td>{{fault.description}}</td><td>{{date(fault.firstDetectedAtUtc)}}</td></tr></tbody></table></div><p v-if="faultCount&&!reading?.faultTrackingPersisted" class="error-message">首次检测时间暂未保存，显示本次检测值。</p></template><p v-if="reading?.diagnostic" class="error-message">{{reading.diagnostic}}</p></div>
 <dl v-else class="single-archive"><div v-for="row in archive" :key="row[0]"><dt>{{row[0]}}</dt><dd :title="String(row[1])">{{row[1]}}</dd></div></dl></div>
 </article>
 <TankRealtimeTrend :key="tank.id" :tank-id="tank.id" :config="config" @more="emit('trends',tank.id)"/>
 <div class="single-source-line"><span>最近接收 {{date(reading?.receivedAtUtc)}}</span><span>{{reading?.sourceTimeConfirmed?'来源时间 '+date(reading.sourceAtUtc):'来源时间未确认 · 采用接收时间'}}</span><button v-if="reading" class="text-button basis-link" @click="openBasis('configuration')">配置记录 {{reading.configurationRevision}}</button><button v-if="reading" class="text-button basis-link" @click="openBasis('calculation')">计量依据 {{reading.calculationRevision}}</button><details class="single-source-details"><summary>来源与计算说明</summary><div class="source-explanation-panel" role="note" aria-label="来源与计算说明"><strong>来源与计算说明</strong><p>采集通道：{{sourceChannel?.name||'未配置'}} · {{sourceProtocol}}</p><p v-if="!reading">暂未取得采集读数。</p><p v-else>{{reading.sourceTimeConfirmed?'仪表数据时间：'+date(reading.sourceAtUtc):'来源时间未确认，采用软件接收时间：'+date(reading.receivedAtUtc)}}</p><p v-if="reading&&!valid">当前读数无效或已过期，请先检查采集状态。</p><p v-for="note in sourceNotes" :key="note">{{note}}</p><p v-if="!sourceNotes.length">当前暂无额外说明。计算参数和罐容表版本请点击“计量依据”查看。</p></div></details></div>
 </div></section><RecordBasisDialog v-if="selectedBasis" v-bind="selectedBasis" @close="selectedBasis=null"/></section></template>
