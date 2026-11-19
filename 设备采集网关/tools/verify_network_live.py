"""Compiled Windows/loopback network transaction tests; never change host networking."""
from pathlib import Path
import argparse,ctypes,hashlib,http.cookiejar,json,os,shlex,shutil,socket,struct,subprocess,sys,tempfile,time,urllib.request,urllib.error
ROOT=Path(__file__).resolve().parents[1]
SHELL=Path.home()/'.cache/codex-runtimes/codex-primary-runtime/dependencies/native/git/usr/bin/sh.exe'
FLAGS=getattr(subprocess,'CREATE_NO_WINDOW',0)|getattr(subprocess,'DETACHED_PROCESS',0)

def posix(p):
 s=Path(p).resolve().as_posix();return '/'+s[0].lower()+s[2:] if s[1:3]==':/' else s

def stop(base):
 path=base/'data/fixture-pids.json'
 if not path.exists():return
 k=ctypes.WinDLL('kernel32',use_last_error=True);k.OpenProcess.restype=ctypes.c_void_p;k.CloseHandle.argtypes=[ctypes.c_void_p];k.TerminateProcess.argtypes=[ctypes.c_void_p,ctypes.c_uint];k.QueryFullProcessImageNameW.argtypes=[ctypes.c_void_p,ctypes.c_uint,ctypes.c_wchar_p,ctypes.POINTER(ctypes.c_uint)]
 for name,pid in json.loads(path.read_text()).items():
  h=k.OpenProcess(0x100001|0x1000,False,pid)
  if not h:continue
  try:
   buf=ctypes.create_unicode_buffer(32768);size=ctypes.c_uint(32768)
   if k.QueryFullProcessImageNameW(h,0,buf,ctypes.byref(size)):
    assert Path(buf.value).resolve()==(base/'bin'/(name+'.exe')).resolve(),'Refusing to stop another application'
    k.TerminateProcess(h,0)
  finally:k.CloseHandle(h)
 time.sleep(.2);path.unlink(missing_ok=True)

def service(base,action):
 assert action in ['network-stop','network-start']
 with (base/'logs/service-calls.log').open('a') as f:f.write(action+'\n')
 if action=='network-stop':stop(base);return
 failure=base/'fail-start-once'
 if failure.exists():failure.unlink();raise RuntimeError('Injected start failure in isolated bridge')
 plan=json.loads((base/'plan.json').read_text());common=['--ipc',plan['ipc'],'--token-file',str(base/'data/ipc.token'),'--network-base',str(base)]
 core=['--simulate','--network-fixture','--config',str(base/'config/gateway.json'),'--db',str(base/'data/gateway-v2.db'),'--service-script',str(base/'service.sh'),'--service-shell',str(SHELL),*common]
 web=['--listen',plan['web'],'--auth-file',str(base/'data/admin.json'),*common]
 env=os.environ.copy();env['PATH']=str(SHELL.parent)+os.pathsep+env.get('PATH','');pids={}
 for name,args in [('gateway-core',core),('gateway-web',web)]:
  with (base/'logs'/(name+'.log')).open('ab') as log:
   p=subprocess.Popen([str(base/'bin'/(name+'.exe')),*args],cwd=base,stdout=log,stderr=log,env=env,creationflags=FLAGS)
  pids[name]=p.pid
 (base/'data/fixture-pids.json').write_text(json.dumps(pids))

if len(sys.argv)>1 and sys.argv[1]=='service':service(Path(sys.argv[2]),sys.argv[3]);sys.exit(0)
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--keep-running',action='store_true');parser.add_argument('--version',default='0.8.0');parser.add_argument('--date',default='2026-11-09');opt=parser.parse_args()
base=Path(tempfile.mkdtemp(prefix='network-live-',dir=ROOT/'tmp'))
for name in ['data','config','logs','bin','fixture']:(base/name).mkdir()
bins=ROOT/'output/windows-amd64'
for name in ['gateway-core','gateway-web']:shutil.copyfile(bins/(name+'.exe'),base/'bin'/(name+'.exe'))
def freeport():
 with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
north,ipc,web=freeport(),freeport(),freeport();url=f'http://127.0.0.1:{web}'
(base/'plan.json').write_text(json.dumps({'ipc':f'127.0.0.1:{ipc}','web':f'127.0.0.1:{web}'}))
cfg=json.loads((ROOT/'deploy/config.demo.json').read_text(encoding='utf-8-sig'));cfg['meters']=cfg['meters'][:1];cfg['fast_ms']=1000;cfg['detail_ms']=1000;cfg['tcp'].update(listen=f'192.168.0.230:{north}',allow_writes=True);cfg['allowed_actions']=[2,4]
(base/'config/gateway.json').write_text(json.dumps(cfg),encoding='utf-8')
(base/'config/service.env').write_text(f'WEB_LISTEN=192.168.1.230:{web}\nWATCHDOG=\nTLS_CERT=\nTLS_KEY=\n')
for n,ip in [('net.conf','192.168.1.230'),('net2.conf','192.168.0.230')]:
 (base/'fixture'/n).write_text(f'# simulated vendor file\nMETHOD=STATIC\nIPADDR={ip}\nNETMASK=255.255.255.0\nGATEWAY=0.0.0.0\n')
script='#!/bin/sh\nexec '+shlex.quote(posix(sys.executable))+' '+shlex.quote(posix(__file__))+' service '+shlex.quote(str(base))+' "$1"\n'
(base/'service.sh').write_text(script,encoding='utf-8',newline='\n')
password='Network-Fixture-2026!'
subprocess.run([str(base/'bin/gateway-web.exe'),'--init-admin','--auth-file',str(base/'data/admin.json')],input=(password+'\n').encode(),capture_output=True,check=True,creationflags=FLAGS)
browser=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()));csrf='';checks=[]
def request(path,method='GET',body=None,token=None):
 headers={'Origin':url,'Content-Type':'application/json','X-CSRF-Token':csrf if token is None else token}
 req=urllib.request.Request(url+'/api/'+path,data=None if body is None else json.dumps(body).encode(),headers=headers,method=method)
 try:
  with browser.open(req,timeout=8) as r:return r.status,json.load(r)
 except urllib.error.HTTPError as e:return e.code,e.read().decode()
def api(path,method='GET',body=None):
 code,data=request(path,method,body);assert code in [200,202],(path,code,data);return data
def wait(fn,timeout=20):
 end=time.monotonic()+timeout
 while time.monotonic()<end:
  try:
   v=fn()
   if v:return v
  except (OSError,urllib.error.URLError,AssertionError):pass
  time.sleep(.1)
 raise AssertionError('Timed out waiting for '+getattr(fn,'__name__','condition'))
def login():
 global csrf
 wait(lambda:request('session')[0] in [200,401]);csrf=api('login','POST',{'password':password})['csrf']
def phase(p):
 n=api('network');return n if n['phase']==p else None
def check(name,v):assert v,name;checks.append({'name':name,'passed':True});print(name,flush=True)
def mb(addr,value):
 pdu=struct.pack('>BHH',6,addr,value)
 with socket.create_connection(('127.0.0.1',north),timeout=3) as s:s.sendall(struct.pack('>HHHB',9,0,6,1)+pdu);return s.recv(260)[7:]
def recover():
 return subprocess.run([str(base/'bin/gateway-core.exe'),'--network-base',str(base),'--network-recover','--network-fixture','--simulate'],cwd=base,capture_output=True,creationflags=FLAGS)
service(base,'network-start')
try:
 login();initial=api('network');check('network_page_and_supported_fixture',initial['supported'] and initial['settings']['tcp_interface']=='eth1')
 with browser.open(url) as r:check('embedded_network_form',b'network-panel' in r.read() and r.headers['X-Gateway-Web-Version']==opt.version)
 # The configuration API may change a service port but never the selected host.
 original=api('config');bad_host=json.loads(json.dumps(original));bad_host['tcp']['listen']=f'0.0.0.0:{north}'
 check('ordinary_configuration_cannot_change_TCP_host',request('config','PUT',bad_host)[0]==409 and api('config')==original)
 changed=json.loads(json.dumps(original));new_port=freeport();changed['tcp']['listen']=f'192.168.0.230:{new_port}'
 api('config','PUT',changed)
 with socket.create_connection(('127.0.0.1',new_port),timeout=3) as connection:
  connection.settimeout(3);connection.sendall(bytes.fromhex('000100000006010400000004'));reply=connection.recv(260)
  check('service_port_change_rebinds_TCP',len(reply)>=9 and reply[7]==4)
 now=api('config');check('port_change_preserves_network_binding_and_instruments',now['meters']==original['meters'] and now['ports']==original['ports'] and api('network')['settings']['tcp_interface']=='eth1')
 check('status_exposes_current_TCP_address',api('status')['tcp']['listen']==f'192.168.0.230:{new_port}')
 now['tcp']['listen']=original['tcp']['listen'];api('config','PUT',now)
 check('original_service_port_can_be_restored',api('status')['tcp']['listen']==original['tcp']['listen'])
 check('CSRF_required',request('network/apply','POST',{'confirmed':True},token='')[0]==403)
 candidate=initial['settings'];candidate['interfaces'][0]['ip']='192.168.10.231';candidate['interfaces'][1]['ip']='192.168.20.231';candidate['interfaces'][1]['gateway']='192.168.20.1'
 bad=json.loads(json.dumps(candidate));bad['interfaces'][0]['mask']='255.0.255.0';check('invalid_mask_rejected',request('network/apply','POST',{'settings':bad,'confirmed':True})[0]==409)
 mb(30000,5);wait(lambda:api('commands')['controls'][0]['main']['state'] in [3,4,5]);check('unreset_control_blocks_network',request('network/apply','POST',{'settings':candidate,'confirmed':True})[0]==409);mb(30000,0)
 auth_before=hashlib.sha256((base/'data/admin.json').read_bytes()).hexdigest();gate=api('network/apply','POST',{'settings':candidate,'confirmed':True})
 check('journal_gate_persisted_before_response',gate['busy'] and (base/'data/network-state.json').exists())
 check('duplicate_apply_rejected',request('network/apply','POST',{'settings':candidate,'confirmed':True})[0]==409)
 check('Modbus_commands_and_reset_blocked',mb(30000,5)==bytes([0x86,6]) and mb(30000,0)==bytes([0x86,6]))
 check('configuration_write_blocked',request('config','PUT',api('config'))[0]==409)
 check('program_restart_blocked',request('maintenance/restart','POST',{'confirmed':True})[0]==409)
 check('password_change_blocked',request('admin/password','POST',{'current_password':password,'new_password':'Different-Fixture-2026!','confirm_password':'Different-Fixture-2026!'})[0]==503)
 wait(lambda:request('session')[0]==401);login();wait(lambda:phase('trial'))
 check('independent_worker_restarted_core_and_web',api('status')['version']==opt.version and api('network')['busy'])
 check('TCP_and_web_listeners_follow_settings',api('config')['tcp']['listen']==f'192.168.20.231:{north}' and 'WEB_LISTEN=0.0.0.0:' in (base/'config/service.env').read_text())
 check('wrong_job_cannot_confirm',request('network/confirm','POST',{'id':'0'*32,'confirmed':True})[0]==409)
 api('network/confirm','POST',{'id':gate['id'],'confirmed':True});wait(lambda:phase('confirmed'))
 check('explicit_confirm_persisted_and_account_preserved',not api('network')['busy'] and hashlib.sha256((base/'data/admin.json').read_bytes()).hexdigest()==auth_before)
 stop(base);check('confirmed_boot_does_not_restore_old_IP',recover().returncode==0 and '192.168.20.231' in (base/'fixture/net2.conf').read_text());service(base,'network-start');login()
 # Verify a full production 180-second timeout; no test-only short timeout flag.
 before={str(p.relative_to(base)):p.read_bytes() for p in [base/'fixture/net.conf',base/'fixture/net2.conf',base/'config/gateway.json',base/'config/service.env']}
 target=json.loads(json.dumps(candidate));target['interfaces'][1]['ip']='192.168.20.232'
 api('network/apply','POST',{'settings':target,'confirmed':True});wait(lambda:request('session')[0]==401);login();trial=wait(lambda:phase('trial'));started=time.monotonic()
 check('production_trial_window_180_seconds',160000<trial['deadline_ms']-int(time.time()*1000)<=180000)
 print('WAITING_FOR_REAL_180_SECOND_TIMEOUT',flush=True)
 while time.monotonic()-started<200:
  try:
   n=api('network')
   if n['phase']=='rolled_back':break
   if request('session')[0]==401:login()
  except (AssertionError,OSError,urllib.error.URLError):
   try:login()
   except (AssertionError,OSError,urllib.error.URLError):pass
  time.sleep(1)
 else:raise AssertionError('180-second rollback not observed')
 check('unconfirmed_trial_restores_files_and_services',all((base/p).read_bytes()==b for p,b in before.items()) and not api('network')['busy'])
 # Simulate power loss by stopping only the fixture's own processes/worker.
 job=api('network/apply','POST',{'settings':target,'confirmed':True});wait(lambda:request('session')[0]==401);login();wait(lambda:phase('trial'))
 state=json.loads((base/'data/network-state.json').read_text());worker=state['pid'];stop(base)
 k=ctypes.WinDLL('kernel32',use_last_error=True);k.OpenProcess.restype=ctypes.c_void_p;k.TerminateProcess.argtypes=[ctypes.c_void_p,ctypes.c_uint];k.CloseHandle.argtypes=[ctypes.c_void_p]
 h=k.OpenProcess(0x100001|0x1000,False,worker);assert h
 try:k.TerminateProcess(h,0)
 finally:k.CloseHandle(h)
 time.sleep(.3);check('interrupted_trial_boot_recovers',recover().returncode==0 and all((base/p).read_bytes()==b for p,b in before.items()))
 service(base,'network-start');login();check('power_recovery_has_no_replayed_control',api('commands')['controls'][0]['main']['state']==0 and api('network')['phase']=='rolled_back')
 wait(lambda:api('data')[0]['valid']==127);check('acquisition_resumed_after_recovery',api('data')[0]['valid']==127)
 record={'date':opt.date,'software':opt.version,'passed':True,'hardware_executed':False,'host_network_modified':False,'OS_restarted':False,'fixture':'Windows compiled core/web and independent network worker; simulated vendor files and service process bridge; socket proof production logic separately unit-tested','checks':checks,'binaries':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in [bins/'gateway-core.exe',bins/'gateway-web.exe']}}
 (ROOT/f'tests/network-compiled-verification-{opt.version}-{opt.date.replace(chr(45),str())}.json').write_text(json.dumps(record,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 (ROOT/'tmp/network-preview.json').write_text(json.dumps({'url':url,'password':password,'base':str(base),'stop':str(base/'stop')}),encoding='utf-8');print(json.dumps({'passed':True,'checks':len(checks),'url':url,'base':str(base)}),flush=True)
 if opt.keep_running:
  while not (base/'stop').exists():time.sleep(.2)
finally:stop(base)
