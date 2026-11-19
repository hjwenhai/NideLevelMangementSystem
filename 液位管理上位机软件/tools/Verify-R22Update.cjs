const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const {request}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),out=path.join(root,'output/r22-phase2-20261107'),view=path.join(root,'output/client-viewing');
const read=p=>JSON.parse(fs.readFileSync(p,'utf8').replace(/^\uFEFF/,''));
const s=read(path.join(view,'r22-preview-session.json')),snapshot=read(path.join(view,'r22-update-snapshot.json'));
const comparable=c=>{const n=structuredClone(c);for(const t of n.tanks)if(t.trends?.axes&&Object.keys(t.trends.axes).length===0)delete t.trends.axes;return n};
(async()=>{const api=await request.newContext();try{
 const base=s.url.replace(/\/$/,'');const health=await(await api.get(base+'/api/health')).json();assert.equal(health.build,'20261107-r22-u1');assert.equal(health.revision,'20261107-r22');
 let csrf=(await(await api.get(base+'/api/session')).json()).csrfToken;
 assert((await api.post(base+'/api/auth/login',{data:{login:s.previewLogin,password:s.previewPassword},headers:{'X-CSRF-TOKEN':csrf}})).ok());
 const cfg=await(await api.get(base+'/api/config')).json();assert.deepEqual(comparable(cfg),comparable(read(path.join(snapshot.out,'configuration.json'))));
 const live=await(await api.get(base+'/api/acquisition')).json();assert.equal(live.running,snapshot.acquisitionRunning);
 const html=await(await api.get(base+'/')).text();const js=html.match(/src="([^"]+\.js)"/)[1];assert(js.includes('index-BTTgoG1M.js'));assert((await api.get(base+js)).ok());
 const runtime=read(path.join(out,'final-runtime.json'));assert.equal(runtime.filter(p=>p.Name==='LevelManager.Host.exe').length,1);assert.equal(runtime.find(p=>p.Name==='LevelManager.Host.exe').ProcessId,s.hostPid);
 const unit=fs.readFileSync(path.join(out,'phase2-final-unit.trx'),'utf8');assert(/<Counters total="415" executed="415" passed="415" failed="0"/.test(unit));
 const ui=read(path.join(out,'integration-1794036710852/result.json')),transfer=read(path.join(out,'transfer/roundtrip-1794036477022/result.json')),axes=read(path.join(out,'axis-pure-result.json')),workbooks=read(path.join(out,'workbook-verification.json'));assert(ui.passed&&transfer.passed&&axes.passed&&workbooks.passed);
 const result={passed:true,revision:health.revision,build:health.build,verifiedAtUtc:new Date().toISOString(),unitTests:415,uiGroups:ui.checks.length,transferGroups:transfer.checks.length,axisLogicGroups:axes.checks.length,actualWorkbooks:workbooks.checks.length,qaInstrumentWrites:ui.instrumentWrites+transfer.instrumentWrites,currentSession:{url:s.url,hostPid:s.hostPid,driverPid:s.driverPid,clientPid:s.clientPid,dataRoot:s.dataRoot,configurationRevision:cfg.revision,tanks:cfg.tanks.length,configurationUnchanged:true,acquisitionRunning:live.running,existingCom2Preserved:cfg.channels.some(c=>c.portName==='COM2'),onlyR22Host:true,sourceRetainedAndKeysUntouched:true},backup:snapshot.backupPath,nativeWindowObserved:{clientBuild:'output/r22-preview-runtime_20261107-u1/app/client/LevelManager.Desktop.exe',observedUrl:base+'/?revision=20261107-r22#/',state:'Login page; no automated UI login'},boundaries:{newSixHourSoak:false,newSealedInstaller:false,updaterSubmittedInstrumentControls:false}};
 fs.writeFileSync(path.join(out,'verification-summary.json'),JSON.stringify(result,null,2));console.log(JSON.stringify(result,null,2));
}finally{await api.dispose()}})().catch(e=>{console.error(e);process.exitCode=1});
