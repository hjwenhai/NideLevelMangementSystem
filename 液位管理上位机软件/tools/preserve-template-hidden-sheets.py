"""Preserve native sheet visibility omitted by the artifact-tool XLSX export.

Cell values and formatting are authored with artifact-tool. Only the unsupported
workbook sheet-state attribute is patched; all other ZIP entries stay byte-identical.
"""
from pathlib import Path
import re,zipfile,json,hashlib,xml.etree.ElementTree as E

root=Path(__file__).resolve().parents[1]
qa=root/'output/r22-template-import-qa_20261107'
template=qa/'最新r22下载的空白模板.xlsx'
source=root/'outputs/r22-template-import_20261107/储罐导入模板_已填写测试样例.xlsx'
target=source.with_name(source.stem+'_修正版.xlsx')
N={'s':'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
assert not target.exists()
with zipfile.ZipFile(template) as z:
    original=E.fromstring(z.read('xl/workbook.xml'))
    states={s.attrib['name']:s.attrib.get('state','visible') for s in original.findall('s:sheets/s:sheet',N)}
with zipfile.ZipFile(source) as zin:
    xml=zin.read('xl/workbook.xml').decode('utf-8')
    for name,state in states.items():
        if state=='visible':continue
        pattern=r'(<(?:[A-Za-z_][\w.-]*:)?sheet\b[^>]*\bname="'+re.escape(name)+r'"[^>]*)(/>)'
        def change(m):
            attributes=re.sub(r'\sstate="[^"]*"','',m.group(1))
            return attributes+' state="'+state+'"'+m.group(2)
        xml,count=re.subn(pattern,change,xml)
        assert count==1,(name,count)
    parsed=E.fromstring(xml)
    sheets=parsed.findall('s:sheets/s:sheet',N)
    assert {s.attrib['name']:s.attrib.get('state','visible') for s in sheets}==states
    for view in parsed.findall('s:bookViews/s:workbookView',N):
        assert sheets[int(view.attrib.get('activeTab','0'))].attrib.get('state','visible')=='visible'
    with zipfile.ZipFile(target,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as zout:
        for item in zin.infolist():zout.writestr(item,xml.encode('utf-8') if item.filename=='xl/workbook.xml' else zin.read(item.filename))
with zipfile.ZipFile(source) as a,zipfile.ZipFile(target) as b:
    assert a.namelist()==b.namelist()
    assert b.testzip() is None
    changed=[n for n in a.namelist() if a.read(n)!=b.read(n)]
    assert changed==['xl/workbook.xml']
report={'passed':True,'file':str(target),'worksheetStates':states,'changedZipParts':changed,'allCellsStylesValidationsUnchanged':True,'sha256':hashlib.sha256(target.read_bytes()).hexdigest().upper(),'artifactToolVisibilityApiAvailable':False,'fallback':'Preserved native workbook sheet-state metadata only; two bounded public API lookups found no supported visibility setter'}
(qa/'visibility-fix.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False,indent=2))
