from pathlib import Path
root=Path(__file__).resolve().parents[1]
def edit(name,fn):
 p=root/name;s=p.read_text(encoding='utf-8-sig');p.write_text(fn(s),encoding='utf-8-sig' if name.endswith('.ps1') else 'utf-8')
def replace(s,a,b):
 if a not in s:raise Exception('missing anchor '+a[:100])
 return s.replace(a,b)
edit('src/LevelManager.Host/Program.cs',lambda s:s.replace('20261102-r20','20261106-r21').replace('return Results.Ok(CapacityExcel.Preview(memory.ToArray(),Number("sheet",0),Number("firstRow",2),Number("heightColumn",1),Number("volumeColumn",2),ct));','return Results.Ok(ctx.Request.Query["kind"]=="Decimal" ? CapacityExcel.PreviewDecimal(memory.ToArray(),Number("sheet",0),Number("firstRow",2),ct) : CapacityExcel.Preview(memory.ToArray(),Number("sheet",0),Number("firstRow",2),Number("heightColumn",1),Number("volumeColumn",2),ct));'))
edit('src/LevelManager.Infrastructure/SqliteCapacityTableStore.cs',lambda s:s.replace('Points = Array.AsReadOnly(table.Points.ToArray())','Points = Array.AsReadOnly(table.Points.ToArray()), DecimalRows = Array.AsReadOnly(table.DecimalRows.ToArray())').replace('next.Points.Count','next.Points.Count + next.DecimalRows.Count'))
edit('src/LevelManager.Infrastructure/SqliteRecordBasisStore.cs',lambda s:s.replace('table.Points.Count,table.ReferenceDensityKgM3','table.Points.Count+table.DecimalRows.Count,table.ReferenceDensityKgM3').replace('小数表只在已确认组合规则后参与，当前不参与计算。','基础体积按该版本的主表插值或主表下节点＋小数增量计算，两者不叠加；不足1mm按所选规则处理。密度取该版本配置的人工工况或仪表实测值，20℃标准密度独立保留。'))
edit('web/src/api.ts',lambda s:s.replace('instrumentFaults:{code:string;','instrumentFaults:{displayCode:string|null;code:string;').replace('standardDensity20KgM3:number|null}','standardDensity20KgM3:number|null;manualOperatingDensityKgM3:number|null;densitySource:"Instrument"|"Manual";showInstrumentDensity:boolean;baseVolumeMethod:"Interpolate"|"MainPlusDecimal";fractionalMillimetreRule:"Unconfirmed"|"Linear"|"IntegerOnly"}').replace('points:{height:number;volume:number}[]}','points:{height:number;volume:number}[];decimalRows:DecimalCapacityRow[]}').replace('export interface CapacitySummary','export interface DecimalCapacityRow {startHeightMm:number;endHeightMm:number;number:number;centimetreLitres:number;millimetreLitres:number}\nexport interface CapacitySummary'))
def app(s):
 s=s.replace('V0.3.0 · r20','V0.3.0 · r21')
 s=s.replace('standardDensity20KgM3:null}}','standardDensity20KgM3:null,manualOperatingDensityKgM3:null,densitySource:"Instrument",showInstrumentDensity:true,baseVolumeMethod:"Interpolate",fractionalMillimetreRule:"Linear"}}')
 s=replace(s,'function acceptTank(){','function copyTank(t:Tank){if((config.value?.tanks.length||0)>=128){error.value="最多配置128个罐";return}tankDraft.value=clone(t);Object.assign(tankDraft.value,{id:crypto.randomUUID(),code:"",name:(t.name||t.code)+"（副本）",instrumentTag:"",productNumber:"",address:0,enabled:false,hidden:false,pinnedAtUtc:null});tankDraft.value.metering={...tankDraft.value.metering,enabled:false,mainTableId:null,staticPressureTableId:null,decimalTableId:null};message.value="已复制为停用草稿，请填写新罐号、仪表地址及本罐罐容表后启用。"}\nfunction acceptTank(){')
 s=replace(s,'@click="tankDraft=clone(t)">编辑</button>','@click="tankDraft=clone(t)">编辑</button><button v-if="canConfigure" class="text-button" @click="copyTank(t)">复制</button>')
 s=s.replace(':class="{active:view===item[0]}" @click="choose(item[0])"',':class="{active:view===item[0],\'alarm-attention\':item[0]===\'alarms\'&&(alarmSummary?.unacknowledged||0)>(alarmSummary?.recoveredUnacknowledged||0),\'alarm-present\':item[0]===\'alarms\'&&(alarmSummary?.active||0)>0}" @click="choose(item[0])"')
 s=replace(s,'<label class="config-field">人工水体积 m³', '''<label class="config-field">计算密度来源<select v-model="tankDraft.metering.densitySource" aria-label="计算密度来源"><option value="Instrument">仪表实测密度</option><option value="Manual">人工工况密度</option></select></label><label class="config-field">人工工况密度 kg/m³<input :value="tankDraft.metering.manualOperatingDensityKgM3??''" aria-label="人工工况密度" type="number" step="any" min="0.001" max="1000000" @input="tankDraft!.metering.manualOperatingDensityKgM3=($event.target as HTMLInputElement).value?Number(($event.target as HTMLInputElement).value):null"></label><label class="checkbox"><input v-model="tankDraft.metering.showInstrumentDensity" type="checkbox">显示仪表实测密度（仍采集并保存历史）</label><label class="config-field">人工水体积 m³''')
 s=s.replace('质量使用仪表实测密度。20℃标准密度独立保存，不代替实测密度。','质量与静压力修正使用所选工况密度；来源无效不自动替换。20℃标准密度独立保存。')
 s=s.replace('小数表（暂未参与计算）','小数表')
 s=replace(s,'<p class="help wide-field">当前体积＝主表容积＋静压力修正量×仪表实测密度÷参考密度。选择已校核并启用的表；小数表暂不参与计算。</p>','''<label class="config-field">基础体积算法<select v-model="tankDraft.metering.baseVolumeMethod" aria-label="基础体积算法"><option value="Interpolate">主表插值（不叠加小数表）</option><option value="MainPlusDecimal">主表下节点＋小数表增量</option></select></label><p class="help wide-field">组合模式须关联五列分区小数表，不足1mm按相邻毫米线性插值。静压力修正＝表量×所选工况密度÷参考密度，主表与小数表不重复插值。</p>''')
 s=s.replace("{key:'density',label:'仪表密度 '+unitName('density'),digits:1}","{key:'density',label:'仪表实测密度 '+unitName('density'),digits:1},{key:'manualDensity',label:'人工工况密度 '+unitName('density'),digits:1}")
 s=s.replace("density:value(r?.instrumentDensityKgM3,'density'),volume:","density:t.metering.showInstrumentDensity===false?null:value(r?.instrumentDensityKgM3,'density'),manualDensity:t.metering.manualOperatingDensityKgM3&&config.value?converted(t.metering.manualOperatingDensityKgM3,'density',config.value.display):null,volume:")
 # Card source label is explicit; hide measured value only where requested.
 s=s.replace('仪表密度 <b>{{quantity(reading(t.id),reading(t.id)?.instrumentDensityKgM3,\'density\')}}</b>','仪表密度 <b>{{quantity(reading(t.id),reading(t.id)?.instrumentDensityKgM3,\'density\')}}</b>')
 return s
edit('web/src/App.vue',app)
def detail(s):
 s=s.replace("{label:'瞬时温度 · IR26'","{label:'温度'")
 s=s.replace("{label:'仪表实测密度',value:value(props.reading?.instrumentDensityKgM3,'density'),unit:unit('density',props.config.display),status:quality(props.reading?.instrumentDensityKgM3)},","...(props.tank.metering.showInstrumentDensity===false?[]:[{label:'仪表实测密度',value:value(props.reading?.instrumentDensityKgM3,'density'),unit:unit('density',props.config.display),status:quality(props.reading?.instrumentDensityKgM3)}]),\n {label:'人工工况密度',value:props.tank.metering.manualOperatingDensityKgM3?converted(props.tank.metering.manualOperatingDensityKgM3,'density',props.config.display).toLocaleString('zh-CN',{maximumFractionDigits:3}):'—',unit:unit('density',props.config.display),status:props.tank.metering.densitySource==='Manual'?'用于计算':'独立输入'},")
 s=s.replace("...[1,2].map(i=>", "...[1,2].filter(i=>props.tank[i===1?'pressure1':'pressure2'].enabled).map(i=>")
 s=s.replace('displayMetric,unit,','displayMetric,converted,unit,')
 s=s.replace('<td>{{fault.code}}</td>','<td>{{fault.displayCode||\'—\'}}<small>{{fault.code}}</small></td>').replace('f.code,f.description','f.displayCode||\'—\',f.description+\'（\'+f.code+\'）\'')
 return s
edit('web/src/TankDetailScreen.vue',detail)
for p in ['web/src/App.vue','web/src/TankDetail.vue','web/src/TankDetailScreen.vue','web/src/RecordBasisDialog.vue','web/src/HistoryData.vue','web/src/units.ts']:
 if not (root/p).exists():continue
 def labels(s):
  for a,b in [('ATS固定测点温度','温度计'),('ATS固定点温度','温度计'),('ATS固定点','固定点温度计'),('ATS测点温度','温度计'),('ATS实时平均','温度计实时平均'),('ATS点数','温度计点数'),('ATS第','温度计第'),('瞬时温度 · IR26','温度'),('瞬时温度 ℃','温度 ℃'),('瞬时温度 °C','温度 °C')]:s=s.replace(a,b)
  return s
 edit(p,labels)
edit('web/src/RecordBasisDialog.vue',lambda s:s.replace("<dt>人工水体积", "<dt>人工水体积"))
edit('web/src/workspace-ui.css',lambda s:s+'''
/* r21: readable detail metrics and alarm acknowledgement attention. */
.single-primary-metrics{grid-template-columns:repeat(6,minmax(0,1fr))}
.single-primary-metric strong{font-size:30px}.single-primary-metric small,.single-extra-metric small{font-size:14px}
.single-extra-metric strong{font-size:24px}.single-detail-tabs button,.single-vessel-panel .button,.single-vessel-caption{font-size:14px}
.single-primary-metric .metric-quality{font-size:13px}.single-detail-screen{font-size:14px}
.sidebar nav button.alarm-present{color:#f2a89a}.sidebar nav button.alarm-attention .ui-icon{color:#ff8c7b;animation:alarm-menu-pulse 1.4s ease-in-out infinite}
@keyframes alarm-menu-pulse{0%,100%{opacity:1}50%{opacity:.3}}
@media(prefers-reduced-motion:reduce){.sidebar nav button.alarm-attention .ui-icon{animation:none;color:#ff8c7b}}
''')
