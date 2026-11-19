// Actual product export in the native client. Native open/reveal is verified separately with Windows UI.
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const {chromium}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),out=path.join(root,'output/native-download-verification_'+Date.now());fs.mkdirSync(out,{recursive:true});
let browser,cdp;
const delay=ms=>new Promise(r=>setTimeout(r,ms));
async function until(fn){for(let n=0;n<200;n++){const v=await fn();if(v)return v;await delay(100)}throw Error('native download timeout')}
(async()=>{
 const info=JSON.parse(fs.readFileSync(path.join(root,'output/client-viewing/r10-preview-session.json'),'utf8'));assert.equal(info.revision,'r10-download-card-review');
 browser=await chromium.connectOverCDP('http://127.0.0.1:'+info.cdpPort);cdp=await browser.newBrowserCDPSession();await cdp.send('Browser.setDownloadBehavior',{behavior:'default'});
 const page=browser.contexts()[0].pages().find(p=>p.url().startsWith(info.url));assert(page);const errors=[];page.on('pageerror',e=>errors.push(String(e)));
 await page.locator('aside').getByRole('button',{name:'储罐配置',exact:true}).click();
 await page.getByRole('button',{name:'导出储罐配置',exact:true}).click();
 const records=path.join(info.profileRoot,'download-records.json');
 const exported=await until(()=>{try{return JSON.parse(fs.readFileSync(records,'utf8')).find(r=>r.State==='Completed'&&path.basename(r.FilePath).startsWith('储罐配置')&&fs.existsSync(r.FilePath))}catch{return null}});
 assert.equal(path.extname(exported.FilePath),'.xlsx');assert.equal(fs.readFileSync(exported.FilePath).subarray(0,4).toString('hex'),'504b0304');
 const copy=path.join(out,'实际下载的储罐配置.xlsx');fs.copyFileSync(exported.FilePath,copy);
 assert(await page.getByRole('button',{name:'下载文件',exact:true}).isVisible());assert.deepEqual(errors,[]);
 fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:true,download:exported,validationCopy:copy,nativeHistoryPath:records,desktopBridgeVisible:true,nativeOpenAndReveal:'awaiting Windows UI verification',pageErrors:errors,realDeviceAccess:false},null,2));
 console.log(out);
})().catch(e=>{fs.writeFileSync(path.join(out,'failure.json'),JSON.stringify({error:e.stack},null,2));console.error(e);process.exitCode=1}).finally(async()=>{if(cdp)await cdp.send('Browser.setDownloadBehavior',{behavior:'default'}).catch(()=>{});if(browser)await browser.close()});
