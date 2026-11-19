"""Verify compiled analog switch/API/Modbus behavior on loopback; optional temporary browser preview."""
from pathlib import Path
from types import SimpleNamespace
import argparse,csv,hashlib,http.cookiejar,io,json,socket,subprocess,tempfile,time,urllib.request
from modbus_client import read

ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--keep-running',action='store_true')
options=parser.parse_args()
WORK=ROOT/'tmp/analog-release-20261018';WORK.mkdir(exist_ok=True)
run=Path(tempfile.mkdtemp(prefix='run-',dir=WORK))
checks=[];children=[];logs=[]
def check(name,condition):
    assert condition,name
    checks.append({'name':name,'passed':True})
def port():
    with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
ipc,web,north=port(),port(),port()
cfg=json.loads((ROOT/'deploy/config.demo.json').read_text(encoding='utf-8-sig'))
cfg['meters']=cfg['meters'][:2]
cfg['meters'][0]['name']='首台试验液位计';cfg['meters'][1]['name']='二号罐液位计'
cfg['fast_ms']=1000;cfg['detail_ms']=1000;cfg['history']['period_sec']=1
cfg['tcp'].update(listen=f'127.0.0.1:{north}',allow_writes=False);cfg['allowed_actions']=[]
config=run/'gateway.json';config.write_text(json.dumps(cfg,ensure_ascii=False),encoding='utf-8')
auth,token=run/'admin.json',run/'token'
password='Analog-Simulation-2026!'
bins=ROOT/'output/windows-amd64';flags=getattr(subprocess,'CREATE_NO_WINDOW',0)
subprocess.run([str(bins/'gateway-web.exe'),'--init-admin','--auth-file',str(auth)],input=(password+'\n').encode(),capture_output=True,check=True,creationflags=flags)
base=f'http://127.0.0.1:{web}'
opener=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
csrf=''
def request(path,method='GET',data=None):
    req=urllib.request.Request(base+path,data=None if data is None else json.dumps(data).encode(),headers={'Content-Type':'application/json','X-CSRF-Token':csrf},method=method)
    return opener.open(req,timeout=4)
def api(path,method='GET',data=None):
    with request('/api/'+path,method,data) as r:return json.load(r)
def wait(fn):
    end=time.monotonic()+10
    while time.monotonic()<end:
        try:
            value=fn()
            if value:return value
        except (OSError,urllib.error.URLError):pass
        time.sleep(.05)
    raise AssertionError('wait timed out')
def start(name,arguments):
    log=(run/f'{name}-{len(logs)}.log').open('wb');logs.append(log)
    p=subprocess.Popen([str(bins/(name+'.exe')),*map(str,arguments)],stdout=log,stderr=log,creationflags=flags);children.append(p);return p
def core():return start('gateway-core',['--simulate','--leds=false','--config',config,'--db',run/'gateway.db','--ipc',f'127.0.0.1:{ipc}','--token-file',token])
def apply(**changes):
    c=api('config')
    for key,value in changes.items():
        if key=='analog':c['meters'][0]['analog']=value
        else:c[key]=value
    return api('config','PUT',c)
def view():return api('data')[0]
try:
    cp=core();wait(lambda:token.exists())
    start('gateway-web',['--listen',f'127.0.0.1:{web}','--ipc',f'127.0.0.1:{ipc}','--token-file',token,'--auth-file',auth])
    wait(lambda:request('/').status==200)
    csrf=api('login','POST',{'password':password})['csrf']
    check('compiled_version',api('status')['version']=='0.3.6')
    wait(lambda:view()['groups'][0]['sequence']>0)
    check('default_analog_off_not_sampled',not view()['config']['analog'] and view()['groups'][5]['time_ms']==0)
    with request('/') as r:html=r.read().decode()
    with request('/analog.js') as r:helper=r.read().decode()
    check('embedded_switch_panel_and_helper','模拟量采集' in html and 'id="analog-values"' in html and 'describeAnalog' in helper)
    apply(analog=True)
    wait(lambda:view()['groups'][5]['time_ms']>0)
    data=view();saved=json.loads(config.read_text(encoding='utf-8'))
    check('switch_saved_and_polled',saved['meters'][0]['analog'] and data['groups'][5]['sequence']>0)
    check('committed_period_and_monotonic_age',data['detail_period_ms']==1000 and data['groups'][5]['age_ms']<3000 and not data['collection_paused'])
    check('second_meter_remains_off',not api('data')[1]['config']['analog'] and api('data')[1]['groups'][5]['time_ms']==0)
    mb=SimpleNamespace(host='127.0.0.1',port=north,unit=1)
    check('north_four_words_match_sample',read(mb,3027,4,4)==data['raw'][27:31])
    check('north_group_read_status_set',read(mb,3124,1,4)[0]&1==1)
    apply(analog=False)
    stopped=view()['groups'][5]['sequence'];fast=view()['groups'][0]['sequence']
    wait(lambda:view()['groups'][0]['sequence']>fast)
    check('off_stops_and_clears_sample',view()['groups'][5]['sequence']==stopped and view()['groups'][5]['time_ms']==0 and read(mb,3124,1,4)[0]&1==0)
    apply(analog=True)
    wait(lambda:view()['groups'][5]['sequence']>stopped and view()['groups'][5]['time_ms']>0)
    check('re_enable_requires_new_sample',view()['groups'][5]['time_ms']>0)
    api('simulator/fault','PUT',{'port':1,'address':1,'fault':{'unsupported_start':27}})
    wait(lambda:view()['groups'][5]['unsupported'])
    check('unsupported_does_not_hide_fast_validity',view()['valid']==127 and bool(view()['groups'][5]['error']))
    apply(analog=False);apply(analog=True)
    wait(lambda:view()['groups'][5]['time_ms']>0 and not view()['groups'][5]['unsupported'])
    check('off_on_retries_unsupported',view()['groups'][5]['failures']>0)
    apply(mode='maintenance')
    check('maintenance_reported',view()['collection_paused'])
    apply(mode='normal')
    wait(lambda:view()['groups'][5]['time_ms']>0)
    cp.terminate();cp.wait(timeout=10);cp=core()
    wait(lambda:view()['config']['analog'] and view()['groups'][5]['time_ms']>0)
    check('restart_keeps_switch_and_resumes_collection',api('config')['meters'][0]['analog'])
    now=int(time.time()*1000)
    with request(f'/api/history.csv?from={now-60000}&to={now+60000}&slot=1') as r:rows=list(csv.reader(io.StringIO(r.read().decode('utf-8-sig'))))
    check('history_filter_still_works',len(rows)>1 and all(row[1]=='1' for row in rows[1:]) and len(rows[0])==17)
    # Preview starts with a real simulated sample and analog enabled for meter1 only.
    report={'date':'2026-10-18','software':'0.3.6','passed':True,'hardware_executed':False,'host_clock_modified':False,'checks':checks,
            'binaries':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in [bins/'gateway-core.exe',bins/'gateway-web.exe']},'logs':str(run.relative_to(ROOT))}
    (ROOT/'tests/analog-compiled-verification-20261018.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    metadata={'url':base,'password':password,'stop':str(run/'stop'),'run':str(run)}
    (WORK/'preview.json').write_text(json.dumps(metadata),encoding='utf-8')
    print(json.dumps({'passed':True,'checks':len(checks),'url':base,'stop':str(run/'stop')},ensure_ascii=False),flush=True)
    if options.keep_running:
        while not (run/'stop').exists():time.sleep(.25)
finally:
    for child in reversed(children):
        if child.poll() is None:child.terminate();child.wait(timeout=10)
    for log in logs:log.close()
