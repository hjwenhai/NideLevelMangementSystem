import assert from 'node:assert/strict';
import {readFile,writeFile,mkdtemp} from 'node:fs/promises';
import {createServer} from 'node:http';
import {spawn} from 'node:child_process';

import {fileURLToPath} from 'node:url';
import path from 'node:path';

const date=process.argv[2]||'20261117';
assert.match(date,/^\d{8}$/);
const root=fileURLToPath(new URL('../',import.meta.url));
const staticDir=path.join(root,'src/internal/web/static');
const module=await readFile(path.join(staticDir,'indicators.js'),'utf8');
const {describeIndicators}=await import('data:text/javascript;base64,'+Buffer.from(module).toString('base64'));
const status={modes:[4,2,2],driver_enabled:true,enabled_meters:2,good_meters:1,reasons:[{severity:'warning',message:'COM1 / 地址2 · 槽位2（试验表） · 温度状态及平均温度 IR22～26：最近一次采集失败，CRC不匹配'}]};
assert.match(describeIndicators(status,false,false).driver.message,/现场观察/);
assert.match(describeIndicators(status,true,false).driver.title,/模拟/);
assert.match(describeIndicators({...status,driver_error:'写入失败'},false,false).driver.title,/异常/);
assert.match(describeIndicators({...status,driver_enabled:false},false,false).driver.title,/未启用/);
assert.match(describeIndicators({...status,modes:[4,0,0]},false,true).lamps[1].explanation,/维护/);
let html=await readFile(path.join(staticDir,'index.html'),'utf8');
html=html.replace('id="workspace" hidden','id="workspace"').replace('id="login"','id="login" hidden').replace('id="maintenance" class="page" hidden','id="maintenance" class="page"');
const app=await readFile(path.join(staticDir,'app.js'),'utf8');
const render=app.slice(app.indexOf('function renderIndicators('));
const script="import {describeIndicators} from './indicators.js';\nconst $=id=>document.getElementById(id);\n"+render+"\nrenderIndicators("+JSON.stringify(status)+",false,false);window.refreshIndicators=()=>renderIndicators("+JSON.stringify(status)+",false,false);document.querySelectorAll('section.page').forEach(e=>e.hidden=e.id!=='maintenance');";
const server=createServer(async(req,res)=>{
 try{
  const name=new URL(req.url,'http://localhost').pathname.slice(1);
  const content=name===''?html:name==='app.js'?script:await readFile(path.join(staticDir,path.basename(name)));
  res.setHeader('Content-Type',name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'text/html; charset=utf-8');
  res.end(content);
 }catch{res.writeHead(404).end()}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const dir=await mkdtemp(path.join(root,'tmp/indicator-browser-'));

try{
 for(const [name,width] of [['desktop',1366],['mobile',390]]){
  const output=path.join(root,'tests','indicators-'+name+'-'+date+'.png');
  const profile=path.join(dir,name);
  const child=spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',[
   '--headless','--no-first-run','--no-default-browser-check','--disable-background-networking',
   '--user-data-dir='+profile,'--remote-debugging-port=0','about:blank'
  ],{stdio:'ignore',windowsHide:true});
  let socket;
  try{
   let port;
   for(let i=0;i<100;i++){
    try{port=Number((await readFile(path.join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0]);break}catch{}
    await new Promise(r=>setTimeout(r,100));
   }
   assert.ok(port,'Browser debug port not ready');
   const target=await (await fetch('http://127.0.0.1:'+port+'/json/new?about:blank',{method:'PUT'})).json();
   socket=new WebSocket(target.webSocketDebuggerUrl);
   await new Promise((resolve,reject)=>{socket.onopen=resolve;socket.onerror=reject});
   let seq=0;const pending=new Map();
   socket.onmessage=e=>{const m=JSON.parse(e.data);if(m.id){const p=pending.get(m.id);pending.delete(m.id);m.error?p.reject(m.error):p.resolve(m.result)}};
   const send=(method,params={})=>new Promise((resolve,reject)=>{const id=++seq;pending.set(id,{resolve,reject});socket.send(JSON.stringify({id,method,params}));setTimeout(()=>{if(pending.delete(id))reject(new Error(method+' timeout'))},10000).unref()});
   await send('Emulation.setDeviceMetricsOverride',{width,height:1800,deviceScaleFactor:1,mobile:width<500});
   await send('Page.navigate',{url:'http://127.0.0.1:'+server.address().port+'/'});
   await send('Runtime.evaluate',{expression:"new Promise(r=>{const t=setInterval(()=>{if(document.getElementById('sys-driver-title')?.textContent){clearInterval(t);r(true)}},50)})",awaitPromise:true});

   const toggle=await send('Runtime.evaluate',{expression:`(()=>{
    const panel=document.getElementById('sys-reasons-panel');
    const summary=document.getElementById('sys-reasons-title');
    if(panel.open)return 'not initially collapsed';
    summary.click();
    if(!panel.open)return 'expand failed';
    window.refreshIndicators();
    if(!panel.open)return 'refresh closed panel';
    summary.click();
    window.refreshIndicators();
    if(panel.open)return 'refresh reopened panel';
    return summary.textContent==='当前告警原因（1项）'?'passed':'count missing';
   })()`,returnByValue:true});
   assert.equal(toggle.result.value,'passed',JSON.stringify(toggle));
   if(name==='desktop')await send('Runtime.evaluate',{expression:"document.getElementById('sys-reasons-title').click()"});
   const result=await send('Runtime.evaluate',{expression:'({html:document.documentElement.outerHTML,width:document.documentElement.scrollWidth,viewport:innerWidth})',returnByValue:true});
   const dom=result.result.value;
   assert.match(dom.html,/SYS1 · 程序运行/);assert.match(dom.html,/实体灯控制 · 已启用/);assert.match(dom.html,/CRC不匹配/);
   assert.ok(dom.width<=width,JSON.stringify({name,...dom,html:undefined}));
   const screen=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true});
   await writeFile(output,Buffer.from(screen.data,'base64'));
   await send('Browser.close').catch(()=>{});
  }finally{socket?.close();child.kill()}
 }
 await writeFile(path.join(root,'tests/indicator-display-'+date+'.json'),JSON.stringify({passed:true,scenarios:['hardware enabled','simulation','driver failure','driver disabled','maintenance','desktop render','390px render','default collapsed','expand and collapse','state preserved on refresh','collapsed count visible'],physical_device_used:false},null,2));
 console.log('Indicator descriptions and desktop/mobile rendering passed');
}finally{server.close()}
