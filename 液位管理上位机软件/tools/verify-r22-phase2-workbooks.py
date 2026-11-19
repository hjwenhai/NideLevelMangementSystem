from pathlib import Path
from zipfile import ZipFile
import xml.etree.ElementTree as E
import json

root = Path(__file__).resolve().parents[1]
out = root / 'output/r22-phase2-20261107'
ui = out / 'integration-1794036710852'
transfer = out / 'transfer/roundtrip-1794036477022'
ns = {'s':'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
files = list(ui.glob('*.xlsx')) + list(transfer.glob('*.xlsx'))
results=[]
for file in files:
    with ZipFile(file) as z:
        styles=E.fromstring(z.read('xl/styles.xml'))
        borders=styles.find('s:borders',ns)
        formats=styles.find('s:cellXfs',ns)
        valid=set()
        for i,b in enumerate(borders):
            if all((edge:=b.find('s:'+side,ns)) is not None and edge.get('style')=='thin' and edge.find('s:color',ns).get('rgb')=='FFD9E1E6' for side in ('left','right','top','bottom')):
                valid.add(i)
        assert valid, str(file)
        checked=0
        for sheet in [n for n in z.namelist() if n.startswith('xl/worksheets/sheet') and n.endswith('.xml')]:
            for cell in E.fromstring(z.read(sheet)).findall('.//s:sheetData/s:row/s:c',ns):
                fmt=formats[int(cell.get('s','0'))]
                assert int(fmt.get('borderId','0')) in valid,(str(file),sheet,cell.get('r'))
                checked+=1
        results.append({'file':str(file.relative_to(root)), 'borderedCells':checked})
        if file.parent==ui and file.name.startswith('capacity-'):
            with ZipFile(root/'assets/capacity-templates'/file.name) as original:
                for name in original.namelist():
                    if name.startswith('xl/worksheets/') and name.endswith('.xml'):
                        assert E.tostring(E.fromstring(original.read(name)))==E.tostring(E.fromstring(z.read(name))), (file.name,name,'template content changed')
                results[-1]['templateSheetsUnchanged']=True
assert len(results)==10,len(results)
(out/'workbook-verification.json').write_text(json.dumps({'passed':True,'checks':results},ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'passed':True,'files':len(results),'borderedCells':sum(r['borderedCells'] for r in results),'templatesUnchanged':3}))
