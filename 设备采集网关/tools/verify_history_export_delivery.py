"""Read-only delivery consistency checks; no board access and no sample commands executed."""
from pathlib import Path
import csv,hashlib,json,re

ROOT=Path(__file__).resolve().parents[1]
load=lambda p:json.loads((ROOT/p).read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
checks=[]
def check(name,condition,detail=None):
    assert condition,(name,detail)
    checks.append({'name':name,'passed':True,'detail':detail})
for path in ['tests/history-export-compiled-verification-20261016.json','tests/history-export-browser-verification-20261016.json','tests/v3-live-verification-0.3.5-20261016.json','tests/runtime-package-verification-0.3.5-20261016-r1.json','tests/history-export-document-syntax-20261016.json']:
    data=load(path)
    check(path,data['passed'] and data['software']=='0.3.5')
    for binary,digest in data.get('binaries',{}).items():check('final Windows binary '+binary,sha(ROOT/'output/windows-amd64'/binary)==digest)
build=(ROOT/'tmp/history-export-build-20261016.log').read_text(encoding='utf-8-sig')
check('full Go tests vet TypeScript and three target builds','Built Windows amd64, Linux ARM64 and ARMv7' in build and 'FAIL' not in build)
packages=load('tests/runtime-package-verification-0.3.5-20261016-r1.json')['packages']
for package in packages:
    check('13 runtime files '+package['architecture'],package['file_count']==13 and package['elf_static'] and package['config_valid'])
    check('archive checksum '+package['architecture'],sha(ROOT/'output/packages'/package['archive'])==package['sha256'])
    for name,digest in package['binaries'].items():check('packaged binary '+package['architecture']+'/'+name,sha(ROOT/'output'/package['architecture']/name)==digest)
for file in ['docs/10_运行部署与维护.md','docs/23_时钟校核与断电恢复.md']:
    text=(ROOT/file).read_text(encoding='utf-8')
    check('published package/hash '+file,'0.3.5-20261016-r1' in text and packages[0]['sha256'].upper() in text)
spec=load('protocols/north-v3.json');before=load('docs/附件/north-v3-design.json')
check('north addresses and schemas unchanged',spec['version']==3 and spec['control_fields']==before['control_fields'] and before['current_software']=='0.3.5' and before['customer_reset_allowed_states']==[0,3,4,5])
csv_path=ROOT/'output/ui-review-history-export-20261016/browser-slot-01.csv'
with csv_path.open(encoding='utf-8-sig',newline='') as f:rows=list(csv.reader(f))
check('browser actual download crosses pages and only slot1',len(rows)>2001 and len(rows[0])==17 and all(r[1]=='1' and r[15]=='首台试验液位计' for r in rows[1:]),{'rows':len(rows)-1})
fact=(ROOT/'source_notes/SYS实体指示灯驱动确认_20261016.md').read_text(encoding='utf-8')
check('SYS user field confirmation scope','用户' in fact and '已确认' in fact and '软件0.3.5的历史导出筛选仍需升级后现场使用确认' in fact)
mds=[ROOT/p for p in ['README.md','00_index.md','progress.md']]+list((ROOT/'docs').glob('*.md'))+list((ROOT/'decisions').glob('*.md'))
broken=[]
for p in mds:
    for link in re.findall(r'\]\((<[^>]+>|[^)]+)\)',p.read_text(encoding='utf-8-sig')):
        url=link.strip('<>').split('#')[0]
        if not url or re.match(r'^(https?://|app:|codex:)',url):continue
        dest=(p.parent/url).resolve()
        if dest==ROOT/'tests/history-export-delivery-verification-20261016.json':continue
        if not dest.exists():broken.append((str(p.relative_to(ROOT)),url))
check('active document links',not broken,broken)
sources=[ROOT/p for p in ['src/internal/web/web.go','src/internal/web/history_test.go','src/internal/core/api.go','src/web/app.ts','src/internal/web/static/index.html','protocols/north-v3.json']]
result={'date':'2026-10-16','software':'0.3.5','release':'0.3.5-20261016-r1','passed':True,'hardware_executed_by_agent':False,'host_clock_modified':False,'checks':checks,
        'SYS_driver_user_confirmed':True,'SYS_confirmation_source':'source_notes/SYS实体指示灯驱动确认_20261016.md',
        'sources':{str(p.relative_to(ROOT)):sha(p) for p in sources},'documents':{str(p.relative_to(ROOT)):sha(p) for p in mds},
        'remaining':['Install 0.3.5 on GT6757S and verify history selector/export','Other unrecorded field fault modes, RTC and long-run acceptance are independent of the confirmed SYS driver']}
(ROOT/'tests/history-export-delivery-verification-20261016.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'passed':True,'checks':len(checks),'release':result['release']},ensure_ascii=False))
