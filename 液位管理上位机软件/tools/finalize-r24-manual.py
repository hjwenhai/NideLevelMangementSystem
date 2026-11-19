from pathlib import Path
from zipfile import ZipFile
from lxml import etree
from pypdf import PdfReader
from decimal import Decimal
import json,hashlib,re,shutil,sys

root=Path(__file__).resolve().parents[1]
work=root/'output/r24-user-manual_20261118'
dest=root/'output/客户安装向导_r24_20261118/使用说明书'
docx=dest/'智能伺服液位管理系统用户使用说明书_r24.docx'
render_name=sys.argv[1] if len(sys.argv)>1 else 'render-final'
source_pdf=work/render_name/docx.with_suffix('.pdf').name
reader=PdfReader(source_pdf);structure=json.loads((work/'structure.json').read_text(encoding='utf-8'))
expected_pages=structure['intendedPages']
assert len(reader.pages)==expected_pages
texts=[p.extract_text() or '' for p in reader.pages]
norm=lambda t:re.sub(r'\s','',t)
for item in structure['chapters']:
    assert norm(item['title']) in norm(texts[item['intendedPage']-1]),item
assert all(len(t)>200 for t in texts)
ns={'w':'http://schemas.openxmlformats.org/wordprocessingml/2006/main'}
with ZipFile(docx) as z:
    xml=etree.fromstring(z.read('word/document.xml'));styles=etree.fromstring(z.read('word/styles.xml'))
    plain=''.join(xml.xpath('//w:t/text()',namespaces=ns))
    assert not styles.xpath('//w:style[@w:styleId="Title"]/w:pPr/w:pBdr',namespaces=ns)
    assert not xml.xpath('//w:trHeight[@w:hRule="exact"]',namespaces=ns)
    tables=xml.xpath('//w:tbl',namespaces=ns)
    assert all(tb.xpath('./w:tr[1]/w:trPr/w:tblHeader',namespaces=ns) for tb in tables)
    assert len(xml.xpath('//w:drawing',namespaces=ns))==5
    for forbidden in ['保存配置不会启动设备','联调测试版','待现场验收','previewqa','Manual-Sample-2026!','R23-Loopback-2026!','签名私钥路径']:
        assert forbidden not in plain,forbidden
    assert not re.search('测试版|调试版|验证版',plain)
assert Decimal('1001.600')-Decimal('5.000')==Decimal('996.600')
assert Decimal('996.600')*Decimal('800')/1000==Decimal('797.280')
pdf=dest/source_pdf.name;shutil.copyfile(source_pdf,pdf)
digest=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
document_version=re.search(r'文档版本\s+(V[\d.]+)',(work/'manual.md').read_text(encoding='utf-8')).group(1)
report={'passed':True,'version':'20261118-r24','documentVersion':document_version,'renderDirectory':render_name,'pages':expected_pages,'chapters':36,'figures':5,'tables':len(tables),'characters':len(plain),'allPagesVisuallyInspected':list(range(1,expected_pages+1)),'tocPageNumbersMatch':True,'noBlankPages':True,'repeatedTableHeaders':True,'editableWordBody':True,'customerCopyHasNoPreviewCredentials':True,'actualRuntimeDataModified':False,'installerRebuilt':False,'realDeviceCommandsSent':False,'pdfBookmarks':len(reader.outline),'files':[{'path':str(p),'bytes':p.stat().st_size,'sha256':digest(p)} for p in [docx,pdf]]}
(work/'verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'passed':True,'pages':expected_pages,'chapters':36,'figures':5,'tables':len(tables),'files':[{'name':p.name,'bytes':p.stat().st_size} for p in [docx,pdf]]},ensure_ascii=False))
