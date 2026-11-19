"""Run compiled Windows binaries against simulated meters; no physical devices."""
from pathlib import Path
import json,subprocess,socket,struct,time,urllib.request,urllib.error,http.cookiejar,sys,hashlib,argparse
from types import SimpleNamespace
from modbus_client import exchange,read

root=Path(__file__).resolve().parents[1];run=root/'tmp/v3-implementation/live';run.mkdir(parents=True,exist_ok=True)
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--version',default='0.4.3')
parser.add_argument('--date',default='2026-10-27')
parser.add_argument('--keep-running',action='store_true')
options=parser.parse_args()
def port():
 with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
north,ipc,web=port(),port(),port()
core=root/'output/windows-amd64/gateway-core.exe';webbin=root/'output/windows-amd64/gateway-web.exe'
cfg=json.loads((root/'deploy/config.demo.json').read_text(encoding='utf-8-sig'));cfg['tcp'].update(listen=f'127.0.0.1:{north}',allow_writes=True,max_clients=8);cfg['allowed_actions']=[1,2,4,8,16,32,64,128,256]
cfgpath=run/f'config-{north}.json';cfgpath.write_text(json.dumps(cfg,ensure_ascii=False),encoding='utf-8')
auth=run/f'admin-{north}.json';db=run/f'db-{north}.sqlite';token=run/f'token-{north}'
# Fixture password is only for this isolated simulation, never a customer credential.
password='V3-Simulation-Only!20261011'
subprocess.run([str(webbin),'--init-admin','--auth-file',str(auth)],input=password+'\n',text=True,encoding='utf-8',check=True,capture_output=True)
children=[];checks=[]
def check(name,fn):fn();checks.append({'name':name,'passed':True})
def expect(condition,message='assertion failed'):
 if not condition:raise AssertionError(message)
def wait(fn,seconds=10):
 end=time.monotonic()+seconds
 while time.monotonic()<end:
  try:
   if fn():return
  except (OSError,urllib.error.URLError):pass
  time.sleep(.05)
 raise AssertionError('wait timed out')
opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()));csrf=''
def http(path,method='GET',data=None,with_csrf=True):
 b=None if data is None else json.dumps(data).encode();headers={'Content-Type':'application/json'}
 if with_csrf:headers['X-CSRF-Token']=csrf
 req=urllib.request.Request(f'http://127.0.0.1:{web}/api/{path}',b,headers,method=method)
 with opener.open(req,timeout=5) as r:return json.load(r)
args=SimpleNamespace(host='127.0.0.1',port=north,unit=1)
def write(a,v,fc=6):
 p=struct.pack('>BHH',6,a,v) if fc==6 else struct.pack('>BHHBH',16,a,1,2,v)
 return exchange(args.host,args.port,args.unit,p)
def state(slot=1):return read(args,30000+12*(slot-1),12,3)
def terminal(slot=1):wait(lambda:state(slot)[1] in [3,4,5]);return state(slot)
def start(binary,arguments,role):
 log=open(run/f'{role}-{north}.log','wb')
 p=subprocess.Popen([str(binary),*arguments],stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW);children.append(p);return p
try:
 cp=start(core,['--simulate','--config',str(cfgpath),'--db',str(db),'--ipc',f'127.0.0.1:{ipc}','--token-file',str(token)],'core')
 wp=start(webbin,['--listen',f'127.0.0.1:{web}','--ipc',f'127.0.0.1:{ipc}','--token-file',str(token),'--auth-file',str(auth)],'web')
 wait(lambda:token.exists() and urllib.request.urlopen(f'http://127.0.0.1:{web}',timeout=1).status==200)
 csrf=http('login','POST',{'password':password})['csrf']
 status=http('status');check('compiled_version_discovery',lambda:expect(status['version']==options.version and status['map_version']==3 and status['vendor_docs_version']=='V1.8'))
 wait(lambda:all(v['valid']==127 for v in http('data') if v['config']['enabled']))
 check('64_meter_live_capture',lambda:expect(sum(v['valid']==127 for v in http('data'))==64))
 check('north_d2_discovery',lambda:expect(read(args,0,30)[28:30]==[3,12]))
 check('four_batch_regions_96_words_each',lambda:expect(all(len(read(args,base,96))==96 for base in [15000,15100,15200,15300]) and all(read(args,base,96)[i*3+2]==127 for base in [15100,15300] for i in range(32))))
 decoded=json.loads(subprocess.check_output([sys.executable,str(root/'tools/modbus_client.py'),'--host',args.host,'--port',str(args.port),'batch'],encoding='utf-8'))
 check('client_tool_decodes_all64_batches',lambda:expect(len(decoded)==64 and decoded[63]['slot']==64 and decoded[63]['valid']==127))
 for slot in [1,16,17,32,33,48,49,64]:check(f'control_slot_{slot}_boundary',lambda slot=slot:expect(len(state(slot))==12))
 check('FC06_echo',lambda:expect(write(30000,5)==struct.pack('>BHH',6,30000,5)))
 terminal();check('up_command_readback',lambda:expect(state()[0:3]==[5,3,0] and state()[4:6]==[0,1]))
 write(30000,5);check('terminal_duplicate_no_new_cycle',lambda:expect(state()[4:6]==[0,1]))
 try:exchange(args.host,args.port,args.unit,struct.pack('>BHHBHH',16,30000,2,4,5,1));raise AssertionError('D1 accepted')
 except RuntimeError as e:expect('exception 3' in str(e))
 checks.append({'name':'D1_two_word_rejected_no_side_effect','passed':state()[4:6]==[0,1]})
 try:write(40000,5);raise AssertionError('legacy accepted')
 except RuntimeError as e:expect('exception 2' in str(e))
 checks.append({'name':'legacy_40000_closed','passed':True})
 write(30000,0);write(30000,5,16);terminal();check('FC10_single_word_new_cycle',lambda:expect(state()[4:6]==[0,2]))
 write(30000,0)
 http('simulator/fault','PUT',{'port':1,'address':1,'fault':{'drop_write_reply':True,'unsupported_start':-1}})
 write(30000,5);terminal();check('lost_write_reply_unknown_latched',lambda:expect(state()[0:3]==[5,5,6]))
 uncertain=state();write(30000,5)
 check('unknown_duplicate_not_replayed',lambda:expect(state()==uncertain))
 try:write(30000,6);raise AssertionError('unknown different action accepted')
 except RuntimeError as e:expect('exception 6' in str(e))
 checks.append({'name':'unknown_different_action_busy','passed':state()==uncertain})
 cycle=http('commands')['controls'][0]['main'];req={'slot':1,'stop':False,'id':cycle['request']['id'],'operator':'SIM-TEST','reason':'软件模拟核查：已检查模拟动作状态，不是实机核查','site_verified':True}
 try:http('control/maintenance-reset','POST',None,False);raise AssertionError('CSRF bypass')
 except urllib.error.HTTPError as e:expect(e.code==403)
 checks.append({'name':'maintenance_CSRF_required','passed':True})
 http('control/maintenance-reset','POST',req);check('maintenance_reset_audited',lambda:expect(state()[0:3]==[0,0,0] and any(e['kind']=='control_maintenance_reset' and 'SIM-TEST' in e['message'] for e in http('events'))))
 # Simulated site confirmation only. A production client must obtain real human confirmation.
 write(30000,5);terminal();before=http('commands')['controls'][0]
 expect(before['main']['state']==5)
 write(30000,0)
 reset_events=[json.loads(e['message']) for e in http('events') if e['kind']=='control_reset']
 audit=next(e for e in reset_events if e['before']==before)
 check('unknown_FC06_reset_source_time_original_audit',lambda:expect(audit['source'].startswith('tcp:') and audit['reset_time_ms']>0 and audit['manual_confirmation_required'] and not audit['stop'] and audit['operator']=='' and state()[0:3]==[0,0,0]))
 count=state()[4:6];time.sleep(.3)
 check('reset_never_automatically_replays',lambda:expect(state()[0:3]==[0,0,0] and state()[4:6]==count))
 try:http('control/maintenance-reset','POST',req);raise AssertionError('old review ID accepted')
 except urllib.error.HTTPError as e:expect(e.code==409)
 checks.append({'name':'old_review_cannot_reset_cleared_cycle','passed':True})
 http('simulator/fault','PUT',{'port':1,'address':1,'fault':{'unsupported_start':-1}})
 write(30000,5);terminal()
 check('new_action_requires_explicit_write_after_reset',lambda:expect(state()[1]==3 and state()[4:6]!=count))
 write(30007,1);wait(lambda:state()[8] in [3,4,5]);check('independent_stop_own_cycle',lambda:expect(state()[7:10]==[1,3,0] and state()[0:3]==[5,3,0]));write(30007,0)
 http('simulator/fault','PUT',{'port':1,'address':1,'fault':{'drop_write_reply':True,'unsupported_start':-1}})
 write(30007,1);wait(lambda:state()[8]==5);before=http('commands')['controls'][0];write(30007,0,16)
 audit=next(json.loads(e['message']) for e in http('events') if e['kind']=='control_reset' and json.loads(e['message'])['before']==before)
 check('unknown_stop_FC10_reset_independent_audit',lambda:expect(state()[7:10]==[0,0,0] and audit['stop'] and audit['after']['main']==before['main']))
 write(30000,0)
 http('simulator/fault','PUT',{'port':1,'address':1,'fault':{'unsupported_start':-1}})
 page=urllib.request.urlopen(f'http://127.0.0.1:{web}').read().decode()
 check('offline_web_manual_review_form',lambda:expect('reset-form' in page and '控制失败与不确定结果复位' in page and '控制结果不确定时的厂家核查' not in page))
 # Verify migrated/latched terminal data by opening the stopped binary's database.
 write(30000,5);terminal();snapshot=state();cp.terminate();cp.wait(timeout=10)
 cp=start(core,['--simulate','--config',str(cfgpath),'--db',str(db),'--ipc',f'127.0.0.1:{ipc}','--token-file',str(token)],'core-restart')
 wait(lambda:read(args,0,3)[2]==3)
 check('compiled_restart_terminal_preserved',lambda:expect(state()==snapshot))
 write(30000,0)
 # Reset the last terminal record, restart again and verify that it stays idle.
 cp.terminate();cp.wait(timeout=10)
 cp=start(core,['--simulate','--config',str(cfgpath),'--db',str(db),'--ipc',f'127.0.0.1:{ipc}','--token-file',str(token)],'core-reset-restart')
 wait(lambda:read(args,0,3)[2]==3)
 check('compiled_reset_durable_across_restart_no_replay',lambda:expect(state()[0:3]==[0,0,0] and state()[7:10]==[0,0,0] and any(e['kind']=='control_reset' for e in http('events'))))
 result={'passed':all(c['passed'] for c in checks),'date':options.date,'software':options.version,'map_version':3,'simulation_only':True,'hardware_executed':False,'checks':checks,'binaries':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in (core,webbin)}}
 (root/f'tests/v3-live-verification-{options.version}-{options.date.replace("-", "")}.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
 print(json.dumps({'passed':result['passed'],'checks':len(checks),'software':result['software']},ensure_ascii=False))
 if options.keep_running:
  stop=run/f'preview-{north}.stop'
  (root/'tmp/capacity64-preview.json').write_text(json.dumps({'url':f'http://127.0.0.1:{web}','password':password,'north':north,'ipc':ipc,'stop':str(stop),'core_pid':cp.pid,'web_pid':wp.pid}),encoding='utf-8')
  print('Preview ready',flush=True)
  deadline=time.monotonic()+1800
  while time.monotonic()<deadline and not stop.exists():time.sleep(.5)
 if True:
  for p in children:
   if p.poll() is None:p.terminate();p.wait(timeout=10)
except BaseException:
 for p in children:
  if p.poll() is None:p.terminate();p.wait(timeout=10)
 raise
