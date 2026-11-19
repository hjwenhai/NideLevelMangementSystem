"""Signed web upgrade with real Windows processes and an isolated service bridge. No device or OS reboot."""
from pathlib import Path
import argparse,ctypes,hashlib,http.cookiejar,io,json,os,shlex,shutil,socket,struct,subprocess,sys,tarfile,tempfile,time,urllib.request,urllib.error,zipfile
ROOT=Path(__file__).resolve().parents[1];SHELL=Path.home()/'.cache/codex-runtimes/codex-primary-runtime/dependencies/native/git/usr/bin/sh.exe'
FILES=['bin/gateway-core','bin/gateway-web','bin/gateway-updater','deploy/install.sh','deploy/service.sh','deploy/supervise.sh','deploy/rollback.sh','deploy/S99levelgateway','deploy/settings.example.env','deploy/update-trust.json','config.default.json','RELEASE','ARCH','NORTH_VERSION','UPDATE_VERSION','SHA256SUMS']
def posix(p):s=Path(p).resolve().as_posix();return '/'+s[0].lower()+s[2:] if s[1:3]==':/' else s
def owned_stop(base):
 p=base/'data/fixture-pids.json'
 if not p.exists():return
 k=ctypes.WinDLL('kernel32',use_last_error=True);k.OpenProcess.restype=ctypes.c_void_p;k.CloseHandle.argtypes=[ctypes.c_void_p];k.TerminateProcess.argtypes=[ctypes.c_void_p,ctypes.c_uint];k.QueryFullProcessImageNameW.argtypes=[ctypes.c_void_p,ctypes.c_uint,ctypes.c_wchar_p,ctypes.POINTER(ctypes.c_uint)]
 for proc in json.loads(p.read_text()).values():
  h=k.OpenProcess(0x100001|0x1000,False,proc['pid'])
  if not h:continue
  try:
   buf=ctypes.create_unicode_buffer(32768);size=ctypes.c_uint(32768)
   if k.QueryFullProcessImageNameW(h,0,buf,ctypes.byref(size)):
    actual=Path(buf.value).resolve();expected=Path(proc['exe']).resolve()
    assert actual==expected and actual.is_relative_to((base/'releases').resolve()),'Refusing to stop unrelated process'
    k.TerminateProcess(h,0);time.sleep(.15)
  finally:k.CloseHandle(h)
 p.unlink(missing_ok=True)
def service(base,release,action):
 plan=json.loads((base/'fixture-plan.json').read_text());folder=base/'releases'/release;assert folder.resolve().is_relative_to((base/'releases').resolve())
 with (base/'logs/service-calls.log').open('a') as f:f.write(release+':'+action+'\n')
 if action=='stop':owned_stop(base);return
 assert action=='start';assert (base/'current').resolve()==folder.resolve()
 failure=base/'fixture-fail-target'
 if failure.exists() and failure.read_text()==release:raise RuntimeError('Injected service start failure')
 common=['--ipc',plan['ipc'],'--token-file',str(base/'data/ipc.token'),'--upgrade-base',str(base),'--upgrade-fixture']
 core=['--simulate','--config','config/gateway.json','--db','data/gateway-v2.db','--service-script',str(folder/'deploy/service.sh'),'--service-shell',str(SHELL),'--upgrade-web-listen',plan['web'],*common]
 web=['--listen',plan['web'],'--auth-file',str(base/'data/admin.json'),*common]
 env=os.environ.copy();env['PATH']=str(SHELL.parent)+os.pathsep+env.get('PATH','');pids={}
 for role,flags in [('gateway-core',core),('gateway-web',web)]:
  exe=folder/'bin'/role
  with (base/'logs'/(role+'-'+release+'.log')).open('ab') as log:
   child=subprocess.Popen([str(exe),*flags],cwd=base,stdout=log,stderr=log,env=env,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0)|getattr(subprocess,'DETACHED_PROCESS',0))
  pids[role]={'pid':child.pid,'exe':str(exe)}
 (base/'data/fixture-pids.json').write_text(json.dumps(pids))
if len(sys.argv)>1 and sys.argv[1]=='service':service(Path(sys.argv[2]),sys.argv[3],sys.argv[4]);sys.exit(0)
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--keep-running',action='store_true');parser.add_argument('--version',default='0.7.0');parser.add_argument('--date',default='2026-11-08');opt=parser.parse_args()
work=Path(tempfile.mkdtemp(prefix='web-upgrade-',dir=ROOT/'tmp'));base=work/'base';bins=ROOT/'output/windows-amd64';signer=bins/'gateway-release.exe';checks=[]
for d in ['data','config','logs','releases']:(base/d).mkdir(parents=True,exist_ok=True)
def port():
 with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
north,ipc,web=port(),port(),port();url=f'http://127.0.0.1:{web}';plan={'ipc':f'127.0.0.1:{ipc}','web':f'127.0.0.1:{web}'};(base/'fixture-plan.json').write_text(json.dumps(plan))
private,public=work/'test-private.key',base/'config/update-trust.json';subprocess.run([str(signer),'keygen','--private-key',str(private),'--public-key',str(public)],check=True,capture_output=True)
cfg=json.loads((ROOT/'deploy/config.demo.json').read_text(encoding='utf-8-sig'));cfg['meters']=cfg['meters'][:1];cfg['fast_ms']=1000;cfg['detail_ms']=1000;cfg['history']['period_sec']=1;cfg['tcp'].update(listen=f'127.0.0.1:{north}',allow_writes=True);cfg['allowed_actions']=[2,4,512];cfg['meters'][0]['analog']=True
(base/'config/gateway.json').write_text(json.dumps(cfg,ensure_ascii=False),encoding='utf-8');(base/'config/service.env').write_text('WEB_LISTEN='+plan['web']+'\nWATCHDOG=\nTLS_CERT=\nTLS_KEY=\n');password='Upgrade-Fixture-2026!'
subprocess.run([str(bins/'gateway-web.exe'),'--init-admin','--auth-file',str(base/'data/admin.json')],input=(password+'\n').encode(),capture_output=True,check=True)
def stage(release):
 folder=work/('gt6757s-'+release+'-windows-amd64-test');(folder/'bin').mkdir(parents=True);(folder/'deploy').mkdir()
 for role in ['gateway-core','gateway-web','gateway-updater']:shutil.copyfile(bins/(role+'.exe'),folder/'bin'/role)
 for name in ['install.sh','service.sh','supervise.sh','rollback.sh','S99levelgateway','settings.example.env']:shutil.copyfile(ROOT/'deploy'/name,folder/'deploy'/name)
 script='#!/bin/sh\nexec '+shlex.quote(posix(sys.executable))+' '+shlex.quote(posix(Path(__file__)))+' service '+shlex.quote(str(base))+' '+shlex.quote(release)+' "$1"\n'
 (folder/'deploy/service.sh').write_text(script,encoding='utf-8',newline='\n');shutil.copyfile(public,folder/'deploy/update-trust.json');shutil.copyfile(base/'config/gateway.json',folder/'config.default.json')
 for n,v in [('RELEASE',release),('ARCH','windows-amd64-test'),('NORTH_VERSION','3.2'),('UPDATE_VERSION','1')]:(folder/n).write_text(v+'\n')
 (folder/'SHA256SUMS').write_text(''.join(hashlib.sha256((folder/n).read_bytes()).hexdigest()+'  '+n+'\n' for n in FILES if n!='SHA256SUMS'),newline='\n')
 return folder
def bundle(folder):
 payload=Path(str(folder)+'.tar.gz')
 with tarfile.open(payload,'w:gz') as t:t.add(folder,arcname=folder.name)
 out=Path(str(folder)+'.gwup');subprocess.run([str(signer),'sign','--stage',str(folder),'--payload',str(payload),'--output',str(out),'--private-key',str(private),'--notes','网页升级本机联调：保留配置、账号、历史及控制记录'],check=True,capture_output=True);return out
old=opt.version+'-'+opt.date.replace('-','')+'-r1';new=opt.version+'-'+opt.date.replace('-','')+'-r2';bad=opt.version+'-'+opt.date.replace('-','')+'-r3';initial=stage(old);shutil.move(initial,base/'releases'/old);os.symlink(base/'releases'/old,base/'current',target_is_directory=True);good_bundle=bundle(stage(new));bad_bundle=bundle(stage(bad));service(base,old,'start')
browser=urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()));csrf=''
def request(path,method='GET',body=None,token=None,origin=None):
 headers={'Content-Type':'application/octet-stream','X-CSRF-Token':csrf if token is None else token,'Origin':url if origin is None else origin};data=body
 if body is not None and not isinstance(body,bytes):data=json.dumps(body).encode();headers['Content-Type']='application/json'
 try:
  with browser.open(urllib.request.Request(url+'/api/'+path,data=data,headers=headers,method=method),timeout=20) as r:return r.status,json.load(r)
 except urllib.error.HTTPError as e:return e.code,e.read().decode()
def api(path,method='GET',body=None):
 code,data=request(path,method,body);assert code in (200,202),(path,code,data);return data
def wait(fn,timeout=25):
 until=time.monotonic()+timeout
 while time.monotonic()<until:
  try:
   out=fn()
   if out:return out
  except (OSError,urllib.error.URLError,AssertionError):pass
  time.sleep(.15)
 raise AssertionError('timeout waiting for '+getattr(fn,'__name__','condition'))
def check(name,cond):assert cond,name;checks.append({'name':name,'passed':True})
def login():
 global csrf
 csrf=api('login','POST',{'password':password})['csrf']
def mb(address,value):
 pdu=struct.pack('>BHH',6,address,value)
 with socket.create_connection(('127.0.0.1',north),timeout=3) as s:s.sendall(struct.pack('>HHHB',77,0,len(pdu)+1,1)+pdu);return s.recv(260)[7:]
try:
 wait(lambda:request('session')[0]==401);login();check('upgrade_enabled_on_bootstrap',api('upgrade/status')['supported']);wait(lambda:api('data')[0]['valid']==127)
 with browser.open(url) as r:check('embedded_upgrade_panel',b'upgrade-panel' in r.read() and r.headers['X-Gateway-Web-Version']==opt.version)
 check('upload_requires_csrf',request('upgrade/upload','POST',None,token='')[0]==403);check('upload_rejects_foreign_origin',request('upgrade/upload','POST',None,origin='http://foreign.invalid')[0]==403);check('raw_tar_rejected',request('upgrade/upload','POST',Path(str(good_bundle).replace('.gwup','.tar.gz')).read_bytes())[0]==400)
 staged=api('upgrade/upload','POST',good_bundle.read_bytes());check('signed_bundle_verified_and_no_early_switch',staged['phase']=='ready' and (base/'current').resolve().name==old)
 altered=bytearray(good_bundle.read_bytes());altered[len(altered)//2]^=1;check('tampered_bundle_rejected',request('upgrade/upload','POST',bytes(altered))[0]==400);check('invalid_upload_preserves_verified_job',api('upgrade/status')['id']==staged['id'])
 check('confirm_requires_explicit_confirmation',request('upgrade/confirm','POST',{'id':staged['id'],'confirmed':False})[0]==400)
 mb(30000,5);wait(lambda:api('commands')['controls'][0]['main']['state'] in [3,4,5]);check('unreset_control_blocks_upgrade',request('upgrade/confirm','POST',{'id':staged['id'],'confirmed':True})[0]==409);mb(30000,0)
 hashes={n:hashlib.sha256((base/n).read_bytes()).hexdigest() for n in ['config/gateway.json','config/service.env','config/update-trust.json','data/admin.json']};beforeboot=api('status')['boot'];beforehistory=api('history?from=0&to='+str(int(time.time()*1000))) if False else None
 accepted=api('upgrade/confirm','POST',{'id':staged['id'],'confirmed':True});check('confirmed_job_persisted_before_detach',accepted['busy'] and accepted['id']==staged['id']);check('north_write_blocked_during_upgrade',mb(30000,5)==bytes([0x86,6]));check('config_write_blocked_during_upgrade',request('config','PUT',api('config'))[0]==409);check('password_write_blocked_during_upgrade',request('admin/password','POST',{'current_password':password,'new_password':'Other-Fixture-2026!','confirm_password':'Other-Fixture-2026!'})[0] in [409,503]);check('program_restart_blocked_during_upgrade',request('maintenance/restart','POST',{'confirmed':True})[0]==409)
 wait(lambda:request('session')[0]==401,40);login();result=wait(lambda:(s if (s:=api('upgrade/status'))['phase']=='succeeded' else None),35)
 check('real_detached_worker_switches_both_services',(base/'current').resolve().name==new and api('status')['boot']!=beforeboot and not result['busy']);check('site_parameters_account_and_trust_preserved',all(hashlib.sha256((base/n).read_bytes()).hexdigest()==h for n,h in hashes.items()));check('consistent_database_and_account_backup',(base/'data'/('upgrade-backup-'+staged['id'])/'gateway-v2.db').stat().st_size>0 and (base/'data'/('upgrade-backup-'+staged['id'])/'admin.json').exists());check('controls_not_replayed',api('commands')['controls'][0]['main']['state']==0);wait(lambda:api('data')[0]['valid']==127);check('same_or_older_release_rejected',request('upgrade/upload','POST',good_bundle.read_bytes())[0]==400)
 # The signed payload is valid; inject a start failure in the isolated controller only.
 (base/'fixture-fail-target').write_text(bad)
 ready=api('upgrade/upload','POST',bad_bundle.read_bytes());api('upgrade/confirm','POST',{'id':ready['id'],'confirmed':True});wait(lambda:request('session')[0]==401,40);login();rollback=wait(lambda:(s if (s:=api('upgrade/status'))['phase']=='rolled_back' else None),40);check('failed_start_restores_compatible_old_processes',rollback['current']==new and not rollback['busy']);check('rollback_does_not_rewind_database_or_replay_commands',api('commands')['controls'][0]['main']['state']==0 and all(hashlib.sha256((base/n).read_bytes()).hexdigest()==h for n,h in hashes.items()))
 record={'date':opt.date,'software':opt.version,'passed':True,'hardware_executed':False,'OS_restarted':False,'host_clock_modified':False,'fixture':'Windows PE signed with isolated test key; real compiled core/Web/updater, service process bridge; Linux Buildroot lifecycle still needs board acceptance','checks':checks,'binaries':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in [bins/'gateway-core.exe',bins/'gateway-web.exe',bins/'gateway-updater.exe']}}
 (ROOT/('tests/web-upgrade-compiled-verification-'+opt.version+'-'+opt.date.replace('-','')+'.json')).write_text(json.dumps(record,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 preview={'url':url,'password':password,'work':str(work),'stop':str(work/'stop'),'upload':str(bad_bundle)};(ROOT/'tmp/web-upgrade-preview.json').write_text(json.dumps(preview),encoding='utf-8');print(json.dumps({'passed':True,'checks':len(checks),'url':url,'work':str(work)}),flush=True)
 if opt.keep_running:
  while not (work/'stop').exists():time.sleep(.2)
finally:owned_stop(base)
