from pathlib import Path
root=Path(__file__).resolve().parents[1]
def edit(name,old,new):
 p=root/name;s=p.read_text(encoding='utf-8-sig');assert old in s,(name,old[:100]);p.write_text(s.replace(old,new),encoding='utf-8')
edit('web/src/TankConfigurationTransfer.vue','issues:string[];changedTanks:string[];note:string','issues:string[];changedTanks:string[];differences:{tankCode:string;field:string;before:string;after:string}[];note:string')
edit('web/src/TankConfigurationTransfer.vue',"fileName=ref('');","fileName=ref(''),reviewTank=ref('');")
edit('web/src/TankConfigurationTransfer.vue',"preview.value=await upload('/api/tank-configuration/import-preview',file)","preview.value=await upload('/api/tank-configuration/import-preview',file);reviewTank.value=preview.value?.changedTanks[0]||''")
edit('web/src/TankConfigurationTransfer.vue','· 更新 {{preview.updated}} 罐','· 已有 {{preview.updated}} 罐')
edit('web/src/TankConfigurationTransfer.vue','<p class="table-associations">{{preview.changedTanks.join(\'、\')}}</p>','<p class="import-tank-codes">{{preview.changedTanks.join(\'、\')}}</p><template v-if="preview.differences.length"><label class="config-field">查看储罐变更<select v-model="reviewTank" aria-label="查看储罐导入变更"><option v-for="code in preview.changedTanks" :key="code" :value="code">{{code}}</option></select></label><div class="table-wrap import-differences"><table><thead><tr><th>字段</th><th>原值</th><th>导入值</th></tr></thead><tbody><tr v-for="(difference,n) in preview.differences.filter(d=>d.tankCode===reviewTank)" :key="n"><td>{{difference.field}}</td><td>{{difference.before}}</td><td>{{difference.after}}</td></tr><tr v-if="!preview.differences.some(d=>d.tankCode===reviewTank)"><td colspan="3">本罐没有字段变化</td></tr></tbody></table></div></template><p v-else class="help">本次导入各字段与当前配置一致。</p>')
edit('web/src/HistoryRecords.vue',"if(!next)applied=new URLSearchParams(","const requested=next?applied:new URLSearchParams(")
edit('web/src/HistoryRecords.vue',"const query=new URLSearchParams(applied);if(next", "const query=new URLSearchParams(requested);if(next")
edit('web/src/HistoryRecords.vue',"page.value=await api('/api/history?'+query)","const loaded=await api<HistoryPage>('/api/history?'+query);applied=requested;page.value=loaded")
# Template header and identification columns stay visible; instructions wrap at a readable width.
p=root/'src/LevelManager.Infrastructure/TankConfigurationExcel.cs';s=p.read_text(encoding='utf-8')
s=s.replace('new SheetView(new Pane{State=PaneStateValues.Frozen,VerticalSplit=1,TopLeftCell="A2",ActivePane=PaneValues.BottomLeft}){WorkbookViewId=0}', 'new SheetView(new Pane{State=PaneStateValues.Frozen,VerticalSplit=1,HorizontalSplit=name=="罐体与通讯"?2:1,TopLeftCell=name=="罐体与通讯"?"C2":"B2",ActivePane=PaneValues.BottomRight}){WorkbookViewId=0,ShowGridLines=false}')
s=s.replace('new Columns(new Column{Min=1,Max=80,Width=24,CustomWidth=true}),data)', 'new Columns(new Column{Min=1,Max=1,Width=name=="罐体与通讯"?42:24,CustomWidth=true},new Column{Min=2,Max=80,Width=name=="填写说明"?110:24,CustomWidth=true}),data)')
s=s.replace('Height=at==1?38:22','Height=at==1?42:name=="填写说明"?66:22')
s=s.replace('new CellFormat(),new CellFormat{FontId=1','new CellFormat{Alignment=new Alignment{Vertical=VerticalAlignmentValues.Center}},new CellFormat{FontId=1')
s=s.replace('Vertical=VerticalAlignmentValues.Center}}));styles.Stylesheet.Save();','Vertical=VerticalAlignmentValues.Center}},new CellFormat{Alignment=new Alignment{WrapText=true,Vertical=VerticalAlignmentValues.Top}}));styles.Stylesheet.Save();')
s=s.replace('StyleIndex=at==1?1u:0u','StyleIndex=at==1?1u:name=="填写说明"?2u:0u')
p.write_text(s,encoding='utf-8')
p=root/'web/src/workspace-ui.css';s=p.read_text(encoding='utf-8');s+='\n.configuration-transfer .import-tank-codes{max-width:none;overflow-wrap:anywhere;line-height:1.8}.import-differences{max-height:34vh;overflow:auto;margin-top:12px}.import-differences td{max-width:360px;overflow-wrap:anywhere}.configuration-transfer .config-field select{max-width:360px}\n';p.write_text(s,encoding='utf-8')
print('r10 review polish applied')
