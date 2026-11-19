"""Final read-only audit for the capacity64 runtime and customer deliverables."""
from pathlib import Path
import hashlib,json,re,zipfile
R=Path(__file__).resolve().parents[1];RELEASE='0.4.0-20261019-r2'
load=lambda p:json.loads((R/p).read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
checks=[]
def check(name,ok,detail=None):
 assert ok,(name,detail)
 checks.append(dict(name=name,passed=True,detail=detail))
spec=load('protocols/north-v3.json');old=load('archive/capacity64-20261019/protocols/north-v3.json');design=load('docs/附件/north-v3-design.json')
check('capacity64_and16',spec['capacity']==64 and spec['max_per_south_port']==16)
check('machine_control_contract_matches',design['control_region']==next(r for r in spec['regions'] if r['name']=='simple_control'))
for key in ['summary_fields','control_fields','commands','quality_bits','valid_bits','groups']:
 check('preserved '+key,spec[key]==old[key])
regions={r['name']:r for r in spec['regions']};addresses=set()
for r in spec['regions']:
 block=set(range(r['base'],r['base']+r['stride']*r['count']))
 check('nonoverlap '+r['name'],not addresses.intersection(block) and max(block)<65536);addresses.update(block)
for before in old['regions']:
 after=regions[before['name']]
 check('old32_addresses_preserved '+before['name'],all(before[k]==after[k] for k in ['base','stride','access']) and after['count']>=before['count'])
for name,base in [('batch_measurements',15000),('batch_status',15100),('batch_measurements_extended',15200),('batch_status_extended',15300)]:
 check('batch32x3 '+name,regions[name]['base']==base and regions[name]['stride']==3 and regions[name]['count']==32)
paths=['tests/v3-live-verification-0.4.0-20261019.json','tests/capacity64-compiled-upgrade-20261019.json','tests/system-maintenance-compiled-verification-0.4.0-20261019.json','tests/rtc-compiled-integration-0.4.0-20261019.json','tests/install-flow-verification-0.4.0-20261019.json','tests/program-restart-shell-verification-0.4.0-20261019.json','tests/capacity64-document-verification-20261019.json','tests/capacity64-history-export-20261019.json','tests/capacity64-browser-20261019.json','tests/customer-materials-v1.2-verification.json','tests/runtime-package-verification-'+RELEASE+'.json']
for p in paths:
 report=load(p);check('report '+p,report['passed'] and report['software']=='0.4.0')
 for item in report.get('checks',[]):
  if isinstance(item,dict):check('subcheck '+item['name'],item['passed'])
 for name,digest in report.get('binaries',{}).items():check('final tested binary '+name,sha(R/'output/windows-amd64'/name)==digest)
 if 'binary_sha256' in report:check('migration tested final core',sha(R/'output/windows-amd64/gateway-core.exe')==report['binary_sha256'])
 for name,digest in report.get('documents',{}).items() if isinstance(report.get('documents'),dict) else []:
  check('document hash '+name,sha(R/'docs'/name)==digest)
build=(R/'tmp/capacity64-build-20261019.log').read_text(encoding='utf-8-sig')
check('all Go tests vet TS and three targets','FAIL' not in build and 'Built Windows amd64, Linux ARM64 and ARMv7' in build)
auth=(R/'src/internal/command/command.go').read_text(encoding='utf-8')
check('auth10d9_precedes_action',auth.index('write(129, 0x10d9)')<auth.index('write(699, r.Action)'))
check('all_normal_and_independent_stop_auth_test_present','TestEveryFloatOperationAuthenticatesBeforeAction' in (R/'src/internal/command/authentication_test.go').read_text() and 'independent-stop' in (R/'src/internal/command/authentication_test.go').read_text())
check('saturated16_slow_task_regression_present','TestSaturated16MeterBusStillServicesDetailsAndParameters' in (R/'src/internal/core/saturation_test.go').read_text() and 'slowCredit = true' in (R/'src/internal/core/core.go').read_text())
packages=load(paths[-1])['packages']
arm64=next(p for p in packages if p['architecture']=='linux-arm64')
for name in ['10_运行部署与维护.md','23_时钟校核与断电恢复.md']:
 text=(R/'docs'/name).read_text(encoding='utf-8')
 documented=re.findall(r'(?i)\b[0-9a-f]{64}\b',text)
 check('documented current ARM64 digest '+name,bool(documented) and all(d.lower()==arm64['sha256'] for d in documented))
size_text=(R/'docs/10_运行部署与维护.md').read_text(encoding='utf-8')
check('documented current ARM64 sizes',f"{arm64['archive_bytes']:,}字节" in size_text and f"{arm64['installed_payload_bytes']:,}字节" in size_text and f"{arm64['installed_payload_bytes']/1048576:.2f}MiB" in size_text)
for p in packages:
 stage=R/'output/packages'/('gt6757s-'+RELEASE+'-'+p['architecture'])
 check('package '+p['architecture'],p['file_count']==13 and sha(R/'output/packages'/p['archive'])==p['sha256'])
 for n,digest in p['binaries'].items():check('package contains final '+p['architecture']+'/'+n,sha(R/'output'/p['architecture']/n)==digest)
 env=(stage/'deploy/settings.example.env').read_text()
 for key,value in [('HARDWARE_WRITES','0'),('WATCHDOG',''),('TLS_CERT',''),('TLS_KEY','')]:check('default '+p['architecture']+'/'+key,re.search(r'^'+key+'='+re.escape(value)+r'$',env,re.M) is not None)
check('old released package unchanged',sha(R/'output/packages/gt6757s-0.3.7-20261019-r2-linux-arm64.tar.gz')=='4d97f44683c09923168d2d42ba308e837c8b9ffb1cca37069df25993589be462')
check('SYS confirmed driver source exists',(R/'source_notes/SYS实体指示灯驱动确认_20261016.md').exists())
for p in load('tests/customer-materials-v1.2-verification.json')['documents']:
 check('current Word '+p['docx'],sha(R/p['docx'])==p['docx_sha256']);check('current PDF '+p['pdf'],sha(R/p['pdf'])==p['pdf_sha256'])
export=R/'output/customer-docs/gt6757s-customer-v3.1-d2-20261019-r1.zip'
with zipfile.ZipFile(export) as z:
 for name,source in [('12_客户通讯协议V3.md','docs/12_客户通讯协议V3.md'),('GT6757S_客户通讯协议_V3.1-D2.pdf','output/pdf/GT6757S_客户通讯协议_V3.1-D2.pdf')]:
  names=[n for n in z.namelist() if n.endswith('/'+name)];check('customer export '+name,len(names)==1 and z.read(names[0])==(R/source).read_bytes())
broken=[]
docs=[R/'README.md',R/'00_index.md',R/'output/customer-materials/README.md',R/'output/packages/README.md']+list((R/'docs').glob('*.md'))
for p in docs:
 for target in re.findall(r'\]\(([^)]+)\)',p.read_text(encoding='utf-8')):
  if re.match(r'^(https?://|#|mailto:)',target):continue
  target=target.split('#')[0].strip('<>')
  if target and not (p.parent/target).exists() and (p.parent/target).resolve()!=(R/'tests/capacity64-delivery-20261019.json').resolve():broken.append((str(p.relative_to(R)),target))
check('active document links',not broken,broken)
report=dict(date='2026-10-19',software='0.4.0',release=RELEASE,passed=True,checks=checks,packages=packages,hardware_executed=False,limits=['每口16台时效、电气条件、真实动作和72小时需上板验收','Go race未运行，本机无C编译器；常规并发用例已执行','实体SYS驱动沿用用户已确认实现；看门狗保持关闭','平均测温任务仍由上位机组织，未新增任务一次认证/强制新读I6专用接口'])
average=load('tests/capacity64-average-implementation-20261019.json')
check('average implementation capacity update preserved workflow',average['passed'] and not average['render_pending'] and average['pages']==16 and len(average['changed_paragraphs'])==2 and average['IFX_32_scope_preserved'] and average['tables_and_images_preserved'])
check('average implementation final Word hash',sha(Path(average['path']))==average['output_sha256'])
(R/'tests/capacity64-delivery-20261019.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(dict(passed=True,checks=len(checks),release=RELEASE),ensure_ascii=False))
