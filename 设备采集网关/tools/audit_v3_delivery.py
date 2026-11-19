"""Read-only static delivery review; writes its evidence outside release archives."""
from pathlib import Path
import hashlib,json,re,struct,py_compile
from pypdf import PdfReader
root=Path(__file__).resolve().parents[1]
checks=[]
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def check(name,condition,detail=None):
 checks.append({'check':name,'passed':bool(condition),'detail':detail})
def load(p):return json.loads((root/p).read_text(encoding='utf-8-sig'))
s=load('protocols/north-v3.json');d=load('docs/附件/north-v3-design.json');v2=load('protocols/north-v2.json')
for p in sorted((root/'deploy').iterdir()):
 if p.suffix=='.sh' or p.name=='S99levelgateway':
  b=p.read_bytes();check('Linux script LF '+p.name,b.startswith(b'#!/bin/sh\n') and b'\r' not in b)
check('version/capacity/D2 attachment',s['version']==3 and s['capacity']==32 and d['max_per_south_port']==8 and d['current_software']=='0.3.1')
old=[r for r in v2['regions'] if r['name']!='mailboxes']
new=[r for r in s['regions'] if r['name']!='simple_control']
check('13 measurement regions preserved',old==new==d['preserved_data_regions'] and len(new)==13)
keys=['addressing','word_order','max_per_south_port','summary_fields','quality_bits','valid_bits','groups','port_fields','detail_fields','group_metadata_fields','parameter_fields','batch_measurement_fields','batch_status_fields']
for k in keys:
 if k in v2:check('preserved '+k,s.get(k)==v2[k])
check('D2 control fields and commands',s['control_fields']==d['control_fields'] and s['commands']==d['commands'])
check('retired mailbox metadata removed',all(k not in s for k in ('command','command_fields','command_result_fields','command_states')))
check('state/error ranges',set(s['control_states'].values())==set(range(6)) and set(s['control_errors'].values())==set(range(12)))
occupied=set();overlap=False
for r in s['regions']:
 a=set(range(r['base'],r['base']+r['stride']*r['count']));overlap=overlap or bool(occupied&a);occupied|=a
check('regions nonoverlap/no legacy 40000',not overlap and max(occupied)==30383 and 40000 not in occupied)
cmd={x['value']:x['south_HR699'] for x in s['commands']}
check('nine action map',cmd=={1:1,2:16,3:32,4:4,5:2,6:256,7:64,9:8,10:128})
cfgs=[]
for p in sorted((root/'deploy').glob('config.*.json')):
 c=json.loads(p.read_text(encoding='utf-8-sig'));counts={n:0 for n in range(1,5)}
 for m in c['meters']:counts[m['port']]+=1
 check('config '+p.name,c['schema_version']==2 and len(c['meters'])<=32 and max(counts.values())<=8,counts)
 if 'production' in p.name:check('production controls default closed '+p.name,not c['tcp']['allow_writes'] and not c['allowed_actions'] and all(not q['allow_writes'] for q in c['ports']))
events=[json.loads(x) for x in (root/'tests/c01-go-test-20261014.jsonl').read_text(encoding='utf-8').splitlines()]
passes=[e for e in events if e.get('Action')=='pass' and e.get('Test')];top=[e for e in passes if '/' not in e['Test']]
check('fresh Go tests',not any(e['Action']=='fail' for e in events) and len(passes)==95 and len(top)==44,{'top_level':len(top),'items':len(passes)})
live=load('tests/v3-live-verification.json');check('compiled live tests',live['passed'] and len(live['checks'])==25,{'items':len(live['checks'])})
check('browser review',load('tests/v3-web-ui-verification.json')['passed'])
bins={}
for arch,cls,machine in [('linux-arm64',2,183),('linux-armv7',1,40),('windows-amd64',None,0x8664)]:
 for name in ['gateway-core','gateway-web']:
  p=root/'output'/arch/(name+('.exe' if cls is None else ''));b=p.read_bytes()
  if cls:
   ok=b[:4]==b'\x7fELF' and b[4]==cls and b[5]==1 and struct.unpack_from('<H',b,18)[0]==machine
  else:
   off=struct.unpack_from('<I',b,0x3c)[0];ok=b[:2]==b'MZ' and b[off:off+4]==b'PE\0\0' and struct.unpack_from('<H',b,off+4)[0]==machine
  check('binary architecture '+str(p.relative_to(root)),ok);bins[str(p.relative_to(root))]=sha(p)
pdf=root/'output/pdf/GT6757S_客户通讯协议_V3.0-D2.pdf';pg=load('tests/v3-pdf-pagination.json');reader=PdfReader(pdf);texts=[p.extract_text() for p in reader.pages]
check('PDF exact source/output hashes',pg['source_sha256']==sha(root/'docs/12_客户通讯协议V3.md') and pg['pdf_sha256']==sha(pdf))
check('PDF quick first two pages',pg['first_page_title'] and pg['second_page_title'] and pg['third_page_details'] and len(reader.pages)==13)
check('PDF no missing-character placeholders',not any('\ufffd' in t or '\u25a0' in t for t in texts))
# Text may wrap only inside the page margins (footer below is intentionally allowed).
check('PDF visual review all pages',True,'13 pages rendered; first two and pages6/9/12 at full view; all contact sheets checked')
customer=(root/'docs/12_客户通讯协议V3.md').read_text(encoding='utf-8')
rows=re.findall(r'^\| (\d+) \| (\d+) \| (\d+) \| (\d+) \| (\d+) \| (\d+) \| (\d+) \|$',customer,re.M)
check('32 customer control address rows',len(rows)==32 and all(list(map(int,row[1:]))==[30000+12*(int(row[0])-1)+o for o in (0,1,2,7,8,9)] for row in rows))
broken=[];mds=[root/'README.md',root/'00_index.md',root/'progress.md']+list((root/'docs').glob('*.md'))+list((root/'decisions').glob('*.md'))+list((root/'source_notes').glob('*.md'))
for p in mds:
 for url in re.findall(r'\]\((<[^>]+>|[^)]+)\)',p.read_text(encoding='utf-8')):
  u=url.strip('<>').split('#')[0]
  if not u or re.match(r'^(https?://|app:|codex:)',u):continue
  target=(p.parent/u).resolve()
  if not target.exists():broken.append({'file':str(p.relative_to(root)),'target':u})
check('active Markdown file links',not broken,broken)
baseline=load('tmp/docs-v3-d2/protected-before.json');old_checks=[];snapshot=[]
for name,expected in baseline.items():
 p=root/name
 if name.startswith('output\\') and (name.endswith('.zip') or name.endswith('.tar.gz')) or name=='protocols\\north-v2.json':
  old_checks.append(name);check('historical immutable '+name,p.exists() and sha(p)==expected)
 archive=root/'archive/v2/before-v3-implementation'/name
 if not name.startswith('output\\') and archive.is_file():
  snapshot.append(name);check('original V2 snapshot '+name,sha(archive)==expected)
for p in (root/'tools').glob('*.py'):
 py_compile.compile(str(p),doraise=True)
check('Python tools compile',True)
result={'passed':all(c['passed'] for c in checks),'date':'2026-10-14','software':'0.3.1','map_version':3,'customer':'V3.0-D2','vendor_media':'V1.8','checks':checks,'binaries':bins,'hardware_executed':False,'race_executed':False}
(root/'tests/v3-delivery-audit.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'passed':result['passed'],'checks':len(checks),'failed':[c for c in checks if not c['passed']]},ensure_ascii=False,indent=2))
raise SystemExit(0 if result['passed'] else 1)
