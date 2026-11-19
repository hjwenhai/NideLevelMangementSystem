const fs=require('fs'),path=require('path');
const {request}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const r=path.resolve(__dirname,'..'),file=path.join(r,'output/client-viewing/r22-preview-session.json');
(async()=>{
 const s=JSON.parse(fs.readFileSync(file,'utf8').replace(/^\uFEFF/,''));
 const api=await request.newContext({timeout:4000});const base=s.url.replace(/\/$/,'');
 try{
  const health=await (await api.get(base+'/api/health')).json();
  let session=await(await api.get(base+'/api/session')).json();
  const login=await api.post(base+'/api/auth/login',{headers:{'X-CSRF-TOKEN':session.csrfToken},data:{login:s.previewLogin,password:s.previewPassword}});
  if(!login.ok()){console.log(JSON.stringify({online:true,build:health.build,configurationRead:false}));return}
  const config=await(await api.get(base+'/api/config')).json();
  const unresolved=await(await api.get(base+'/api/controls?unresolved=true')).json();
  const tasks=await(await api.get(base+'/api/wireless-tasks?active=true')).json();
  const report={online:true,health,configurationRevision:config.revision,tanks:config.tanks.map(t=>({code:t.code,confirmationTimeoutSeconds:t.controls.confirmationTimeoutSeconds,movementTimeoutSeconds:t.controls.movementTimeoutSeconds})),unresolved:unresolved.map(x=>({id:x.id,state:x.state})),activeTasks:tasks.filter(t=>t.holdsTank).length};
  fs.mkdirSync(path.join(r,'output/confirmation-qa_20261113'),{recursive:true});
  fs.writeFileSync(path.join(r,'output/confirmation-qa_20261113/local-before.json'),JSON.stringify(report,null,2));
  console.log(JSON.stringify({online:true,tanks:report.tanks,pending:report.unresolved,activeTasks:report.activeTasks}));
 }catch(e){console.log(JSON.stringify({online:false,error:e.message}))}
 finally{await api.dispose()}
})();
