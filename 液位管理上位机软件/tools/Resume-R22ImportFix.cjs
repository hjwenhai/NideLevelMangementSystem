// Resume the EXISTING r22 data/profile. No setup, configuration PUT, license
// issuance, control submission or completed-soak database is used here.
const fs=require('fs'),path=require('path'),net=require('net'),assert=require('assert/strict');
const {spawn}=require('child_process');
const {request}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),view=path.join(root,'output/client-viewing');
const read=p=>JSON.parse(fs.readFileSync(p,'utf8').replace(/^\uFEFF/,''));
const snapshot=read(path.join(view,'r22-update-snapshot.json'));
const prior=read(path.join(snapshot.out,'before-session.json'));
const before=read(path.join(snapshot.out,'configuration.json'));
const runtime=path.join(root,'output/r22-preview-runtime_20261107-u2');
const out=path.join(snapshot.out,'resumed');fs.mkdirSync(out,{recursive:true});
const save=(n,v)=>fs.writeFileSync(path.join(out,n),JSON.stringify(v,null,2));
const delay=ms=>new Promise(r=>setTimeout(r,ms));
const gateway=require('../tests/support/mock-gateway.cjs')({control:true,remoteLevel:true,strictControl:true,optionalDensityUnsupported:true,remoteFlag:false});
let api,host,csrf;const base=prior.url.replace(/\/$/,'');
async function call(route,method='GET',data){const r=await api.fetch(base+route,{method,data,headers:method==='GET'?{}:{'X-CSRF-TOKEN':csrf}});const t=await r.text();assert.equal(r.status(),200,route+': '+t);return t?JSON.parse(t):null}
function alive(pid){try{process.kill(pid,0);return true}catch{return false}}
function listening(port){return new Promise(resolve=>{const s=net.connect({host:'127.0.0.1',port});let done=false;const finish=v=>{if(done)return;done=true;s.destroy();resolve(v)};s.once('connect',()=>finish(true));s.once('error',()=>finish(false));s.setTimeout(700,()=>finish(false));})}
function comparable(c){const n=structuredClone(c);for(const t of n.tanks)if(t.trends?.axes&&Object.keys(t.trends.axes).length===0)delete t.trends.axes;return n;}
(async()=>{
 assert.equal(prior.revision,'r22');assert.equal(base,'http://127.0.0.1:55407');assert.equal(prior.gatewayPort,55406);
 assert.equal(path.resolve(prior.dataRoot),path.join(root,'output/r22-preview-1794033145639/isolated-data'));
 assert.equal(snapshot.unresolved,0);assert.equal(snapshot.activeTasks,0);
 assert(!alive(prior.driverPid)&&!alive(prior.hostPid),'old r22 still running');
 assert(!(await listening(55407))&&!(await listening(55406)),'old r22 ports still open');
 gateway.setAnalog(12000,14000);
 await new Promise((resolve,reject)=>{gateway.server.once('error',reject);gateway.server.listen(55406,'127.0.0.1',resolve)});
 const log=fs.openSync(path.join(out,'host-u2.log'),'a');
 host=spawn(path.join(runtime,'app/server/LevelManager.Host.exe'),['--data-root',prior.dataRoot,'--urls',base],{windowsHide:true,env:{...process.env,DOTNET_ROOT:path.join(root,'.build-tools/dotnet')},stdio:['ignore',log,log]});fs.closeSync(log);
 api=await request.newContext();let ready=false;
 for(let n=0;n<200;n++){try{if((await api.get(base+'/api/health')).ok()){ready=true;break}}catch{}if(host.exitCode!==null)throw Error('updated host exited');await delay(100)}assert(ready,'updated host not ready');
 const health=await call('/api/health');assert.equal(health.revision,'20261107-r22');assert.equal(health.build,'20261107-r22-u2');
 csrf=(await call('/api/session')).csrfToken;
 await call('/api/auth/login','POST',{login:prior.previewLogin,password:prior.previewPassword});csrf=(await call('/api/session')).csrfToken;
 const cfg=await call('/api/config');assert.deepEqual(comparable(cfg),comparable(before),'existing configuration changed');
 save('configuration-after.json',cfg);save('health.json',health);
 const acquisitionCanResume=snapshot.acquisitionRunning&&cfg.tanks.some(t=>t.enabled&&cfg.channels.some(c=>c.id===t.channelId&&c.enabled));
 if(acquisitionCanResume)await call('/api/acquisition/start','POST',{});
 const acquisition=await call('/api/acquisition');assert.equal(acquisition.running,acquisitionCanResume);
 const record={...prior,stage:'Running',driverPid:process.pid,hostPid:host.pid,clientPid:null,hostBuild:health.build,clientExe:path.join(runtime,'app/client/LevelManager.Desktop.exe'),startedAtUtc:new Date().toISOString(),tanks:cfg.tanks.length,historySeconds:cfg.historyIntervalSeconds,realDeviceAccess:null,serialChannelConfigured:cfg.channels.some(c=>c.enabled&&c.protocol!=='GatewayTcp'),configurationPreserved:true,updateEvidence:snapshot.out,stopFile:path.join(prior.previewDirectory,'stop-preview-u2.flag')};
 assert(!fs.existsSync(record.stopFile),'updated stop flag already exists');
 fs.writeFileSync(path.join(view,'r22-preview-session.json'),JSON.stringify(record,null,2));fs.writeFileSync(path.join(prior.previewDirectory,'preview-session.json'),JSON.stringify(record,null,2));
 save('startup-verification.json',{passed:true,revision:health.revision,build:health.build,url:prior.url,configurationRevision:cfg.revision,tanks:cfg.tanks.length,dataRoot:prior.dataRoot,profileRoot:prior.profileRoot,configurationUnchanged:true,acquisitionResumed:acquisition.running,instrumentWrites:gateway.writes,existingChannelsPreserved:cfg.channels.map(c=>({protocol:c.protocol,serial:c.portName,host:c.host,port:c.tcpPort})),note:'Existing configuration and COM2 retained. Zero tanks: acquisition remains stopped until user imports and starts it. No instrument commands submitted by updater.'});
 console.log('R22 UPDATE READY '+prior.url+'; '+out);
 while(!fs.existsSync(record.stopFile)){if(host.exitCode!==null)throw Error('updated r22 host exited');await delay(1000)}
 save('stopped.json',{atUtc:new Date().toISOString(),instrumentWrites:gateway.writes});
})().catch(e=>{save('failure.json',{error:e.stack});console.error(e);process.exitCode=1}).finally(async()=>{if(host&&host.exitCode===null)host.kill();await api?.dispose();gateway.close()});
