const fs=require('fs'),path=require('path'),net=require('net'),assert=require('assert/strict');
const {request}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),view=path.join(root,'output/client-viewing');
const read=p=>JSON.parse(fs.readFileSync(p,'utf8').replace(/^\uFEFF/,''));
const snapshot=read(path.join(view,'r22-update-snapshot.json')),s=read(path.join(snapshot.out,'before-session.json'));
const save=(n,v)=>fs.writeFileSync(path.join(snapshot.out,n),JSON.stringify(v,null,2));
const delay=ms=>new Promise(r=>setTimeout(r,ms));
function alive(pid){try{process.kill(pid,0);return true}catch{return false}}
function listening(port){return new Promise(resolve=>{const socket=net.connect({host:'127.0.0.1',port});let done=false;const finish=value=>{if(done)return;done=true;socket.destroy();resolve(value)};socket.once('connect',()=>finish(true));socket.once('error',()=>finish(false));socket.setTimeout(700,()=>finish(false));})}
let api,csrf;const base=s.url.replace(/\/$/,'');
async function call(route,method='GET',data){const r=await api.fetch(base+route,{method,data,headers:method==='GET'?{}:{'X-CSRF-TOKEN':csrf}});const text=await r.text();assert.equal(r.status(),200,route+': '+text);return text?JSON.parse(text):null}
(async()=>{
 assert.equal(s.hostBuild,'20261107-r22-u1');assert.equal(base,'http://127.0.0.1:55407');assert.equal(s.gatewayPort,55406);
 assert.equal(path.resolve(s.stopFile),path.join(root,'output/r22-preview-1794033145639/stop-preview-u1.flag'));
 assert.equal(snapshot.unresolved,0);assert.equal(snapshot.activeTasks,0);assert(fs.statSync(snapshot.backupPath).size>0);
 api=await request.newContext();assert.equal((await call('/api/health')).revision,'20261107-r22');csrf=(await call('/api/session')).csrfToken;
 await call('/api/auth/login','POST',{login:s.previewLogin,password:s.previewPassword});csrf=(await call('/api/session')).csrfToken;
 assert.deepEqual(await call('/api/config'),read(path.join(snapshot.out,'configuration.json')),'configuration changed since backup; take a new backup');
 const live=await call('/api/acquisition');if(live.running){const stopped=await call('/api/acquisition/stop','POST',{});assert.equal(stopped.running,false);save('acquisition-stopped.json',stopped);}
 fs.writeFileSync(s.stopFile,JSON.stringify({requestedAtUtc:new Date().toISOString(),reason:'User requested r22 update and restart; business backup saved',backup:snapshot.backupPath}));
 let stopped=false;for(let n=0;n<150;n++){if(!alive(s.driverPid)&&!alive(s.hostPid)&&!(await listening(55407))&&!(await listening(55406))){stopped=true;break}await delay(200)}assert(stopped,'old r22 did not exit');
 const record={...s,stage:'StoppedForUpdate',stoppedAtUtc:new Date().toISOString(),updateEvidence:snapshot.out};fs.writeFileSync(path.join(view,'r22-preview-session.json'),JSON.stringify(record,null,2));
 save('stop-verification.json',{passed:true,hostExited:true,driverExited:true,portsClosed:[55407,55406],acquisitionStopped:true,dataRoot:s.dataRoot,backupPath:snapshot.backupPath,filesDeleted:false});console.log('OLD R22 STOPPED; backup '+snapshot.out);
})().catch(e=>{save('stop-failure.json',{error:e.stack});console.error(e);process.exitCode=1}).finally(async()=>{await api?.dispose()});
