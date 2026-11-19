"""Final read-only audit of the 0.3.7 program-maintenance runtime release."""
from pathlib import Path
import hashlib,json,re
ROOT=Path(__file__).resolve().parents[1];RELEASE='0.3.7-20261019-r2'
load=lambda p:json.loads((ROOT/p).read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
checks=[]
def check(name,condition,detail=None):
 assert condition,(name,detail)
 checks.append({'name':name,'passed':True,'detail':detail})
reports=[('tests/system-maintenance-compiled-verification-20261019.json',24),('tests/system-maintenance-browser-verification-20261019.json',6),
 ('tests/v3-live-verification-0.3.7-20261019.json',32),('tests/rtc-compiled-integration-0.3.7-20261019.json',9),
 ('tests/install-flow-verification-0.3.7-20261019.json',12),('tests/program-restart-shell-verification-20261019.json',5),
 ('tests/runtime-package-verification-'+RELEASE+'.json',None),('tests/system-maintenance-document-verification-20261019.json',None)]
for path,count in reports:
 data=load(path);check(path,data['passed'] and data['date']=='2026-10-19' and data['software']=='0.3.7')
 if count:check('all passing '+path,len(data['checks'])==count and all(item['passed'] for item in data['checks']))
 for name,digest in data.get('binaries',{}).items():check('tested final binary '+name,sha(ROOT/'output/windows-amd64'/name)==digest)
check('production_relative_DB_path_tested',load(reports[0][0])['relative_database_path_like_supervisor'])
check('actual_service_case_source_matches',sha(ROOT/'deploy/service.sh')==load(reports[5][0])['service_sha256'])
concurrency=[json.loads(line) for line in (ROOT/'tests/system-maintenance-concurrency-20261019.jsonl').read_text(encoding='utf-8-sig').splitlines()]
check('authorized_write_and_password_revocation_serialization',any(item.get('Action')=='pass' and item.get('Test')=='TestPasswordRevocationSerializesWithAuthorizedWrites' for item in concurrency) and not any(item.get('Action')=='fail' for item in concurrency))
build=(ROOT/'tmp/system-maintenance-build-20261019.log').read_text(encoding='utf-8-sig')
check('full Go tests vet TypeScript three target build','Built Windows amd64, Linux ARM64 and ARMv7' in build and 'FAIL' not in build)
packages=load(reports[6][0])['packages']
for item in packages:
 arch=item['architecture'];stage=ROOT/'output/packages'/('gt6757s-'+RELEASE+'-'+arch)
 check('13 fixed runtime files '+arch,item['file_count']==13 and item['elf_static'] and item['config_valid'])
 check('archive SHA256 '+arch,sha(ROOT/'output/packages'/item['archive'])==item['sha256'])
 for name,digest in item['binaries'].items():check('final packaged program '+arch+'/'+name,sha(ROOT/'output'/arch/name)==digest)
 settings=(stage/'deploy/settings.example.env').read_text(encoding='utf-8')
 check('HTTP watchdog and hardware control defaults closed '+arch,all(re.search('(?m)^'+key+'=$',settings) for key in ['TLS_CERT','TLS_KEY','WATCHDOG']) and 'HARDWARE_WRITES=0' in settings)
 cfg=load(str((stage/'config.default.json').relative_to(ROOT)))
 check('schema2 no default actions '+arch,cfg['schema_version']==2 and not cfg['allowed_actions'] and not cfg['tcp']['allow_writes'])
 check('no private credentials in package '+arch,not any(any(word in p.lower() for word in ['admin.json','.key','.crt','ca.key']) for p in item['files']))
previous=load('tests/analog-delivery-verification-20261018.json');old_sources={key.replace('\\','/'):value for key,value in previous['sources'].items()};old_docs={key.replace('\\','/'):value for key,value in previous['documents'].items()}
check('D2 runtime protocol unchanged',sha(ROOT/'protocols/north-v3.json')==old_sources['protocols/north-v3.json'])
check('physical SYS driver unchanged',sha(ROOT/'src/internal/platform/led.go')==old_sources['src/internal/platform/led.go'])
check('customer protocol unchanged',sha(ROOT/'docs/12_客户通讯协议V3.md')==old_docs['docs/12_客户通讯协议V3.md'])
design=load('docs/附件/north-v3-design.json');spec=load('protocols/north-v3.json')
check('D2 map and control reset contract maintained',design['current_software']=='0.3.7' and spec['version']==3 and design['control_fields']==spec['control_fields'] and design['customer_reset_allowed_states']==[0,3,4,5])
for name in ['docs/10_运行部署与维护.md','docs/23_时钟校核与断电恢复.md','output/packages/README.md']:
 text=(ROOT/name).read_text(encoding='utf-8');check('published current package/hash '+name,RELEASE in text and packages[0]['sha256'].upper() in text)
syntax=load(reports[7][0]);check('commands parsed only with Chinese comments',syntax['commands_executed'] is False and syntax['blocks']==53 and syntax['deployment_HTTPS_system_commands_have_Chinese_comments'])
for name,digest in syntax['documents'].items():check('final command document '+name,sha(ROOT/'docs'/name)==digest)
files=[ROOT/p for p in ['README.md','00_index.md','progress.md','output/packages/README.md']]+list((ROOT/'docs').glob('*.md'))+list((ROOT/'decisions').glob('*.md'))
broken=[]
for p in files:
 for link in re.findall(r'\]\((<[^>]+>|[^)]+)\)',p.read_text(encoding='utf-8-sig')):
  url=link.strip('<>').split('#')[0]
  if not url or re.match(r'^(https?://|app:|codex:)',url):continue
  target=(p.parent/url).resolve()
  if target==ROOT/'tests/system-maintenance-delivery-verification-20261019.json':continue
  if not target.exists():broken.append((str(p.relative_to(ROOT)),url))
check('active document links',not broken,broken)
for name in load(reports[1][0])['screenshots']:check('browser evidence '+name,(ROOT/name).read_bytes().startswith(b'\xff\xd8\xff'))
check('no browser whole-device restart route',"r.URL.Path == \"/api/reboot\"" not in (ROOT/'src/internal/web/web.go').read_text(encoding='utf-8') and '重启设备</button>' not in (ROOT/'src/internal/web/static/index.html').read_text(encoding='utf-8'))
sources=['src/internal/web/web.go','src/internal/web/maintenance.go','src/internal/web/maintenance_test.go','src/internal/web/maintenance_concurrency_test.go','src/internal/core/maintenance.go','src/internal/core/maintenance_test.go','src/internal/core/core.go','src/internal/core/api.go','src/internal/platform/restart.go','src/internal/platform/restart_linux.go','src/internal/platform/restart_windows.go','src/cmd/gateway-core/main.go','src/web/app.ts','src/internal/web/static/app.js','src/internal/web/static/index.html','deploy/service.sh','deploy/supervise.sh','protocols/north-v3.json']
result={'date':'2026-10-19','software':'0.3.7','release':RELEASE,'passed':True,'checks':checks,'hardware_executed_by_agent':False,'host_clock_modified':False,
 'OS_restarted':False,'default_HTTP':True,'default_watchdog_disabled':True,'SYS_driver_user_confirmed':True,'packages':packages,
 'sources':{name:sha(ROOT/name) for name in sources},'documents':{str(p.relative_to(ROOT)):sha(p) for p in files},
 'remaining':['Upgrade real GT6757S and confirm normal service stop/start and webpage reconnection using new package','Check actual phone browser, full-load acquisition and other independent pending board acceptance'],
 'candidate_note':'0.3.7-20261019-r1 is a local candidate; r2 resolves relative database-derived restart log paths and was rechecked with supervisor-style relative paths.'}
(ROOT/'tests/system-maintenance-delivery-verification-20261019.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'passed':True,'release':RELEASE,'checks':len(checks)},ensure_ascii=False))
