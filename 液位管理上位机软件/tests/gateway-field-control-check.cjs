// Isolated loopback reproduction of the field profile: no optional density group, IR51 bit15=0.
const fs=require('fs'),path=require('path'),net=require('net'),crypto=require('crypto'),assert=require('assert/strict');
const{spawn}=require('child_process');
const{request}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),out=path.join(root,'output/gateway-field-control-'+Date.now());fs.mkdirSync(out,{recursive:true});
const gateway=require('./support/mock-gateway.cjs')({control:true,remoteLevel:true,optionalDensityUnsupported:true,remoteFlag:false});
let host,api,url,csrf,config;const checks=[],pause=ms=>new Promise(r=>setTimeout(r,ms));
async function until(fn){for(let i=0;i<200;i++){const value=await fn();if(value)return value;await pause(100)}throw Error('local field control timeout')}
async function call(route,method='GET',data,status=200){const r=await api.fetch(url+route,{method,data,headers:method==='GET'?{}:{'X-CSRF-TOKEN':csrf}});const text=await r.text();assert.equal(r.status(),status,route+': '+text);return text?JSON.parse(text):null}
async function finish(record){return until(async()=>(await call('/api/controls?tank='+record.target.tank.id)).find(r=>r.id===record.id&&['ReadbackConfirmed','Rejected','Unknown'].includes(r.state)))}
async function command(tank,command){return finish(await call('/api/tanks/'+tank.id+'/controls','POST',{configurationRevision:config.revision,command,confirmed:true}))}
async function reset(record){const r=await finish(await call('/api/controls/'+record.id+'/reset','POST',{reason:'隔离模拟已核对原模式与结果，仅清所选通道',confirmed:true}));assert.equal(r.state,'ReadbackConfirmed')}
(async()=>{
 const tcpPort=await gateway.listen(),probe=net.createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));url='http://127.0.0.1:'+probe.address().port;await new Promise(r=>probe.close(r));api=await request.newContext();
 const log=fs.openSync(path.join(out,'host.log'),'a');const build=process.env.LEVEL_UI_BUILD_ROOT||'output/r14-gateway-control-build_20261030/server';
 host=spawn(path.join(root,build,'LevelManager.Host.exe'),['--data-root',path.join(out,'isolated-data'),'--urls',url],{windowsHide:true,stdio:['ignore',log,log]});fs.closeSync(log);
 await until(async()=>{try{return(await api.get(url+'/api/health')).ok()}catch{return false}});csrf=(await call('/api/session')).csrfToken;
 await call('/api/auth/setup','POST',{login:'fieldqa',displayName:'隔离网关动作测试',password:'Field-Loopback-Test-2026!'});await call('/api/auth/login','POST',{login:'fieldqa',password:'Field-Loopback-Test-2026!'});csrf=(await call('/api/session')).csrfToken;
 await require('./support/license.cjs').importTestLicense(api,url,csrf,{expiresAtUtc:null});
 config=await call('/api/config');const channelId=crypto.randomUUID();config.channels=[{id:channelId,name:'本机隔离现场配置模拟',protocol:'GatewayTcp',host:'127.0.0.1',tcpPort,portName:'',unitId:1,pollIntervalMs:500,readTimeoutMs:400,readRetries:0,enabled:true}];
 const commands=['MeasureI','MeasureII','MeasureIII','Hoist','Lower','Density','Repeatability','OscillationAvoidance','Stop'];
 config.tanks=[1,64].map(address=>({id:crypto.randomUUID(),code:'FIELD-'+address,medium:'柴油',channelId,address,shape:'Vertical',heightMm:12000,temperatureMode:'None',enabled:true,hidden:false,controls:{allowedCommands:commands,confirmationTimeoutSeconds:3,movementTimeoutSeconds:600}}));config.readingExpirySeconds=3;config.historyIntervalSeconds=60;
 config=await call('/api/config','PUT',config);await call('/api/acquisition/start','POST',{});await until(async()=>(await call('/api/acquisition')).readings.every(r=>r.floatPositionMm.isValid&&r.instrumentStatus===1&&r.instrumentFault===0));
 for(const tank of config.tanks){
  for(const name of ['MeasureI','Hoist','Stop','Lower','Stop','MeasureII','MeasureIII','Density','Repeatability','OscillationAvoidance','MeasureI']){
   const record=await command(tank,name);assert.equal(record.state,'ReadbackConfirmed',tank.code+' '+name+': '+record.result);await reset(record);
   checks.push(tank.code+' '+name+'：命令提交、模式回读、原通道人工复位');
  }
 }
 gateway.setStatusGroup(4);await until(async()=>(await call('/api/acquisition')).readings.every(r=>r.instrumentStatus===null));const beforeMissing=gateway.writes;
 const rejected=await call('/api/tanks/'+config.tanks[0].id+'/controls','POST',{configurationRevision:config.revision,command:'Hoist',confirmed:true},409);assert.match(rejected.error,/仪表状态/);assert.equal(gateway.writes,beforeMissing);checks.push('实际状态组无效时仍拒绝，无写入');
 gateway.setStatusGroup(null);gateway.setInstrumentFault(32);await until(async()=>(await call('/api/acquisition')).readings.every(r=>r.instrumentFault===32));const beforeFault=gateway.writes;const fault=await command(config.tanks[0],'Hoist');assert.equal(fault.state,'Rejected');assert.equal(gateway.writes,beforeFault);checks.push('仪表真实故障时仍拒绝，无写入');
 gateway.setInstrumentFault(0);await until(async()=>(await call('/api/acquisition')).readings.every(r=>r.instrumentFault===0));
 gateway.unknownNext();const unknown=await command(config.tanks[0],'Lower');assert.equal(unknown.state,'Unknown');const afterUnknown=gateway.writes;await pause(500);assert.equal(gateway.writes,afterUnknown);checks.push('未知结果保留、不自动重发或复位');
 assert(gateway.writeFrames.some(f=>f.at===30756&&f.values[0]===5));assert(gateway.writeFrames.some(f=>f.at===30756&&f.values[0]===6));assert(gateway.writeFrames.some(f=>f.at===30763&&f.values[0]===1));
 fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:true,checks,fieldProfile:{optionalDensityGroup:'unsupported',ir51RemoteBit:0},writeFrames:gateway.writeFrames,realDeviceAccess:false,packageBuild:build},null,2));console.log('PASS '+checks.length+' field-profile control groups: '+out);
})().catch(e=>{fs.writeFileSync(path.join(out,'failed.json'),JSON.stringify({error:e.stack,checks},null,2));console.error(e);process.exitCode=1}).finally(async()=>{await api?.dispose();host?.kill();gateway.close()});
