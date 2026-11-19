const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const {pathToFileURL}=require('url');
const {chromium}=require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root=path.resolve(__dirname,'..'),packageRoot=path.resolve(process.env.LEVEL_PACKAGE_ROOT||path.join(root,'output/中文联调候选包_0.3.0-zh-test.1_r7')),docs=path.join(packageRoot,'docs'),out=path.join(root,process.env.LEVEL_VERIFICATION_DIR||'output/p7-customer-docs-verification');fs.mkdirSync(out,{recursive:true});
let browser;(async()=>{
 browser=await chromium.launch({executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe',headless:true});const page=await browser.newPage({viewport:{width:1366,height:950}});const checked=[];
 for(const name of fs.readdirSync(docs).filter(n=>n.endsWith('.html'))){const html=fs.readFileSync(path.join(docs,name),'utf8');assert(!/\b(AI|OpenAI|ChatGPT|Codex)\b|人工智能|大模型/i.test(html),name);for(const m of html.matchAll(/href="([^"#]+)"/g)){if(!/^[a-z]+:/i.test(m[1]))assert(fs.existsSync(path.resolve(docs,decodeURIComponent(m[1]))),name+': '+m[1]);}
  await page.goto(pathToFileURL(path.join(docs,name)).href);assert.equal(await page.locator('h1').count(),1);assert(await page.locator('h1').innerText());assert(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+2),name);await page.screenshot({path:path.join(out,name.replace('.html','.png')),fullPage:true});checked.push(name);
 }
 const js=fs.readdirSync(path.join(packageRoot,'app/server/wwwroot/assets')).filter(n=>n.endsWith('.js'));for(const name of js){const text=fs.readFileSync(path.join(packageRoot,'app/server/wwwroot/assets',name),'utf8');assert(!/\b(OpenAI|ChatGPT|Codex)\b|人工智能|大模型/.test(text),name);}
 fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({passed:true,documents:checked,viewport:{width:1366,height:950},relativeLinksExist:true,noRootHorizontalOverflow:true,customerAttributionTermsAbsent:true},null,2));console.log('PASS: '+checked.length+' customer HTML instructions/link/layout checks');
})().catch(e=>{console.error(e);process.exitCode=1}).finally(async()=>{if(browser)await browser.close()});
