const fs=require('fs'),path=require('path'),net=require('net'),assert=require('assert/strict'),crypto=require('crypto');
const {spawn}=require('child_process');
const {request,chromium}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const gateway=require('./support/mock-gateway.cjs')();
const root=path.resolve(__dirname,'..'),qa=path.join(root,process.env.LEVEL_VERIFICATION_DIR||'output/p3-verification');fs.mkdirSync(qa,{recursive:true});
const data=path.join(qa,'isolated-'+Date.now()),dotnet=path.join(root,'.build-tools/dotnet/dotnet.exe'),dll=path.join(root,process.env.LEVEL_UI_BUILD_ROOT||'output/engineering','LevelManager.Host.dll');
const sample=path.resolve(root,'../客户发过来的技术协议/罐容表/罐容表示例/补充19个罐的罐容表(1)/立式金属罐毫米容量表61_T_301.xls');
const originalHash=crypto.createHash('sha256').update(fs.readFileSync(sample)).digest('hex');
let host,browser,api,csrf,url,lastPage;const errors=[],external=[];
async function until(fn,timeout=20000){const end=Date.now()+timeout;while(Date.now()<end){const result=await fn();if(result)return result;await new Promise(r=>setTimeout(r,100))}throw Error('verification timed out')}
async function call(target,method='GET',body){const response=await api.fetch(url+target,{method,data:body,headers:method==='GET'?{}:{'X-CSRF-TOKEN':csrf}});let value;try{value=await response.json()}catch{value=await response.text()}return {status:response.status(),value}}
(async()=>{
 const gatewayPort=await gateway.listen();const probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));url='http://127.0.0.1:'+port;api=await request.newContext();
 const log=fs.openSync(path.join(qa,'host.log'),'w');host=spawn(dotnet,[dll,'--data-root',data,'--urls',url],{windowsHide:true,env:{...process.env,DOTNET_ROOT:path.dirname(dotnet)},stdio:['ignore',log,log]});
 await until(async()=>{try{return(await call('/api/health')).status===200}catch{return false}});
 csrf=(await call('/api/session')).value.csrfToken;
 assert.equal((await call('/api/auth/setup','POST',{login:'metering',password:'Test-Isolation-2026!',displayName:'计量界面测试'})).status,200);
 assert.equal((await call('/api/auth/login','POST',{login:'metering',password:'Test-Isolation-2026!'})).status,200);csrf=(await call('/api/session')).value.csrfToken;await require('./support/license.cjs').importTestLicense(api,url,csrf);
 let config=(await call('/api/config')).value;const channelId=crypto.randomUUID();
 config.channels=[{id:channelId,name:'隔离网关',protocol:'GatewayTcp',portName:'',baudRate:38400,dataBits:8,parity:'Even',stopBits:1,host:'127.0.0.1',tcpPort:gatewayPort,unitId:1,pollIntervalMs:500,readTimeoutMs:1000,readRetries:0,enabled:true}];
 config.tanks=Array.from({length:6},(_,i)=>({id:crypto.randomUUID(),code:'T-'+(101+i),instrumentTag:'',medium:i<3?'柴油':'煤油',zone:'一罐区',channelId,address:i+1,shape:['Vertical','Sphere','FloatingRoof'][i%3],heightMm:12000,temperatureMode:'Ats',atsPointCount:3,enabled:true,hidden:false,metering:{enabled:false,mainTableId:null,staticPressureTableId:null,decimalTableId:null,waterVolumeM3:i===0?4.25:0,standardDensity20KgM3:900}}));config.historyIntervalSeconds=1;config.readingExpirySeconds=5;
 const initial=await call('/api/config','PUT',config);assert.equal(initial.status,200);config=initial.value;
 browser=await chromium.launch({executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});const context=await browser.newContext({viewport:{width:1920,height:1080},storageState:await api.storageState(),acceptDownloads:true});const p=await context.newPage();lastPage=p;p.on('pageerror',e=>errors.push(e.stack));p.on('request',r=>{if(!r.url().startsWith(url))external.push(r.url())});
 await p.goto(url);await p.getByRole('button',{name:'基础数据',exact:true}).click();
 async function importTable(kind,number,name,column){
  await p.getByRole('button',{name:'新增罐容表',exact:true}).click();const dialog=p.getByRole('dialog',{name:'编辑罐容表',exact:true});
  await dialog.getByLabel('表编号 *',{exact:true}).fill(number);await dialog.getByLabel('表名称 *',{exact:true}).fill(name);await dialog.getByLabel('表类型',{exact:true}).selectOption(kind);await dialog.getByLabel('高度单位',{exact:true}).selectOption('Metre');await dialog.getByLabel('容积/修正量列（从1开始）',{exact:true}).fill(String(column));
  await dialog.locator('input[type=file]').setInputFiles(sample);await dialog.getByText(/已读取.*共\d+组/).waitFor();
  assert.equal(await dialog.getByLabel('关联储罐',{exact:true}).count(),0);
  await dialog.getByRole('button',{name:'预览校核',exact:true}).click();await dialog.getByRole('button',{name:'校核并保存',exact:true}).click();await dialog.waitFor({state:'detached'});
  const table=(await call('/api/capacity-tables')).value.find(t=>t.number===number);assert(table.pointCount>16000);return(await call('/api/capacity-tables/'+table.id)).value;
 }
 const main=await importTable('Main','CT-CUSTOMER-301','客户样例主罐容表',2);
 const pressure=await importTable('StaticPressure','PT-CUSTOMER-301','客户样例静压力修正表',3);assert.equal(pressure.referenceDensityKgM3,1000);
 async function bulk(table,action='Associate',one=false){const row=p.locator('main tbody tr').filter({hasText:table.number});await row.getByRole('button',{name:'批量关联',exact:true}).click();const dialog=p.getByRole('dialog',{name:'批量关联罐容表',exact:true});await dialog.getByLabel('操作',{exact:true}).selectOption(action);if(one)await dialog.locator('.bulk-tanks label').filter({hasText:'T-101'}).locator('input').check();else await dialog.getByRole('button',{name:'全选筛选结果',exact:true}).click();await dialog.getByRole('button',{name:'保存批量配置',exact:true}).click();await dialog.waitFor({state:'detached'})}
 await bulk(main);await bulk(pressure);config=(await call('/api/config')).value;assert(config.tanks.every(t=>t.metering.enabled&&t.metering.mainTableId===main.id&&t.metering.staticPressureTableId===pressure.id));
 assert.equal(await p.getByRole('button',{name:'批量关联 / 取消',exact:true}).count(),0);await p.screenshot({path:path.join(qa,'capacity-tables-linked.png'),fullPage:false});
 await p.getByRole('button',{name:'罐区总览',exact:true}).click();await p.getByRole('button',{name:'启动采集',exact:true}).click();
 const expectedMain=main.points.find(x=>Math.abs(x.height-1)<1e-9).volume,expectedPressure=pressure.points.find(x=>Math.abs(x.height-1)<1e-9).volume;
 const expectedCurrent=expectedMain+expectedPressure*.832,expectedNet=expectedCurrent-4.25;
 let live=await until(async()=>{const v=(await call('/api/acquisition')).value;return v.readings?.[0]?.netVolumeM3.quality==='Good'?v:false});
 assert(Math.abs(live.readings[0].netVolumeM3.value-expectedNet)<.000001);assert(Math.abs(live.readings[0].massT.value-expectedNet*.832)<.000001);
 await p.locator('.engineering-tank-content strong').first().filter({hasText:'1,000.0'}).waitFor();await p.screenshot({path:path.join(qa,'overview-three-shapes.png'),fullPage:true});
 await p.getByRole('button',{name:'查看实时详情 →',exact:true}).first().click();await p.locator('.detail-ats tbody tr').first().waitFor();assert.match(await p.locator('.detail-ats').innerText(),/0.00/);await p.screenshot({path:path.join(qa,'ats-and-metering-detail.png'),fullPage:false});await p.getByRole('button',{name:'返回总览',exact:true}).click();
 const from=new Date(Date.now()-120000).toISOString(),to=new Date(Date.now()+120000).toISOString();const historyUrl='/api/history?start='+encodeURIComponent(from)+'&end='+encodeURIComponent(to)+'&tank='+config.tanks[0].id;
 let old=await until(async()=>{const result=(await call(historyUrl)).value;return result.rows?.find(r=>r.netVolumeM3.quality==='Good')||false});
 const saved=await call('/api/capacity-tables','POST',{...main,points:main.points.map(x=>({...x,volume:x.volume*2}))});assert.equal(saved.status,200);
 await until(async()=>{const v=(await call('/api/acquisition')).value.readings[0];return v.calculationRevision!==old.calculationRevision&&v.netVolumeM3.quality==='Good'});
 const original=(await call(historyUrl)).value.rows.find(r=>r.atUtcMs===old.atUtcMs);assert.equal(original.netVolumeM3.value,old.netVolumeM3.value);assert.equal(original.calculationRevision,old.calculationRevision);
 await p.getByRole('button',{name:'基础数据',exact:true}).click();await bulk(pressure,'Remove',true);config=(await call('/api/config')).value;assert.equal(config.tanks[0].metering.staticPressureTableId,null);assert.equal(config.tanks[1].metering.staticPressureTableId,pressure.id);
 await p.getByRole('button',{name:'历史数据',exact:true}).click();await p.locator('main tbody tr').filter({hasText:'T-101'}).first().waitFor();await p.screenshot({path:path.join(qa,'history-values.png'),fullPage:false});
 await p.setViewportSize({width:1366,height:768});assert(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));await p.screenshot({path:path.join(qa,'history-1366.png'),fullPage:false});
 await call('/api/acquisition/stop','POST',{});assert.equal(gateway.writes,0);assert.equal(originalHash,crypto.createHash('sha256').update(fs.readFileSync(sample)).digest('hex'));assert.deepEqual(errors,[]);assert.deepEqual(external,[]);
 fs.writeFileSync(path.join(qa,'result.json'),JSON.stringify({passed:true,customerSourceSha256:originalHash,mainRows:main.points.length,staticRows:pressure.points.length,expectedMain,expectedPressure,expectedCurrent,expectedNet,oldHistoryUnchanged:true,instrumentWrites:gateway.writes,consoleErrors:errors,externalRequests:external,checks:['customer XLS through actual UI import','unit selection and preview','reference density','bulk association six tanks','removed obsolete dropdown/link text','live customer formula','three tank shapes','ATS zero point','old history frozen after table version change','cancel only selected table/tank','1366 layout','source file preserved']},null,2));console.log('PASS: 12 metering/Excel/live/history/UI groups; customer XLS',main.points.length,'rows');
})().catch(async e=>{console.error(e);if(lastPage){await lastPage.screenshot({path:path.join(qa,'failure.png'),fullPage:false});fs.writeFileSync(path.join(qa,'failure.txt'),await lastPage.locator('body').innerText())}process.exitCode=1}).finally(async()=>{if(browser)await browser.close();if(api)await api.dispose();if(host&&host.exitCode===null){const ended=new Promise(r=>host.once('exit',r));host.kill();await ended}gateway.close()});
