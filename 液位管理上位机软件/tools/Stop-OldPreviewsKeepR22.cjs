const fs=require('fs'),path=require('path'),net=require('net'),assert=require('assert/strict');
const {request}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),view=path.join(root,'output/client-viewing'),out=path.join(view,'stop-old-keep-r22-'+Date.now());fs.mkdirSync(out,{recursive:true});
const read=p=>JSON.parse(fs.readFileSync(p,'utf8').replace(/^\uFEFF/,''));const save=(n,v)=>fs.writeFileSync(path.join(out,n),JSON.stringify(v,null,2));const delay=ms=>new Promise(r=>setTimeout(r,ms));
const keep=read(path.join(view,'r22-preview-session.json'));assert.equal(keep.revision,'r22');
const targets=[['r20-comparison-session.json','20261102-r20','r20-comparison-preview-'],['r21-preview-session.json','20261106-r21','r21-preview-']];
function alive(pid){try{process.kill(pid,0);return true}catch{return false}}
function listening(port){return new Promise(resolve=>{const socket=net.connect({host:'127.0.0.1',port});let done=false;function end(value){if(done)return;done=true;socket.destroy();resolve(value)}socket.once('connect',()=>end(true));socket.once('error',()=>end(false));socket.setTimeout(700,()=>end(false));})}
async function stopOne(file,expected,prefix){
 const filePath=path.join(view,file),s=read(filePath);assert.equal(s.revision,expected.endsWith('r20')?'r20':'r21');assert.equal(new URL(s.url).hostname,'127.0.0.1');
 assert.notEqual(s.hostPid,keep.hostPid);assert.notEqual(s.driverPid,keep.driverPid);assert.notEqual(s.dataRoot,keep.dataRoot);
 const directory=path.resolve(s.previewDirectory);assert(directory.startsWith(path.join(root,'output',prefix)));assert.equal(path.resolve(s.stopFile),path.join(directory,'stop-preview.flag'));
 const api=await request.newContext();let csrf;const base=s.url.replace(/\/$/,'');
 async function call(route,method='GET',data){const reply=await api.fetch(base+route,{method,data,headers:method==='GET'?{}:{'X-CSRF-TOKEN':csrf}});const text=await reply.text();assert.equal(reply.status(),200,route+': '+text);return text?JSON.parse(text):null}
 try{
  const health=await call('/api/health');assert.equal(health.revision,expected);save(s.revision+'-health.json',health);
  csrf=(await call('/api/session')).csrfToken;await call('/api/auth/login','POST',{login:s.previewLogin,password:s.previewPassword});csrf=(await call('/api/session')).csrfToken;
  const config=await call('/api/config'),live=await call('/api/acquisition'),controls=await call('/api/controls'),tasks=await call('/api/wireless-tasks');
  save(s.revision+'-configuration.json',config);save(s.revision+'-acquisition-before.json',live);save(s.revision+'-controls.json',controls);save(s.revision+'-wireless-tasks.json',tasks);
  const job=await call('/api/storage/backup','POST',{});let status;
  for(let n=0;n<150;n++){status=(await call('/api/data-jobs')).find(j=>j.id===job.id);if(['Completed','Failed','Cancelled','Interrupted'].includes(status?.state))break;await delay(100)}
  assert.equal(status?.state,'Completed');const backup=await api.get(base+'/api/data-jobs/'+job.id+'/download');assert(backup.ok());const backupPath=path.join(out,s.revision+'-business-before-stop.zip');fs.writeFileSync(backupPath,await backup.body());
  if(live.running){const stopped=await call('/api/acquisition/stop','POST',{});assert.equal(stopped.running,false);save(s.revision+'-acquisition-stopped.json',stopped);}
  fs.writeFileSync(s.stopFile,JSON.stringify({requestedAtUtc:new Date().toISOString(),reason:'User requested stopping all older backends and keeping r22',keepRevision:'r22'}));
  const port=Number(new URL(s.url).port);let exited=false;
  for(let n=0;n<80;n++){if(!alive(s.driverPid)&&!alive(s.hostPid)&&!(await listening(port))&&!(await listening(s.gatewayPort))){exited=true;break}await delay(200)}
  assert(exited,s.revision+' driver/host/gateway have not exited');
  const stoppedAtUtc=new Date().toISOString();const updated={...s,stage:'StoppedByUser',stoppedAtUtc,stopReason:'Only r22 preview remains running',shutdownEvidence:out};fs.writeFileSync(filePath,JSON.stringify(updated,null,2));fs.writeFileSync(path.join(directory,'preview-session.json'),JSON.stringify(updated,null,2));
  const result={revision:s.revision,stopped:true,hostPid:s.hostPid,driverPid:s.driverPid,hostPort:port,gatewayPort:s.gatewayPort,portsClosed:true,processesExited:true,acquisitionStopped:true,backupPath,dataRoot:s.dataRoot,configurationRevision:config.revision,controlsSaved:controls.length,wirelessTasksSaved:tasks.length,filesDeleted:false,stoppedAtUtc};save(s.revision+'-stop-result.json',result);return result;
 }finally{await api.dispose()}
}
(async()=>{
 const probe=await request.newContext();try{assert.equal((await(await probe.get(keep.url+'api/health')).json()).revision,'20261107-r22')}finally{await probe.dispose()}
 const results=[];for(const t of targets)results.push(await stopOne(...t));
 const api=await request.newContext();let health;try{health=await(await api.get(keep.url+'api/health')).json();assert.equal(health.revision,'20261107-r22');assert(alive(keep.hostPid)&&alive(keep.driverPid));assert(await listening(keep.gatewayPort))}finally{await api.dispose()}
 const result={passed:true,out,stopped:results,r22:{revision:health.revision,url:keep.url,hostPid:keep.hostPid,driverPid:keep.driverPid,gatewayPort:keep.gatewayPort,unchanged:true},filesDeleted:false};save('result.json',result);fs.writeFileSync(path.join(view,'stop-old-keep-r22-latest.json'),JSON.stringify(result,null,2));console.log(JSON.stringify(result,null,2));
})().catch(error=>{save('failure.json',{error:error.stack});console.error(error);process.exitCode=1});
