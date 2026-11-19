// Render the built application with browser-only fixtures. No real backend,
// database, credentials, private keys or instrument endpoints are accessed.
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const {chromium}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),qa=path.join(root,'output/r22-latest-delivery-qa_20261107/inventory-layout_20261108'),web=path.join(qa,'web');
const metric=value=>({value,quality:'Good',isValid:true});
const tanks=Array.from({length:5},(_,i)=>({id:'layout-'+i,code:'T-10'+(i+1),name:'布局校核',medium:'柴油',mediumId:null,zone:'一罐区',shape:['Vertical','Sphere','FloatingRoof'][i%3],heightMm:12000,channelId:'layout-channel',address:i+1,temperatureMode:'None',enabled:true,hidden:false,atsHeightsMm:[],atsPointCount:1,controls:{allowedCommands:[]},alarms:{},metering:{enabled:true,showInstrumentDensity:true,manualOperatingDensityKgM3:800,densitySource:'Manual',waterVolumeM3:0}}));
const cfg={revision:1,channels:[{id:'layout-channel',name:'布局校核通道',protocol:'GatewayTcp',enabled:true}],tanks,groups:[],schedules:[],media:[],display:{refreshIntervalSeconds:1,heightUnit:'Millimetre',densityUnit:'KilogramPerCubicMetre',volumeUnit:'CubicMetre',massUnit:'Tonne',volumeDecimals:3,massDecimals:3},historyIntervalSeconds:60,retentionMonths:12,readingExpirySeconds:30};
const checks=[],errors=[],writes=[];let browser;
function response(name){const now=new Date().toISOString();switch(name){
case '/api/session':return {needsSetup:false,csrfToken:'layout-only',user:{accountId:'layout',displayName:'布局校核',role:'Viewer',permissions:[]}};
case '/api/presentation':return {branding:{softwareName:'液位管理系统',manufacturerName:'耐德东京计装',manufacturerInformation:'',logoDataUrl:null,iconDataUrl:null},enabledModules:['average-temperature']};
case '/api/authorization/status':return {allowsLicensedActions:true,effectiveUtc:now,expiresAtUtc:null,status:'Active'};
case '/api/storage/recovery':return {requiresReview:false,message:''};
case '/api/config':return cfg;
case '/api/capacity-tables':return [];
case '/api/alarms/summary':return {summary:{active:0,unacknowledged:0,recoveredUnacknowledged:0,tanks:[]},error:''};
case '/api/acquisition':return {running:true,serverAtUtc:now,configurationRevision:1,sessionId:'layout',historyError:'',channels:[],readings:tanks.map(t=>({tankId:t.id,configurationRevision:1,receivedAtUtc:now,levelMm:metric(4820),instantTemperatureC:metric(24.35),instrumentDensityKgM3:metric(802),netVolumeM3:metric(575.84),massT:metric(460.672),instrumentFault:0,instrumentFaults:[],atsPoints:[],diagnostic:'',instrumentStatus:1}))};
default:throw Error('Unexpected fixture API: '+name)}}
(async()=>{
 browser=await chromium.launch({channel:'msedge',headless:true});
 const context=await browser.newContext({viewport:{width:1920,height:1080}});
 const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/*',async route=>{
  const request=route.request(),url=new URL(request.url());
  if(request.method()!=='GET'){writes.push(request.method()+' '+url.pathname);return route.abort()}
  if(url.pathname.startsWith('/api/'))return route.fulfill({json:response(url.pathname)});
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
 assert.deepEqual(errors,[]);assert.deepEqual(writes,[]);
 fs.writeFileSync(path.join(qa,'layout-result.json'),JSON.stringify({passed:true,checks,errors,writes,backendAccessed:false,atUtc:new Date().toISOString()},null,2));
 console.log(JSON.stringify({passed:true,checks:checks.length,writes:writes.length,backendAccessed:false}));
})().catch(e=>{fs.writeFileSync(path.join(qa,'layout-failure.json'),JSON.stringify({error:e.stack,errors,writes},null,2));console.error(e);process.exitCode=1}).finally(async()=>{await browser?.close()});
