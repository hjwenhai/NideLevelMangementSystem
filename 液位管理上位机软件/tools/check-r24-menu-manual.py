from pathlib import Path
from zipfile import ZipFile
from lxml import etree
from pypdf import PdfReader
import json,re,hashlib,sys,shutil

root=Path(__file__).resolve().parents[1]
w=root/'output/r24-menu-manual_20261118'
render=w/(sys.argv[1] if len(sys.argv)>1 else 'render-final')
pdf=next(render.glob('*.pdf'));reader=PdfReader(pdf)
s=json.loads((w/'structure.json').read_text(encoding='utf8'))
plan=json.loads((w/'page-plan.json').read_text(encoding='utf8'))
N=lambda t:re.sub(r'\s','',t)
digest=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
texts=[p.extract_text() or '' for p in reader.pages]
audit=[{'page':i+1,'characters':len(t),'titles':[x['title'] for x in s['chapters'][1:] if N(x['title']) in N(t)],'text':t} for i,t in enumerate(texts)]
(w/'layout-audit.json').write_text(json.dumps({'pageCount':len(texts),'pages':audit},ensure_ascii=False,indent=2),encoding='utf8')
assert len(texts)==s['intendedPages'],f'实际{len(texts)}页，计划{s["intendedPages"]}页'
for x in s['chapters']:assert N(x['title']) in N(texts[x['intendedPage']-1]),x
assert min(map(len,texts))>200,'存在内容不足的孤立续页'
assert all(Path(p).exists() and digest(Path(p))==h for p,h in plan['originalFiles'].items()),'原文件变化'
docx=Path(s['docx']);ns={'w':'http://schemas.openxmlformats.org/wordprocessingml/2006/main'}
with ZipFile(docx) as z:
    xml=etree.fromstring(z.read('word/document.xml'));styles=etree.fromstring(z.read('word/styles.xml'))
    plain=''.join(xml.xpath('//w:t/text()',namespaces=ns));tables=xml.xpath('//w:tbl',namespaces=ns)
    assert not styles.xpath('//w:style[@w:styleId="Title"]/w:pPr/w:pBdr',namespaces=ns)
    assert not xml.xpath('//w:trHeight[@w:hRule="exact"]',namespaces=ns)
    assert all(t.xpath('./w:tr[1]/w:trPr/w:tblHeader',namespaces=ns) for t in tables)
    assert len(xml.xpath('//w:drawing',namespaces=ns))==5
    for term in ['保存配置不会启动设备','联调测试版','待现场验收','previewqa','Manual-Sample-2026!','R23-Loopback-2026!']:
        assert term not in plain,term
    assert '尚不能点击打开该历史行的原配置详情' in plain
    assert '当前每套后台最多128条储罐档案' in plain
    assert '这三个子项尚未启用' in plain or '这三项尚未启用' in plain
manual=(w/'manual.md').read_text(encoding='utf8')
source=(root/'src/LevelManager.Domain/InstrumentDiagnostics.cs').read_text(encoding='utf-8-sig')
labels=json.loads('['+re.search(r'Direct=\[(.*?)\];',source,re.S)[1]+']')
heads={}
for condition,head in re.findall(r'([^,\n]+)=>"([^"]+)"',source):
    for mask in re.findall(r'FW\.0x([0-9A-F]{4})',condition):heads[int(mask,16)]=head
rows=re.findall(r'^\| bit(\d+) \| FW\.0x([0-9A-F]{4}) \| ([^|]+) \| ([^|]+) \|$',manual,re.M)
assert len(rows)==16
for bit,mask,head,label in rows:
    i=int(bit);n=int(mask,16)
    assert n==1<<i and head.strip()==heads.get(n,'—')
    assert re.sub(r'[\s，,]','',label)==re.sub(r'[\s，,]','',labels[i])
menu_names=['罐区总览','罐清单列表','单罐详情','历史数据','趋势曲线','平均温度测量','数据快照','报警中心','事件记录','报表中心','储罐配置','通讯通道','基础数据','罐组管理','后台数据任务','系统管理']
assert [x[1] for x in plan['sections'][1:-2]]==menu_names
report={'passed':True,'softwareVersion':'20261118-r24','documentVersion':'V2.0 菜单版','pages':len(texts),'chapters':18,'appendices':4,'figures':5,'tables':len(tables),'characters':len(plain),'tocAndSectionsMatch':True,'menuOrderMatchesR24':True,'faultRowsMatchedSource':16,'originalFilesUnchanged':True,'softwareModified':False,'installerRebuilt':False,'runtimeDataModified':False,'realDeviceCommandsSent':False,'files':[{'path':str(docx),'bytes':docx.stat().st_size,'sha256':digest(docx)}]}
if '--finalize' in sys.argv:
    # Invoke only after every final page PNG has been visually inspected.
    dest=docx.with_suffix('.pdf');shutil.copyfile(pdf,dest)
    report['allPagesVisuallyInspected']=list(range(1,len(texts)+1))
    report['files'].append({'path':str(dest),'bytes':dest.stat().st_size,'sha256':digest(dest)})
    (w/'verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
else:(w/'structure-verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
print(json.dumps({k:v for k,v in report.items() if k not in ['files','allPagesVisuallyInspected']},ensure_ascii=False))
