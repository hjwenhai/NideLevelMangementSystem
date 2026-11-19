from pathlib import Path
root=Path(__file__).resolve().parents[1]
p=root/'web/src/RecordBasisDialog.vue';s=p.read_text(encoding='utf-8');s=s.replace('仪表当时实测密度；有效数据才参与',"{{basis.metering.densitySource==='Manual'?'人工工况密度 '+basis.metering.manualOperatingDensityKgM3+' kg/m³':'仪表当时实测密度'}}；有效数据才参与")
s=s.replace('<h3>当时关联的罐容表版本</h3>',"<p class=\"help\">基础体积算法：{{basis.metering.baseVolumeMethod==='MainPlusDecimal'?'主表下节点＋小数增量':'主表插值'}}；不足1mm：{{basis.metering.fractionalMillimetreRule==='IntegerOnly'?'仅整数毫米':'相邻毫米线性插值'}}</p><h3>当时关联的罐容表版本</h3>")
s=s.replace('共{{table.points.length}}组','共{{(table.decimalRows?.length||0)+table.points.length}}组')
s=s.replace('<table><thead><tr><th>高度（','''<table v-if="table.kind==='Decimal'&&(table.decimalRows?.length||0)>0"><thead><tr><th>起始mm</th><th>结束mm</th><th>序号</th><th>厘米增量L</th><th>毫米增量L</th></tr></thead><tbody><tr v-for="(r,i) in table.decimalRows.slice(0,20)" :key="i"><td>{{r.startHeightMm}}</td><td>{{r.endHeightMm}}</td><td>{{r.number}}</td><td>{{r.centimetreLitres}}</td><td>{{r.millimetreLitres}}</td></tr></tbody></table><table v-else><thead><tr><th>高度（''')
p.write_text(s,encoding='utf-8')
# User-facing terminology changes only; protocol identifiers stay Ats/IR26.
for p in (root/'web/src').glob('*'):
 if p.suffix not in ['.vue','.ts']:continue
 s=p.read_text(encoding='utf-8-sig')
 for a,b in [('ATS第','温度计第'),('ATS测点','温度计测点'),('ATS固定点','固定点温度计'),('ATS点数','温度计点数'),('ATS与无线浮子','温度计与无线浮子'),('瞬时温度 ℃ · IR26','温度 ℃'),('瞬时温度 · IR26','温度'),('仪表IR26。','仪表温度。')]:s=s.replace(a,b)
 p.write_text(s,encoding='utf-8')
