from pathlib import Path
root=Path(__file__).resolve().parents[1]
p=root/'tests/scheduled-average-check.cjs';s=p.read_text(encoding='utf-8');s=s.replace("const tomorrowDue=new Date(Date.now()+60000);cfg.schedules.push({...cfg.schedules[0],id:crypto.randomUUID(),name:'密码变化撤销授权验证'","const tomorrowDue=new Date(Date.now()+60000),passwordScheduleId=crypto.randomUUID();cfg.schedules.push({...cfg.schedules[0],id:passwordScheduleId,name:'密码变化撤销授权验证'")
s=s.replace("j.id!==first.id&&j.state==='Failed'","j.requestJson?.includes(passwordScheduleId)&&j.state==='Failed'")
p.write_text(s,encoding='utf-8')
p=root/'tests/native-soak-check.cjs';s=p.read_text(encoding='utf-8').replace('候选包_0.3.0-zh-test.1_r9','候选包_0.3.0-zh-test.1_r10').replace('LEVEL_SOAK_SECONDS||259200','LEVEL_SOAK_SECONDS||21600').replace('durationSeconds:duration,startedAtUtc:new Date(started).toISOString(),','durationSeconds:duration,startedAtUtc:new Date(started).toISOString(),targetAtUtc:new Date(started+duration*1000).toISOString(),plannedRestartAtSeconds:duration<600?120:duration<=21600?Math.floor(duration/2):21600,')
p.write_text(s,encoding='utf-8')
p=root/'tests/native-package-check.cjs';s=p.read_text(encoding='utf-8').replace('候选包_0.3.0-zh-test.1_r9','候选包_0.3.0-zh-test.1_r10');p.write_text(s,encoding='utf-8')
p=root/'tests/customer-docs-check.cjs';s=p.read_text(encoding='utf-8');s=s.replace("out=path.join(root,'output/p7-customer-docs-verification')","out=path.join(root,process.env.LEVEL_VERIFICATION_DIR||'output/p7-customer-docs-verification')");p.write_text(s,encoding='utf-8')
p=root/'tests/priority-r10-check.cjs';s=p.read_text(encoding='utf-8')
needle="checks.push('修改Excel档案后真实更新；预览令牌绑定账号且不可重复提交');"
assert needle in s
s=s.replace(needle,needle+"\n await page.getByLabel('导入储罐配置文件',{exact:true}).setInputFiles(file);await until(()=>page.locator('.import-differences').count().then(Boolean));assert(await page.locator('.import-differences').getByText('Excel修改后的储罐',{exact:true}).isVisible());await page.screenshot({path:path.join(out,'03b-import-field-differences.png')});await page.getByRole('checkbox',{name:'已核对储罐、通讯绑定、报警、计量及允许操作，确认保存本次配置',exact:true}).check();await page.getByRole('button',{name:'确认导入并保存',exact:true}).click();await until(async()=> (await admin.call('/api/config')).tanks[0].name==='岗位授权后修改');cfg=await admin.call('/api/config');checks.push('导入预览逐罐展示字段原值与新值，经画面确认后实际保存');")
p.write_text(s,encoding='utf-8')
print('r10 release test routing updated, default soak 6h with midpoint restart')
