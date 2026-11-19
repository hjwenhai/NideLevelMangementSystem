// Read-only UI verification against the local r23 preview; synthetic row responses stay in the test browser.
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const {request,chromium}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),out=path.join(root,'output/r23-layout-fix_20261114/qa'),web=path.join(root,'src/LevelManager.Host/wwwroot'),url='http://127.0.0.1:55423';fs.mkdirSync(out,{recursive:true});
let api,browser,page;const checks=[],errors=[],geometry=[];
async function nav(name){await page.locator('aside').getByRole('button',{name,exact:true}).click();}
async function box(selector){return page.locator(selector).first().evaluate(e=>({top:e.getBoundingClientRect().top,left:e.getBoundingClientRect().left,bottom:e.getBoundingClientRect().bottom,height:e.getBoundingClientRect().height}))}
async function stableScroll(kind){
 const query=kind==='history'?'.history-query':'.query-panel',wrap=kind==='history'?'.history-results>.table-wrap':'.event-results>.table-wrap',thead=kind==='history'?'.history-results thead th':'.event-results thead th',toolbar=kind==='history'?'.history-results>.toolbar':'.events-main>.toolbar';
 await page.locator(thead).first().waitFor();const start={query:await box(query),head:await box(thead),buttons:await box(toolbar)};
 const pos=await page.locator(wrap).evaluate(e=>({h:e.clientHeight,scroll:e.scrollHeight-e.clientHeight}));assert(pos.h>=100,kind+' needs visible results '+JSON.stringify(pos));assert(pos.scroll>500,kind+' needs scrollable rows');
 await page.locator(wrap).hover();await page.mouse.wheel(0,950);await page.waitForTimeout(120);
 assert((await page.locator(wrap).evaluate(e=>e.scrollTop))>100,kind+' real wheel must scroll records');
 for(const [k,s] of Object.entries({query,head:thead,buttons:toolbar})){assert(Math.abs((await box(s)).top-start[k].top)<2,kind+' '+k+' shifted with result wheel')}
 await page.locator(query).hover();await page.mouse.wheel(0,900);await page.waitForTimeout(120);await page.evaluate(()=>window.scrollTo(0,800));
 for(const [k,s] of Object.entries({query,head:thead,buttons:toolbar})){assert(Math.abs((await box(s)).top-start[k].top)<2,kind+' '+k+' shifted with page wheel')}
 const document=await page.evaluate(()=>({scrollY:window.scrollY,scrollHeight:document.documentElement.scrollHeight,height:innerHeight}));assert.equal(document.scrollY,0,kind+' outer page must not scroll');assert(document.scrollHeight<=document.height+2,kind+' outer vertical overflow '+JSON.stringify(document));
 return {kind,viewport:page.viewportSize(),rowsViewport:pos.h,positions:start,document};
}
(async()=>{
 api=await request.newContext();const health=await(await api.get(url+'/api/health')).json();assert.equal(health.build,'20261113-r23');let csrf=(await(await api.get(url+'/api/session')).json()).csrfToken;
 const auth=await api.post(url+'/api/auth/login',{headers:{'X-CSRF-TOKEN':csrf},data:{login:'previewqa',password:'Preview-Isolated-2026!'}});assert(auth.ok());
 const config=await(await api.get(url+'/api/config')).json(),acq=await(await api.get(url+'/api/acquisition')).json();assert.equal(config.tanks.length,16);const reading=acq.readings.find(r=>r.levelMm.isValid);assert(reading);
 browser=await chromium.launch({channel:'msedge',headless:true});const ctx=await browser.newContext({viewport:{width:1704,height:990},storageState:await api.storageState()});page=await ctx.newPage();page.on('pageerror',e=>errors.push(String(e)));
 if(!process.env.R23_LIVE_ASSETS)await page.route('**/*',async route=>{const u=new URL(route.request().url());const file=path.resolve(web,'.'+decodeURIComponent(u.pathname));if(u.origin===url&&(u.pathname==='/'||u.pathname.startsWith('/assets/'))){const p=u.pathname==='/'?path.join(web,'index.html'):file;if(p.startsWith(web+path.sep)&&fs.existsSync(p)){await route.fulfill({path:p});return}}await route.continue()});
 await page.goto(url);await page.locator('.overview-card').first().waitFor();if(!(await page.locator('.app-shell').getAttribute('class')).includes('sidebar-is-collapsed'))await page.getByRole('button',{name:'切换侧栏',exact:true}).click();
 assert.equal(await page.getByLabel('卡片大小',{exact:true}).locator('option[value=compact]').innerText(),'紧凑');
 for(const width of [1920,1704,1536,1280]){
  await page.setViewportSize({width,height:width===1280?720:990});
  for(const size of ['compact','standard','large']){
   await page.getByLabel('卡片大小',{exact:true}).selectOption(size);await page.waitForTimeout(80);
   const g=await page.evaluate(()=>{const grid=document.querySelector('.tank-grid'),card=grid.querySelector('.overview-card'),graphic=card.querySelector('.tank-graphic'),level=card.querySelector('.overview-level');const range=document.createRange();range.selectNodeContents(level);return {width:innerWidth,size:grid.className,cols:getComputedStyle(grid).gridTemplateColumns.split(' ').length,card:card.getBoundingClientRect().width,graph:[graphic.getBoundingClientRect().width,graphic.getBoundingClientRect().height],overflow:document.documentElement.scrollWidth>innerWidth,levelOverflow:range.getBoundingClientRect().right>card.getBoundingClientRect().right-6}});
   geometry.push(g);assert(!g.overflow,'horizontal overflow '+JSON.stringify(g));assert(!g.levelOverflow,'level text overflow '+JSON.stringify(g));
   if(width>=1704)assert.equal(g.cols,{compact:8,standard:6,large:4}[size],JSON.stringify(g));
   if(size==='compact')assert.deepEqual(g.graph,[100,155]);
   if(width===1704)await page.screenshot({path:path.join(out,'总览-'+size+'-1704.png')});
  }
 }
 checks.push('1920/1704可用宽度紧凑8、标准6、大号4；1536/1280自适应，无横向溢出；紧凑罐图100×155不变');
 await page.setViewportSize({width:1704,height:990});await nav('历史数据');await page.locator('.history-query').waitFor();assert((await page.locator('.history-results').innerText()).includes('尚未查询'));
 await page.route('**/api/history?*',r=>r.fulfill({json:{rows:Array.from({length:500},(_,i)=>({...reading,tankId:reading.tankId,atUtcMs:Date.now()-i*60000,configurationRevision:config.revision,calculationRevision:1,interfaceMm:{value:null,quality:'Unconfigured',isValid:false}})),next:null}}));
 await page.getByRole('button',{name:'查询',exact:true}).click();await page.locator('.history-results tbody tr').nth(499).waitFor();
 for(const [width,height] of [[1920,1080],[1704,990],[1280,720]]){await page.setViewportSize({width,height});geometry.push(await stableScroll('history'));if(width===1704)await page.screenshot({path:path.join(out,'历史数据-固定区.png')});}
 const jobs=page.locator('.history-export-tasks');assert(!(await jobs.getAttribute('open')));await jobs.locator('summary').click();await jobs.getByRole('heading',{name:'历史导出任务'}).waitFor();assert((await box('.history-results>.table-wrap')).height>70);await jobs.locator('summary').click();checks.push('历史500行：记录区域和页面上分别滚轮，查询/导出按钮/表头不移动；导出任务入口可展开');
 await page.route('**/api/events?*',r=>r.fulfill({json:Array.from({length:500},(_,i)=>({id:String(i+1),atUtcMs:Date.now()-i*60000,category:'系统事件',tankCode:'T-101',content:'滚动布局样例 '+i,result:'已记录',actorName:'本机预览管理员',details:'{}'}))}));await nav('事件记录');await page.locator('.event-results tbody tr').nth(499).waitFor();
 for(const [width,height] of [[1920,1080],[1704,990],[1280,720]]){await page.setViewportSize({width,height});geometry.push(await stableScroll('events'));if(width===1704)await page.screenshot({path:path.join(out,'事件记录-固定区.png')});}checks.push('事件500行：记录区域和页面上分别滚轮，分类/查询/导出按钮/表头不移动');
 await nav('罐区总览');await page.locator('.overview-card').first().waitFor();await page.getByLabel('卡片大小',{exact:true}).selectOption('compact');assert.equal(await page.locator('.records-shell').count(),0);assert(await page.evaluate(()=>getComputedStyle(document.body).overflow!=='hidden'));checks.push('离开记录页恢复总览滚动，不把全局页面锁住');
 assert.deepEqual(errors,[]);fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:true,checks,geometry,errors,servedFrontend:process.env.R23_LIVE_ASSETS?'live-55423':'built-source',configWrites:0,realDeviceActions:0},null,2));console.log(JSON.stringify({passed:true,checks,cases:geometry.length,out}));
})().catch(async e=>{if(page)try{await page.screenshot({path:path.join(out,'failure.png')})}catch{}fs.writeFileSync(path.join(out,'failure.json'),JSON.stringify({error:String(e),stack:e.stack,checks,geometry,errors},null,2));console.error(e);process.exitCode=1}).finally(async()=>{if(browser)await browser.close();if(api)await api.dispose()});
