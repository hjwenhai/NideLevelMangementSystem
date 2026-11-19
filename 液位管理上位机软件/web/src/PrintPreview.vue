<script setup lang="ts">
import {computed,nextTick,onUnmounted,watch} from 'vue';
import {can,currentPermissions} from './api';
import {printReport,closePrintPreview} from './printPreview';
const props=defineProps<{licensed:boolean}>();
const allowed=computed(()=>props.licensed&&can('DataExport')&&(!printReport.value?.readPermission||can(printReport.value.readPermission)));
watch([()=>props.licensed,currentPermissions],()=>{if(!allowed.value)closePrintPreview()},{deep:true});
onUnmounted(closePrintPreview);
let previousFocus:HTMLElement|null=null;
watch(printReport,async report=>{if(report){previousFocus=document.activeElement as HTMLElement;await nextTick();document.querySelector<HTMLElement>('.report-preview-overlay')?.focus()}else if(previousFocus?.isConnected)previousFocus.focus()});
function keys(event:KeyboardEvent){if(event.key==='Escape'){closePrintPreview();return}if(event.key!=='Tab')return;const buttons=Array.from(document.querySelectorAll<HTMLButtonElement>('.report-preview-toolbar button:not(:disabled)'));const first=buttons[0],last=buttons[buttons.length-1];if(!first||!last)return;if(event.shiftKey&&(document.activeElement===first||!buttons.includes(document.activeElement as HTMLButtonElement))){event.preventDefault();last.focus()}else if(!event.shiftKey&&(document.activeElement===last||!buttons.includes(document.activeElement as HTMLButtonElement))){event.preventDefault();first.focus()}}

async function printNow(){if(!allowed.value||!printReport.value)return;await nextTick();for(const image of Array.from(document.querySelectorAll<HTMLImageElement>('.report-print-sheet img'))){if(!image.complete)await image.decode().catch(()=>{})}if(allowed.value)window.print()}
</script>
<template><Teleport to="body"><div v-if="printReport&&allowed" class="report-preview-overlay" role="dialog" aria-label="打印预览" aria-modal="true" tabindex="-1" @keydown="keys"><section class="report-preview-window"><header class="report-preview-toolbar"><div><h2>打印预览</h2><span>冻结当前显示内容 · A4横向 · 可选择打印机或保存为PDF</span></div><div><button class="button" @click="closePrintPreview">关闭</button><button class="button primary" :disabled="!allowed" @click="printNow">打印 / 保存PDF</button></div></header><div class="report-preview-scroll"><article class="report-print-sheet"><header class="report-heading"><h1>{{printReport.title}}</h1><p>生成时间：{{printReport.capturedAt}}</p><p v-for="(line,i) in printReport.scope" :key="i">{{line}}</p></header><figure v-if="printReport.image" class="report-figure"><img :src="printReport.image" :alt="printReport.imageTitle||'报表图形'"><figcaption>{{printReport.imageTitle}}</figcaption></figure><section v-for="(section,i) in printReport.sections" :key="i" class="report-section"><h2>{{section.title}}</h2><table><thead><tr><th v-for="(h,k) in section.headers" :key="k">{{h}}</th></tr></thead><tbody><tr v-for="(row,j) in section.rows" :key="j"><td v-for="(value,k) in row" :key="k">{{value===null||value===undefined?'—':value}}</td></tr><tr v-if="!section.rows.length"><td :colspan="section.headers.length">当前范围没有记录</td></tr></tbody></table><p v-if="section.note" class="report-note">{{section.note}}</p></section><footer class="report-footer">数值与数据状态同时保留；无效、缺测与未配置的值不按零处理。</footer></article></div></section></div></Teleport></template>
<style>
.report-preview-overlay{position:fixed;inset:0;z-index:180;background:#142f3e77;display:grid;place-items:center;padding:24px}
.report-preview-window{width:min(1280px,100%);max-height:calc(100dvh - 48px);display:flex;flex-direction:column;background:#f0f3f4;border-radius:12px;box-shadow:0 20px 80px #14303c44;overflow:hidden}
.report-preview-toolbar{display:flex;align-items:center;justify-content:space-between;gap:20px;background:white;border-bottom:1px solid #dce5e7;padding:16px 24px}
.report-preview-toolbar h2{font-size:18px;margin:0 0 5px;color:#213c49}.report-preview-toolbar span{color:#7f97a1;font-size:12px}.report-preview-toolbar>div:last-child{display:flex;gap:10px;flex-shrink:0}
.report-preview-scroll{overflow:auto;padding:22px;min-height:0}.report-print-sheet{background:white;color:#203b48;padding:28px 32px;margin:auto;max-width:1120px;font-family:'Microsoft YaHei UI','Microsoft YaHei',sans-serif}
.report-heading{text-align:center;margin-bottom:22px}.report-heading h1{font-size:22px;margin:0 0 10px;color:#172f3b}.report-heading p{font-size:12px;margin:5px 0;color:#536e7a;white-space:pre-wrap}
.report-section{margin:18px 0}.report-section h2{font-size:15px;margin:0 0 9px;color:#203b48}.report-section table{width:100%;border-collapse:collapse;font-size:11px;table-layout:auto}.report-section th,.report-section td{padding:7px 8px;border:1px solid #d5dfe3;vertical-align:top;text-align:left;white-space:normal;overflow-wrap:anywhere;line-height:1.5;color:#203b48}.report-section th{background:#eef4f4;font-weight:600}
.report-section thead{display:table-header-group}.report-section tr{break-inside:avoid}.report-note,.report-footer{font-size:10px;color:#627d88;line-height:1.7;margin-top:9px}.report-footer{border-top:1px solid #d5dfe3;padding-top:10px}.report-figure{margin:12px 0;text-align:center}.report-figure img{max-width:100%;max-height:330px;object-fit:contain}.report-figure figcaption{font-size:10px;color:#657d88}
@media print{
 @page{size:A4 landscape;margin:12mm}
 body:has(>.report-preview-overlay)>*{display:none!important}body>.report-preview-overlay{display:block!important;position:static!important;padding:0!important;background:white!important;width:100%!important;place-items:normal!important}
 .report-preview-overlay .report-preview-window{display:block!important;width:100%!important;max-width:none!important;max-height:none!important;overflow:visible!important;border-radius:0;box-shadow:none;background:white}
 .report-preview-overlay .report-preview-toolbar{display:none!important}.report-preview-overlay .report-preview-scroll{padding:0;overflow:visible;max-height:none}
 .report-print-sheet{padding:0;max-width:none;width:100%;margin:0}.report-heading{margin-bottom:4mm}.report-heading h1{font-size:17pt}.report-heading p{font-size:9pt}
 .report-section{margin:4mm 0}.report-section h2{font-size:11pt}.report-section table{font-size:8pt}.report-section th,.report-section td{padding:1.5mm 2mm;color:#000;border-color:#aaa}.report-section th{background:#eee;print-color-adjust:exact}
 .report-figure img{max-height:110mm}.report-figure img[src^="data:image/svg+xml"]{max-height:45mm;max-width:50mm}.report-note,.report-footer{font-size:8pt}
}
</style>
