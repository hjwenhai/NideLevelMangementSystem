"""Compiled localhost-only remote-level integration. Never connect to physical devices."""
from pathlib import Path
import argparse,hashlib,http.cookiejar,json,socket,struct,subprocess,tempfile,time,urllib.request,urllib.error
from types import SimpleNamespace
from modbus_client import exchange,read
R=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--keep-running',action='store_true');parser.add_argument('--version',default='0.6.0');parser.add_argument('--date',default='2026-11-08');args=parser.parse_args()
work=Path(tempfile.mkdtemp(prefix='remote-level-live-',dir=R/'tmp'))
def port():
 with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
north,ipc,web=port(),port(),port();base=f'http://127.0.0.1:{web}'
cfg=json.loads((R/'deploy/config.demo.json').read_text(encoding='utf-8-sig'));cfg['tcp'].update(listen=f'127.0.0.1:{north}',allow_writes=True);cfg['allowed_actions']=[2,4]
config=work/'gateway.json';config.write_text(json.dumps(cfg,ensure_ascii=False),encoding='utf-8')
bins=R/'output/windows-amd64';auth,db,token=work/'admin.json',work/'data.db',work/'ipc.token';password='Remote-Level-Simulation!20261029'
subprocess.run([str(bins/'gateway-web.exe'),'--init-admin','--auth-file',str(auth)],input=password+'\n',encoding='utf-8',capture_output=True,check=True,creationflags=subprocess.CREATE_NO_WINDOW)
children=[];checks=[];csrf='';opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
def start(name,extra):
 log=open(work/(name+str(len(children))+'.log'),'wb');p=subprocess.Popen([str(bins/(name+'.exe')),*extra],stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW);children.append(p);return p
def core():return start('gateway-core',['--simulate','--config',str(config),'--db',str(db),'--ipc',f'127.0.0.1:{ipc}','--token-file',str(token)])
def wait(fn):
 end=time.monotonic()+12
 while time.monotonic()<end:
  try:
   if fn():return
  except (OSError,urllib.error.URLError):pass
  time.sleep(.05)
 raise AssertionError('condition timed out')
def http(path,method='GET',body=None,csrf_token=True,origin=None):
 headers={'Content-Type':'application/json'}
 if csrf_token:headers['X-CSRF-Token']=csrf
 if origin:headers['Origin']=origin
 request=urllib.request.Request(base+'/api/'+path,None if body is None else json.dumps(body).encode(),headers,method=method)
 with opener.open(request,timeout=5) as response:return json.load(response)
def expect(value,name):
 assert value,name
 checks.append({'name':name,'passed':True})
def http_error(code,*a,**kw):
 try:http(*a,**kw)
 except urllib.error.HTTPError as e:return e.code==code
 return False
client=SimpleNamespace(host='127.0.0.1',port=north,unit=1)
def fc06(a,v):return exchange(client.host,north,1,struct.pack('>BHH',6,a,v))
def target(slot,raw):return exchange(client.host,north,1,struct.pack('>BHHBI',16,31000+4*(slot-1),2,4,raw&0xffffffff))
def cycle(slot):return http('commands')['controls'][slot-1]['main']
def done(slot):wait(lambda:cycle(slot)['state'] in [3,4,5]);return cycle(slot)
human={'operator':'SIM-01','reason':'模拟仪表尺量校准测试','site_verified':True}
def reset(slot):return http('control/maintenance-reset','POST',{'slot':slot,'stop':False,'id':cycle(slot)['request']['id'],**human})
try:
 cp=core();wp=start('gateway-web',['--listen',f'127.0.0.1:{web}','--ipc',f'127.0.0.1:{ipc}','--token-file',str(token),'--auth-file',str(auth)])
 wait(lambda:token.exists() and urllib.request.urlopen(base,timeout=1).status==200)
 csrf=http('login','POST',{'password':password})['csrf']
 expect(http('status')['version']==args.version,'compiled_current_software')
 expect(read(client,0,31)[30]==1,'north_reports_remote_level_extension')
 body={'slot':1,'target_raw':150100,**human}
 expect(http_error(403,'control/level-adjust','POST',body,False),'web_missing_csrf_rejected')
 expect(http_error(403,'control/level-adjust','POST',body,True,'http://other.invalid'),'web_cross_origin_rejected')
 expect(http_error(409,'control/level-adjust','POST',body),'calibration_disabled_by_default')
 cfg=http('config');cfg['allowed_actions'].append(512);http('config','PUT',cfg)
 expect(http_error(400,'control/level-adjust','POST',{'slot':1,**human}),'omitted_target_not_interpreted_as_zero')
 expect(http_error(400,'control/level-adjust','POST',{**body,'site_verified':False}),'web_requires_human_confirmation')
 expect(http_error(400,'control/level-adjust','POST',{**body,'target_raw':150100.5}),'web_rejects_fractional_raw_integer')
 expect(http_error(400,'control/level-adjust','POST',{**body,'target_raw':1000000}),'web_rejects_target_out_of_range')
 expect(http('control/level-adjust','POST',body)['accepted'],'web_target_submission_accepted')
 c=done(1);v=c['level_adjustment']
 expect(c['state']==3 and c['sent'] and c['request']['south_action']==0 and v['password_cleared'],'web_transaction_confirmed_without_FW699_action')
 expect(v['new_offset_raw']==150100-v['fresh_level_raw']+v['previous_offset_raw'],'fresh_read_formula_matches_audit')
 wait(lambda:http('data')[0]['level_mm']==15010)
 expect(read(client,1004,2)==[2,0x4a54],'north_reports_new_instrument_level')
 count=c['count'];fc06(30000,11);expect(cycle(1)['count']==count,'repeated_command_does_not_start_another_write')
 expect(http_error(409,'control/level-adjust','POST',{**body,'target_raw':150200}),'web_blocks_second_target_until_result_reset')
 old_id=c['request']['id'];reset(1)
 expect(http('data')[0]['level_mm']==15010 and cycle(1)['state']==0,'web_reset_only_clears_result_not_offset')
 expect(http_error(409,'control/maintenance-reset','POST',{'slot':1,'stop':False,'id':old_id,**human}),'stale_result_id_rejected')
 expect(target(64,50000)[0]==16,'north_atomic_target_pair_slot64')
 expect(read(client,31252,4,3)==[0,50000,1,0],'north_target_ready_flag')
 expect(fc06(30756,11)[0]==6,'north_command11_slot64')
 c=done(64);expect(c['state']==3 and c['request']['port']==4 and c['request']['unit']==16,'slot64_routes_COM4_address16')
 wait(lambda:http('data')[63]['level_mm']==5000)
 expect(read(client,31252,4,3)==[0,50000,0,0],'accepted_target_consumed_without_becoming_new_command')
 try:target(64,50010);raise AssertionError('busy target was accepted')
 except RuntimeError as e:expect(str(e)=='Modbus exception 6','target_pair_rejected_while_result_latched')
 snapshot=read(client,30756,12,3);cp.terminate();cp.wait(timeout=10);cp=core();wait(lambda:http('status')['uptime_sec']<10)
 expect(read(client,30756,12,3)==snapshot and cycle(64)['request']['id']==c['request']['id'],'compiled_restart_preserves_result_without_replay')
 reset(64);cfg=http('config');cfg['tcp']['allow_writes']=False;http('config','PUT',cfg)
 expect(http('control/level-adjust','POST',{'slot':1,'target_raw':150200,**human})['accepted'],'web_works_without_Modbus_TCP_write_permission')
 expect(done(1)['state']==3,'second_web_calibration_confirmed');reset(1)
 cfg=http('config');cfg['tcp']['allow_writes']=True;http('config','PUT',cfg)
 expect(target(1,0)[0]==16 and fc06(30000,11)[0]==6,'zero_is_valid_explicit_target')
 zero=done(1);expect(zero['state']==4 and zero['error']==12,'zero_target_allowed_but_calculated_offset_range_still_checked');reset(1)
 # Leave an idle, explicitly simulation-only service for manual browser review.
 # A new positive value will be entered through the actual UI.
 result={'passed':True,'software':args.version,'date':args.date,'hardware_executed':False,'checks':checks,'binaries':{n:sha for n,sha in ((n,hashlib.sha256((bins/n).read_bytes()).hexdigest()) for n in ['gateway-core.exe','gateway-web.exe'])}}
 (R/f'tests/remote-level-live-{args.version}-{args.date.replace(chr(45),str())}.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 print(json.dumps({'passed':True,'checks':len(checks)},ensure_ascii=False),flush=True)
 if args.keep_running:
  stop=work/'preview.stop';preview={'url':base,'password':password,'north':north,'ipc':ipc,'stop':str(stop),'core_pid':cp.pid,'web_pid':wp.pid,'work':str(work)}
  (R/'tmp/remote-level-preview.json').write_text(json.dumps(preview),encoding='utf-8');print('Preview ready',flush=True)
  deadline=time.monotonic()+1800
  while time.monotonic()<deadline and not stop.exists():time.sleep(.5)
finally:
 for child in children:
  if child.poll() is None:child.terminate();child.wait(timeout=10)
