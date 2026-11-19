"""Check the appended gateway chapter while retaining the original implementation."""
from pathlib import Path
import hashlib
import json
import zipfile
from docx import Document
from docx.oxml.ns import qn
from lxml import etree
from PIL import Image, ImageChops

ROOT=Path(__file__).resolve().parents[1]
TMP=ROOT/'tmp/gateway-average-host-20261012'
manifest=json.loads((TMP/'preservation.json').read_text(encoding='utf-8'))
source=Path(manifest['source']); final=ROOT/'archive/c01/20261014_before_confirmation_update/implementation/无线温度浮子平均温度测量实施过程与流程图_V1.3_含GT6757X网关_20261012.docx'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
assert sha(source)==manifest['source_sha256']
a,b=Document(source),Document(final)
assert len(a.sections)==len(b.sections)==1
assert etree.tostring(a.sections[0]._sectPr)==etree.tostring(b.sections[0]._sectPr)
editable_texts={item['old'].replace('\n','').replace('\t','') for item in manifest['edited_source_slots']}
preserved_body=0
for x,y in zip(a.element.body,b.element.body):
    if x.tag==qn('w:sectPr'): break
    if ''.join(x.xpath('.//w:t/text()')) in editable_texts: continue
    assert etree.tostring(x,method='c14n',exclusive=True)==etree.tostring(y,method='c14n',exclusive=True)
    preserved_body+=1
assert len(a.tables)==6 and len(b.tables)==9
for x,y in zip(a.tables,b.tables):
    assert etree.tostring(x._tbl,method='c14n',exclusive=True)==etree.tostring(y._tbl,method='c14n',exclusive=True)
with zipfile.ZipFile(source) as za,zipfile.ZipFile(final) as zb:
    allowed={'word/document.xml','word/_rels/document.xml.rels','[Content_Types].xml','word/settings.xml'}
    untouched=[n for n in za.namelist() if n not in allowed]
    assert all(za.read(n)==zb.read(n) for n in untouched)
    assert all(za.read(n)==zb.read(n) for n in za.namelist() if n.startswith('word/media/'))
    assert 'word/media/image3.png' in zb.namelist()
    settings=etree.fromstring(zb.read('word/settings.xml'))
    assert settings.find(qn('w:updateFields')).get(qn('w:val'))=='true'

text='\n'.join(p.text for p in b.paragraphs)+'\n'+'\n'.join(c.text for t in b.tables for r in t.rows for c in r.cells)
pages=json.loads((TMP/'content.json').read_text(encoding='utf-8'))
for page in pages:
    for block in page:
        if block[0] in ('p','h'): assert block[1] in text,block[1]
        elif block[0]=='step': assert block[1] in text and block[2] in text
        elif block[0]=='table': assert all(s in text for s in block[1]+[x for row in block[2] for x in row])
assert '每罐每次完整任务仅一次' in text
assert 'FC04 I6，int16，0.01℃' in text and 'I26' in text
assert '合计60分钟' in text and '当前软件0.3.0' in text and '接口待适配' in text
assert '平均测温由上位机完成' in text
assert '不新增平均测温任务寄存器区' in text
assert '浮子速度2至5mm/s' in text and '3秒内移动约6至15mm' in text
assert 'D=3000+144×(n－1)' in text and 'D+6：原始I6' in text
assert '不能保证停留结束后10秒内完成新读' in text
assert '任务不由网关接管' in text
for obsolete in ['任务开始后不依赖上位机持续在线','网关负责认证、模式切换','建议在后续北向协议中新增独立','须增加任务状态机']:
    assert obsolete not in text,obsolete

render=TMP/'render-r1'
assert len(list(render.glob('page-*.png')))==16
assert all(Image.open(render/f'page-{i}.png').size[0]>800 for i in range(1,17))

protocol=json.loads((ROOT/'tests/v3-pdf-pagination.json').read_text(encoding='utf-8-sig'))
assert sha(ROOT/'docs/12_客户通讯协议V3.md')==protocol['source_sha256']
assert sha(ROOT/'output/pdf/GT6757S_客户通讯协议_V3.0-D2.pdf')==protocol['pdf_sha256']
pk=json.loads((ROOT/'tests/v3-package-verification.json').read_text(encoding='utf-8-sig'))
for item in pk['packages']+[pk['customer']]:
    candidates=list((ROOT/'output').rglob(item['archive']))
    assert len(candidates)==1 and sha(candidates[0])==item['sha256']
report={**manifest,'passed':True,'date':'2026-10-12','output_sha256':sha(final),
        'pages':16,'original_pages':10,'original_body_items_preserved':preserved_body,
        'original_tables_preserved':6,'original_figures_preserved':2,'original_equations_preserved':True,
        'all_appended_text_checked':True,'visual_review':'render-r1共16页逐页检查，正文、表格、三张图及页码无裁切或溢出',
        'source_docx_unchanged':True,
        'existing_d2_protocol_and_runtime_archives_unchanged':True,
        'authoring_notes':'V1.3职责修订，第12至17节及流程图改为上位机执行；源文件四处说明同步；只交付Word',
        'rule_priority':'上位机执行完整任务；V1.2实际P和I6规则保留；一次认证和新读I6待适配',
        'gateway_task_area_removed':True,'fast_target_seconds':3,'float_speed_mm_s':[2,5]}
(ROOT/'tests/gateway-average-document-v1.3-host-verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'passed':True,'pages':16,'source_unchanged':True,'tables_preserved':6,'figures_preserved':2,'runtime_unchanged':True},ensure_ascii=False))
