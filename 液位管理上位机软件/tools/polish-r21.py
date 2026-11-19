from pathlib import Path
root=Path(__file__).resolve().parents[1]
p=root/'web/src/App.vue';s=p.read_text(encoding='utf-8-sig').replace('ATS多点温度计','多点温度计').replace('参与ATS平均温度','参与温度计平均温度');p.write_text(s,encoding='utf-8')
p=root/'web/src/TankDetailScreen.vue';s=p.read_text(encoding='utf-8-sig').replace('<div class="single-primary-metrics">', '<div class="single-primary-metrics" :style="{\'--metric-count\':keyMetrics.length}">');p.write_text(s,encoding='utf-8')
p=root/'web/src/workspace-ui.css';s=p.read_text(encoding='utf-8-sig')+'\n.single-primary-metrics{grid-template-columns:repeat(var(--metric-count,6),minmax(0,1fr))}@media(max-width:1300px){.single-primary-metrics{grid-template-columns:repeat(3,minmax(0,1fr))}}\n';p.write_text(s,encoding='utf-8')
