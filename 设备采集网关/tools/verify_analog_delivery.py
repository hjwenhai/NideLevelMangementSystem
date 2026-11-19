"""Verify analog UI release evidence and delivery consistency, without accessing hardware."""
from pathlib import Path
import hashlib,json,re
ROOT=Path(__file__).resolve().parents[1]
load=lambda p:json.loads((ROOT/p).read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
checks=[]
def check(name,condition,detail=None):
 assert condition,(name,detail)
 checks.append({'name':name,'passed':True,'detail':detail})
evidence=[
 ('tests/analog-view-verification-20261018.json',13),
 ('tests/analog-compiled-verification-20261018.json',15),
 ('tests/analog-browser-verification-20261018.json',6),
 ('tests/v3-live-verification-0.3.6-20261018.json',32),
 ('tests/runtime-package-verification-0.3.6-20261018-r1.json',None),
 ('tests/analog-document-syntax-20261018.json',None),
]
for path,count in evidence:
 data=load(path)
 check(path,data['passed'] and data['software']=='0.3.6' and data['date']=='2026-10-18')
 if count:check('passing count '+path,len(data['checks'])==count and all(c['passed'] for c in data['checks']))
 for binary,digest in data.get('binaries',{}).items():check('tested final Windows binary '+binary,sha(ROOT/'output/windows-amd64'/binary)==digest)
build=(ROOT/'tmp/analog-ui-build-20261018.log').read_text(encoding='utf-8-sig')
check('full Go tests vet TypeScript three architectures','Built Windows amd64, Linux ARM64 and ARMv7' in build and 'FAIL' not in build and 'src/internal/core' in build)
packages=load(evidence[4][0])['packages']
for package in packages:
 arch=package['architecture']
 check('13 runtime files '+arch,package['file_count']==13 and package['elf_static'] and package['config_valid'])
 check('archive hash '+arch,sha(ROOT/'output/packages'/package['archive'])==package['sha256'])
 for name,digest in package['binaries'].items():check('final packaged binary '+arch+'/'+name,sha(ROOT/'output'/arch/name)==digest)
for path in ['docs/10_运行部署与维护.md','docs/23_时钟校核与断电恢复.md','output/packages/README.md']:
 text=(ROOT/path).read_text(encoding='utf-8')
 check('current package and hash '+path,'0.3.6-20261018-r1' in text and packages[0]['sha256'].upper() in text)
prior=load('tests/history-export-delivery-verification-20261016.json')
baseline={k.replace('\\','/'):v for k,v in prior['sources'].items()}
check('north protocol byte-for-byte unchanged',sha(ROOT/'protocols/north-v3.json')==baseline['protocols/north-v3.json'])
old_docs={k.replace('\\','/'):v for k,v in prior['documents'].items()}
check('customer protocol unchanged',sha(ROOT/'docs/12_客户通讯协议V3.md')==old_docs['docs/12_客户通讯协议V3.md'])
check('SYS driver document retains field confirmation',sha(ROOT/'docs/11_SYS状态指示灯.md')==old_docs['docs/11_SYS状态指示灯.md'])
spec=load('protocols/north-v3.json');design=load('docs/附件/north-v3-design.json')
check('D2 map version and controls unchanged',spec['version']==3 and design['current_software']=='0.3.6' and design['control_fields']==spec['control_fields'] and design['customer_reset_allowed_states']==[0,3,4,5])
cfg=load('deploy/config.demo.json')
check('existing schema2 and analog disabled default',cfg['schema_version']==2 and all(not m['analog'] for m in cfg['meters']))
for path in ['README.md','00_index.md','progress.md','docs/10_运行部署与维护.md']:
 check('current release '+path,'0.3.6-20261018-r1' in (ROOT/path).read_text(encoding='utf-8'))
syntax=load(evidence[5][0]);check('manual commands only parsed and commented',syntax['commands_executed'] is False and syntax['blocks']==45 and syntax['main_deployment_commands_have_chinese_comments'])
# Verify the comments adjacent to every deployment command contain actual Chinese text.
manifest=load('tmp/manual-reset-doc-syntax/manifest.json')
bad_comments=[]
for block in manifest['blocks']:
 if not block['source'].startswith('10_'):continue
 lines=Path(block['path']).read_text(encoding='utf-8').splitlines()
 for index,line in enumerate(lines):
  if not line.strip() or line.lstrip().startswith('#') or line.strip() in ['(',')','}']:continue
  previous=next((s.strip() for s in reversed(lines[:index]) if s.strip()),'')
  if not previous.startswith('#') or not re.search(r'[\u4e00-\u9fff]',previous):bad_comments.append((block['path'],index+1))
check('every deployment command has adjacent Chinese comment',not bad_comments,bad_comments)
files=[ROOT/p for p in ['README.md','00_index.md','progress.md','output/packages/README.md']]+list((ROOT/'docs').glob('*.md'))+list((ROOT/'decisions').glob('*.md'))
broken=[]
for p in files:
 for link in re.findall(r'\]\((<[^>]+>|[^)]+)\)',p.read_text(encoding='utf-8-sig')):
  url=link.strip('<>').split('#')[0]
  if not url or re.match(r'^(https?://|app:|codex:)',url):continue
  target=(p.parent/url).resolve()
  if target==ROOT/'tests/analog-delivery-verification-20261018.json':continue
  if not target.exists():broken.append((str(p.relative_to(ROOT)),url))
check('active local document links',not broken,broken)
screens=load(evidence[2][0])['screenshots']
for name in screens:check('browser screenshot '+name,(ROOT/name).read_bytes().startswith(b'\xff\xd8\xff'))
sources=['src/internal/cache/cache.go','src/internal/cache/analog_test.go','src/internal/core/core.go','src/internal/core/analog_test.go','src/web/analog.ts','src/web/app.ts','src/internal/web/static/analog.js','src/internal/web/static/app.js','src/internal/web/static/index.html','src/internal/web/static/style.css','src/internal/core/api.go','src/internal/platform/led.go','protocols/north-v3.json']
result={'date':'2026-10-18','software':'0.3.6','release':'0.3.6-20261018-r1','passed':True,'hardware_executed_by_agent':False,'host_clock_modified':False,
 'checks':checks,'sources':{name:sha(ROOT/name) for name in sources},'documents':{str(p.relative_to(ROOT)):sha(p) for p in files},
 'packages':packages,'SYS_driver_user_confirmed':True,'SYS_confirmation_source':'source_notes/SYS实体指示灯驱动确认_20261016.md',
 'remaining':['Upgrade GT6757S, enable analog per actual meter and verify four readings against physical IR27-30','Measure full-load cycle after enabling analog; complete other outstanding board acceptance separately']}
(ROOT/'tests/analog-delivery-verification-20261018.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'passed':True,'release':result['release'],'checks':len(checks)},ensure_ascii=False))
