<script setup lang="ts">
import {axisBounds,tankAxes} from './trendSettings';
import {onMounted,onUnmounted,ref,watch,nextTick} from 'vue';
import {init,use,type EChartsType} from 'echarts/core';
import {LineChart} from 'echarts/charts';
import {GridComponent,TooltipComponent,LegendComponent} from 'echarts/components';
import {CanvasRenderer} from 'echarts/renderers';
import {api,type Configuration,type TrendResult,type TrendQuantity} from './api';
import {converted,unit,type Quantity} from './units';
use([LineChart,GridComponent,TooltipComponent,LegendComponent,CanvasRenderer]);
const props=defineProps<{tankId:string;config:Configuration}>(),emit=defineEmits<{more:[]}>();
const canvas=ref<HTMLElement|null>(null),error=ref(''),hasData=ref(false),paused=ref(false),axisWarning=ref('');
const quantities:TrendQuantity[]=['Level','NetVolume','Mass'];
const labels=['液位','净体积','质量'],types:Quantity[]=['level','volume','mass'],colors=['#318c9b','#b88442','#8370b0'];
let chart:EChartsType|undefined,timer:ReturnType<typeof setInterval>|undefined,observer:ResizeObserver|undefined,latest:TrendResult|null=null,active=true,busy=false,generation=0;
function draw(){
 if(!canvas.value||!latest)return;chart??=init(canvas.value);const settings=tankAxes(props.config.tanks.find(t=>t.id===props.tankId));axisWarning.value=latest.series.some(s=>{const a=settings[s.selection.quantity];return a.mode==='Fixed'&&a.minimum!==null&&a.maximum!==null&&s.points.some(p=>p.quality==='Good'&&p.value!==null&&(p.value<a.minimum!||p.value>a.maximum!))})?'有数据超出固定纵轴范围，请通过“更多曲线”调整范围查看。':'';const units=types.map(t=>unit(t,props.config.display));
 chart.setOption({animation:false,color:colors,legend:{top:0,right:90,itemWidth:15,itemHeight:3,textStyle:{fontSize:11,color:'#6d8894'},data:labels},grid:{top:38,bottom:28,left:56,right:108},tooltip:{trigger:'axis',confine:true},
 xAxis:{type:'time',min:latest.startUtcMs,max:latest.endUtcMs,axisLabel:{fontSize:10,color:'#859da7',formatter:(n:number)=>new Date(n).toLocaleTimeString('zh-CN',{hour12:false,hour:'2-digit',minute:'2-digit'})},axisLine:{lineStyle:{color:'#dbe6ea'}},splitLine:{show:false}},
 yAxis:types.map((_,i)=>({type:'value',...axisBounds(tankAxes(props.config.tanks.find(t=>t.id===props.tankId))[quantities[i]!],latest!.series[i]?.points.filter(p=>p.quality==='Good'&&p.value!==null).map(p=>converted(p.value!,types[i]!,props.config.display))||[],converted(1,types[i]!,props.config.display)),name:units[i],position:i===0?'left':'right',offset:i===2?58:0,scale:true,nameTextStyle:{fontSize:10,color:colors[i]},axisLabel:{fontSize:10,color:colors[i],formatter:(n:number)=>n.toLocaleString('zh-CN',{maximumFractionDigits:2})},splitLine:{show:i===0,lineStyle:{color:'#edf2f4'}},axisLine:{show:false}})),
 series:latest.series.map((s,i)=>({name:labels[i],type:'line',yAxisIndex:i,showSymbol:s.points.length<3,symbolSize:5,connectNulls:false,smooth:false,lineStyle:{width:1.7},data:s.points.map(p=>[p.atUtcMs,p.quality==='Good'&&p.value!==null?converted(p.value,types[i]!,props.config.display):null])}))},{notMerge:true});
}
async function load(){if(!active||busy||paused.value)return;busy=true;const token=generation;try{
 const result=await api<TrendResult>('/api/trends/realtime','POST',{startUtcMs:0,endUtcMs:1,maximumPoints:600,series:quantities.map(quantity=>({tankId:props.tankId,quantity}))});
 if(!active||token!==generation)return;latest=result;hasData.value=result.series.some(s=>s.points.some(p=>p.quality==='Good'&&p.value!==null));error.value='';await nextTick();draw();
 }catch(e){if(active&&token===generation)error.value=e instanceof Error?e.message:String(e)}finally{busy=false}}
onMounted(()=>{void load();observer=new ResizeObserver(()=>chart?.resize());if(canvas.value)observer.observe(canvas.value);timer=setInterval(()=>void load(),2000)});
onUnmounted(()=>{active=false;generation++;if(timer)clearInterval(timer);observer?.disconnect();chart?.dispose()});
watch(()=>props.tankId,()=>{generation++;latest=null;chart?.clear();hasData.value=false;void load()});watch(()=>props.config.display,draw,{deep:true});watch(()=>props.config.tanks.find(t=>t.id===props.tankId)?.trends.axes,draw,{deep:true});
</script>
<template><article class="panel single-realtime-panel"><header><div><h2>实时趋势</h2><span>最近 1 小时 · 缺测断线</span></div><div><button class="text-button" @click="paused=!paused">{{paused?'继续更新':'暂停'}}</button><button class="text-button" @click="emit('more')">更多曲线 / 历史 →</button></div></header><p v-if="axisWarning" class="error-message trend-inline-error">{{axisWarning}}</p><p v-if="error" class="error-message trend-inline-error">{{error}}</p><div ref="canvas" class="single-realtime-canvas" role="img" aria-label="单罐实时趋势：液位、净体积、质量"></div><span v-if="!hasData&&!error" class="single-trend-empty">暂无有效曲线点，等待采集；无效值不补零。</span></article></template>
