from pathlib import Path
import json, zipfile, hashlib
import openpyxl
import xml.etree.ElementTree as ET
root = Path(__file__).resolve().parents[2]
source = root / '客户确认/20261103问题反馈.xlsx'
out = root / '液位管理上位机软件/output/feedback-review-20261106'
out.mkdir(parents=True, exist_ok=True)
wb = openpyxl.load_workbook(source, data_only=False)
data = {'source': str(source), 'sha256': hashlib.sha256(source.read_bytes()).hexdigest(), 'sheets': []}
for sheet in wb:
    rows = []
    for row in sheet:
        cells = [{'cell': c.coordinate, 'value': str(c.value), 'comment': c.comment.text if c.comment else None} for c in row if c.value is not None or c.comment]
        if cells: rows.append(cells)
    data['sheets'].append({'name': sheet.title, 'state': sheet.sheet_state, 'merged': [str(r) for r in sheet.merged_cells.ranges], 'rows': rows})
with zipfile.ZipFile(source) as z:
    for name in z.namelist():
        if name.startswith('xl/media/'):
            (out / Path(name).name).write_bytes(z.read(name))
        if name.startswith('xl/drawings/') and name.endswith('.xml'):
            (out / Path(name).name).write_bytes(z.read(name))
(out / 'workbook-content.json').write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(data, ensure_ascii=False, indent=2))
print('MEDIA:', [p.name for p in out.iterdir() if p.suffix.lower() in ('.png','.jpg','.jpeg')])
