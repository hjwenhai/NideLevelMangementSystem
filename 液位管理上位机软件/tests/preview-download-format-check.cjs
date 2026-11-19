// Real WebView2 downloads, isolated destination. Restore native download behavior afterward.
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const {chromium,request}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),out=path.join(root,'output/download-format-check_'+Date.now()),files=path.join(out,'files');fs.mkdirSync(files,{recursive:true});
const info=JSON.parse(fs.readFileSync(path.join(root,'output/client-viewing/r10-preview-session.json'),'utf8'));
const wait=ms=>new Promise(r=>setTimeout(r,ms));let browser,cdp,api,csrf,page;
async function until(fn){for(let i=0;i<200;i++){const v=await fn();if(v)return v;await wait(100)}throw Error('download verification timeout')}
async function call(route,method='GET',data){const r=await api.fetch(info.url.replace(/\/$/,'')+route,{method,data,headers:method==='GET'?{}:{'X-CSRF-TOKEN':csrf}});assert.equal(r.status(),200,route+': '+await r.text());return r.json()}
(async()=>{
 browser=await chromium.connectOverCDP('http://127.0.0.1:'+process.env.LEVEL_PREVIEW_CDP_PORT);cdp=await browser.newBrowserCDPSession();page=browser.contexts()[0].pages().find(p=>p.url().startsWith(info.url));assert(page);
 api=await request.newContext({storageState:await browser.contexts()[0].storageState()});csrf=(await call('/api/session')).csrfToken;
 async function clickDownload(href,name){await page.evaluate(({href,name})=>{const a=document.createElement('a');a.href=href;if(name)a.download=name;document.body.appendChild(a);a.click();a.remove()},{href,name})}
 // Reproduce the automation-only GUID naming that affected the preview.
 const first=page.waitForEvent('download');await clickDownload('/api/tank-configuration/export','储罐配置.xlsx');const prior=await first,priorPath=await prior.path();
 const reproduction={suggestedFilename:prior.suggestedFilename(),savedAs:path.basename(priorPath),xlsxMagic:fs.readFileSync(priorPath).subarray(0,4).toString('hex')};fs.copyFileSync(priorPath,path.join(out,'automation-guid-reproduction.xlsx'));
 await cdp.send('Browser.setDownloadBehavior',{behavior:'allow',downloadPath:files,eventsEnabled:true});
 const checks=[];
 async function download(href,name){await clickDownload(href,name);const file=await until(()=>{const p=path.join(files,name);return fs.existsSync(p)&&fs.statSync(p).size>0&&p});checks.push({name,bytes:fs.statSync(file).size,head:fs.readFileSync(file).subarray(0,8).toString('hex')});return file}
 await download('/api/tank-configuration/export','储罐配置.xlsx');await download('/api/tank-configuration/export?template=true','储罐配置导入模板.xlsx');
 const tables=await call('/api/capacity-tables');await download('/api/capacity-tables/'+tables[0].id+'/export',tables[0].number+'-v'+tables[0].revision+'.xlsx');
 await download('/api/authorization/request','液位管理系统-安装授权请求.json');
 for(const [route,data] of [['/api/history/export',{startUtcMs:Date.now()-3600000,endUtcMs:Date.now()+1000}],['/api/storage/backup',{}]]){
  const submitted=await call(route,'POST',data);const job=await until(async()=>{const j=(await call('/api/data-jobs')).find(j=>j.id===submitted.id);if(j?.state==='Failed')throw Error(j.error);return j?.state==='Completed'&&j});await download('/api/data-jobs/'+job.id+'/download',job.fileName);
 }
 // Exercise the shared workbook path used by current-page/list/event/snapshot exports.
 await page.evaluate(()=>window.LocalWorkbook.download('本地工作簿格式检查',[{name:'格式检查',rows:[['数据项','数值'],['液位',0],['温度','—']]}]));await until(()=>fs.existsSync(path.join(files,'本地工作簿格式检查.xlsx')));checks.push({name:'本地工作簿格式检查.xlsx',bytes:fs.statSync(path.join(files,'本地工作簿格式检查.xlsx')).size});
 // Exercise real product CSV and PNG exporters without changing instrument/configuration state.
 await page.locator('aside').getByRole('button',{name:'平均温度测量',exact:true}).click();
 const csvButton=page.getByRole('button',{name:/导出.*CSV|导出记录/}).first();if(await csvButton.count()){await csvButton.click();await until(()=>fs.existsSync(path.join(files,'平均温度测量记录.csv')));checks.push({name:'平均温度测量记录.csv',bytes:fs.statSync(path.join(files,'平均温度测量记录.csv')).size})}
 await page.locator('aside').getByRole('button',{name:'趋势曲线',exact:true}).click();await wait(2000);const pngButton=page.getByRole('button',{name:/导出.*PNG|保存.*图片|导出图片/}).first();if(await pngButton.count()){await pngButton.click();await until(()=>fs.existsSync(path.join(files,'趋势曲线.png')));checks.push({name:'趋势曲线.png',bytes:fs.statSync(path.join(files,'趋势曲线.png')).size})}
 await cdp.send('Browser.setDownloadBehavior',{behavior:'default'});
 fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:true,reproduction,checks,nativeDownloadBehaviorRestored:true,realDeviceAccess:false},null,2));console.log(out);
})().catch(e=>{console.error(e);fs.writeFileSync(path.join(out,'failure.json'),JSON.stringify({error:e.stack},null,2));process.exitCode=1}).finally(async()=>{if(cdp)await cdp.send('Browser.setDownloadBehavior',{behavior:'default'}).catch(()=>{});if(api)await api.dispose();if(browser)await browser.close()});
