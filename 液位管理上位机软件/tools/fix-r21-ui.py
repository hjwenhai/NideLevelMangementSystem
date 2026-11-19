from pathlib import Path
root=Path(__file__).resolve().parents[1]
p=root/'web/src/CapacityTables.vue';s=p.read_text(encoding='utf-8');s=s.replace("{{draft.kind==='Decimal'?'高度余数':'液位高度'}}",'液位高度');p.write_text(s,encoding='utf-8')
p=root/'web/src/TankRealtimeTrend.vue';s=p.read_text(encoding='utf-8').replace('minimumAxisBounds(latest.series','minimumAxisBounds(latest!.series');p.write_text(s,encoding='utf-8')
