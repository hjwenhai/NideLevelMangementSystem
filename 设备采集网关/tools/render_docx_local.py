"""Use the bundled skill DOCX renderer; PDFium replaces unavailable Poppler raster tools."""
from pathlib import Path
import importlib.util,os,sys
import pypdfium2 as pdfium

skill=Path.home()/'.codex/plugins/cache/openai-primary-runtime/documents/26.1017.11052/skills/documents/render_docx.py'
os.environ['PATH']='C:/Program Files/LibreOffice/program'+os.pathsep+os.environ.get('PATH','')
spec=importlib.util.spec_from_file_location('skill_renderer',skill)
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
def raster(path,dpi=150,output_folder=None,**kwargs):
 doc=pdfium.PdfDocument(path);paths=[]
 for i in range(len(doc)):
  page=doc[i];bitmap=page.render(scale=dpi/72);out=Path(output_folder)/f'pdfium-{i+1}.png';bitmap.to_pil().save(out);paths.append(str(out));bitmap.close();page.close()
 doc.close();return paths
module.convert_from_path=raster
module.main()
