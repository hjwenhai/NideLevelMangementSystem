const fs=require('fs'),path=require('path'),net=require('net'),assert=require('assert/strict'),crypto=require('crypto');
const {spawn}=require('child_process'),{request,chromium}=require(process.env.LEVEL_PLAYWRIGHT_PATH||'C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const gateway=require('./support/mock-gateway.cjs')(),root=path.resolve(__dirname,'..'),out=path.join(root,process.env.LEVEL_VERIFICATION_DIR||'output/p4-trend-verification');fs.mkdirSync(out,{recursive:true});let host,browser,page,api,url,csrf;const errors=[],external=[];
async function until(fn){for(let i=0;i<200;i++){const v=await fn();if(v)return v;await new Promise(r=>setTimeout(r,100))}throw Error('verification timed out')}
async function call(route,method='GET',body){const r=await api.fetch(url+route,{method,data:body,headers:method==='GET'?{}:{'X-CSRF-TOKEN':csrf}});return {status:r.status(),value:await r.json()}}
async function checkHourInBrowser(){
 const route='**/api/trends/realtime';
 // Keep the actual API time bounds and add a controlled 45-minute-old display sample.
 await page.route(route,async requestRoute=>{
  const response=await requestRoute.fetch();
  const result=await response.json();
  const old=result.series.find(s=>s.selection.quantity==='Level');
  old.points.unshift({atUtcMs:result.startUtcMs+900000,value:1379,quality:'Good',gapMarker:false},{atUtcMs:result.startUtcMs+960000,value:null,quality:'Offline',gapMarker:true});
  old.sourceRows++;
  await requestRoute.fulfill({response,json:result});
 });
 async function seeOlderValue(selector,left,right,top){
  const plot=page.locator(selector);
  await plot.locator('canvas').waitFor();
  await plot.scrollIntoViewIfNeeded();
  const box=await plot.boundingBox();
  const pointX=box.x+left+(box.width-left-right)*.25;
  await page.mouse.move(pointX-2,box.y+top);
  await plot.getByText(/1,?379/).first().waitFor({timeout:4000});
 }
 await page.getByRole('button',{name:'单罐详情',exact:true}).click();
 await page.getByText('最近 1 小时 · 缺测断线',{exact:true}).waitFor();
 await seeOlderValue('.single-realtime-canvas',56,108,90);
 await page.locator('.single-realtime-panel').screenshot({path:path.join(out,'single-tank-hour.png')});
 await page.getByRole('button',{name:'更多曲线 / 历史 →',exact:true}).click();
 await page.getByText(/实时曲线显示最近 1 小时/).waitFor();
  await page.locator('.trend-canvas canvas').waitFor();
  await page.getByRole('button',{name:'暂停查看',exact:true}).click();
  await seeOlderValue('.trend-canvas',110,65,250);
 await page.getByRole('button',{name:'打印预览',exact:true}).click();
 const preview=page.getByRole('dialog',{name:'打印预览'});
 await preview.getByRole('heading',{name:'实时趋势 · 最近 1 小时',exact:true}).waitFor();
 await preview.getByRole('button',{name:'关闭',exact:true}).click();
 await page.unroute(route);
  await page.getByRole('button',{name:'继续更新',exact:true}).click();
}
(async()=>{
 const gatewayPort=await gateway.listen(),probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));url='http://127.0.0.1:'+port;api=await request.newContext();const dotnet=path.join(root,'.build-tools/dotnet/dotnet.exe'),log=fs.openSync(path.join(out,'host.log'),'w');host=spawn(dotnet,[path.join(root,process.env.LEVEL_UI_BUILD_ROOT||'output/engineering','LevelManager.Host.dll'),'--data-root',path.join(out,'isolated-'+Date.now()),'--urls',url],{windowsHide:true,env:{...process.env,DOTNET_ROOT:path.dirname(dotnet)},stdio:['ignore',log,log]});await until(async()=>{try{return(await call('/api/health')).status===200}catch{return false}});
 csrf=(await call('/api/session')).value.csrfToken;await call('/api/auth/setup','POST',{login:'trendtest',displayName:'趋势核验',password:'Trend-Isolated-2026!'});await call('/api/auth/login','POST',{login:'trendtest',password:'Trend-Isolated-2026!'});csrf=(await call('/api/session')).value.csrfToken;await require('./support/license.cjs').importTestLicense(api,url,csrf);
 let cfg=(await call('/api/config')).value;const channelId=crypto.randomUUID(),tankId=crypto.randomUUID();cfg.channels=[{id:channelId,name:'隔离网关',protocol:'GatewayTcp',portName:'',baudRate:38400,dataBits:8,parity:'Even',stopBits:1,host:'127.0.0.1',tcpPort:gatewayPort,unitId:1,pollIntervalMs:500,readTimeoutMs:1000,readRetries:0,enabled:true}];cfg.tanks=[{id:tankId,code:'T-101',name:'趋势示例罐',instrumentTag:'LT-101',medium:'柴油',zone:'',channelId,address:1,shape:'Vertical',heightMm:12000,temperatureMode:'Ats',atsPointCount:3,enabled:true,hidden:false}];cfg.historyIntervalSeconds=1;const saved=await call('/api/config','PUT',cfg);assert.equal(saved.status,200,JSON.stringify(saved.value));await call('/api/acquisition/start','POST',{});const live=await until(async()=>{const v=(await call('/api/acquisition')).value;return v.readings[0].levelMm.isValid?v:false});const session=live.sessionId;
 const query=()=>({startUtcMs:Date.now()-120000,endUtcMs:Date.now()+60000,series:[{tankId,quantity:'Level'},{tankId,quantity:'InstantTemperature'}],maximumPoints:2000});
 await until(async()=>((await call('/api/trends/realtime','POST',query())).value.series[0].points.some(p=>p.value===1000)));
 const realtime=(await call('/api/trends/realtime','POST',query())).value;
 assert.equal(realtime.endUtcMs-realtime.startUtcMs,3600000);
 assert.equal(realtime.endUtcMs,realtime.serverAtUtcMs+1);
 assert(realtime.series[0].points[0].atUtcMs>realtime.startUtcMs);
 gateway.setOffline(true);await until(async()=>((await call('/api/trends/realtime','POST',query())).value.series[0].points.some(p=>p.quality==='Offline'&&p.value===null)));await new Promise(r=>setTimeout(r,1300));gateway.setOffline(false);gateway.setLevelMm(2000);await until(async()=>((await call('/api/trends/realtime','POST',query())).value.series[0].points.some(p=>p.value===2000)));
 const historical=await until(async()=>{const r=(await call('/api/trends/history','POST',query())).value;return r.series[0].points.some(p=>p.value===2000)&&r.series[0].points.some(p=>p.value===null)?r:false});assert(historical.series[1].points.some(p=>p.value===28));const invalid=await call('/api/trends/history','POST',{...query(),series:Array.from({length:6},()=>({tankId,quantity:'Level'}))});assert.equal(invalid.status,400);
 browser=await chromium.launch({executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});const context=await browser.newContext({viewport:{width:1920,height:1080},storageState:await api.storageState(),acceptDownloads:true});page=await context.newPage();page.on('pageerror',e=>errors.push(e.stack));page.on('request',r=>{if(!r.url().startsWith(url)&&!r.url().startsWith('data:'))external.push(r.url())});await page.goto(url);await checkHourInBrowser();await page.locator('.table-foot').filter({hasText:'来源记录'}).waitFor();
 assert.equal(await page.locator('.trend-selection').count(),3);await page.getByRole('button',{name:'增加曲线',exact:true}).click();await until(async()=>!await page.getByRole('button',{name:'查询 / 更新',exact:true}).isDisabled());await page.getByRole('button',{name:'增加曲线',exact:true}).click();assert.equal(await page.locator('.trend-selection').count(),5);assert(await page.getByRole('button',{name:'增加曲线',exact:true}).isDisabled());await until(async()=>!await page.getByRole('button',{name:'保存该罐共用曲线设置',exact:true}).isDisabled());
 await page.getByLabel('曲线1颜色',{exact:true}).fill('#e04c59');await page.getByRole('button',{name:'保存该罐共用曲线设置',exact:true}).click();await page.getByRole('status').waitFor();cfg=(await call('/api/config')).value;assert.equal(cfg.tanks[0].trends.quantities.length,5);assert.equal(cfg.tanks[0].trends.colors[0],'#e04c59');assert.equal((await call('/api/acquisition')).value.sessionId,session);
 await page.getByRole('button',{name:'暂停查看',exact:true}).click();const frozen=await page.locator('.table-foot').innerText();gateway.setLevelMm(2500);await new Promise(r=>setTimeout(r,2300));assert.equal(await page.locator('.table-foot').innerText(),frozen);await page.getByRole('button',{name:'继续更新',exact:true}).click();
 await page.getByLabel('曲线数据范围',{exact:true}).selectOption('history');await until(async()=>!await page.getByRole('button',{name:'查询 / 更新',exact:true}).isDisabled());await page.screenshot({path:path.join(out,'five-curves-history.png'),fullPage:true});
 const pngDownload=page.waitForEvent('download');await page.getByRole('button',{name:'保存曲线图片',exact:true}).click();await(await pngDownload).saveAs(path.join(out,'trend-export.png'));assert(fs.statSync(path.join(out,'trend-export.png')).size>10000);
 await page.setViewportSize({width:1366,height:768});assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));await page.screenshot({path:path.join(out,'trends-1366.png'),fullPage:true});assert.equal(gateway.writes,0);assert.deepEqual(errors,[]);assert.deepEqual(external,[]);await call('/api/acquisition/stop','POST',{});
 const checks=['one-hour realtime API bounds with a partly filled window','45-minute-old sample and hour axis on both pages','realtime print title','realtime actual sample','offline null gap','recovery after gap','archived trend values','more than five rejected','five curve UI','shared per tank preset','color persists','preset keeps acquisition session','pause freezes view','history mode','PNG export','1366 viewport','fully local assets'];
 fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:true,checks,instrumentWrites:gateway.writes,pageErrors:errors,externalRequests:external},null,2));console.log(`PASS: ${checks.length} trend API/UI groups`);
})().catch(async e=>{console.error(e);if(page){await page.screenshot({path:path.join(out,'failure.png')});fs.writeFileSync(path.join(out,'failure.txt'),await page.locator('body').innerText())}process.exitCode=1}).finally(async()=>{if(browser)await browser.close();if(api)await api.dispose();if(host&&host.exitCode===null){const p=new Promise(r=>host.once('exit',r));host.kill();await p}gateway.close()});
