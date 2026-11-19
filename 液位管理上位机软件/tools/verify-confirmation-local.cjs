const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const {request}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const r=path.resolve(__dirname,'..'),qa=path.join(r,'output/confirmation-qa_20261113');
const read=p=>JSON.parse(fs.readFileSync(p,'utf8').replace(/^\uFEFF/,''));
(async()=>{
 const s=read(path.join(r,'output/client-viewing/r22-preview-session.json')),base=s.url.replace(/\/$/,'');
 const api=await request.newContext();
 try{
 const health=await(await api.get(base+'/api/health')).json();assert.equal(health.controlConfirmation,'20261113');
 let csrf=(await(await api.get(base+'/api/session')).json()).csrfToken;
 assert((await api.post(base+'/api/auth/login',{headers:{'X-CSRF-TOKEN':csrf},data:{login:s.previewLogin,password:s.previewPassword}})).ok());
 csrf=(await(await api.get(base+'/api/session')).json()).csrfToken;
 const config=await(await api.get(base+'/api/config')).json();
 assert.deepEqual(config,read(path.join(qa,'configuration-before-local.json')));
 const state=read(path.join(qa,'restart-state.json'));
 if(state.acquisitionRunning){const reply=await api.post(base+'/api/acquisition/start',{headers:{'X-CSRF-TOKEN':csrf},data:{}});assert(reply.ok(),await reply.text())}
 const live=await(await api.get(base+'/api/acquisition')).json();assert.equal(live.running,state.acquisitionRunning);
 fs.writeFileSync(path.join(qa,'local-update-verified.json'),JSON.stringify({passed:true,url:base,health,configurationPreserved:true,acquisitionRestored:live.running,updatedAtUtc:new Date().toISOString()},null,2));
 console.log(JSON.stringify({passed:true,controlConfirmation:health.controlConfirmation,tanks:config.tanks.length,configurationPreserved:true,acquisitionRunning:live.running}));
 }finally{await api.dispose()}
})().catch(e=>{console.error(e);process.exitCode=1});
