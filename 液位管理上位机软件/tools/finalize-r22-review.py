from pathlib import Path
r=Path(__file__).resolve().parents[1]
p=r/'web/src/App.vue';s=p.read_text(encoding='utf-8-sig');assert 'V0.3.0 · r21' in s;s=s.replace('V0.3.0 · r21','V0.3.0 · r22');p.write_text(s,encoding='utf-8')
p=r/'tests/r22-ui-review.cjs';s=p.read_text(encoding='utf-8-sig').replace("['MeasureI','Hoist','Lower','Stop','RemoteLevel']","['MeasureI','MeasureII','MeasureIII','Hoist','Lower','Density','Repeatability','OscillationAvoidance','Stop','RemoteLevel']")
s=s.replace("await page.getByRole('heading',{name:'罐区总览',exact:true}).waitFor();", "await page.getByRole('heading',{name:'罐区总览',exact:true}).waitFor();assert((await page.locator('.workspace-name').innerText()).includes('r22'));" )
p.write_text(s,encoding='utf-8')
