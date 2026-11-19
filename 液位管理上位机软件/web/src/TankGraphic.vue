<script setup lang="ts">
import { computed, useId } from 'vue';
const props = defineProps<{ shape:string;height:number;level:number|null;good:boolean;color?:string;showPercent?:boolean;alarmLimits?:{lowLow:number|null;low:number|null;high:number|null;highHigh:number|null};atsPoints?:{index:number;heightMm:number|null;quality:string;value:number|null}[] }>();
const id=`tank-${useId()}-`;
const percent=computed(()=>props.good&&props.level!==null?Math.max(0,Math.min(1,props.level/props.height)):0);
const y=computed(()=>(props.shape==='Sphere'?195:200)-percent.value*(props.shape==='Sphere'?146:156));
const markers=computed(()=>(props.atsPoints||[]).filter(p=>p.heightMm!==null&&p.heightMm>=0&&p.heightMm<=props.height).map(p=>({...p,y:(props.shape==='Sphere'?195:200)-p.heightMm!/(props.height||1)*(props.shape==='Sphere'?146:156)})));
const alarmMarkers=computed(()=>Object.entries(props.alarmLimits||{}).filter((entry):entry is [string,number]=>['lowLow','low','high','highHigh'].includes(entry[0])&&typeof entry[1]==='number'&&entry[1]>=0&&entry[1]<=props.height).map(([key,height])=>({key,height,name:({lowLow:'低低限',low:'低限',high:'高限',highHigh:'高高限'} as Record<string,string>)[key],color:key==='lowLow'||key==='highHigh'?'#ce6c62':'#c49d48',y:(props.shape==='Sphere'?195:200)-height/(props.height||1)*(props.shape==='Sphere'?146:156)})));
const color=computed(()=>props.good?(props.color||'#73afa1'):'#c7d2d6');
</script>
<template>
 <svg class="tank-graphic" viewBox="0 0 200 250" role="img" :aria-label="shape==='Sphere'?'球罐':shape==='FloatingRoof'?'浮顶罐':'立式罐'">
  <defs><linearGradient :id="id+'shell'"><stop stop-color="#e9f0f1" stop-opacity=".7"/><stop offset=".5" stop-color="#f7fafb" stop-opacity=".3"/><stop offset="1" stop-color="#bfd2d7" stop-opacity=".65"/></linearGradient><linearGradient :id="id+'liquid'"><stop :stop-color="color" stop-opacity=".65"/><stop offset="1" :stop-color="color"/></linearGradient><clipPath :id="id+'clip'"><circle v-if="shape==='Sphere'" cx="86" cy="122" r="73"/><path v-else d="M28 44 A58 14 0 0 1 144 44 L144 200 A58 14 0 0 1 28 200Z"/></clipPath></defs>
  <ellipse cx="86" cy="229" rx="77" ry="8" fill="#e4ebed"/>
  <template v-if="shape==='Sphere'"><path d="M44 179L33 223M127 179L138 223" stroke="#8fa9b2" stroke-width="4"/><circle cx="86" cy="122" r="73" :fill="`url(#${id}shell)`" stroke="#bed1d8" stroke-width="1.3"/><ellipse cx="86" cy="122" rx="73" ry="20" fill="none" stroke="#d5e2e6" stroke-dasharray="3 3"/></template>
  <template v-else><path d="M28 44L28 200A58 14 0 0 0 144 200L144 44" :fill="`url(#${id}shell)`" stroke="#bed1d8" stroke-width="1.3"/><ellipse cx="86" cy="44" rx="58" ry="14" fill="#f5f9fa" stroke="#bed1d8" stroke-width="1.3"/></template>
  <g :clip-path="`url(#${id}clip)`"><rect v-if="good&&percent>0" x="0" :y="y" width="160" height="220" :fill="`url(#${id}liquid)`"/><ellipse v-if="good&&percent>0" cx="86" :cy="y" rx="73" ry="12" :fill="color" opacity=".8"/></g>
  <template v-if="shape==='FloatingRoof'"><path d="M20 54L20 208M12 65H20M12 84H20M12 104H20M12 124H20M12 144H20M12 164H20M12 184H20M12 204H20" stroke="#9bb2ba" stroke-width="2"/><ellipse v-if="good" cx="86" :cy="y" rx="57" ry="11" fill="#b5c8ce" stroke="#6e909c" stroke-width="2"/><path d="M130 35L130 202" stroke="#8ca7b0" stroke-width="2"/></template>
  <template v-else><path d="M86 14L86 23" stroke="#668895" stroke-width="2"/><rect x="73" y="17" width="26" height="9" rx="3" fill="#3e6271"/><path :d="`M86 26V${good?y:150}`" stroke="#5c808f" stroke-width="1.5"/><ellipse v-if="good" cx="86" :cy="y" rx="12" ry="4" fill="#2d5362"/></template>
  <g aria-label="液位报警限位置"><g v-for="marker in alarmMarkers" :key="marker.key"><title>{{marker.name+'：'+marker.height+' mm'}}</title><path :d="`M8 ${marker.y-4}l9 4-9 4z`" :fill="marker.color"/><path :d="`M18 ${marker.y}H29`" :stroke="marker.color" stroke-width="1"/></g></g>
  <g v-if="atsPoints?.length" class="ats-tank-markers" aria-label="ATS已配置高度测点"><g v-for="p in markers" :key="p.index" :data-point="p.index" :data-height-mm="p.heightMm"><title>{{'温度计第'+p.index+'点，高度'+p.heightMm+'mm，'+(p.quality==='Good'?p.value+'℃':'无有效温度')}}</title><line x1="115" :y1="p.y" x2="148" :y2="p.y" stroke="#668b96" stroke-width=".6"/><circle cx="125" :cy="p.y" r="4.8" :fill="p.quality==='Good'?'#237f76':p.quality==='Fault'?'#bd4945':'#879ba3'" stroke="white" stroke-width=".8"/><text x="125" :y="p.y+2" text-anchor="middle" font-size="6" font-weight="700" fill="white">{{p.index}}</text></g></g>
  <path d="M177 44V200M172 44H182M172 122H182M172 200H182" stroke="#cadbe0" fill="none"/>
  <text x="186" y="47" font-size="8" fill="#8da4af">{{height/1000}}m</text><text x="185" y="203" font-size="8" fill="#8da4af">0</text>
  <text v-if="showPercent!==false" x="86" y="178" text-anchor="middle" font-size="12" font-weight="600" fill="white">{{good?Math.round(percent*100)+'%':'—'}}</text>
 </svg>
</template>
