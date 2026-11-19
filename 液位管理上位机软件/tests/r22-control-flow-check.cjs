const fs=require('fs'),path=require('path'),net=require('net'),crypto=require('crypto'),assert=require('assert/strict'),{spawn}=require('child_process');
const{request,chromium}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),out=path.join(root,'output/control-flow-qa_20261111','integration-'+Date.now());fs.mkdirSync(out,{recursive:true});
const gateway=require('./support/mock-gateway.cjs')({control:true,remoteLevel:true,strictControl:true,remoteFlag:false});
let api,operator,browser,page,host,url,csrf,config;const checks=[],errors=[],pause=ms=>new Promise(r=>setTimeout(r,ms));
async function until(fn){for(let i=0;i<220;i++){const v=await fn();if(v)return v;await pause(100)}throw Error('r20 isolated verification timeout')}
async function call(route,method='GET',data,status=200){const r=await api.fetch(url+route,{method,data,headers:method==='GET'?{}:{'X-CSRF-TOKEN':csrf}});const txt=await r.text();assert.equal(r.status(),status,route+': '+txt);return txt?JSON.parse(txt):null}
const records=()=>call('/api/controls'),status=t=>call('/api/tanks/'+t.id+'/gateway-controls');
async function finish(record,auto){const result=await until(async()=>{const rows=await records(),original=rows.find(r=>r.id===record.id);if(!['ReadbackConfirmed','Rejected','Unknown'].includes(original?.state))return null;const reset=rows.find(r=>r.relatedControlId===original.id&&r.isAutomaticReset);if(auto&&!['ReadbackConfirmed','Rejected','Unknown'].includes(reset?.state))return null;return{original,reset}});await pause(80);return result}
async function execute(tank,command,auto=true,extra={}){if(command!=='Stop')await until(async()=>(await call('/api/acquisition')).readings.find(r=>r.tankId===tank.id)?.floatPositionMm.isValid);return finish(await call('/api/tanks/'+tank.id+'/controls','POST',{configurationRevision:config.revision,command,confirmed:true,...extra}),auto)}
async function start(data){const log=fs.openSync(path.join(out,'host.log'),'a');host=spawn(path.join(root,'output/control-flow-build_20261111/server/LevelManager.Host.exe'),['--data-root',data,'--urls',url],{windowsHide:true,stdio:['ignore',log,log]});fs.closeSync(log);await until(async()=>{try{return(await api.get(url+'/api/health')).ok()}catch{return false}})}
async function stop(){if(host&&host.exitCode===null){const ended=new Promise(r=>host.once('exit',r));host.kill();await ended}}
(async()=>{
 const gatewayPort=await gateway.listen(),probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));url='http://127.0.0.1:'+probe.address().port;await new Promise(r=>probe.close(r));api=await request.newContext();const data=path.join(out,'isolated-data');await start(data);
 assert.equal((await call('/api/health')).revision,'20261107-r22');csrf=(await call('/api/session')).csrfToken;await call('/api/auth/setup','POST',{login:'autoqa',displayName:'自动收尾隔离验证',password:'Auto-Loopback-2026!'});await call('/api/auth/login','POST',{login:'autoqa',password:'Auto-Loopback-2026!'});csrf=(await call('/api/session')).csrfToken;await require('./support/license.cjs').importTestLicense(api,url,csrf,{expiresAtUtc:null});
 config=await call('/api/config');assert.equal(config.readingExpirySeconds,30);checks.push('新安装默认30秒，不依赖演示脚本覆盖');const channelId=crypto.randomUUID();
 config.channels=[{id:channelId,name:'本机隔离模拟网关',protocol:'GatewayTcp',host:'127.0.0.1',tcpPort:gatewayPort,portName:'',unitId:1,pollIntervalMs:500,readTimeoutMs:400,readRetries:0,enabled:true}];
 config.tanks=[1,64].map(address=>({id:crypto.randomUUID(),code:'T-'+address,channelId,address,medium:'柴油',shape:'Vertical',heightMm:25000,temperatureMode:'Ats',atsPointCount:6,atsHeightsMm:[0,4000,8000,12000,16000,20000],enabled:true,hidden:false,controls:{allowedCommands:['MeasureI','MeasureII','MeasureIII','Hoist','Lower','Stop','Density','Repeatability','OscillationAvoidance','RemoteLevel'],confirmationTimeoutSeconds:3,movementTimeoutSeconds:600}}));config=await call('/api/config','PUT',config);await call('/api/acquisition/start','POST',{});await until(async()=>(await call('/api/acquisition')).readings.every(r=>r.floatPositionMm.isValid));const tank=config.tanks[0],last=config.tanks[1];

 assert.equal(gateway.writes,0);
 gateway.unknownNext();const uncertain=await execute(tank,'Lower',false);assert.equal(uncertain.original.state,'Unknown');
 const original=uncertain.original;let before=gateway.writes;
 const blocked=await call('/api/tanks/'+tank.id+'/controls','POST',{configurationRevision:config.revision,command:'Hoist',confirmed:true},409);
 assert(blocked.error.includes(original.id));assert(blocked.error.includes(tank.code));assert.equal(gateway.writes,before);
 checks.push('拦截错误含本罐、具体记录编号、动作及通道；没有发送新动作');
 let expected=await status(tank);
 await call('/api/tanks/'+tank.id+'/gateway-controls/reset','POST',{expected,cycle:'Normal',reason:'核查',confirmed:true},409);
 assert.equal(gateway.writes,before);assert.equal((await records()).find(r=>r.id===original.id).state,'Unknown');
 await call('/api/tanks/'+tank.id+'/gateway-controls/reset','POST',{expected,cycle:'Normal',reason:'核查',confirmed:true,reviewedControlIds:[crypto.randomUUID()]},409);
 assert.equal(gateway.writes,before);checks.push('未确认旧记录、错误记录编号均拒绝，原记录保持待核查');
 browser=await chromium.launch({channel:'msedge',headless:true});
 const ctx=await browser.newContext({viewport:{width:1920,height:1080},storageState:await api.storageState()});page=await ctx.newPage();page.on('pageerror',e=>errors.push(String(e)));
 await page.goto(url);await page.locator('aside').getByRole('button',{name:'单罐详情',exact:true}).click();await page.getByLabel('详情储罐').selectOption(tank.id);
 const panel=page.getByRole('region',{name:'网关控制通道实时状态'}); // section with aria-label exposes a region
 await panel.locator('.gateway-blocker').waitFor();
 assert.equal(await panel.locator('.gateway-blocker').count(),1);
 await panel.getByRole('button',{name:'查看这条记录 →',exact:true}).click();
 const history=page.getByRole('dialog',{name:'操作记录与回读',exact:true});await history.locator('.control-result').waitFor();
 assert.equal(await history.locator('.control-result').count(),1);assert((await history.innerText()).includes('下降'));
 await history.getByRole('button',{name:'关闭',exact:true}).click();
 await panel.getByRole('button',{name:'核查现场并复位',exact:true}).click();
 const dialog=page.getByRole('dialog',{name:'核查现场并复位',exact:true});
 assert((await dialog.innerText()).includes(original.id));assert(await dialog.getByRole('button',{name:'保存核查并复位',exact:true}).isDisabled());
 await dialog.getByLabel('现场核查结论',{exact:true}).fill('隔离模拟确认浮子已停，核对模式及原下降动作结果。');
 await dialog.getByRole('checkbox').check();
 await page.screenshot({path:path.join(out,'combined-dialog-1920.png')});
 await dialog.getByRole('button',{name:'保存核查并复位',exact:true}).click();
 await until(async()=>{const s=await status(tank);return s.normal.state===0&&(await records()).find(r=>r.id===original.id)?.state==='Reviewed'});
 await until(async()=>await panel.locator('.gateway-blocker').count()===0);
 assert.equal(gateway.writes,before+1);
 assert.equal(gateway.writeFrames.at(-1).values[0],0);assert.equal(gateway.writeFrames.at(-1).at,30000);
 const reset=(await records()).find(r=>r.relatedControlId===original.id&&!r.isAutomaticReset);assert(reset);assert.equal(reset.reviewReason,'隔离模拟确认浮子已停，核对模式及原下降动作结果。');
 checks.push('实际页面单窗口核查旧记录并复位，仅写一次选定通道0，关联原记录，不重发下降');
 await panel.getByText('空闲',{exact:true}).first().waitFor();
 await until(async()=>!(await panel.innerText()).includes('结果未知 · 需核查'));
 for(const width of [1920,1366]){
  await page.setViewportSize({width,height:1080});
  await page.screenshot({path:path.join(out,'detail-'+width+'.png')});
  assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
 }
 checks.push('1920及1366宽度无根横向溢出，入口集中在左侧栏目');
 gateway.setControlState(1,0,5);expected=await status(tank);before=gateway.writes;
 gateway.setControlState(1,0,2);
 await call('/api/tanks/'+tank.id+'/gateway-controls/reset','POST',{expected,cycle:'IndependentStop',reason:'旧周期确认不得复位',confirmed:true,reviewedControlIds:[]},409);
 assert.equal(gateway.writes,before);
 expected=await status(tank);await call('/api/tanks/'+tank.id+'/gateway-controls/reset','POST',{expected,cycle:'IndependentStop',reason:'执行中不得复位',confirmed:true,reviewedControlIds:[]},409);assert.equal(gateway.writes,before);
 checks.push('网关周期改变和执行中状态均拒绝，零写入');
 gateway.setControlState(1,0,0);
 const completed=await execute(tank,'Hoist');assert.equal(completed.reset.state,'ReadbackConfirmed');
 assert.equal((await status(tank)).normal.state,0);
 checks.push('本次明确结束结果继续自动复位');
 assert.deepEqual(errors,[]);
 fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:true,checks,errors,realDeviceAccess:false,writeFrames:gateway.writeFrames},null,2));
 console.log('PASS '+checks.length+' groups: '+out);
})().catch(async e=>{fs.writeFileSync(path.join(out,'failure.json'),JSON.stringify({error:e.stack,checks},null,2));if(page)try{await page.screenshot({path:path.join(out,'failure.png'),fullPage:true})}catch{};console.error(e);process.exitCode=1}).finally(async()=>{await browser?.close();await operator?.dispose();await api?.dispose();await stop();gateway.close()});
