"""Read-only final consistency review for the 0.3.4 manual reset delivery."""
from pathlib import Path
import hashlib
import json
import re
import zipfile
from pypdf import PdfReader

ROOT = Path(__file__).resolve().parents[1]
RELEASE = '0.3.4-20261016-r2'
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
load = lambda p: json.loads((ROOT/p).read_text(encoding='utf-8-sig'))
checks = []

def check(name, condition, detail=None):
    assert condition, (name, detail)
    checks.append({'name':name, 'passed':True, 'detail':detail})

events=[json.loads(line) for line in (ROOT/'tests/manual-reset-go-test-20261016.jsonl').read_text(encoding='utf-8-sig').splitlines()]
passes=[e for e in events if e.get('Action')=='pass' and e.get('Test')]
check('full_go_suite', not any(e.get('Action')=='fail' for e in events) and len(passes)>100,
      {'top_level':sum('/' not in e['Test'] for e in passes), 'including_subtests':len(passes)})
build=(ROOT/'tmp/manual-reset-build-20261016.log').read_text(encoding='utf-8-sig')
check('final_full_build_vet_typescript_and_three_architectures', 'Built Windows amd64, Linux ARM64 and ARMv7' in build and 'FAIL' not in build)
for file in ['v3-live-verification-0.3.4-20261016.json','rtc-compiled-integration-0.3.4-20261016.json','install-flow-verification-0.3.4-20261016.json','manual-reset-browser-verification-20261016.json','manual-reset-document-syntax-20261016.json']:
    report=load('tests/'+file)
    check(file, report['passed'] and report['software']=='0.3.4',len(report.get('checks',[])))
    for name,digest in report.get('binaries',{}).items():
        check('final_binary_evidence '+name, sha(ROOT/'output/windows-amd64'/name)==digest)

package=load(f'tests/runtime-package-verification-{RELEASE}.json')
check('runtime_package_checks',package['passed'] and package['version']==RELEASE and len(package['packages'])==2)
for p in package['packages']:
    check(p['architecture']+' 13 files static ELF hash',p['file_count']==13 and p['elf_static'] and p['config_valid'] and sha(ROOT/'output/packages'/p['archive'])==p['sha256'])
    for name,digest in p['binaries'].items():
        check(p['architecture']+' packaged binary matches final build '+name,sha(ROOT/'output'/p['architecture']/name)==digest)
digest=package['packages'][0]['sha256'].upper()
for path in ['docs/10_运行部署与维护.md','docs/23_时钟校核与断电恢复.md']:
    text=(ROOT/path).read_text(encoding='utf-8')
    check('correct published package/hash '+path,RELEASE in text and digest in text)

spec=load('protocols/north-v3.json'); attachment=load('docs/附件/north-v3-design.json'); old=load('protocols/north-v2.json')
check('reset_contract_attachment',attachment['current_software']=='0.3.4' and attachment['customer_reset_allowed_states']==[0,3,4,5] and attachment['customer_manual_confirmation_required_states']==[5] and not attachment['automatic_uncertain_reset_allowed'])
check('measurement_regions_preserved',[r for r in spec['regions'] if r['name']!='simple_control']==[r for r in old['regions'] if r['name']!='mailboxes'])
check('no_new_registers',spec['control_fields']==attachment['control_fields'] and spec['regions'][-1]['base']==30000 and spec['regions'][-1]['stride']==12)

pdf=ROOT/'output/pdf/GT6757S_客户通讯协议_V3.0-D2.pdf'
pagination=load('tests/v3-pdf-pagination.json')
reader=PdfReader(pdf);texts=[p.extract_text() for p in reader.pages]
check('PDF_source_hash_and_pagination',pagination['source_sha256']==sha(ROOT/'docs/12_客户通讯协议V3.md') and pagination['pdf_sha256']==sha(pdf) and len(texts)==13 and pagination['first_page_title'] and pagination['second_page_title'] and pagination['third_page_details'])
check('PDF_manual_reset_wording','人工核查并留档后可写0复位' in texts[1] and '客户写0拒绝06' not in ''.join(texts) and '客户不能复位' not in ''.join(texts))
check('PDF_visual_review',len(list((ROOT/'tmp/pdfs/manual-reset-20261016').glob('page-*.png')))==13,'All-page contact sheet and changed pages 2/8 visually inspected; no clipping/overlap observed.')
customer=ROOT/'output/customer-docs/gt6757s-customer-v3.0-d2-20261016-r1'
with zipfile.ZipFile(str(customer)+'.zip') as z:
    names=[n for n in z.namelist() if not n.endswith('/')]
    check('customer_zip_only_four_files',len(names)==4,names)
    for file in ('12_客户通讯协议V3.md','GT6757S_客户通讯协议_V3.0-D2.pdf'):
        check('customer_zip_matches_current '+file,z.read(next(n for n in names if n.endswith('/'+file)))==(customer/file).read_bytes())

mds=[ROOT/p for p in ['README.md','00_index.md','progress.md']]+list((ROOT/'docs').glob('*.md'))+list((ROOT/'decisions').glob('*.md'))
broken=[]
for path in mds:
    for link in re.findall(r'\]\((<[^>]+>|[^)]+)\)',path.read_text(encoding='utf-8-sig')):
        url=link.strip('<>').split('#')[0]
        if not url or re.match(r'^(https?://|app:|codex:)',url):continue
        target=(path.parent/url).resolve()
        # This report is written only after the review passes; allow its own incoming links.
        if target==ROOT/'tests/manual-reset-delivery-verification-20261016.json':continue
        if not target.exists():broken.append((str(path.relative_to(ROOT)),url))
check('active_document_links',not broken,broken)
active=['docs/01_需求讨论草案.md','docs/02_寄存器与写入方案.md','docs/04_网页配置与数据显示.md','docs/10_运行部署与维护.md','docs/12_客户通讯协议V3.md','docs/14_简易控制内部设计与状态机.md','docs/15_北向寄存器点表V3.md','docs/17_GT6757S安装与板端联调操作说明.md','docs/24_手机访问与网页操作说明.md']
contradictions=[]
for file in active:
    for line in (ROOT/file).read_text(encoding='utf-8').splitlines():
        if any(word in line for word in ('1/2/5','客户不能复位','状态5客户不能清除','客户写0拒绝06','5由厂家核查后','状态5须厂家现场')):
            contradictions.append((file,line))
check('no_obsolete_current_reset_rules',not contradictions,contradictions)
ui=(ROOT/'src/internal/web/static/index.html').read_text(encoding='utf-8')
check('UI_manual_review_and_no_default_confirmation','控制结果不确定时的人工核查' in ui and '厂家核查' not in ui and 'id="reset-verified" type="checkbox" required' in ui)
source_files=[ROOT/'src/internal/command/command.go',ROOT/'src/internal/storage/control.go',ROOT/'src/internal/core/core.go',ROOT/'src/internal/core/api.go',ROOT/'src/internal/web/static/index.html',ROOT/'tools/build.ps1']
result={'date':'2026-10-16','software':'0.3.4','release':RELEASE,'passed':True,'hardware_executed':False,'physical_phone_executed':False,'host_clock_modified':False,'checks':checks,
        'sources':{str(p.relative_to(ROOT)):sha(p) for p in source_files},
        'documents':{str(p.relative_to(ROOT)):sha(p) for p in mds},
        'package_sha256':{p['architecture']:p['sha256'] for p in package['packages']},
        'customer_pdf_sha256':sha(pdf),
        'remaining':['GT6757S upgrade and field manual confirmation workflow acceptance','actual RS485 electrical/CRC and real meter action tests','RTC battery power-off test and real phone/router Wi-Fi acceptance']}
(ROOT/'tests/manual-reset-delivery-verification-20261016.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'passed':True,'checks':len(checks),'release':RELEASE},ensure_ascii=False))
