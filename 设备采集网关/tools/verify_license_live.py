"""Compiled offline license lifecycle on Windows loopback, never physical hardware."""
from pathlib import Path
import argparse,hashlib,http.cookiejar,json,os,socket,struct,subprocess,tempfile,time,urllib.request,urllib.error
ROOT=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--keep-running',action='store_true');p.add_argument('--version',default='0.8.1');p.add_argument('--date',default='2026-11-09');args=p.parse_args()
work=Path(tempfile.mkdtemp(prefix='license-live-',dir=ROOT/'tmp'));(work/'data').mkdir();(work/'config').mkdir()
bins=ROOT/'output/windows-amd64';flags=getattr(subprocess,'CREATE_NO_WINDOW',0)
def port():
 with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
north,ipc,web=port(),port(),port();url=f'http://127.0.0.1:{web}'
cfg=json.loads((ROOT/'deploy/config.demo.json').read_text(encoding='utf-8-sig'));cfg['meters']=cfg['meters'][:1];cfg['fast_ms']=1000;cfg['detail_ms']=1000;cfg['tcp'].update(listen=f'127.0.0.1:{north}',allow_writes=True);cfg['allowed_actions']=[2,4]
config=work/'config/gateway.json';config.write_text(json.dumps(cfg),encoding='utf-8');license=work/'config/license.json'
password='License-Test-Only-2026!';subprocess.run([str(bins/'gateway-web.exe'),'--init-admin','--auth-file',str(work/'data/admin.json')],input=(password+'\n').encode(),capture_output=True,check=True,creationflags=flags)
jar=http.cookiejar.CookieJar();opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar));csrf='';checks=[];children=[];logs=[];core=None
def call(path,method='GET',data=None,token=True):
 b=None if data is None else json.dumps(data).encode();h={'Content-Type':'application/json'}
 if token:h['X-CSRF-Token']=csrf
 try:
  with opener.open(urllib.request.Request(url+'/api/'+path,b,h,method=method),timeout=5) as r:return r.status,json.loads(r.read() or b'{}')
 except urllib.error.HTTPError as e:return e.code,e.read().decode()
def api(path,method='GET',data=None):
 code,v=call(path,method,data);assert code in [200,202],(path,code,v);return v
def wait(fn,seconds=15):
 end=time.monotonic()+seconds
 while time.monotonic()<end:
  try:
   v=fn()
   if v:return v
  except (OSError,AssertionError,ValueError):pass
  time.sleep(.1)
 raise AssertionError('condition timeout')
def check(name,v):assert v,name;checks.append(name)
def start_core(identity='0102030405060708'):
 global core
 f=(work/f'core-{len(logs)}.log').open('ab');logs.append(f)
 core=subprocess.Popen([str(bins/'gateway-core.exe'),'--simulate','--license-test-id',identity,'--config',str(config),'--db',str(work/'data/gateway.db'),'--ipc',f'127.0.0.1:{ipc}','--token-file',str(work/'data/ipc.token')],cwd=work,stdout=f,stderr=f,creationflags=flags);children.append(core)
 wait(lambda:(work/'data/ipc.token').exists() and core.poll() is None)
def restart(identity='0102030405060708'):
 core.terminate();core.wait(10);(work/'data/ipc.token').unlink(missing_ok=True);start_core(identity);wait(lambda:api('status')['license']['device_code'])
def closed():
 try:
  with socket.create_connection(('127.0.0.1',north),timeout=.4):return False
 except OSError:return True
def issue(req,name):
 request=work/(name+'.gwreq');request.write_text(json.dumps(req));out=work/(name+'.gwlic')
 r=subprocess.run([str(bins/'gateway-license.exe'),'issue','--request',str(request),'--customer','厂家内部联调非交付','--output',str(out),'--private-key',str(ROOT/'private/license-signing.key'),'--registry',str(work/'issued.jsonl')],capture_output=True,creationflags=flags)
 assert r.returncode==0,r.stderr.decode(errors='replace');return json.loads(out.read_text(encoding='utf-8'))
try:
 start_core();f=(work/'web.log').open('ab');logs.append(f)
 child=subprocess.Popen([str(bins/'gateway-web.exe'),'--listen',f'127.0.0.1:{web}','--ipc',f'127.0.0.1:{ipc}','--token-file',str(work/'data/ipc.token'),'--auth-file',str(work/'data/admin.json')],cwd=work,stdout=f,stderr=f,creationflags=flags);children.append(child)
 wait(lambda:call('session')[0]==401)
 check('request_requires_login',call('license/request')[0]==401)
 session=api('login','POST',{'password':password});csrf=session['csrf']
 s=api('status')['license'];check('unlicensed_management_open',s['required'] and not s['valid'] and s['state']=='locked')
 check('no_unlicensed_modbus_listener',closed())
 check('no_unlicensed_measurements',api('data')[0]['valid']==0)
 req=api('license/request');check('request_contains_digest_not_raw_id',len(req['device_code'])==64 and '0102030405060708' not in json.dumps(req))
 grant=issue(req,'fixture');bad=json.loads(json.dumps(grant));bad['license']['customer']='篡改'
 check('csrf_required',call('license/import','POST',{'license':grant,'confirmed':True},False)[0]==403)
 check('confirmation_required',call('license/import','POST',{'license':grant,'confirmed':False})[0]==400)
 check('tampered_grant_rejected',call('license/import','POST',{'license':bad,'confirmed':True})[0]==409 and not license.exists())
 other=dict(req);other['device_code']=hashlib.sha256(b'GT6757X-LevelGateway\nsoc-serial\n1112131415161718').hexdigest();wrong=issue(other,'other')
 check('other_device_grant_rejected',call('license/import','POST',{'license':wrong,'confirmed':True})[0]==409)
 s=api('license/import','POST',{'license':grant,'confirmed':True});check('import_saved_requires_manual_restart',s['restart_required'] and not s['valid'] and closed())
 original=license.read_bytes();restart();wait(lambda:api('data')[0]['valid']==127)
 check('restart_activates_permanent_license',api('status')['license']['valid'] and not closed())
 with socket.create_connection(('127.0.0.1',north),timeout=2) as sock:
  sock.settimeout(2);sock.sendall(bytes.fromhex('000100000006010400000004'));reply=sock.recv(64);check('authorized_modbus_read_responds',len(reply)>=9 and reply[7]==4)
 api('time','POST',{'utc_ms':int(time.time()*1000)})
 check('calibration_keeps_license',license.read_bytes()==original and api('status')['license']['valid'])
 restart('1112131415161718');check('copied_whole_data_does_not_authorize_other_board',not api('status')['license']['valid'] and closed())
 restart();wait(lambda:api('data')[0]['valid']==127);check('original_board_still_authorized',api('status')['license']['valid'])
 license.write_text('{}');s=api('status')['license'];check('running_file_corruption_warns_without_abrupt_stop',s['valid'] and not s['file_valid'])
 restart();check('corrupt_file_blocks_next_start',not api('status')['license']['valid'] and closed())
 license.write_bytes(original);restart();wait(lambda:api('data')[0]['valid']==127)
 events=api('events');check('license_import_source_recorded','license_imported' in json.dumps(events) and 'web:127.0.0.1' in json.dumps(events))
 page=opener.open(url).read().decode();check('license_ui_embedded',all(x in page for x in ['license-panel','license-banner','license-export','license-import']))
 r=subprocess.run([str(bins/'gateway-license.exe'),'verify','--input',str(work/'fixture.gwlic'),'--public-key',str(ROOT/'src/internal/licensing/trusted-public.json')],capture_output=True,creationflags=flags);check('factory_verification_tool',r.returncode==0)
 record={'date':args.date,'software':args.version,'passed':True,'hardware_executed':False,'hardware_ids':'Windows-only simulated; production Linux refuses test identity flag','checks':checks,'binaries':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in [bins/'gateway-core.exe',bins/'gateway-web.exe',bins/'gateway-license.exe']}}
 (ROOT/f'tests/license-compiled-verification-{args.version}-{args.date.replace(chr(45),str())}.json').write_text(json.dumps(record,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 (ROOT/'tmp/license-preview.json').write_text(json.dumps({'url':url,'password':password,'work':str(work),'stop':str(work/'stop')}),encoding='utf-8')
 print(json.dumps({'passed':True,'checks':len(checks),'url':url,'work':str(work)}),flush=True)
 if args.keep_running:
  while not (work/'stop').exists():time.sleep(.2)
finally:
 for child in reversed(children):
  if child.poll() is None:child.terminate();child.wait(10)
 for f in logs:f.close()
