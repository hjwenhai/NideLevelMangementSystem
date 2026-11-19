// Pure loopback r20 comparison; never opens the original r20 database or COM ports.
const fs=require('fs'),path=require('path'),net=require('net'),crypto=require('crypto'),assert=require('assert/strict');
const {spawn}=require('child_process');
const {request}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const revision='r20';
const root=path.resolve(__dirname,'..'),out=path.join(root,'output/'+revision+'-comparison-preview-'+Date.now());fs.mkdirSync(out,{recursive:true});
const pkg=path.join(root,'output/客户交付资料_r20_20261102/液位管理系统_r20_自动结果复位改进版_20261102');
const fieldProfile=true;
const gateway=require('../tests/support/mock-gateway.cjs')({control:true,remoteLevel:true,strictControl:true,optionalDensityUnsupported:fieldProfile,remoteFlag:!fieldProfile});
const delay=ms=>new Promise(r=>setTimeout(r,ms));
let api,host,csrf,url;
async function until(fn){for(let i=0;i<250;i++){const r=await fn();if(r)return r;await delay(100)}throw Error('preview startup timeout')}
async function freePort(){const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));const p=s.address().port;await new Promise(r=>s.close(r));return p}
async function call(route,method='GET',body){const r=await api.fetch(url+route,{method,data:body,headers:method==='GET'?{}:{'X-CSRF-TOKEN':csrf}});const t=await r.text();assert.equal(r.status(),200,route+': '+t);return t?JSON.parse(t):null}
function save(name,value){fs.writeFileSync(path.join(out,name),JSON.stringify(value,null,2))}
(async()=>{
 const manifest=JSON.parse(fs.readFileSync(path.join(pkg,'package-manifest.json'),'utf8').replace(/^\uFEFF/,''));assert.equal(manifest.revision,revision);
 const gatewayPort=await gateway.listen();gateway.setAnalog(12000,14000);url='http://127.0.0.1:'+await freePort();
 const previewDll=null;const log=fs.openSync(path.join(out,'host.log'),'a');host=spawn(previewDll?path.join(root,'.build-tools/dotnet/dotnet.exe'):path.join(pkg,'app/server/LevelManager.Host.exe'),[...(previewDll?[path.resolve(root,previewDll)]:[]),'--data-root',path.join(out,'isolated-data'),'--urls',url],{windowsHide:true,env:{...process.env,DOTNET_ROOT:path.join(root,'.build-tools/dotnet')},stdio:['ignore',log,log]});fs.closeSync(log);
 api=await request.newContext();await until(async()=>{try{return(await api.get(url+'/api/health')).ok()}catch{return false}});
 csrf=(await call('/api/session')).csrfToken;
 await call('/api/auth/setup','POST',{login:'previewqa',displayName:'画面预览管理员',password:'Preview-Isolated-2026!'});
 await call('/api/auth/login','POST',{login:'previewqa',password:'Preview-Isolated-2026!'});csrf=(await call('/api/session')).csrfToken;
 await require('../tests/support/license.cjs').importTestLicense(api,url,csrf,{expiresAtUtc:null,branding:{softwareName:'智能伺服液位管理系统',manufacturerName:'耐德东京计装',manufacturerInformation:'独立模拟预览'}});
 const tables=[];
 for(const [number,kind,name,total] of [['CT-DEMO-001','Main','3000 m³演示罐容主表',3000],['PT-DEMO-001','StaticPressure','静压力容积修正演示表',12],['DT-DEMO-001','Decimal','小数增量示例（组合规则待确认）',0.25]]){
  tables.push(await call('/api/capacity-tables','POST',{id:crypto.randomUUID(),revision:0,number,name,kind,heightUnit:'Millimetre',volumeUnit:'CubicMetre',enabled:true,referenceDensityKgM3:1000,notes:'仅模拟示例，实际使用应导入检定资料。',points:kind==='Decimal'?[{height:0,volume:0},{height:1,volume:total}]:[{height:0,volume:0},{height:3000,volume:total/4},{height:6000,volume:total/2},{height:9000,volume:total*0.75},{height:12000,volume:total}]}));
 }
 let cfg=await call('/api/config');const channelId=crypto.randomUUID();
 cfg.channels=[{id:channelId,name:'独立模拟网关（本机）',protocol:'GatewayTcp',portName:'',host:'127.0.0.1',tcpPort:gatewayPort,unitId:1,baudRate:38400,dataBits:8,parity:'Even',stopBits:1,pollIntervalMs:1000,readTimeoutMs:400,readRetries:0,enabled:true}];
 cfg.media=['柴油','汽油','煤油'].map((name,i)=>({id:crypto.randomUUID(),code:['DIESEL','PETROL','KEROSENE'][i],name,color:['#73aa9c','#7cacc0','#a3a8c1'][i]}));
 cfg.tanks=Array.from({length:12},(_,i)=>{const count=[16,8,4][i%3],medium=cfg.media[Math.floor(i/2)%3];return{
  id:crypto.randomUUID(),code:'T-'+(i<6?101+i:201+i-6),name:medium.name+'储罐'+(i+1),instrumentTag:'LT-'+(i+1),productNumber:'',batch:'首批',diameterMm:i%3===1?12000:18000,nominalCapacityM3:3000,notes:'独立模拟数据，仅用于画面与功能查看。',medium:medium.name,mediumId:medium.id,zone:i<6?'一罐区':'二罐区',channelId,address:i+1,shape:['Vertical','Sphere','FloatingRoof'][i%3],heightMm:12000,temperatureMode:true&&(i===4||i===5)?(i===4?'Wireless':'None'):'Ats',atsPointCount:count,atsHeightsMm:Array.from({length:count},(_,p)=>i===0&&p===count-1?null:Math.round(p*10500/(count-1))),enabled:true,hidden:false,
  pressure1:{enabled:true,unit:'KPa',at4Ma:0,at20Ma:100},pressure2:{enabled:i%2===0,unit:'KPa',at4Ma:0,at20Ma:160},
  alarms:{level:{lowLow:500,low:1500,high:11000,highHigh:11500,hysteresis:200,delaySeconds:0},temperature:{low:0,high:60,hysteresis:1,delaySeconds:0},density:{low:600,high:1000,hysteresis:10,delaySeconds:0},communicationEnabled:true,instrumentEnabled:true},
  metering:{enabled:true,mainTableId:tables[0].id,staticPressureTableId:tables[1].id,waterVolumeM3:1,standardDensity20KgM3:850},controls:{allowedCommands:[...(true?['MeasureI','MeasureII','MeasureIII','Hoist','Lower','Density','Repeatability','OscillationAvoidance','Stop']:['MeasureI','Stop']),...(true?['RemoteLevel']:[])],wirelessMeasurementEnabled:false,confirmationTimeoutSeconds:5,movementTimeoutSeconds:600}
 }});
 cfg.groups=[...['一罐区','二罐区'].map((name,i)=>({id:crypto.randomUUID(),name,kind:'Zone',parentId:null,tankIds:cfg.tanks.slice(i*6,i*6+6).map(t=>t.id)})),{id:crypto.randomUUID(),name:'柴油罐组',kind:'TankGroup',parentId:null,tankIds:cfg.tanks.filter(t=>t.medium==='柴油').map(t=>t.id)}];
 // Produce genuine good/invalid/recovered preview records, then return to 60-second archiving.
 cfg.historyIntervalSeconds=5;cfg.readingExpirySeconds=30;cfg.display={...cfg.display,refreshIntervalSeconds:1};cfg=await call('/api/config','PUT',cfg);
 await call('/api/acquisition/start','POST',{});await until(async()=>{const a=await call('/api/acquisition');return a.readings.length===12&&a.readings.every(r=>r.levelMm.isValid)});
 await delay(5500);gateway.setOffline(true);await delay(8500);gateway.setOffline(false);await until(async()=>(await call('/api/acquisition')).readings.every(r=>r.levelMm.isValid));await delay(5500);
 cfg=await call('/api/config','PUT',{...cfg,historyIntervalSeconds:60});
 await call('/api/users','POST',{login:'operatorqa',displayName:'模拟操作员',role:'Operator',enabled:true,securityVersion:0,newPassword:'Preview-Isolated-2026!'});
 await call('/api/users','POST',{login:'viewerqa',displayName:'模拟浏览员',role:'Viewer',enabled:true,securityVersion:0,newPassword:'Preview-Isolated-2026!'});
 const health=await call('/api/health');assert.equal(health.revision,'20261102-r20');
 const profileRoot=path.join(out,'client-profile'),clientExe=path.join(pkg,'app/client/LevelManager.Desktop.exe');
 const record={stage:'Running',revision:'r20',url:url+'/',driverPid:process.pid,hostPid:host.pid,gatewayPort,hostBuild:'sealed-r20',clientExe,profileRoot,dataRoot:path.join(out,'isolated-data'),previewDirectory:out,startedAtUtc:new Date().toISOString(),tanks:12,historySeconds:60,previewLogin:'previewqa',previewPassword:'Preview-Isolated-2026!',realDeviceAccess:false,usesCompletedSoakData:false,stopFile:path.join(out,'stop-preview.flag')};
 save('preview-session.json',record);fs.mkdirSync(path.join(root,'output/client-viewing'),{recursive:true});fs.writeFileSync(path.join(root,'output/client-viewing/r20-comparison-session.json'),JSON.stringify(record,null,2));
 save('startup-verification.json',{passed:true,revision:health.revision,acquisitionRunning:(await call('/api/acquisition')).running,instrumentWrites:gateway.writes,realDeviceAccess:false,allChannelsLoopback:cfg.channels.every(c=>c.protocol==='GatewayTcp'&&c.host==='127.0.0.1'&&c.tcpPort===gatewayPort)});
 console.log('R20 PREVIEW READY '+url+'; '+out);
 while(!fs.existsSync(record.stopFile)){if(host.exitCode!==null)throw Error('r20 preview host exited');await delay(1000)}
 save('preview-stopped.json',{atUtc:new Date().toISOString(),instrumentWrites:gateway.writes});
})().catch(e=>{save('failure.json',{error:e.stack});console.error(e);process.exitCode=1}).finally(async()=>{if(host&&host.exitCode===null)host.kill();await api?.dispose();gateway.close()});
