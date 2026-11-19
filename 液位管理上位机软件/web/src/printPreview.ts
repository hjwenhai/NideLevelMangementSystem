import {shallowRef} from 'vue';
import {can} from './api';

export interface PrintSection {title:string;headers:string[];rows:unknown[][];note?:string}
export interface PrintReport {title:string;scope:string[];sections:PrintSection[];image?:string;imageTitle?:string;readPermission?:string;capturedAt?:string}
export const printReport=shallowRef<PrintReport|null>(null);
export function previewPrint(report:PrintReport){
 if(!can('DataExport')||report.readPermission&&!can(report.readPermission))return;
 // Freeze visible data, conditions and values. Polling cannot change an open preview.
 printReport.value=JSON.parse(JSON.stringify({...report,capturedAt:new Date().toLocaleString('zh-CN',{hour12:false})}));
}
export function closePrintPreview(){printReport.value=null}
