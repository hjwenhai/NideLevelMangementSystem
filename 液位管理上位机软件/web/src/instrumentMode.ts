import type {LiveReading} from './api';
export function instrumentMode(reading:LiveReading|undefined,valid:boolean,protocol:string|undefined):string{
 if(!valid||reading?.instrumentStatus===null||reading?.instrumentStatus===undefined)return '未知（等待有效状态）';
 const status=reading.instrumentStatus,mode=status&(protocol==='IfxL25'?7:0x01ff);
 const names:Record<number,string>=protocol==='IfxL25'?{1:'测量Ⅰ',2:'上升',3:'下降',4:'测量Ⅱ（界面）',5:'停止',6:'测量Ⅲ（罐底）',7:'远程密度'}:{1:'测量Ⅰ',2:'上升',4:'停止',8:'重复性检查',16:'测量Ⅱ（界面）',32:'测量Ⅲ（罐底）',64:'远程密度',128:'液体震荡回避',256:'下降'};
 return names[mode]||'未知（状态码 '+status+'）';
}
