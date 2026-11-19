// Render the built application with browser-only fixtures. No real backend,
// database, credentials, private keys or instrument endpoints are accessed.
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const {chromium}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),qa=path.join(root,'output/control-flow-qa_20261111/old-records'),web=path.join(qa,'web');
const metric=value=>({value,quality:'Good',isValid:true});
const tanks=Array.from({length:5},(_,i)=>({id:'layout-'+i,code:'T-10'+(i+1),name:'布局校核',medium:'柴油',mediumId:null,zone:'一罐区',shape:['Vertical','Sphere','FloatingRoof'][i%3],heightMm:12000,channelId:'layout-channel',address:i+1,temperatureMode:'None',enabled:true,hidden:false,atsHeightsMm:[],atsPointCount:1,pressure1:{enabled:false,unit:'KPa'},pressure2:{enabled:false,unit:'KPa'},controls:{allowedCommands:[]},alarms:{},metering:{enabled:true,showInstrumentDensity:true,manualOperatingDensityKgM3:800,densitySource:'Manual',waterVolumeM3:0}}));
const cfg={revision:1,channels:[{id:'layout-channel',name:'布局校核通道',protocol:'GatewayTcp',enabled:true}],tanks,groups:[],schedules:[],media:[],display:{refreshIntervalSeconds:1,heightUnit:'Millimetre',densityUnit:'KilogramPerCubicMetre',volumeUnit:'CubicMetre',massUnit:'Tonne',volumeDecimals:3,massDecimals:3},historyIntervalSeconds:60,retentionMonths:12,readingExpirySeconds:30};
const checks=[],errors=[],writes=[],mockReviewRequests=[];let browser;
const recent=Array.from({length:55},(_,i)=>({id:'recent-'+i,command:'MeasureI',cycle:'Normal',state:i===54?'Unknown':'ReadbackConfirmed',actorName:'历史操作'+i,createdAtUtc:new Date(Date.now()-i*60000).toISOString(),result:i===54?'历史结果待核查':'模式回读已确认'}));
const older={id:'old-r18-unresolved',command:'Hoist',cycle:'Normal',state:'Unknown',actorName:'r18较早待核查记录',createdAtUtc:'2026-10-26T00:00:00Z',result:'升级前结果不确定，须人工核查'};
function response(name,query){const now=new Date().toISOString();switch(name){
case '/api/session':return {needsSetup:false,csrfToken:'layout-only',user:{accountId:'layout',displayName:'布局校核',role:'Viewer',permissions:['ControlReview']}};
case '/api/presentation':return {branding:{softwareName:'液位管理系统',manufacturerName:'耐德东京计装',manufacturerInformation:'',logoDataUrl:null,iconDataUrl:null},enabledModules:['average-temperature']};
case '/api/authorization/status':return {allowsLicensedActions:true,effectiveUtc:now,expiresAtUtc:null,status:'Active'};
case '/api/storage/recovery':return {requiresReview:false,message:''};
case '/api/config':return cfg;
case '/api/capacity-tables':return [];
case '/api/controls':return query.get('unresolved')==='true'?[...recent.filter(r=>r.state==='Unknown'),...(older.state==='Unknown'?[older]:[])]:recent;
case '/api/tanks/layout-0/gateway-controls':return {configurationRevision:1,acquisitionSessionId:'layout',gatewayBootId:1,gatewayBindingVersion:1,normal:{state:0,error:0},independentStop:{state:0,error:0}};
case '/api/alarms/summary':return {summary:{active:0,unacknowledged:0,recoveredUnacknowledged:0,tanks:[]},error:''};
case '/api/acquisition':return {running:true,serverAtUtc:now,configurationRevision:1,sessionId:'layout',historyError:'',channels:[],readings:tanks.map(t=>({tankId:t.id,configurationRevision:1,receivedAtUtc:now,surfaceBasis:'测量Ⅰ液面',floatPositionMm:metric(4820),currentVolumeM3:metric(575.84),calculationNotes:[],levelMm:metric(4820),instantTemperatureC:metric(24.35),instrumentDensityKgM3:metric(802),netVolumeM3:metric(575.84),massT:metric(460.672),instrumentFault:0,instrumentFaults:[],atsPoints:[],diagnostic:'',instrumentStatus:1}))};
default:throw Error('Unexpected fixture API: '+name)}}
(async()=>{
 browser=await chromium.launch({channel:'msedge',headless:true});
 const context=await browser.newContext({viewport:{width:1920,height:1080}});
 const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',async route=>{
  const request=route.request(),url=new URL(request.url());
  if(request.method()==='POST'&&url.pathname==='/api/trends/realtime')return route.fulfill({json:{series:[]}});
  if(request.method()==='POST'&&url.pathname==='/api/controls/old-r18-unresolved/review'){
   const body=request.postDataJSON();assert(body.confirmed&&body.reason.trim());mockReviewRequests.push({path:url.pathname,body});older.state='Reviewed';return route.fulfill({json:{message:'人工核查已记录，未发送仪表动作'}});
  }
  if(request.method()!=='GET'){writes.push(request.method()+' '+url.pathname);return route.abort()}
  if(url.pathname.startsWith('/api/'))return route.fulfill({json:response(url.pathname,url.searchParams)});
  const file=path.resolve(web,'.'+(url.pathname==='/'?'/index.html':url.pathname));
  assert(file.startsWith(web+path.sep));assert(fs.existsSync(file),'Missing built asset '+url.pathname);
  const contentType={'.html':'text/html; charset=utf-8','.js':'text/javascript; charset=utf-8','.css':'text/css; charset=utf-8','.ico':'image/x-icon'}[path.extname(file)]||'application/octet-stream';
  return route.fulfill({path:file,contentType});
 });
 await page.goto('http://r22-layout.invalid/');await page.locator('.overview-card').first().waitFor();
 for(const width of [1920,1366]){
  await page.setViewportSize({width,height:1080});
  for(const size of ['compact','standard','large']){
   await page.getByLabel('卡片大小',{exact:true}).selectOption(size);
   const card=page.locator('.overview-card').first();
   assert.deepEqual(await card.locator('.inventory-label').allTextContents(),['仪表密度','人工工况密度','净体积','质量']);
   assert.deepEqual(await card.locator('.inventory-unit').allTextContents(),['kg/m³','kg/m³','m³','t']);
   assert.deepEqual((await card.locator('.overview-card-inventory strong').allTextContents()).slice(0,3),['802.0','800.0','575.840']);
   const boxes=await card.locator('.overview-card-inventory>div').evaluateAll(nodes=>nodes.slice(0,3).map(node=>{
    const labels=node.querySelectorAll('small'),value=node.querySelector('strong');
    return {label:labels[0].getBoundingClientRect().y,unit:labels[1].getBoundingClientRect().y,value:value.getBoundingClientRect().y,labelFits:labels[0].scrollWidth<=labels[0].clientWidth+1};
   }));
   for(const key of ['label','unit','value'])assert(Math.max(...boxes.map(b=>b[key]))-Math.min(...boxes.map(b=>b[key]))<1,key+' misaligned');
   assert(boxes.every(b=>b.label<b.unit&&b.unit<b.value&&b.labelFits),'Rows clipped or unordered');
   await card.screenshot({path:path.join(qa,`card-${width}-${size}.png`)});
   if(width===1920&&size==='standard')await page.screenshot({path:path.join(qa,'overview-1920.png')});
   checks.push({width,size,aligned:true,boxes});
  }
 }
 cfg.tanks.forEach(t=>t.metering.showInstrumentDensity=false);
 await page.reload();await page.locator('.overview-card').first().waitFor();
 assert.deepEqual(await page.locator('.overview-card').first().locator('.inventory-label').allTextContents(),['人工工况密度','净体积','质量']);
 checks.push({hiddenInstrumentDensity:true,passed:true});
 cfg.display.densityUnit='GramPerCubicCentimetre';await page.reload();await page.locator('.overview-card').first().waitFor();
 assert.equal(await page.locator('.overview-card').first().locator('.overview-card-inventory strong').first().innerText(),'0.8000');
 assert.equal(await page.locator('.overview-card').first().locator('.inventory-unit').first().innerText(),'g/cm³');
 checks.push({densityUnitConversionPreserved:true,passed:true});

 cfg.display.densityUnit='KilogramPerCubicMetre';cfg.tanks.forEach(t=>t.metering.showInstrumentDensity=true);
 await page.reload();await page.locator('.overview-card').first().waitFor();
 await page.getByRole('button',{name:'单罐详情',exact:true}).click();
 await page.getByRole('button',{name:/操作记录与回读/}).click();
 const dialog=page.getByRole('dialog',{name:'操作记录与回读',exact:true});await dialog.locator('.control-result').first().waitFor();
 assert.equal(await dialog.locator('.control-result').count(),10);
 assert.equal(await dialog.locator('.control-result').filter({hasText:'r18较早待核查记录'}).count(),1);
 assert.equal(await dialog.getByRole('button',{name:'人工核查记录',exact:true}).count(),2);
 assert((await dialog.locator('.control-record-summary').innerText()).includes('待核查 2 项'));
 checks.push({olderPendingOutsideRecentListVisible:true,pendingFirst:true,mergedUniqueRecords:56});
 await dialog.getByRole('button',{name:'显示更多操作记录',exact:true}).click();assert.equal(await dialog.locator('.control-result').count(),30);
 await dialog.screenshot({path:path.join(qa,'pending-records.png')});
 checks.push({showMorePassed:true});assert.equal(mockReviewRequests.length,0);
 await dialog.locator('.control-result').filter({hasText:'r18较早待核查记录'}).getByRole('button',{name:'人工核查记录',exact:true}).click();
 const review=page.getByRole('dialog',{name:'人工核查仪表操作',exact:true});
 assert(await review.getByRole('button',{name:'保存核查结论',exact:true}).isDisabled());
 assert((await review.innerText()).includes('不停止、不复位、不重发动作'));
 await review.getByLabel('现场核查结论',{exact:true}).fill('仅浏览器样例：已人工核实旧操作，记录核查结论。');
 await review.getByRole('checkbox').check();await review.getByRole('button',{name:'保存核查结论',exact:true}).click();
 await page.waitForFunction(()=>document.querySelector('.control-record-summary')?.textContent?.includes('待核查 1 项'));
 assert.equal(mockReviewRequests.length,1);assert.equal(writes.length,0);
 checks.push({manualReviewConfirmationRequired:true,onlySelectedRecordReviewed:true,noInstrumentOrResetRequest:true});
 assert.deepEqual(errors,[]);assert.deepEqual(writes,[]);
 fs.writeFileSync(path.join(qa,'record-result.json'),JSON.stringify({passed:true,checks,errors,writes,mockReviewRequests,backendAccessed:false,atUtc:new Date().toISOString()},null,2));
 console.log(JSON.stringify({passed:true,checks:checks.length,writes:writes.length,backendAccessed:false}));
})().catch(e=>{fs.writeFileSync(path.join(qa,'record-failure.json'),JSON.stringify({error:e.stack,errors,writes},null,2));console.error(e);process.exitCode=1}).finally(async()=>{await browser?.close()});
