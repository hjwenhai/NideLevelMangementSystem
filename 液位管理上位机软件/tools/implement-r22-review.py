from pathlib import Path
import zipfile
r=Path(__file__).resolve().parents[1]
snapshot=r/'output/r22-review-20261107/r21-source-before-r22.zip';snapshot.parent.mkdir(parents=True,exist_ok=True)
assert not snapshot.exists()
with zipfile.ZipFile(snapshot,'x',zipfile.ZIP_DEFLATED) as z:
 for folder in ['src','web','tests','tools','deployment','assets']:
  for p in (r/folder).rglob('*'):
   if p.is_file() and not any(x in p.relative_to(r).parts for x in ['bin','obj','node_modules','wwwroot']):z.write(p,p.relative_to(r))
p=r/'web/src/TankDetailScreen.vue';s=p.read_text(encoding='utf-8-sig')
s=s.replace('<th>故障编码</th><th>含义</th><th>软件首次检测</th>','<th>表头故障码</th><th>故障编码</th><th>含义</th><th>软件首次检测</th>')
s=s.replace("<td>{{fault.displayCode||'—'}}<small>{{fault.code}}</small></td>","<td>{{fault.displayCode||'—'}}</td><td>{{fault.code}}</td>")
s=s.replace("headers:['故障编码','含义','软件首次检测时间']","headers:['表头故障码','故障编码','含义','软件首次检测']")
s=s.replace("[f.displayCode||'—',f.description+'（'+f.code+'）',date(f.firstDetectedAtUtc)]","[f.displayCode||'—',f.code,f.description,date(f.firstDetectedAtUtc)]")
s=s.replace("?'温度计实时平均温度':'最近任务平均温度'","?'实时平均温度':'最近任务平均温度'")
p.write_text(s,encoding='utf-8')
p=r/'web/src/TrendBoard.vue';s=p.read_text(encoding='utf-8-sig')
s=s.replace("import {minimumAxisBounds} from './trendAxis';","import {minimumAxisBounds} from './trendAxis';\nimport {calendarRange,type CalendarPeriod} from './calendarRange';")
old="function quickRange(days:number){end.value=localInput(Date.now());start.value=localInput(Date.now()-days*86400000);void load(true)}"
new="function quickRange(period:CalendarPeriod){const range=calendarRange(period);end.value=localInput(range.end.getTime());start.value=localInput(range.start.getTime());void load(true,{startUtcMs:range.start.getTime(),endUtcMs:range.end.getTime()})}"
assert old in s;s=s.replace(old,new)
s=s.replace('async function load(reset=false){','async function load(reset=false,range?:{startUtcMs:number;endUtcMs:number}){')
s=s.replace("new Date(start.value).getTime(),endUtcMs:mode.value==='realtime'?1:new Date(end.value).getTime()","(range?.startUtcMs??new Date(start.value).getTime()),endUtcMs:mode.value==='realtime'?1:(range?.endUtcMs??new Date(end.value).getTime())")
s=s.replace("[[1,'最近24小时'],[7,'最近7天'],[30,'最近30天']]","[['day','当日'],['week','本周'],['month','本月']]")
s=s.replace('@click="quickRange(Number(d))"','@click="quickRange(d as CalendarPeriod)" :title="label===\'当日\'?\'今日00:00至当前\':label===\'本周\'?\'本周星期一00:00至当前\':\'本月1日00:00至当前\'"')
p.write_text(s,encoding='utf-8')
p=r/'web/src/App.vue';s=p.read_text(encoding='utf-8-sig')
s=s.replace('<small>瞬时温度</small>','<small>温度</small>').replace("?'ATS平均温度':'最近平均温度'","?'实时平均温度':'最近平均温度'")
a=s.index('<label class="config-field">计算密度来源');b=s.index('<div class="wide-field editor-section"><h3>4～20mA压力输入',a)
old=s[a:b];source=old[:old.index('<label class="checkbox">')];checkbox=old[old.index('<label class="checkbox">'):old.index('<label class="config-field">人工水体积')];water=old[old.index('<label class="config-field">人工水体积'):old.index('<label class="config-field">20℃标准密度')];standard=old[old.index('<label class="config-field">20℃标准密度'):]
s=s[:a]+'<div class="wide-field editor-section meter-density-group"><h3>计算密度</h3><div class="config-fields">'+source+checkbox.replace('class="checkbox"','class="checkbox wide-field"')+'</div><p class="help">质量和静压力修正采用所选来源；人工工况密度可独立填写，切换来源不会清除原值。</p></div>\n<div class="wide-field editor-section meter-reference-group"><h3>扣水与参考资料</h3><div class="config-fields">'+water+standard+'</div><p class="help">人工水体积用于扣水。20℃标准密度独立保留，不代替所选工况密度。</p></div>\n'+s[b:]
s=s.replace('<p>水体积、20℃标准密度及压力输入。</p>','<p>按计算密度、扣水与参考资料、压力输入分别配置。</p>')
p.write_text(s,encoding='utf-8')
p=r/'web/src/InstrumentControls.vue';s=p.read_text(encoding='utf-8-sig').replace('<style scoped>','<style scoped>\n.instrument-controls .control-buttons .button{font-size:14px;line-height:1.5}\n')
p.write_text(s,encoding='utf-8')
p=r/'src/LevelManager.Host/Program.cs';s=p.read_text(encoding='utf-8-sig').replace('revision="20261106-r21"','revision="20261107-r22"');p.write_text(s,encoding='utf-8')
print('r22 explicit UI requests applied; trend axis policy, workbook borders and group multi-selection workflow remain for discussion')
