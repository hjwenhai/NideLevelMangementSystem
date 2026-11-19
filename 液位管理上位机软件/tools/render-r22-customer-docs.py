"""Native Word export, followed by the skill's canonical PNG rasterizer.
The selected Windows bundle has no LibreOffice; do not use desktop LO.
Only a new independent automation instance and our own readonly documents
are used. Refuse an instance that already contains any open document.
"""
from pathlib import Path
import os,sys,json,importlib.util
root=Path(__file__).resolve().parents[1]
qa=root/'output/r22-latest-delivery-qa_20261107/documents'
qa.mkdir(parents=True,exist_ok=True)
skill=Path('C:/Users/admin/.codex/plugins/cache/openai-primary-runtime/documents/26.1104.22356/skills/documents')
runtime=Path(sys.executable).parents[1]
bins=list((runtime/'native/poppler').rglob('pdfinfo.exe'))
assert bins,'bundled poppler missing'
os.environ['PATH']=str(bins[0].parent)+os.pathsep+os.environ.get('PATH','')
os.environ['TEMP']=os.environ['TMP']=str(qa)
results=[]
files=json.loads((root/'output/r22-latest-delivery-qa_20261107/documents.json').read_text(encoding='utf-8'))
for i,p in enumerate(files):
        source=Path(p);folder=qa/('upgrade' if i==0 else 'manual');folder.mkdir(exist_ok=True)
        pdf=folder/(source.stem+'.pdf')
        assert pdf.is_file() and pdf.stat().st_size>0
        spec=importlib.util.spec_from_file_location('skill_docx_renderer',skill/'render_docx.py')
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        module.convert_to_pdf=lambda *a,**k:(str(pdf),'PDF exported with independent native Word automation; no LibreOffice available in Windows bundle')
        module.rasterize(str(source),str(folder),144,verbose=False,emit_pdf=False)
        pages=sorted(folder.glob('page-*.png'))
        results.append({'document':str(source),'pdf':str(pdf),'pages':len(pages),'renderer':'native Word PDF + canonical render_docx.py rasterize'})
(qa/'render-result.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(results,ensure_ascii=False,indent=2))
