// One-time local r23 demo initialization; refuses to overwrite an existing configuration.
const fs=require('fs'),path=require('path'),crypto=require('crypto'),assert=require('assert/strict');
const {request}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),dir=path.join(root,'output/r23-preview_20261113');
const identity=JSON.parse(fs.readFileSync(path.join(dir,'preview-identity.json'),'utf8'));
const previewUrl=new URL(identity.url);assert.equal(previewUrl.hostname,'127.0.0.1');assert.equal(previewUrl.protocol,'http:');assert.equal(identity.realDevices,false);
let api,csrf;
const login='previewqa',password='Preview-Isolated-2026!';
async function call(route,method='GET',data){const r=await api.fetch(identity.url.replace(/\/$/,'')+route,{method,data,headers:method==='GET'?{}:{'X-CSRF-TOKEN':csrf}});const text=await r.text();assert(r.ok(),route+' '+r.status()+' '+text);return text?JSON.parse(text):null}
(async()=>{
 api=await request.newContext();assert.equal((await call('/api/health')).build,'20261113-r23');
 let session=await call('/api/session');csrf=session.csrfToken;
 if(session.needsSetup){await call('/api/auth/setup','POST',{login,password,displayName:'本机预览管理员'});fs.writeFileSync(path.join(dir,'bootstrap-account.json'),JSON.stringify({login,createdBy:'r23-local-preview',atUtc:new Date().toISOString()}));}
 else if(!fs.existsSync(path.join(dir,'initialized.json'))&&!fs.existsSync(path.join(dir,'bootstrap-account.json')))throw Error('已有账号且无本机初始化标记，拒绝重置账号或数据');
 await call('/api/auth/login','POST',{login,password});csrf=(await call('/api/session')).csrfToken;
 let config=await call('/api/config');
 if(fs.existsSync(path.join(dir,'initialized.json'))){console.log(JSON.stringify({alreadyInitialized:true,tanks:config.tanks.length,url:identity.url}));return}
 assert.equal(config.tanks.length,0,'已有储罐，不会覆盖现有配置');assert.equal(config.channels.length,0,'已有通讯通道，不会覆盖现有配置');
 const modules=['average-temperature','operation-interface','operation-bottom','operation-lower','operation-density','operation-level-adjust'];
 await require('../tests/support/license.cjs').importTestLicense(api,identity.url.replace(/\/$/,''),csrf,{sequence:1,expiresAtUtc:null,modules,branding:{softwareName:'智能伺服液位管理系统',manufacturerName:'耐德东京计装',manufacturerInformation:'本机r23独立模拟预览'}});
 const tableBase={heightUnit:'Millimetre',volumeUnit:'CubicMetre',enabled:true,referenceDensityKgM3:1000,notes:'仅本机演示数据，不能用于现场计量',decimalRows:[]};
 const main=await call('/api/capacity-tables','POST',{...tableBase,id:crypto.randomUUID(),revision:0,number:'CT-SIM-R23',name:'3000m³模拟罐容主表',kind:'Main',points:[{height:0,volume:0},{height:3000,volume:750},{height:6000,volume:1500},{height:9000,volume:2250},{height:12000,volume:3000}]});
 const pressure=await call('/api/capacity-tables','POST',{...tableBase,id:crypto.randomUUID(),revision:0,number:'PT-SIM-R23',name:'模拟静压力修正表',kind:'StaticPressure',points:[{height:0,volume:0},{height:3000,volume:.3},{height:6000,volume:.6},{height:9000,volume:.9},{height:12000,volume:1.2}]});
 const channel=crypto.randomUUID();
 config.channels=[{id:channel,name:'r23独立模拟网关（本机16槽位）',protocol:'GatewayTcp',host:'127.0.0.1',tcpPort:identity.gatewayPort,unitId:1,portName:'',pollIntervalMs:1000,readTimeoutMs:1000,readRetries:1,enabled:true}];
 const media=['柴油','汽油','煤油'].map((name,i)=>({id:crypto.randomUUID(),code:['DIESEL','GASOLINE','KEROSENE'][i],name,category:'',color:['#6ea99e','#d09b5e','#839db1'][i],referenceTemperatureC:20,standardDensityKgM3:[850,750,790][i]}));config.media=media;
 config.tanks=Array.from({length:16},(_,i)=>{
  const n=(i<8?101:201)+(i%8),m=media[Math.floor(i/2)%3];
  return {id:crypto.randomUUID(),code:'T-'+n,instrumentTag:'LT-'+n,name:m.name+'模拟储罐 '+n,channelId:channel,address:i+1,medium:m.name,mediumId:m.id,zone:i<8?'一罐区':'二罐区',shape:['Vertical','Vertical','FloatingRoof','Sphere'][i%4],heightMm:12000,diameterMm:18000,nominalCapacityM3:3000,temperatureMode:i%3===0?'Ats':'None',atsPointCount:i%3===0?6:0,atsHeightsMm:i%3===0?[500,2500,4500,6500,8500,10500]:[],enabled:true,hidden:false,notes:`r23独立模拟数据；通讯仅本机${identity.gatewayPort}，不连接真实设备。`,controls:{allowedCommands:['MeasureI','Stop','Hoist','MeasureII','MeasureIII','Lower','Density','Repeatability','OscillationAvoidance','RemoteLevel'],confirmationTimeoutSeconds:30,movementTimeoutSeconds:600},alarms:{level:{lowLow:500,low:1500,high:11000,highHigh:11500,hysteresis:100,delaySeconds:0}},metering:{enabled:true,mainTableId:main.id,staticPressureTableId:pressure.id,waterHeightMm:i===0?100:0,waterVolumeM3:0,manualOperatingDensityKgM3:[800,740,790][Math.floor(i/2)%3],densitySource:i%2?'Instrument':'Manual',showInstrumentDensity:true,standardDensity20KgM3:m.standardDensityKgM3}};
 });
 config.groups=[{id:crypto.randomUUID(),name:'一罐区',kind:'Zone',parentId:null,tankIds:config.tanks.slice(0,8).map(t=>t.id)},{id:crypto.randomUUID(),name:'二罐区',kind:'Zone',parentId:null,tankIds:config.tanks.slice(8).map(t=>t.id)}];
 config.startReadOnlyAcquisitionAtStartup=true;config.historyIntervalSeconds=60;config.retentionMonths=12;config.readingExpirySeconds=30;
 config=await call('/api/config','PUT',config);await call('/api/acquisition/start','POST',{});
 let state;for(let i=0;i<90;i++){state=await call('/api/acquisition');if(state.readings.length===16&&state.readings.every(r=>r.levelMm.isValid))break;await new Promise(r=>setTimeout(r,500))}
 assert.equal(state.readings.filter(r=>r.levelMm.isValid).length,16,'模拟16台应有有效液位');
 const saved={build:'20261113-r23',url:identity.url,login,password,configurationRevision:config.revision,tanks:16,gatewayHost:'127.0.0.1',gatewayPort:identity.gatewayPort,realDeviceAccess:false,serialChannels:0,allModulesForLocalPreview:true,historySeconds:60,readingExpirySeconds:30,atUtc:new Date().toISOString()};
 fs.writeFileSync(path.join(dir,'initialized.json'),JSON.stringify(saved,null,2));
 fs.writeFileSync(path.join(dir,'startup-verification.json'),JSON.stringify({...saved,passed:true,readings:state.readings.map(r=>({tankId:r.tankId,levelValid:r.levelMm.isValid,netVolumeValid:r.netVolumeM3.isValid}))},null,2));
 console.log(JSON.stringify({passed:true,url:identity.url,tanks:16,configurationRevision:config.revision,allReadingsValid:true}));
})().catch(e=>{fs.writeFileSync(path.join(dir,'initialization-error.json'),JSON.stringify({error:String(e),stack:e.stack},null,2));console.error(e);process.exitCode=1}).finally(async()=>{if(api)await api.dispose()});
