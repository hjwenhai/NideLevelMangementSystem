from pathlib import Path
p=Path(__file__).resolve().parents[1]/'tests/r21-transfer-roundtrip.cjs'
s=p.read_text(encoding='utf-8-sig').replace('referenceDensityKgM3:1000,points:','referenceDensityKgM3:998.2,points:')
start=s.index(' // An exported native table')
end=s.index(" assert.equal((await call('/api/acquisition')).running",start)
s=s[:start]+''' // Import each exported workbook into a fresh draft and actually save it. No unit inference.
 await page.getByRole('button',{name:'基础数据',exact:true}).click();
 for(const original of tables){
  await page.getByRole('button',{name:'新增罐容表',exact:true}).click();const d=page.getByRole('dialog',{name:'编辑罐容表'});await d.waitFor();
  await d.getByLabel('表类型',{exact:true}).selectOption(original.kind);
  await d.getByLabel('表编号 *',{exact:true}).fill(original.number+'-IMPORTED');await d.getByLabel('表名称 *',{exact:true}).fill(original.name+'重新导入');
  const reply=page.waitForResponse(r=>r.url().includes('/api/capacity-tables/import-preview'));
  await d.locator('input[type="file"]').setInputFiles({name:original.number+'.xlsx',mimeType:'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',buffer:exported[original.kind]});
  assert.equal((await reply).status(),200);await until(async()=>(await d.locator('textarea').inputValue()).length>0);
  assert.equal(await d.getByLabel('高度单位',{exact:true}).inputValue(),original.heightUnit);assert.equal(await d.getByLabel('容积单位',{exact:true}).inputValue(),original.volumeUnit);
  if(original.kind==='StaticPressure')assert.equal(await d.getByLabel('参考密度 kg/m³',{exact:true}).inputValue(),String(original.referenceDensityKgM3));
  await d.getByRole('button',{name:'预览校核',exact:true}).click();const save=page.waitForResponse(r=>r.url().endsWith('/api/capacity-tables')&&r.request().method()==='POST');await d.getByRole('button',{name:'校核并保存',exact:true}).click();const response=await save;assert.equal(response.status(),200);await d.waitFor({state:'hidden'});
  const newSummary=(await call('/api/capacity-tables')).find(t=>t.number===original.number+'-IMPORTED');assert(newSummary);const restored=await call('/api/capacity-tables/'+newSummary.id);
  for(const field of ['kind','heightUnit','volumeUnit','points','decimalRows'])assert.deepEqual(restored[field],original[field],original.kind+': '+field);
  if(original.kind==='StaticPressure')assert.equal(restored.referenceDensityKgM3,998.2);
  checks.push(original.kind+'：页面从新建导入→核对单位/参考密度→校核→保存→读回，每项与原表一致');
 }
 await page.getByRole('button',{name:'新增罐容表',exact:true}).click();const mismatch=page.getByRole('dialog',{name:'编辑罐容表'});await mismatch.waitFor();const wrong=page.waitForResponse(r=>r.url().includes('/api/capacity-tables/import-preview'));await mismatch.locator('input[type="file"]').setInputFiles({name:'静压表误选主表.xlsx',mimeType:'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',buffer:exported.StaticPressure});assert.equal((await wrong).status(),200);await mismatch.locator('.error-message').filter({hasText:'文件类型为静压力容积修正表'}).waitFor();assert.equal(await mismatch.locator('textarea').inputValue(),'');assert.equal(await mismatch.getByLabel('高度单位',{exact:true}).inputValue(),'Millimetre');await mismatch.getByRole('button',{name:'关闭',exact:true}).click();checks.push('文件类型与所选主表不符时明确拒绝，不改草稿单位或数据');
 const bad=await call('/api/capacity-tables/'+decimal.id);await call('/api/capacity-tables','POST',{...bad,decimalRows:bad.decimalRows.slice(1)},400);assert.deepEqual((await call('/api/capacity-tables/'+decimal.id)).decimalRows,decimal.decimalRows);assert.equal((await call('/api/capacity-tables/'+decimal.id)).revision,decimal.revision);checks.push('缺少小数表序号时拒绝保存，原表版本与数据不变');
 await page.screenshot({path:path.join(out,'capacity-import-saved.png'),fullPage:true});
''' +s[end:]
p.write_text(s,encoding='utf-8')
