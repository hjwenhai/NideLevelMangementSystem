"""Use the packaged DOCX renderer with explicit Windows tool paths."""
import os
from pathlib import Path
import runpy
import sys

runtime = Path.home() / '.cache/codex-runtimes/codex-primary-runtime'
poppler = runtime / 'dependencies/native/poppler/Library/bin'
lo = Path('C:/Program Files/LibreOffice/program')
if not (lo / 'soffice.exe').is_file():
    raise FileNotFoundError('Windows native LibreOffice is required by render_docx.py')
os.environ['PATH'] = os.pathsep.join([str(lo), str(poppler), os.environ.get('PATH', '')])
renderer = Path.home() / '.codex/plugins/cache/openai-primary-runtime/documents/26.1009.11318/skills/documents/render_docx.py'
sys.argv[0] = str(renderer)
runpy.run_path(str(renderer), run_name='__main__')
