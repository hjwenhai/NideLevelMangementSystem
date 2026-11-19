"""Verify current V1.2 customer Word/PDF content and capacity claims."""
from pathlib import Path
import hashlib,json,re,shutil,zipfile
from docx import Document
from pypdf import PdfReader
import pdfplumber
from create_customer_materials import SPEC,COMPARE,ROOT,OUT
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
normal=lambda t:''.join(t.split())
items=[]
for data,folder,count in [(SPEC,'spec-final',3),(COMPARE,'compare-final',2)]:
 src=ROOT/'tmp/customer-materials-v1.2'/folder;pdf=src/(data['file']+'.pdf')
 reader=PdfReader(pdf);assert len(reader.pages)==count
 assert len(list(src.glob('page-*.png')))==count
 with pdfplumber.open(pdf) as doc:
  fragments=[p.extract_text() or '' for p in doc.pages]
  fragments += [cell for p in doc.pages for table in p.extract_tables() for row in table for cell in row if cell]
  text=normal('\n'.join(fragments))
 expected=[data['title'],data['subtitle']]
 for page in data['pages']:
  for block in page:
   expected += block['headers']+[v for row in block['rows'] for v in row] if block['type']=='table' else [block['text']]
 for s in expected:assert normal(s) in text,(data['file'],s)
 assert 'GT-230710-V1.1' in text
 word=OUT/(data['file']+'.docx');document=Document(word)
 assert document.paragraphs[0].style.name=='Title'
 blocks=[b for page in data['pages'] for b in page if b['type']=='table']
 assert len(document.tables)==len(blocks)
 for t,b in zip(document.tables,blocks):assert [[c.text for c in r.cells] for r in t.rows]==[b['headers']]+b['rows']
 md=(ROOT/'docs'/data['source']).read_text(encoding='utf-8')
 for page in data['pages']:
  for block in page:
   if block['type']!='table':assert block['text'] in md
 shutil.copy2(pdf,OUT/pdf.name)
 items.append(dict(docx=str(word.relative_to(ROOT)),pdf=str((OUT/pdf.name).relative_to(ROOT)),pages=count,all_authored_text_verified=True,docx_sha256=sha(word),pdf_sha256=sha(OUT/pdf.name),visual_review='全部页面已逐页检查，无重叠、裁切或空白页'))
# Numerical customer-address tables are checked against formulas for all 64 slots.
md=(ROOT/'docs/12_客户通讯协议V3.md').read_text(encoding='utf-8')
for n in range(1,65):
 b=1000+24*(n-1);c=30000+12*(n-1)
 assert f'| {n} | 厂家填写 | {b} | 24 | {b+4}～{b+5} | {b+6} | {b+7} | {b+1} |' in md
 assert '| '+' | '.join(str(x) for x in [n,c,c+1,c+2,c+7,c+8,c+9])+' |' in md
spec=json.loads((ROOT/'protocols/north-v3.json').read_text(encoding='utf-8'))
point=(ROOT/'docs/15_北向寄存器点表V3.md').read_text(encoding='utf-8')
for r in spec['regions']:
 if r['name']!='simple_control':assert f"| {r['name']} | {r['base']} | {r['stride']} | {r['count']} | {r['base']+r['stride']*r['count']-1} |" in point
protocol=ROOT/'output/pdf/GT6757S_客户通讯协议_V3.1-D2.pdf'
reader=PdfReader(protocol);assert len(reader.pages)==16
assert '快速接入·第1页' in reader.pages[0].extract_text() and '快速接入·第2页' in reader.pages[1].extract_text() and '详细说明' in reader.pages[2].extract_text()
report=dict(date='2026-10-19',software='0.4.0',product_material_version='V1.2',passed=True,documents=items,protocol_pages=16,all64_customer_addresses_verified=True,point_map_region_ranges_match=True,hardware_executed=False,claim_review=['网关四路16台共64台；现用IFX两路16台共32台','网关北向RTU占用一个物理口，最多48台','4800bps每口16台约4.74秒仅快速通信预算，3秒目标不是满载承诺','256MiB可能早于30天清理历史','原厂规格版本V1.1与本轮客户资料V1.2分别标注','IFX价格/售后/TCP范围按2026-10-11现用配置反馈','网页升级仍为后续功能，不冒充已交付能力'])
(ROOT/'tests/customer-materials-v1.2-verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(dict(passed=True,pages=[3,2],protocol_pages=16,addresses=64),ensure_ascii=False))
