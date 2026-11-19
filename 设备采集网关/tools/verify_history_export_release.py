"""Compiled core/web history export check on loopback, with optional temporary UI preview."""
from pathlib import Path
import argparse,csv,hashlib,http.cookiejar,io,json,secrets,socket,subprocess,tempfile,time,urllib.error,urllib.request

ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--keep-running',action='store_true')
args=parser.parse_args()
WORK=ROOT/'tmp/history-export-check';WORK.mkdir(exist_ok=True)
run=Path(tempfile.mkdtemp(prefix='run-',dir=WORK))
checks=[];children=[];streams=[]
def check(name,condition):
    assert condition,name
    checks.append({'name':name,'passed':True})
def port():
    with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
ipc,web,north=port(),port(),port()
cfg=json.loads((ROOT/'deploy/config.demo.json').read_text(encoding='utf-8-sig'))
cfg['meters']=cfg['meters'][:2]
for meter,name in zip(cfg['meters'],['首台试验液位计','二号罐液位计']):meter['name']=name
cfg['fast_ms']=1000;cfg['history']['period_sec']=1
cfg['tcp'].update(listen=f'127.0.0.1:{north}',allow_writes=False)
cfg['allowed_actions']=[]
config=run/'gateway.json';config.write_text(json.dumps(cfg,ensure_ascii=False),encoding='utf-8')
auth,token=run/'admin.json',run/'token'
password='History-Simulation-2026!'
bins=ROOT/'output/windows-amd64'
flags=getattr(subprocess,'CREATE_NO_WINDOW',0)
subprocess.run([str(bins/'gateway-web.exe'),'--init-admin','--auth-file',str(auth)],input=(password+'\n').encode(),capture_output=True,check=True,creationflags=flags)
opener=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
base=f'http://127.0.0.1:{web}'
def request(path,data=None):
    req=urllib.request.Request(base+path,data=None if data is None else json.dumps(data).encode(),headers={'Content-Type':'application/json'})
    return opener.open(req,timeout=5)
def wait(fn):
    until=time.monotonic()+12
    while time.monotonic()<until:
        try:
            value=fn()
            if value:return value
        except (OSError,urllib.error.URLError):pass
        time.sleep(.05)
    raise AssertionError('wait timeout')
def start(name,arguments):
    log=(run/(name+'.log')).open('wb');streams.append(log)
    children.append(subprocess.Popen([str(bins/(name+'.exe')),*map(str,arguments)],stdout=log,stderr=log,creationflags=flags))
def rows(path):
    with request(path) as r:
        disposition=r.headers.get('Content-Disposition','')
        values=list(csv.reader(io.StringIO(r.read().decode('utf-8-sig'))))
    return values,disposition
try:
    start('gateway-core',['--simulate','--leds=false','--config',config,'--db',run/'history.db','--ipc',f'127.0.0.1:{ipc}','--token-file',token])
    wait(lambda:token.exists())
    start('gateway-web',['--listen',f'127.0.0.1:{web}','--ipc',f'127.0.0.1:{ipc}','--token-file',token,'--auth-file',auth])
    wait(lambda:request('/').status==200)
    with request('/api/login',{'password':password}) as r:json.load(r)
    with request('/api/status') as r:status=json.load(r)
    check('compiled_version',status['version']=='0.3.5' and status['map_version']==3)
    def stored():
        with request('/api/history') as r:data=json.load(r)
        return len(data)>=4
    wait(stored)
    now=int(time.time()*1000)
    query=f'/api/history.csv?from={now-60000}&to={now+60000}'
    for slot in (0,1,2,32):
        values,filename=rows(query+f'&slot={slot}')
        check(f'slot_{slot}_headers',len(values[0])==17 and values[0][-2:]==['液位计名称','名称说明'])
        data=values[1:]
        if slot in (1,2):
            check(f'slot_{slot}_only_selected_meter',bool(data) and all(v[1]==str(slot) and v[15]==cfg['meters'][slot-1]['name'] for v in data))
            check(f'slot_{slot}_filename',f'slot-{slot:02d}.csv' in filename)
        elif slot==0:check('all_slots_preserved',set(v[1] for v in data)=={'1','2'})
        else:check('unconfigured_empty_slot_header_only',not data)
    for invalid in ('bad','-1','33'):
        try:request(query+'&slot='+invalid);raise AssertionError('invalid slot accepted')
        except urllib.error.HTTPError as e:check('invalid_slot_'+invalid,e.code==400)
    try:request('/api/history?slot=bad');raise AssertionError('invalid JSON history filter accepted')
    except urllib.error.HTTPError as e:check('invalid_JSON_slot_not_all',e.code==400)
    with request('/') as r:html=r.read().decode()
    check('embedded_slot_and_name_selector','id="history-slot"' in html and '液位计（槽位 / 名称）' in html)
    report={'date':'2026-10-16','software':'0.3.5','passed':True,'hardware_executed':False,'host_clock_modified':False,'checks':checks,
            'binaries':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in [bins/'gateway-core.exe',bins/'gateway-web.exe']},'logs':str(run.relative_to(ROOT))}
    (ROOT/'tests/history-export-compiled-verification-20261016.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    metadata={'url':base,'password':password,'run':str(run),'stop':str(run/'stop')}
    (WORK/'preview.json').write_text(json.dumps(metadata),encoding='utf-8')
    print(json.dumps({'passed':True,'checks':len(checks),'url':base,'stop':str(run/'stop')},ensure_ascii=False),flush=True)
    if args.keep_running:
        while not (run/'stop').exists():time.sleep(.25)
finally:
    for p in reversed(children):
        if p.poll() is None:p.terminate();p.wait(timeout=10)
    for stream in streams:stream.close()
