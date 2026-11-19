"""Fresh C01 checks; historical delivery reports are retained in archive/c01."""
from pathlib import Path
import argparse,hashlib,json,re,struct,zipfile
from lxml import etree
from docx import Document
from pypdf import PdfReader
from PIL import Image,ImageChops
from apply_c01_document import REPLACEMENTS

R=Path(__file__).resolve().parents[1];P=R.parent
parser=argparse.ArgumentParser();parser.add_argument('--skip-packages',action='store_true');args=parser.parse_args()
A=R/'archive/c01/20261014_before_confirmation_update'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
load=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
s=load(R/'protocols/north-v3.json');old=load(A/'protocols/north-v3.json');d=load(R/'docs/附件/north-v3-design.json')
checks=[]
def check(name,ok,detail=None):
 checks.append({'name':name,'passed':bool(ok),'detail':detail})
 if not ok: raise AssertionError((name,detail))
keys=set(old)-{'commands','command_confirmation'}
check('all previous register fields and state/error definitions unchanged',all(s[k]==old[k] for k in keys))
check('D2 attachment matches runtime actions',s['commands']==d['commands'] and s['control_fields']==d['control_fields'])
check('wire action values unchanged',[(c['value'],c['south_HR699']) for c in s['commands']]==[(c['value'],c['south_HR699']) for c in old['commands']])
check('C01 seven action names',[(c['value'],c['chinese']) for c in s['commands'][:7]]==[(1,'测量Ⅰ'),(2,'测量Ⅱ（界面）'),(3,'测量Ⅲ（罐底）'),(4,'停止'),(5,'上升'),(6,'下降'),(7,'远程密度')])
check('generated mapping used by engine','return protocols.SouthAction(cmd)' in (R/'src/internal/command/command.go').read_text(encoding='utf-8'))
events=[json.loads(l) for l in (R/'tests/c01-go-test-20261014.jsonl').read_text(encoding='utf-8-sig').splitlines()]
passed=[e for e in events if e['Action']=='pass' and e.get('Test')];top=[e for e in passed if '/' not in e['Test']]
check('fresh Go test results',not any(e['Action']=='fail' for e in events) and len(top)==44 and len(passed)==95,{'top_level':len(top),'items':len(passed)})
check('seven C01 wire-frame cases and independent stop passed',len([e for e in passed if e['Test'].startswith('TestC01ConfirmedCommandsReachCorrectFWAction/')])==7 and any(e['Test']=='TestC01IndependentStopDoesNotUseRiseCode' for e in passed))
live=load(R/'tests/v3-live-verification.json');check('compiled integration 25 checks',live['passed'] and live['software']=='0.3.1' and len(live['checks'])==25)
for arch,cls,machine in [('windows-amd64',None,0x8664),('linux-arm64',2,183),('linux-armv7',1,40)]:
 for n in ['gateway-core','gateway-web']:
  b=(R/'output'/arch/(n+('.exe' if cls is None else ''))).read_bytes()
  if cls: ok=b[:4]==b'\x7fELF' and b[4]==cls and struct.unpack_from('<H',b,18)[0]==machine
  else:
   off=struct.unpack_from('<I',b,0x3c)[0];ok=b[:2]==b'MZ' and b[off:off+4]==b'PE\0\0' and struct.unpack_from('<H',b,off+4)[0]==machine
  check('binary architecture '+arch+'/'+n,ok)

doc=P/'实施说明/无线温度浮子平均温度测量实施过程与流程图_V1.3_含GT6757X网关_20261012.docx'
before=A/'implementation'/doc.name
with zipfile.ZipFile(before) as za,zipfile.ZipFile(doc) as zb:
 allowed={'word/document.xml','word/media/image2.png'}
 check('DOCX only intended XML and IFX image changed',za.namelist()==zb.namelist() and all(za.read(n)==zb.read(n) for n in za.namelist() if n not in allowed))
 a=etree.fromstring(za.read('word/document.xml'));b=etree.fromstring(zb.read('word/document.xml'));ns={'w':'http://schemas.openxmlformats.org/wordprocessingml/2006/main'}
 for n in a.xpath('//w:t',namespaces=ns):
  text=n.text or ''
  for x,y in REPLACEMENTS.items():text=text.replace(x,y)
  n.text=text
 check('every text change explained by C01 replacements',etree.tostring(a,method='c14n')==etree.tostring(b,method='c14n'))
 check('FW and gateway figures unchanged',za.read('word/media/image1.png')==zb.read('word/media/image1.png') and za.read('word/media/image3.png')==zb.read('word/media/image3.png'))
 check('IFX figure regenerated',za.read('word/media/image2.png')!=zb.read('word/media/image2.png'))
da,db=Document(before),Document(doc)
check('all nine document tables preserved',len(da.tables)==len(db.tables)==9 and all(etree.tostring(a._tbl,method='c14n')==etree.tostring(b._tbl,method='c14n') for a,b in zip(da.tables,db.tables)))
text='\n'.join(p.text for p in db.paragraphs)
check('old IFX command claims removed',all(x not in text for x in ['测量Ⅲ6','停止5','命令6并执行握手','命令5并执行握手','指令值为6']))
check('new IFX commands and ownership explicit','命令3并执行握手' in text and '停止命令4并执行握手' in text and '平均测温由上位机完成' in text)
v12=P/'实施说明/无线温度浮子平均温度测量实施过程与流程图_V1.2_20261009.docx'
check('V1.2 source remains unchanged',sha(v12)=='ed4d2c753ecd3d02145da4595b4291e55cb640776418cdb604ebeea5464e18dc')
check('DOCX 16 reviewed pages',len(list((R/'tmp/c01-20261014/docx-render').glob('page-*.png')))==16)
pdf=R/'output/pdf/GT6757S_客户通讯协议_V3.0-D2.pdf';pg=load(R/'tests/v3-pdf-pagination.json');pdftext='\n'.join(p.extract_text() for p in PdfReader(pdf).pages)
check('PDF 13 pages and first two quick-start pages',pg['pages']==13 and all(pg[k] for k in ['first_page_title','second_page_title','third_page_details']))
check('PDF current source/output hashes',pg['source_sha256']==sha(R/'docs/12_客户通讯协议V3.md') and pg['pdf_sha256']==sha(pdf))
check('PDF labels and decimal/binary clarification','远程密度' in pdftext and '界面' in pdftext and '不能写成十进制101或110' in pdftext and '2026-10-14' in pdftext)
changed=[]
for i in range(1,14):
 a=Image.open(R/f'tmp/c01-20261014/pdf-render-reviewed-r1/page-{i:02}.png');b=Image.open(R/f'tmp/c01-20261014/pdf-render/page-{i:02}.png')
 if ImageChops.difference(a,b).getbbox():changed.append(i)
check('only reviewed PDF date/history pages changed in last iteration',changed==[1,13],changed)
broken=[]
for p in [R/'README.md',R/'00_index.md',R/'progress.md']+list((R/'docs').glob('*.md'))+list((R/'decisions').glob('*.md'))+list((R/'source_notes').glob('*.md')):
 for url in re.findall(r'\]\((<[^>]+>|[^)]+)\)',p.read_text(encoding='utf-8')):
  u=url.strip('<>').split('#')[0]
  if not u or re.match('^(https?://|app:|codex:)',u):continue
  if not (p.parent/u).exists():broken.append([str(p),u])
# Report file is about to be written below and may be linked before first run.
broken=[x for x in broken if not x[1].endswith('c01-verification-20261014.json')]
check('active Markdown links valid',not broken,broken)
pk=None
if not args.skip_packages:
 pk=load(R/'tests/v3-package-verification.json')
 check('new ARM and customer archives verified',pk['passed'] and pk['version']=='0.3.1-20261014-r1')
 for entry in pk['packages']+[pk['customer']]:
  paths=list((R/'output').rglob(entry['archive']))
  check('release SHA256 '+entry['archive'],len(paths)==1 and sha(paths[0])==entry['sha256'])
source=P/'客户确认/无线温度浮子上位机系统当前客户确认事项_V1.3_20260928.docx'
result={'passed':True,'date':'2026-10-14','software':'0.3.1','map_version':3,'confirmation':'2026-10-13 C01','source_docx_sha256':sha(source),'checks':checks,'docx':str(doc),'docx_sha256':sha(doc),'pdf_sha256':sha(pdf),'packages_checked':not args.skip_packages,'visual_review':'Word16页及客户PDF13页逐页检查；最终PDF仅第1/13页更新日期及历史后复查','hardware_executed':False,'race_executed':False}
(R/'tests/c01-verification-20261014.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'passed':True,'checks':len(checks),'packages_checked':not args.skip_packages},ensure_ascii=False))
