from pathlib import Path
import shutil,json,hashlib
R=Path(__file__).resolve().parents[1];B=R/'output/r23-layout-fix_20261114';B.mkdir(exist_ok=True)
files=['web/src/App.vue','web/src/HistoryRecords.vue','web/src/customer-r23.css']
for rel in files:
 dst=B/'before'/rel;dst.parent.mkdir(parents=True,exist_ok=True)
 if not dst.exists():shutil.copy2(R/rel,dst)
p=R/'web/src/App.vue';s=p.read_text(encoding='utf-8');assert '紧凑（最多8列）' in s
s=s.replace('紧凑（最多8列）','紧凑').replace("{'sidebar-is-collapsed':sidebarCollapsed}","{'sidebar-is-collapsed':sidebarCollapsed,'records-shell':['history','events'].includes(view)}")
s=s.replace("'events-main':view==='events'","'events-main':view==='events','records-main':['history','events'].includes(view)")
needle='<section class="panel"><div class="table-wrap"><table><thead><tr><th>时间</th><th>类型</th><th>储罐</th><th>内容</th>'
assert needle in s;s=s.replace(needle,needle.replace('class="panel"','class="panel event-results"'),1)
p.write_text(s,encoding='utf-8')
p=R/'web/src/HistoryRecords.vue';s=p.read_text(encoding='utf-8')
old='<DataJobs ref="jobs" kind="HistoryExport" title="历史导出任务"/>'
assert old in s;s=s.replace(old,'<details class="history-export-tasks"><summary>历史导出任务</summary>'+old+'</details>')
p.write_text(s,encoding='utf-8')
p=R/'web/src/customer-r23.css';s=p.read_text(encoding='utf-8')
old='@media(min-width:1850px){.sidebar-is-collapsed .tank-grid.card-size-compact{grid-template-columns:repeat(8,minmax(0,1fr))}}@media(min-width:1750px) and (max-width:1849px){.sidebar-is-collapsed .tank-grid.card-size-compact{grid-template-columns:repeat(7,minmax(0,1fr))}}'
assert old in s;s=s.replace(old,'')
s+='''
/* r23 20261114: size by the actual grid width, including Windows/browser scaling. */
.overview-main .tank-grid.card-size-compact{gap:8px;grid-template-columns:repeat(auto-fill,minmax(min(100%,max(182px,calc((100% - 56px)/8))),1fr))}
.overview-main .tank-grid.card-size-standard{gap:12px;grid-template-columns:repeat(auto-fill,minmax(min(100%,max(245px,calc((100% - 60px)/6))),1fr))}
.overview-main .tank-grid.card-size-large{gap:16px;grid-template-columns:repeat(auto-fill,minmax(min(100%,max(330px,calc((100% - 48px)/4))),1fr))}
.overview-card{container-type:inline-size}
.card-size-compact .overview-card{padding:9px}.card-size-compact .overview-card .engineering-tank-content{grid-template-columns:100px minmax(0,1fr);gap:2px}
.card-size-compact .overview-card .overview-level{display:block;font-size:clamp(16px,10cqi,21px);line-height:1.3}
.card-size-compact .overview-card .overview-key-values h2{font-size:clamp(15px,9cqi,18px)}
.card-size-compact .overview-card .overview-card-footer{margin:0 -9px -9px;padding:8px 9px}
.card-size-standard .overview-card{padding:14px}.card-size-standard .overview-card .engineering-tank-content{grid-template-columns:120px minmax(0,1fr);gap:6px;min-height:195px;margin:10px 0}
.card-size-standard .overview-card .engineering-tank-content>.tank-graphic{width:120px;height:190px}
.card-size-standard .overview-card .overview-level{display:block;font-size:clamp(20px,10cqi,26px);white-space:nowrap;letter-spacing:-.5px}
.card-size-standard .overview-card .overview-key-values h2{font-size:20px}.card-size-standard .overview-card .overview-key-values .overview-average{font-size:19px}
.card-size-standard .overview-card .overview-card-footer{margin:0 -14px -14px;padding:10px 14px}.card-size-standard .overview-card-inventory strong{font-size:16px;white-space:nowrap}
.card-size-large .overview-card{padding:18px}.card-size-large .overview-card .engineering-tank-content{grid-template-columns:170px minmax(0,1fr);gap:10px;min-height:240px}
.card-size-large .overview-card .engineering-tank-content>.tank-graphic{width:170px;height:235px}.card-size-large .overview-card .overview-level{display:block;font-size:clamp(24px,9cqi,32px);white-space:nowrap;letter-spacing:-.6px}

/* The page has one fixed-height workspace; only result rows scroll vertically. */
body:has(.records-shell){overflow:hidden}
.app-shell.records-shell{height:100vh;height:100dvh;min-height:0;overflow:hidden}
.records-shell>.topbar{flex:none}
main.records-main{display:flex;flex-direction:column;flex:1;min-height:0;overflow:hidden;padding:16px 24px}
.records-main>.page-heading{flex:none;min-height:38px;margin:0 0 12px}.records-main>.page-heading .eyebrow,.records-main>.page-heading .subtitle{display:none}.records-main>.page-heading h1{font-size:27px;line-height:38px}
.records-main>.notice,.records-main>.toolbar{flex:none}.records-main>.toolbar{margin:0 0 12px}.records-main>.toolbar .tabs{margin-bottom:0}
.records-main .query-panel{flex:none;padding:16px;margin:0 0 12px}.records-main .query-fields{gap:12px;grid-template-columns:repeat(5,minmax(0,1fr))}
.records-main .config-field input,.records-main .config-field select{min-width:0;padding:8px 10px;min-height:36px}.records-main .config-field{gap:6px}
.records-main .query-actions,.records-main .history-query .inline-actions{margin-top:10px}.records-main .history-query>.query-fields{margin-bottom:10px}
.history-screen{display:flex;flex-direction:column;flex:1;min-height:0;overflow:hidden;gap:0}
.history-screen>.notice{flex:none}
.history-screen .history-data-items{margin:8px 0;padding-top:8px}.history-screen .history-item-options{gap:8px 16px}
.records-main .history-results,.records-main .event-results{display:flex;flex-direction:column;flex:1;min-height:0;margin:0;overflow:hidden}
.records-main .history-results>.toolbar,.records-main .history-results>.history-ats-option,.records-main .event-results>.table-foot{flex:none}
.records-main .history-results>.toolbar{padding:10px 14px;gap:10px;flex-wrap:wrap}.records-main .history-results>.toolbar>div{display:flex;gap:8px;flex-wrap:wrap}
.records-main .history-results>.table-wrap,.records-main .event-results>.table-wrap{flex:1;min-height:0;max-height:none;overflow:auto;overscroll-behavior:contain}
.records-main .history-results thead th,.records-main .event-results thead th{position:sticky;top:0;z-index:2;background:#f5f9fa}
.history-export-tasks{flex:none;margin-top:10px;border:1px solid #e3ebee;border-radius:9px;background:white;max-height:30vh;overflow:auto}
.history-export-tasks>summary{padding:10px 14px;font-weight:600;color:#2d5961;cursor:pointer;position:sticky;top:0;background:white;z-index:3}
.history-export-tasks>.panel{margin:0;border:0}.history-export-tasks .table-wrap{max-height:18vh;overflow:auto}.history-export-tasks .table-foot{font-size:10px}
@media(max-width:1100px){.records-main .query-fields{grid-template-columns:repeat(3,minmax(0,1fr))}.records-main .history-results>.toolbar>span{font-size:11px}.records-main .button{padding:8px 10px}}
@media(max-height:700px){.records-main .query-panel{padding:10px;max-height:215px;overflow:auto}.records-main>.page-heading{margin-bottom:6px}.records-main>.toolbar{margin-bottom:6px}.history-screen .history-data-items{margin:5px 0}}
'''
p.write_text(s,encoding='utf-8')
print('Applied r23 layout fix')
