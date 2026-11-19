"""Compiled 32-to-64 upgrade fixture. Never contacts hardware or modifies deployment paths."""
from pathlib import Path
import hashlib,json,sqlite3,subprocess,tempfile,socket,time,urllib.request
from types import SimpleNamespace
from modbus_client import read,exchange
R=Path(__file__).resolve().parents[1];W=Path(tempfile.mkdtemp(prefix='capacity-upgrade-',dir=R/'tmp'))
binary=R/'output/windows-amd64/gateway-core.exe';checks=[]
def check(name,condition):
 assert condition,name
 checks.append(dict(name=name,passed=True))
def run(*args):return subprocess.run([str(binary),*map(str,args)],capture_output=True,check=True)
def port():
 with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
north,ipc=port(),port()
old=json.loads((R/'deploy/config.production-64.json').read_text())
old.update(schema_version=2,revision=9,slot_versions=[i+3 for i in range(32)])
old['meters']=old['meters'][:32]
for i,m in enumerate(old['meters']):m.update(port=i//8+1,address=i%8+1,binding_version=i+3,name=f'原仪表{i+1}')
old['tcp'].update(listen=f'127.0.0.1:{north}',allow_writes=True);old['allowed_actions']=[2,4]
cfg=W/'config.json';cfg.write_text(json.dumps(old,ensure_ascii=False),encoding='utf-8');before=cfg.read_bytes()
run('--check-config','--config',cfg);check('check_only_does_not_rewrite_old_config',cfg.read_bytes()==before)
run('--migrate-config','--config',cfg);new=json.loads(cfg.read_text())
check('compiled_migration_appends_slots_without_rebinding',new['schema_version']==3 and len(new['slot_versions'])==64 and new['slot_versions'][:32]==old['slot_versions'] and new['meters']==old['meters'] and new['ports']==old['ports'] and new['tcp']==old['tcp'] and new['allowed_actions']==old['allowed_actions'] and new['revision']==9)
db=W/'old.db';snapshot={'slot':32,'main':{'value':6,'state':5,'error':6,'count':2,'request':{'id':'original-cycle','slot':32,'binding_version':34,'config_revision':9,'port':4,'unit':8,'timeout_ms':500,'command':6,'south_action':256,'source':'tcp:old'},'sent':True,'accepted_ms':1000,'updated_ms':2000},'stop':{'state':0}}
with sqlite3.connect(db) as d:
 d.executescript('CREATE TABLE control_slots(slot INTEGER PRIMARY KEY CHECK(slot BETWEEN 1 AND 32),data TEXT NOT NULL); PRAGMA user_version=2;')
 d.execute('INSERT INTO control_slots VALUES(32,?)',(json.dumps(snapshot),))
backup=W/'before.db';run('--db',db,'--backup-db',backup)
with sqlite3.connect(backup) as d:check('backup_remains_schema2',d.execute('PRAGMA user_version').fetchone()[0]==2)
token=W/'token';log=open(W/'core.log','wb');process=None
args=SimpleNamespace(host='127.0.0.1',port=north,unit=1)
try:
 process=subprocess.Popen([str(binary),'--simulate','--config',str(cfg),'--db',str(db),'--ipc',f'127.0.0.1:{ipc}','--token-file',str(token)],stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
 for _ in range(100):
  try:
   if read(args,0,4)[3]==64:break
  except OSError:pass
  time.sleep(.05)
 check('capacity_discovery_64_16',read(args,0,28)[3]==64 and read(args,27,1)==[16])
 check('old_unknown_control_preserved',read(args,30372,12,3)[:3]==[6,5,6])
 check('new_slot_stays_idle',read(args,30756,12,3)==[0]*12)
 with sqlite3.connect(db) as d:
  check('database_schema3_and_original_cycle_json_retained',d.execute('PRAGMA user_version').fetchone()[0]==3 and json.loads(d.execute('SELECT data FROM control_slots WHERE slot=32').fetchone()[0])['main']['request']['id']=='original-cycle')
 check('backup_source_unchanged',json.loads(sqlite3.connect(backup).execute('SELECT data FROM control_slots WHERE slot=32').fetchone()[0])==snapshot)
finally:
 if process and process.poll() is None:process.terminate();process.wait(timeout=10)
 log.close()
report=dict(date='2026-10-19',software='0.4.0',passed=True,simulation_only=True,checks=checks,binary_sha256=hashlib.sha256(binary.read_bytes()).hexdigest())
(R/'tests/capacity64-compiled-upgrade-20261019.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(json.dumps(report,ensure_ascii=False))
