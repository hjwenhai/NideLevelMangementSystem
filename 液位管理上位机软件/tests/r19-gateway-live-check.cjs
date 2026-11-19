// Dedicated loopback-only regression. Never connects to an installed/customer backend.
const fs=require('fs'),path=require('path'),net=require('net'),assert=require('assert/strict'),crypto=require('crypto');
const{spawn}=require('child_process');
const{request,chromium}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),out=path.join(root,'output/r19-gateway-live-qa_20261102','integration-'+Date.now());fs.mkdirSync(out,{recursive:true});
const gateway=require('./support/mock-gateway.cjs')({control:true,remoteLevel:true,strictControl:true,remoteFlag:false});
const checks=[],errors=[];let host,api,browser,page,url,csrf,config;
const pause=ms=>new Promise(r=>setTimeout(r,ms));
async function until(fn){for(let i=0;i<160;i++){const v=await fn();if(v)return v;await pause(150)}throw Error('r19 isolated verification timed out')}
async function call(route,method='GET',data,status=200){const r=await api.fetch(url+route,{method,data,headers:method==='GET'?{}:{'X-CSRF-TOKEN':csrf}});const text=await r.text();assert.equal(r.status(),status,route+': '+text);return text?JSON.parse(text):null}
async function terminal(record){return until(async()=>(await call('/api/controls')).find(r=>r.id===record.id&&['Rejected','ReadbackConfirmed','Unknown'].includes(r.state)))}
async function command(tank,cmd){if(cmd!=='Stop')await until(async()=>(await call('/api/acquisition')).readings.find(r=>r.tankId===tank.id)?.floatPositionMm.isValid);return terminal(await call('/api/tanks/'+tank.id+'/controls','POST',{configurationRevision:config.revision,command:cmd,confirmed:true,...(cmd==='RemoteLevel'?{targetLevelMm:1200,reason:'隔离校准检查'}:{})}))}
const status=tank=>call('/api/tanks/'+tank.id+'/gateway-controls');
async function reset(tank,cycle,expected){return terminal(await call('/api/tanks/'+tank.id+'/gateway-controls/reset','POST',{expected:expected||await status(tank),cycle,reason:'隔离模拟已核查运动、位置、模式及原结果，仅复位所选通道',confirmed:true}))}
(async()=>{
 const gatewayPort=await gateway.listen(),probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));url='http://127.0.0.1:'+probe.address().port;await new Promise(r=>probe.close(r));
 const log=fs.openSync(path.join(out,'host.log'),'a');host=spawn(path.join(root,'output/r19-gateway-live-build_20261102/server/LevelManager.Host.exe'),['--data-root',path.join(out,'isolated-data'),'--urls',url],{windowsHide:true,stdio:['ignore',log,log]});fs.closeSync(log);
 api=await request.newContext();await until(async()=>{try{return(await api.get(url+'/api/health')).ok()}catch{return false}});
 assert.equal((await call('/api/health')).revision,'20261102-r19');csrf=(await call('/api/session')).csrfToken;
 await call('/api/auth/setup','POST',{login:'r19qa',displayName:'通道隔离验证',password:'R19-Loopback-Only-2026!'});await call('/api/auth/login','POST',{login:'r19qa',password:'R19-Loopback-Only-2026!'});csrf=(await call('/api/session')).csrfToken;
 await require('./support/license.cjs').importTestLicense(api,url,csrf,{expiresAtUtc:null});config=await call('/api/config');
 const channelId=crypto.randomUUID();config.channels=[{id:channelId,name:'隔离网关通道',protocol:'GatewayTcp',host:'127.0.0.1',tcpPort:gatewayPort,portName:'',unitId:1,pollIntervalMs:500,readTimeoutMs:500,readRetries:0,enabled:true}];
 config.tanks=[1,64].map(address=>({id:crypto.randomUUID(),code:'T-'+(100+address),name:'隔离验证储罐',medium:'柴油',zone:'一罐区',channelId,address,shape:'Vertical',heightMm:25000,temperatureMode:'Ats',atsPointCount:6,atsHeightsMm:[0,4000,8000,12000,16000,20000],enabled:true,hidden:false,controls:{allowedCommands:['MeasureI','MeasureII','MeasureIII','Stop','Hoist','Lower','Density','Repeatability','OscillationAvoidance','RemoteLevel'],confirmationTimeoutSeconds:3,movementTimeoutSeconds:600}}));
 config.readingExpirySeconds=3;config.historyIntervalSeconds=60;config=await call('/api/config','PUT',config);await call('/api/acquisition/start','POST',{});
 await until(async()=>(await call('/api/acquisition')).readings.every(r=>r.floatPositionMm.isValid));
 const tank=config.tanks[0],last=config.tanks[1];let s=await status(tank);assert.equal(s.normal.state,0);assert.equal(s.independentStop.state,0);assert.equal(typeof s.gatewayBootId,'string');assert.equal(gateway.writes,0);checks.push('实时读取两通道，无设备写入；网关64位身份以字符串传输');
 await command(tank,'Stop');s=await status(tank);assert.equal(s.independentStop.state,3);const before=gateway.writes;
 const blocked=await command(tank,'MeasureI');assert.equal(blocked.state,'Rejected');assert.match(blocked.result,/独立停止.*复位/);assert.equal(gateway.writes,before);checks.push('现场停止结果未复位场景：普通动作提前明确拒绝，0新增写入');
 const remote=await command(tank,'RemoteLevel');assert.equal(remote.state,'Rejected');assert.match(remote.result,/独立停止/);assert.equal(gateway.writes,before);checks.push('远程液位修改也检查双通道，无目标暂存或提交写入');
 const r=await reset(tank,'IndependentStop');assert.equal(r.state,'ReadbackConfirmed');assert.equal(r.gatewayResetExpectation.independentStop.state,3);assert.equal(gateway.writeFrames.at(-1).at,30007);assert.equal(gateway.writeFrames.at(-1).values[0],0);assert.equal((await status(tank)).normal.state,0);checks.push('实时入口无需旧操作记录；只复位所选停止结果，保存周期证据');
 const action=await command(tank,'MeasureI');assert.equal(action.state,'ReadbackConfirmed');assert.equal((await status(tank)).normal.state,3);await reset(tank,'Normal');checks.push('复位后可另行执行测量Ⅰ，液面与模式恢复');
 gateway.setControlState(1,0,3);const stale=await status(tank);gateway.setControlState(1,0,0);gateway.setControlState(1,0,3);const writes=gateway.writes;
 await call('/api/tanks/'+tank.id+'/gateway-controls/reset','POST',{expected:stale,cycle:'IndependentStop',reason:'旧确认不应清除新周期',confirmed:true},409);assert.equal(gateway.writes,writes);checks.push('同样的状态但周期计数改变：拒绝旧确认，不写复位');
 s=await status(tank);await call('/api/tanks/'+tank.id+'/gateway-controls/reset','POST',{expected:s,cycle:'IndependentStop',reason:'没有勾选确认',confirmed:false},400);assert.equal(gateway.writes,writes);
 const expired={...s,observedAtUtc:new Date(Date.now()-30000).toISOString()};await call('/api/tanks/'+tank.id+'/gateway-controls/reset','POST',{expected:expired,cycle:'IndependentStop',reason:'过期确认拒绝',confirmed:true},409);checks.push('显式确认、非空原因和新鲜状态要求均生效');
 gateway.setControlState(1,0,2);s=await status(tank);await call('/api/tanks/'+tank.id+'/gateway-controls/reset','POST',{expected:s,cycle:'IndependentStop',reason:'执行中禁止复位',confirmed:true},409);assert.equal(gateway.writes,writes);checks.push('执行中状态不允许复位');
 gateway.setControlState(1,0,0);await command(last,'Stop');assert.equal((await status(last)).independentStop.state,3);assert.equal((await reset(last,'IndependentStop')).state,'ReadbackConfirmed');assert.equal(gateway.writeFrames.at(-1).at,30763);checks.push('64号仪表地址与独立停止复位正确');
 browser=await chromium.launch({executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});const ctx=await browser.newContext({viewport:{width:1920,height:1080},storageState:await api.storageState()});page=await ctx.newPage();page.on('pageerror',e=>errors.push(String(e)));
 await page.goto(url);await page.locator('aside').getByRole('button',{name:'单罐详情',exact:true}).click();await page.getByLabel('详情储罐').selectOption(tank.id);
 await page.getByText('当前模式：测量Ⅰ',{exact:true}).waitFor();await page.getByLabel('网关控制通道实时状态').getByText('空闲',{exact:true}).first().waitFor();
 const stop=page.locator('.instrument-controls').getByRole('button',{name:'停止',exact:true});assert.equal(await stop.evaluate(e=>e.classList.contains('danger')),false);assert.equal(await stop.evaluate(e=>getComputedStyle(e).backgroundColor),'rgb(255, 255, 255)');checks.push('停止按钮普通白底；当前模式单独显示，不用按钮颜色代替状态');
 gateway.setControlState(1,0,3);await page.getByLabel('网关控制通道实时状态').getByRole('button',{name:'刷新',exact:true}).click();const liveRow=page.locator('.gateway-live-row').filter({hasText:'独立停止'});await liveRow.getByText('回读已确认 · 待复位',{exact:true}).waitFor();
 await page.screenshot({path:path.join(out,'r19-detail-live-stop-result.png'),fullPage:true});
 await liveRow.getByRole('button',{name:'复位结果',exact:true}).click();const dialog=page.getByRole('dialog',{name:'复位网关实时结果'});assert(await dialog.getByRole('button',{name:'确认复位独立停止结果',exact:true}).isDisabled());
 await dialog.getByLabel('现场核查结论').fill('隔离模拟核对，仅清停止结果，不发送浮子动作');await dialog.getByRole('checkbox').check();await dialog.getByRole('button',{name:'确认复位独立停止结果',exact:true}).click();await dialog.waitFor({state:'hidden'});await until(async()=>(await status(tank)).independentStop.state===0);checks.push('界面复位确认门槛、实时刷新和指定通道写0通过');
 gateway.setControlState(1,5,0);await page.getByLabel('网关控制通道实时状态').getByRole('button',{name:'刷新',exact:true}).click();await page.getByLabel('网关控制通道实时状态').getByText('结果未知 · 须核查',{exact:true}).waitFor();await page.getByLabel('网关控制通道实时状态').getByRole('button',{name:'核查并复位',exact:true}).waitFor();checks.push('未知结果明确标注且需人工确认，无自动复位');
 const initialWrites=gateway.writes;
 await call('/api/users','POST',{login:'r19viewer',displayName:'只读验证',role:'Viewer',enabled:true,newPassword:'R19-Viewer-Only-2026!'});
 const viewer=await request.newContext();let vr=await viewer.get(url+'/api/session'),vcsrf=(await vr.json()).csrfToken;
 await viewer.post(url+'/api/auth/login',{headers:{'X-CSRF-TOKEN':vcsrf},data:{login:'r19viewer',password:'R19-Viewer-Only-2026!'}});vcsrf=(await(await viewer.get(url+'/api/session')).json()).csrfToken;
 assert.equal((await viewer.get(url+'/api/tanks/'+tank.id+'/gateway-controls')).status(),200);
 assert.equal((await viewer.post(url+'/api/tanks/'+tank.id+'/gateway-controls/reset',{headers:{'X-CSRF-TOKEN':vcsrf},data:{expected:await status(tank),cycle:'Normal',reason:'无权限不得复位',confirmed:true}})).status(),403);
 await viewer.dispose();assert.equal(gateway.writes,initialWrites);checks.push('只读岗位能查看通道，后台拒绝无复位权限的写入');
 const ts=require(path.join(root,'web/node_modules/typescript')),mod={exports:{}};
 new Function('module','exports',ts.transpileModule(fs.readFileSync(path.join(root,'web/src/instrumentMode.ts'),'utf8'),{compilerOptions:{module:ts.ModuleKind.CommonJS}}).outputText)(mod,mod.exports);
 for(const [protocol,code,name] of [['GatewayTcp',32769,'测量Ⅰ'],['DirectRtu',4,'停止'],['DirectRtu',49156,'停止'],['IfxL25',4,'测量Ⅱ（界面）'],['IfxL25',5,'停止']])assert.equal(mod.exports.instrumentMode({instrumentStatus:code},true,protocol),name);
 assert.match(mod.exports.instrumentMode({instrumentStatus:1},false,'DirectRtu'),/未知/);checks.push('直连、L25与网关分别解码模式，失效读数显示未知');
await page.route('**/gateway-controls',route=>route.fulfill({status:503,json:{error:'隔离状态读取失败'}}));await page.getByLabel('网关控制通道实时状态').getByRole('button',{name:'刷新',exact:true}).click();await until(async()=>await page.getByLabel('网关控制通道实时状态').getByText('状态未知',{exact:true}).count()===2);assert.equal(await page.getByLabel('网关控制通道实时状态').getByRole('button',{name:'核查并复位',exact:true}).count(),0);assert.equal(gateway.writes,initialWrites);checks.push('读取失败时立即显示未知，隐藏复位，不冒充空闲');
 await page.unroute('**/gateway-controls');gateway.setControlState(1,0,0);await page.getByLabel('网关控制通道实时状态').getByRole('button',{name:'刷新',exact:true}).click();await page.getByLabel('网关控制通道实时状态').getByText('空闲',{exact:true}).first().waitFor();
 const dimensions=await page.evaluate(()=>({body:document.body.scrollHeight,viewport:innerHeight,horizontal:document.documentElement.scrollWidth>innerWidth}));assert(!dimensions.horizontal);await page.screenshot({path:path.join(out,'r19-detail-idle.png'),fullPage:true});
 assert.deepEqual(errors,[]);assert.equal(/\bAI\b|大模型/.test(await page.locator('body').innerText()),false);checks.push('中文详情、1920布局无横向溢出、无页面异常');
 const index=await api.get(url+'/');assert.equal(index.headers()['cache-control'],'no-cache');checks.push('首页重新验证缓存，主程序按后台revision导航避免升级旧画面');
 fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:true,checks,dimensions,errors,writeFrames:gateway.writeFrames,realDeviceAccess:false,url,hostStoppedByFinally:true},null,2));console.log('PASS '+checks.length+' r19 integration/UI groups: '+out);
})().catch(async e=>{fs.writeFileSync(path.join(out,'failure.json'),JSON.stringify({error:e.stack,checks},null,2));if(page)try{await page.screenshot({path:path.join(out,'failure.png'),fullPage:true})}catch{};console.error(e);process.exitCode=1}).finally(async()=>{await browser?.close();await api?.dispose();if(host&&host.exitCode===null){const ended=new Promise(r=>host.once('exit',r));host.kill();await ended}gateway.close()});
