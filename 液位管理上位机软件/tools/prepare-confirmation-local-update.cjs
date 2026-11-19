const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const {request}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),qa=path.join(root,'output/confirmation-qa_20261113');
(async()=>{
 const session=JSON.parse(fs.readFileSync(path.join(root,'output/client-viewing/r22-preview-session.json'),'utf8').replace(/^\uFEFF/,''));
 const base=session.url.replace(/\/$/,'');assert.equal(base,'http://127.0.0.1:55407');
 const api=await request.newContext();
 try{
 let csrf=(await(await api.get(base+'/api/session')).json()).csrfToken;
 assert((await api.post(base+'/api/auth/login',{headers:{'X-CSRF-TOKEN':csrf},data:{login:session.previewLogin,password:session.previewPassword}})).ok());
 csrf=(await(await api.get(base+'/api/session')).json()).csrfToken;
 const config=await(await api.get(base+'/api/config')).json();
 const controls=await(await api.get(base+'/api/controls?unresolved=true')).json();
 assert.equal(controls.length,0,'Current operations need review; defer local restart');
 const tasks=await(await api.get(base+'/api/wireless-tasks?active=true')).json();assert(!tasks.some(t=>t.holdsTank));
 const live=await(await api.get(base+'/api/acquisition')).json();
 fs.writeFileSync(path.join(qa,'configuration-before-local.json'),JSON.stringify(config,null,2));
 fs.writeFileSync(path.join(qa,'restart-state.json'),JSON.stringify({acquisitionRunning:live.running},null,2));
 if(live.running){
 const stopped=await api.post(base+'/api/acquisition/stop',{headers:{'X-CSRF-TOKEN':csrf},data:{}});
 assert(stopped.ok(),await stopped.text());
 }
 console.log('Local acquisition safely stopped; no unresolved controls or measurement tasks');
 }finally{await api.dispose()}
})().catch(e=>{console.error(e);process.exitCode=1});
