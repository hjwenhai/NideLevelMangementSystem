"""Compiled HTTP reset tests with Modbus TCP disabled; isolated simulated gateway only."""
from pathlib import Path
import argparse,copy,hashlib,http.cookiejar,json,socket,sqlite3,subprocess,tempfile,time,urllib.request,urllib.error

ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--keep-running',action='store_true');args=parser.parse_args()
work=Path(tempfile.mkdtemp(prefix='web-reset-',dir=ROOT/'tmp'));checks=[];children=[];logs=[]
bins=ROOT/'output/windows-amd64'
def port():
 with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
web,ipc,north=port(),port(),port();base=f'http://127.0.0.1:{web}'
cfg=json.loads((ROOT/'deploy/config.demo.json').read_text(encoding='utf-8-sig'));cfg['meters']=cfg['meters'][:4]
cfg['tcp'].update(enabled=False,listen=f'127.0.0.1:{north}',allow_writes=False);cfg['fast_ms']=1000;cfg['detail_ms']=1000
cfg['ports'][3].update(role='north_rtu_slave',allow_writes=False)
config=work/'config.json';config.write_text(json.dumps(cfg,ensure_ascii=False),encoding='utf-8')
db,auth,token=work/'gateway.db',work/'admin.json',work/'ipc.token';password='Web-Reset-Only-20261027!'
subprocess.run([str(bins/'gateway-web.exe'),'--init-admin','--auth-file',str(auth)],input=(password+'\n').encode(),capture_output=True,check=True,creationflags=subprocess.CREATE_NO_WINDOW)
browser=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()));csrf=''
def api(path,method='GET',data=None,headers=None):
 h={'Content-Type':'application/json','Origin':base,'X-CSRF-Token':csrf};h.update(headers or {})
 req=urllib.request.Request(base+'/api/'+path,data=None if data is None else json.dumps(data).encode(),method=method,headers=h)
 with browser.open(req,timeout=5) as response:return json.load(response)
def status(path,method='GET',data=None,headers=None):
 try:api(path,method,data,headers);return 200
 except urllib.error.HTTPError as e:return e.code
def wait(fn):
 deadline=time.monotonic()+15
 while time.monotonic()<deadline:
  try:
   value=fn()
   if value:return value
  except (OSError,urllib.error.URLError):pass
  time.sleep(.05)
 raise AssertionError('wait timeout')
def start():
 global csrf
 for name,opts in [('gateway-core',['--simulate','--config',str(config),'--db',str(db),'--ipc',f'127.0.0.1:{ipc}','--token-file',str(token)]),('gateway-web',['--listen',f'127.0.0.1:{web}','--ipc',f'127.0.0.1:{ipc}','--token-file',str(token),'--auth-file',str(auth)])]:
  log=(work/f'{name}-{len(logs)}.log').open('wb');logs.append(log)
  children.append(subprocess.Popen([str(bins/(name+'.exe')),*opts],stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW))
 wait(lambda:status('session')==401)
 csrf=api('login','POST',{'password':password})['csrf']
 wait(lambda:api('status')['version']=='0.4.2')
def stop():
 for child in reversed(children):
  if child.poll() is None:child.terminate();child.wait(timeout=10)
def check(name,condition):assert condition,name;checks.append({'name':name,'passed':True})
def idle():return {'value':0,'state':0,'error':0,'count':0,'request':{'id':'','source':''},'sent':False,'accepted_ms':0,'updated_ms':0}
def cycle(slot,state,stop=False):
 return {'value':1 if stop else 6,'state':state,'error':2 if state==4 else 9,'count':1,
  'request':{'id':f'fixture-{slot}-{stop}-{state}','slot':slot,'binding_version':1,'config_revision':cfg['revision'],'port':(slot-1)//16+1,'unit':(slot-1)%16+1,'timeout_ms':500,'command':4 if stop else 6,'south_action':4 if stop else 256,'stop':stop,'source':'fixture/old-control'},
  'sent':state==5,'accepted_ms':1792396532866,'updated_ms':1792396532983,'message':'仪表非远程，未发送' if state==4 else '运行中断，未决动作不重放'}
def seed():
 with sqlite3.connect(db) as con:
  con.execute('DELETE FROM control_slots')
  for slot,main,st in [(1,cycle(1,4),idle()),(2,idle(),cycle(2,5,True)),(64,cycle(64,5),cycle(64,4,True))]:
   con.execute('INSERT INTO control_slots VALUES(?,?)',(slot,json.dumps({'slot':slot,'main':main,'stop':st},ensure_ascii=False)))
def results():return api('commands')['controls']
def request(slot,channel):
 c=results()[slot-1][channel]
 return {'slot':slot,'stop':channel=='stop','id':c['request']['id'],'operator':'模拟核查01','reason':'模拟现场已核查，只清结果不重发','site_verified':True}
try:
 start();stop();seed();start();csrf=api('login','POST',{'password':password})['csrf']
 wait(lambda:all(v['valid']==127 for v in api('data') if v['config']['enabled']))
 check('current_software',api('status')['version']=='0.4.2')
 check('north_tcp_disabled_and_write_permission_off',api('config')['tcp']['enabled'] is False and api('config')['tcp']['allow_writes'] is False)
 with socket.socket() as s:check('north_port_not_listening',s.connect_ex(('127.0.0.1',north))!=0)
 check('both_states_both_channels_and_disabled_slot64_restored',[(r['slot'],ch,r[ch]['state']) for r in results() for ch in ['main','stop'] if r[ch]['state'] in [4,5]]==[(1,'main',4),(2,'stop',5),(64,'main',5),(64,'stop',4)])
 check('SYS3_initial_critical',api('status')['indicators']['modes'][2]==1)
 req=request(1,'main');before=results()
 for name,mutation in [('confirmation',{'site_verified':False}),('operator',{'operator':''}),('reason',{'reason':''}),('stale_id',{'id':'stale'}),('wrong_channel',{'stop':True})]:
  check('reject_'+name,status('control/maintenance-reset','POST',req|mutation)==(400 if name=='confirmation' else 409) and results()==before)
 check('CSRF_required',status('control/maintenance-reset','POST',None,{'X-CSRF-Token':''})==403)
 check('foreign_origin_rejected',status('control/maintenance-reset','POST',None,{'Origin':'http://foreign.invalid'})==403)
 check('GET_reset_rejected',status('control/maintenance-reset') in [404,405])
 for slot,ch in [(1,'main'),(2,'stop'),(64,'main'),(64,'stop')]:
  before=results();req=request(slot,ch);api('control/maintenance-reset','POST',req);after=results();cleared=after[slot-1][ch]
  check(f'reset_{slot}_{ch}_clears_only_selected',cleared['state']==cleared['value']==cleared['error']==0 and cleared['count']==before[slot-1][ch]['count'] and all(after[i][key]==before[i][key] for i in range(64) for key in ['main','stop'] if (i,key)!=(slot-1,ch)))
  check(f'reset_{slot}_{ch}_audited',any(e['kind']=='control_maintenance_reset' and json.loads(e['message'])['before']==before[slot-1] and json.loads(e['message'])['after']==after[slot-1] and json.loads(e['message'])['operator']==req['operator'] and json.loads(e['message'])['source']=='web/manual-review' for e in api('events')))
  check(f'duplicate_{slot}_{ch}_rejected',status('control/maintenance-reset','POST',req)==409)
 check('SYS3_critical_cleared',api('status')['indicators']['modes'][2]!=1)
 check('no_control_accept_or_send_from_resets',not any(e['kind'] in ['control_accept','control_send_claim','control_running'] for e in api('events')))
 snapshot=results();stop();start();csrf=api('login','POST',{'password':password})['csrf']
 check('reset_durable_and_no_replay_after_restart',results()==snapshot)
 check('collection_continues',wait(lambda:all(v['valid']==127 for v in api('data') if v['config']['enabled'])))
 report={'date':'2026-10-27','software':'0.4.2','passed':True,'hardware_executed':False,'modbus_tcp_enabled':False,'checks':checks,'binaries':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in [bins/'gateway-core.exe',bins/'gateway-web.exe']}}
 (ROOT/'tests/web-control-reset-compiled-20261027.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 if args.keep_running:
  stop();seed();start()
  preview={'url':base,'password':password,'stop':str(work/'stop'),'db':str(db),'work':str(work)}
  (ROOT/'tmp/web-reset-preview.json').write_text(json.dumps(preview),encoding='utf-8')
  print(json.dumps({'passed':True,'checks':len(checks),'url':base,'stop':str(work/'stop')}),flush=True)
  deadline=time.monotonic()+1800
  while not (work/'stop').exists() and time.monotonic()<deadline:time.sleep(.2)
 else:print(json.dumps({'passed':True,'checks':len(checks)}))
finally:
 stop()
 for log in logs:log.close()
