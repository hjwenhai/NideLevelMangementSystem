// Read-only UI regression against the disposable preview; no acquisition/configuration changes.
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const {chromium,request}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),out=path.join(root,'output/card-size-verification_'+Date.now());fs.mkdirSync(out,{recursive:true});
let browser,api;
(async()=>{
 const info=JSON.parse(fs.readFileSync(path.join(root,'output/client-viewing/r10-preview-session.json'),'utf8'));assert.equal(info.revision,'r10-download-card-review');
 api=await request.newContext();let csrf=(await(await api.get(info.url+'api/session')).json()).csrfToken;
 assert((await api.post(info.url+'api/auth/login',{headers:{'X-CSRF-TOKEN':csrf},data:{login:info.previewLogin,password:info.previewPassword}})).ok());
 browser=await chromium.launch({executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});
 const context=await browser.newContext({storageState:await api.storageState(),viewport:{width:1920,height:1080}}),page=await context.newPage(),errors=[];
 page.on('pageerror',e=>errors.push(String(e)));await page.goto(info.url);await page.locator('.overview-card').first().waitFor();
 const selector=page.getByRole('combobox',{name:'卡片大小',exact:true});assert.equal(await selector.inputValue(),'standard');
 await page.waitForFunction(()=>[...document.querySelectorAll('[data-summary] .summary-number')].every(e=>e.textContent!=='—'));
 const totals=await page.locator('[data-summary] .summary-number').allTextContents(),codes=await page.locator('.tank-card-heading>h2').allTextContents(),sizes=[];
 for(const size of ['compact','standard','large']){
  await page.setViewportSize({width:1920,height:1080});await selector.selectOption(size);
  await page.locator('.tank-grid[data-card-size="'+size+'"]').waitFor();
  const geometry=await page.locator('.overview-card').first().evaluate(e=>{const r=e.getBoundingClientRect(),s=e.querySelector('.tank-graphic').getBoundingClientRect();return{cardWidth:r.width,cardHeight:r.height,svgWidth:s.width,svgHeight:s.height}});
  const top=await page.locator('.overview-card').evaluateAll(es=>es.map(e=>e.getBoundingClientRect().top));geometry.firstRow=top.filter(y=>Math.abs(y-top[0])<2).length;
  assert.deepEqual(await page.locator('[data-summary] .summary-number').allTextContents(),totals);assert.deepEqual(await page.locator('.tank-card-heading>h2').allTextContents(),codes);
  await page.screenshot({path:path.join(out,'卡片-'+size+'-1920.png')});
  for(const width of [1366,1093,911,800]){
   await page.setViewportSize({width,height:1080});
   const state=await page.evaluate(()=>({overflow:document.documentElement.scrollWidth>innerWidth+2,overlap:[...document.querySelectorAll('.overview-card')].some(e=>{const a=e.querySelector('.tank-graphic').getBoundingClientRect(),b=e.querySelector('.overview-key-values').getBoundingClientRect();return a.right>b.left+1})}));
   assert(!state.overflow,size+' width '+width+' page overflow');assert(!state.overlap,size+' width '+width+' overlapping graphic/text');
  }
  sizes.push({size,...geometry});
 }
 assert(sizes[0].svgWidth<sizes[1].svgWidth&&sizes[1].svgWidth<sizes[2].svgWidth,'three sizes must visibly change graphics');
 assert(sizes[0].cardHeight<sizes[1].cardHeight&&sizes[1].cardHeight<sizes[2].cardHeight,'three sizes must visibly change card height');
 await page.reload();await selector.waitFor();assert.equal(await selector.inputValue(),'large');
 await page.getByRole('button',{name:'列表显示',exact:true}).click();assert.equal(await selector.count(),0);assert.equal(await page.locator('.current-readings tbody tr').count(),12);
 await page.getByRole('button',{name:'图形显示',exact:true}).click();assert.equal(await selector.inputValue(),'large');
 // Separate browser/profile keeps its own display preference.
 const separate=await browser.newContext({storageState:await api.storageState(),viewport:{width:1366,height:900}}),second=await separate.newPage();await second.goto(info.url);await second.getByRole('combobox',{name:'卡片大小',exact:true}).waitFor();assert.equal(await second.getByRole('combobox',{name:'卡片大小',exact:true}).inputValue(),'standard');
 assert.deepEqual(errors,[]);fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:true,sizes,layoutWidths:[1920,1366,1093,911,800],persistsAfterReload:true,listReturnsToSavedSize:true,independentBrowserPreference:true,unchangedSummaryAndPinOrder:true,pageErrors:errors,realDeviceAccess:false},null,2));console.log(out);
})().catch(e=>{fs.writeFileSync(path.join(out,'failure.json'),JSON.stringify({error:e.stack},null,2));console.error(e);process.exitCode=1}).finally(async()=>{if(browser)await browser.close();if(api)await api.dispose()});
