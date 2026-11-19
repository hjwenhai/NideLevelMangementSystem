"""Compiled core/Web tests and isolated restart bridge. Never restart a real gateway or OS."""
from pathlib import Path
import argparse,csv,hashlib,http.cookiejar,io,json,os,shlex,socket,struct,subprocess,sys,tempfile,time,urllib.request,urllib.error
from types import SimpleNamespace
from modbus_client import read,exchange
ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--keep-running',action='store_true');parser.add_argument('--version',default='0.4.2');parser.add_argument('--date',default='2026-10-27');args=parser.parse_args()
work=Path(tempfile.mkdtemp(prefix='system-maintenance-',dir=ROOT/'tmp'))
(work/'data').mkdir();(work/'logs').mkdir();(work/'preview').mkdir()
bins=ROOT/'output/windows-amd64';shell=Path.home()/'.cache/codex-runtimes/codex-primary-runtime/dependencies/native/git/usr/bin/sh.exe'
def posix(p):s=Path(p).resolve().as_posix();return '/'+s[0].lower()+s[2:] if s[1:3]==':/' else s
def freeport():
 with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
north,ipc,web=freeport(),freeport(),freeport();base=f'http://127.0.0.1:{web}'
cfg=json.loads((ROOT/'deploy/config.demo.json').read_text(encoding='utf-8-sig'));cfg['meters']=cfg['meters'][:2]
cfg['meters'][0].update(name='首台试验液位计',analog=True);cfg['fast_ms']=1000;cfg['detail_ms']=1000;cfg['history']['period_sec']=1
cfg['tcp'].update(listen=f'127.0.0.1:{north}',allow_writes=True);cfg['allowed_actions']=[2,4]
config=work/'gateway.json';config.write_text(json.dumps(cfg,ensure_ascii=False),encoding='utf-8')
auth,token=work/'data/admin.json',work/'data/ipc.token';password='Maintenance-Old-2026!';newpassword='Maintenance-New-2026!'
flags=getattr(subprocess,'CREATE_NO_WINDOW',0)
subprocess.run([str(bins/'gateway-web.exe'),'--init-admin','--auth-file',str(auth)],input=(password+'\n').encode(),capture_output=True,check=True,creationflags=flags)
script=work/'service-fixture.sh';request=work/'restart.request';release=work/'restart.release';calls=work/'restart.calls'
def helper(mode):
 text='#!/bin/sh\nset -eu\n[ "$1" = restart-web ]\nprintf "%s\\n" "$1" >> '+shlex.quote(posix(calls))+'\n'
 if mode=='fail':text+='exit 1\n'
 else:
  waitcode='import time\nfrom pathlib import Path\nwhile not Path('+repr(str(release))+').exists(): time.sleep(.1)'
  text+='rm -f '+shlex.quote(posix(release))+'\n: > '+shlex.quote(posix(request))+'\n'+shlex.quote(posix(sys.executable))+' -c '+shlex.quote(waitcode)+'\nprintf completed >> '+shlex.quote(posix(work/'worker.done'))+'\n'
 script.write_text(text,encoding='utf-8',newline='\n')
helper('fail');children=[];logs=[];checks=[]
def check(name,condition):assert condition,name;checks.append({'name':name,'passed':True})
def start(name,argv):
 log=(work/'logs'/f'{name}-{len(logs)}.log').open('wb');logs.append(log)
 env=os.environ.copy();env['PATH']=str(shell.parent)+os.pathsep+env.get('PATH','')
 p=subprocess.Popen([str(bins/(name+'.exe')),*argv],stdout=log,stderr=log,creationflags=flags,env=env,cwd=work);children.append(p);return p
def core():return start('gateway-core',['--simulate','--config',str(config),'--db','data/gateway-v2.db','--ipc',f'127.0.0.1:{ipc}','--token-file',str(token),'--service-script',str(script),'--service-shell',str(shell)])
def webpage():return start('gateway-web',['--listen',f'127.0.0.1:{web}','--ipc',f'127.0.0.1:{ipc}','--token-file',str(token),'--auth-file',str(auth)])
def client():return urllib.request.build_opener(urllib.request.ProxyHandler({}),urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
def api(path,method='GET',data=None,c=None,csrf_override=None):
 c=c or browser;headers={'Content-Type':'application/json','Origin':base,'X-CSRF-Token':csrf if csrf_override is None else csrf_override}
 req=urllib.request.Request(base+'/api/'+path,data=None if data is None else json.dumps(data).encode(),headers=headers,method=method)
 with c.open(req,timeout=5) as r:return json.load(r)
def code(path,method='GET',data=None,c=None,csrf_override=None):
 try:api(path,method,data,c,csrf_override);return 200
 except urllib.error.HTTPError as err:return err.code
def wait(fn,seconds=15):
 end=time.monotonic()+seconds
 while time.monotonic()<end:
  try:
   value=fn()
   if value:return value
  except (OSError,urllib.error.URLError):pass
  time.sleep(.05)
 raise AssertionError('wait timed out')
mb=SimpleNamespace(host='127.0.0.1',port=north,unit=1)
def write(addr,value):return exchange(mb.host,mb.port,mb.unit,struct.pack('>BHH',6,addr,value))
def state():return read(mb,30000,12,3)
def restart_binaries():
 global cp,wp
 for child in [wp,cp]:child.terminate();child.wait(timeout=10)
 cp=core();wp=webpage();wait(lambda:code('session',c=client())==401)
 release.write_text('continue',encoding='utf-8');request.unlink(missing_ok=True)
browser=client();csrf='';cp=core();wp=webpage()
try:
 wait(lambda:code('session')==401);csrf=api('login','POST',{'password':password})['csrf'];other=client();othercsrf=api('login','POST',{'password':password},c=other)['csrf']
 status=api('status');check('current_version_restart_enabled',status['version']==args.version and status['program_restart']['supported'])
 wait(lambda:api('data')[0]['valid']==127 and api('data')[0]['groups'][5]['time_ms']>0)
 boot=status['boot'];seq=api('data')[0]['groups'][0]['sequence']
 change={'current_password':password,'new_password':newpassword,'confirm_password':newpassword}
 check('password_change_requires_CSRF',code('admin/password','POST',change,csrf_override='')==403)
 bad=dict(change,current_password='wrong');check('wrong_old_password_rejected',code('admin/password','POST',bad)==400)
 check('password_confirmation_required',code('admin/password','POST',dict(change,confirm_password='bad'))==400)
 result=api('admin/password','POST',change);check('password_change_committed',result['ok'] and not result['warning'])
 check('all_old_sessions_revoked',code('session')==401 and code('session',c=other)==401)
 check('old_password_no_longer_accepted',code('login','POST',{'password':password},c=client())==401)
 csrf=api('login','POST',{'password':newpassword})['csrf']
 wait(lambda:api('data')[0]['groups'][0]['sequence']>seq)
 check('password_change_does_not_restart_core',api('status')['boot']==boot)
 events=api('events');serialized=json.dumps(events)
 check('password_audited_without_secrets',any(e['kind']=='admin_password_changed' for e in events) and password not in serialized and newpassword not in serialized and '"hash"' not in serialized)
 check('restart_confirmation_required',code('maintenance/restart','POST',{'confirmed':False})==400)
 check('restart_rejects_arbitrary_script',code('maintenance/restart','POST',{'confirmed':True,'script':'malicious'})==400)
 result=api('maintenance/restart','POST',{'confirmed':True});again=api('maintenance/restart','POST',{'confirmed':True})
 check('pending_restart_deduplicated',result['pending'] and again['id']==result['id'])
 try:write(30000,5);raise AssertionError('control accepted during restart')
 except RuntimeError as err:check('north_writes_busy_while_restarting','exception 6' in str(err))
 check('config_blocked_while_restarting',code('config','PUT',api('config'))==409)
 wait(lambda:not api('status')['program_restart']['pending'])
 check('failed_worker_releases_gate_and_reports_error',bool(api('status')['program_restart'].get('error')) and calls.read_text().splitlines()==['restart-web'])
 api('simulator/fault','PUT',{'port':1,'address':1,'fault':{'delay_ms':250,'unsupported_start':-1}})
 write(30000,5);check('active_control_rejects_restart',code('maintenance/restart','POST',{'confirmed':True})==409)
 wait(lambda:state()[1] in [3,4,5]);write(30000,0)
 api('simulator/fault','PUT',{'port':1,'address':1,'fault':{'drop_write_reply':True,'unsupported_start':-1}})
 write(30000,5);wait(lambda:state()[1]==5);unknown=state()
 helper('bridge');before_calls=len(calls.read_text().splitlines());result=api('maintenance/restart','POST',{'confirmed':True})
 wait(request.exists);check('detached_worker_reached_fixed_service_action',len(calls.read_text().splitlines())==before_calls+1)
 restart_binaries();check('restart_invalidates_web_session',code('session')==401)
 csrf=api('login','POST',{'password':newpassword})['csrf'];status=api('status')
 check('password_and_configuration_survive_restart',status['boot']!=boot and api('config')['meters'][0]['analog'])
 check('unknown_control_preserved_no_reset_or_replay',state()==unknown)
 wait((work/'worker.done').exists);check('worker_survives_old_core_exit_and_finishes',(work/'worker.done').read_text()=='completed')
 wait(lambda:api('data')[0]['valid']==127 and api('data')[0]['groups'][5]['time_ms']>0)
 check('analog_collection_resumes',api('data')[0]['groups'][5]['sequence']>0)
 now=int(time.time()*1000)
 with browser.open(base+f'/api/history.csv?from={now-60000}&to={now+60000}&slot=1') as r:rows=list(csv.reader(io.StringIO(r.read().decode('utf-8-sig'))))
 check('history_filter_and_columns_preserved',len(rows)>1 and len(rows[0])==17 and all(row[1]=='1' for row in rows[1:]))
 page=urllib.request.urlopen(base).read().decode()
 check('system_maintenance_forms_embedded','password-change-form' in page and '重启网关不等于停止浮子' in page and '整机重启' not in page)
 record={'date':args.date,'software':args.version,'passed':True,'relative_database_path_like_supervisor':True,'hardware_executed':False,'host_clock_modified':False,'OS_restarted':False,'checks':checks,
 'restart_scope':'Real detached worker and fixed restart-web shell action, with an isolated harness replacing two Windows test processes; physical Buildroot service lifecycle remains pending.',
 'binaries':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in [bins/'gateway-core.exe',bins/'gateway-web.exe']}}
 (ROOT/f'tests/system-maintenance-compiled-verification-{args.version}-{args.date.replace(chr(45),str())}.json').write_text(json.dumps(record,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 preview={'url':base,'password':newpassword,'stop':str(work/'stop'),'work':str(work)}
 (ROOT/'tmp/system-maintenance-preview.json').write_text(json.dumps(preview),encoding='utf-8');print(json.dumps({'passed':True,'checks':len(checks),'url':base,'stop':str(work/'stop')},ensure_ascii=False),flush=True)
 if args.keep_running:
  while not (work/'stop').exists():
   if request.exists():restart_binaries()
   time.sleep(.1)
finally:
 release.write_text('stop',encoding='utf-8')
 for child in reversed(children):
  if child.poll() is None:child.terminate();child.wait(timeout=10)
 for log in logs:log.close()
