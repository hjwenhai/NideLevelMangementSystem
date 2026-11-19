// Internal QA controller. Changes an existing isolated run's acceptance duration
// without restarting its host, resetting its clock, or changing its fixtures.
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const {spawnSync}=require('child_process');
const {request}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const runRoot=path.resolve(process.argv[2]||''),targetSeconds=Number(process.argv[3]||43200);
assert(runRoot.startsWith(path.resolve(__dirname,'../output/soak')+path.sep));
assert(Number.isInteger(targetSeconds)&&targetSeconds>=150&&targetSeconds<=259200);
const read=name=>JSON.parse(fs.readFileSync(path.join(runRoot,name),'utf8').replace(/^\uFEFF/,''));
const save=(name,data)=>{const f=path.join(runRoot,name);fs.writeFileSync(f+'.tmp',JSON.stringify(data,null,2));fs.renameSync(f+'.tmp',f)};
const run=read('run.json'),started=Date.parse(run.startedAtUtc),end=started+targetSeconds*1000;
assert.equal(run.realDeviceAccess,false);assert.equal(run.windowsServiceInstalled,false);
assert.equal(run.tanks,128);assert.equal(run.historySeconds,60);
assert(run.dataRoot.startsWith(runRoot+path.sep));assert(/^http:\/\/127\.0\.0\.1:\d+$/.test(run.url));
assert(Number.isInteger(run.nodePid)&&run.nodePid>0&&run.nodePid!==process.pid);
const previous=fs.existsSync(path.join(runRoot,'duration-adjustment.json'))?read('duration-adjustment.json'):null;
if(previous){assert(process.argv.includes('--resume')||process.argv.includes('--replace-stopped-controller'),'a controller is already recorded');assert(!fs.existsSync(path.join(runRoot,'original-progress-at-handoff.json')),'Cannot resume after handoff');if(process.argv.includes('--resume'))assert(fs.existsSync(path.join(runRoot,'target-failure.json')),'No failed controller to resume');else{let exists=true;try{process.kill(previous.controllerPid,0)}catch(e){assert.equal(e.code,'ESRCH');exists=false}assert(!exists,'Previous controller is still running')}if(fs.existsSync(path.join(runRoot,'target-failure.json')))fs.renameSync(path.join(runRoot,'target-failure.json'),path.join(runRoot,'target-failure-'+Date.now()+'.json'));save('duration-adjustment-previous-'+Date.now()+'.json',previous)}
const adjustment={originalTargetSeconds:run.durationSeconds,targetSeconds,startedAtUtc:run.startedAtUtc,
 targetAtUtc:new Date(end).toISOString(),adjustedAtUtc:previous?.adjustedAtUtc||new Date().toISOString(),controllerPid:process.pid,
 existingElapsedPreserved:true,hostRestartedForAdjustment:false,
 note:(targetSeconds===43200?'12小时':targetSeconds+'秒')+'为阶段验证，不等同于72小时可靠性验证。结束时仅收尾本次隔离测试的进程，保留全部原始记录。'};
save('duration-adjustment.json',adjustment);
const ps=path.join(process.env.SystemRoot,'System32/WindowsPowerShell/v1.0/powershell.exe');
function powershell(code){const r=spawnSync(ps,['-NoProfile','-Command',"[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);"+code],{encoding:'utf8',windowsHide:true});assert.equal(r.status,0,r.stderr||r.stdout);return r.stdout.replace(/^\uFEFF/,'').trim()}
const quote=s=>"'"+s.replace(/'/g,"''")+"'";
function processTree(){
 return JSON.parse(powershell(`$env:PSModulePath=Join-Path $PSHOME 'Modules'; $all=@(Get-CimInstance Win32_Process); $root=$all|Where-Object ProcessId -eq ${run.nodePid}; if(!$root -or $root.Name -ne 'node.exe' -or $root.CommandLine -notlike '*native-soak-check.cjs*'){throw 'Original isolated driver identity changed'}; $ids=@(${run.nodePid}); do { $old=$ids.Count; $ids+=@($all|Where-Object { $ids -contains [int]$_.ParentProcessId -and $ids -notcontains [int]$_.ProcessId }|ForEach-Object {[int]$_.ProcessId}); } while($ids.Count -gt $old); @($all|Where-Object {$ids -contains [int]$_.ProcessId}|Select-Object ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine,@{n='Created';e={$_.CreationDate.ToUniversalTime().ToString('o')}})|ConvertTo-Json -Depth 4 -Compress`));
}
async function main(){
 processTree();
 console.log('RUNNING: preserve original start; effective target '+targetSeconds+' seconds, '+adjustment.targetAtUtc);
 while(Date.now()<end){
  const p=read('progress.json');assert.notEqual(p.stage,'Failed','Original run failed: '+p.error);
  assert(!fs.existsSync(path.join(runRoot,'failure.json')),'Original run recorded a failure');
  save('target-progress.json',{stage:'Running',...adjustment,elapsedSeconds:Math.floor((Date.now()-started)/1000),originalProgress:p});
  await new Promise(r=>setTimeout(r,Math.min(30000,end-Date.now())));
 }
 // The original driver deliberately restarts at each 6h boundary, including 12h.
 // Leave a small settling window, then retry only read/authentication requests.
 await new Promise(r=>setTimeout(r,10000));
 const before=read('progress.json');assert.equal(before.stage,'Running');
 assert(Date.now()-Date.parse(before.updatedAtUtc)<120000,'Original driver progress is stale');
 assert.equal(before.instrumentWrites,0);assert(before.offlineObserved&&before.recoveryObserved);
 const ctx=await request.newContext();let csrf;
 async function call(route,method='GET',body){for(let attempt=0;;attempt++){try{const r=await ctx.fetch(run.url+route,{method,data:body,headers:method==='GET'?{}:{'X-CSRF-TOKEN':csrf},timeout:10000});const text=await r.text();assert.equal(r.status(),200,route+': '+text);return text?JSON.parse(text):null}catch(e){if(attempt>=29||method!=='GET'&&route!=='/api/auth/login')throw e;await new Promise(r=>setTimeout(r,1000))}}}
 try {
  csrf=(await call('/api/session')).csrfToken;
  await call('/api/auth/login','POST',{login:'soakqa',password:'Isolated-Soak-2026!'});
  csrf=(await call('/api/session')).csrfToken;
  // Let any already scheduled recovery/restart settle before taking the final sample.
  let current;
  for(let n=0;n<60;n++){current=await call('/api/acquisition');if(current.running&&current.readings.length===128&&current.readings.every(r=>r.levelMm.isValid))break;await new Promise(r=>setTimeout(r,1000))}
  assert(current.running&&current.readings.length===128&&current.readings.every(r=>r.levelMm.isValid),'Final acquisition did not recover all 128 tanks');
  save('final-acquisition-summary.json',{atUtc:new Date().toISOString(),running:current.running,count:current.readings.length,valid:current.readings.filter(r=>r.levelMm.isValid).length});
  const tree=processTree();assert(tree.some(p=>p.Name==='LevelManager.Host.exe'&&p.CommandLine.includes(run.dataRoot)),'Owned host identity not found');
  assert(tree.every(p=>['node.exe','LevelManager.Host.exe','chrome.exe','crashpad_handler.exe'].includes(p.Name)||p.Name==='powershell.exe'&&p.CommandLine.includes('Select-Object WorkingSet64,PrivateMemorySize64')),'Unexpected descendant process; preserve run for review');
  save('original-progress-at-handoff.json',read('progress.json'));save('owned-processes-at-handoff.json',tree);
  await call('/api/acquisition/stop','POST',{});
  // First stop the old fixed-duration driver so that it cannot restart its host.
  // Only captured descendants with the SAME creation time are then closed.
  for(const p of [tree.find(p=>p.ProcessId===run.nodePid),...tree.filter(p=>p.ProcessId!==run.nodePid)]){
   powershell(`$env:PSModulePath=Join-Path $PSHOME 'Modules'; $p=Get-CimInstance Win32_Process -Filter 'ProcessId=${p.ProcessId}'; if($p -and $p.CreationDate.ToUniversalTime().ToString('o') -eq ${quote(p.Created)}){Stop-Process -Id ${p.ProcessId} -ErrorAction Stop}`);
  }
 } finally {await ctx.dispose()}
 const py='C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe';
 const checked=spawnSync(py,['-c',"import sqlite3,json,sys;d=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True);print(json.dumps({'rows':d.execute('select count(*) from history_samples').fetchone()[0],'integrity':d.execute('pragma integrity_check').fetchone()[0],'foreignKeys':list(d.execute('pragma foreign_key_check'))}))",path.join(run.dataRoot,'level-manager.db')],{encoding:'utf8',windowsHide:true});
 assert.equal(checked.status,0,checked.stderr);const db=JSON.parse(checked.stdout);
 assert.equal(db.integrity,'ok');assert.deepEqual(db.foreignKeys,[]);
 const final=read('original-progress-at-handoff.json');
 assert.equal(final.instrumentWrites,0);assert(db.rows>=Math.max(1,Math.floor(targetSeconds/60)-final.restarts-2)*128,'Too few archive rows');
 const result={passed:true,...adjustment,actualSeconds:(Date.now()-started)/1000,restarts:final.restarts,
 offlineObserved:final.offlineObserved,recoveryObserved:final.recoveryObserved,instrumentWrites:0,
 history:db,frontendIncluded:true,packageRoot:run.packageRoot,originalRunPreserved:true,
 realDeviceAccess:false,limitation:'仅软件和模拟线路的阶段验证，不代替72小时可靠性、实际仪表、电气线路、掉电和Windows服务现场验证。'};
 save('result.json',result);save('progress.json',{stage:'Completed',...result,updatedAtUtc:new Date().toISOString()});
 save('target-progress.json',{stage:'Completed',...result});console.log('PASS: shortened isolated soak '+targetSeconds+' seconds; '+db.rows+' archive rows');
}
main().catch(e=>{save('target-failure.json',{failed:true,atUtc:new Date().toISOString(),error:e.stack,originalTestNotAutomaticallyStopped:true});console.error(e);process.exitCode=1});
