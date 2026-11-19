from pathlib import Path
from pypdf import PdfReader
from PIL import Image,ImageDraw,ImageFont
import json,re,sys
root=Path(__file__).resolve().parents[1]/'output/r24-user-manual_20261118'
render=root/(sys.argv[1] if len(sys.argv)>1 else 'render')
pdf=next(render.glob('*.pdf'));reader=PdfReader(pdf)
structure=json.loads((root/'structure.json').read_text(encoding='utf-8'))
info=[]
for n,p in enumerate(reader.pages,1):
    text=p.extract_text() or ''; compact=re.sub(r'\s','',text)
    titles=[a['title'] for a in structure['chapters'] if re.sub(r'\s','',a['title']) in compact]
    # Matching the chapter's numbered title is separate from matching references in the table of contents.
    info.append({'page':n,'characters':len(text),'titles':titles,'text':text})
audit={'pageCount':len(info),'pages':info,'headings':structure['chapters']}
(root/'layout-audit.json').write_text(json.dumps(audit,ensure_ascii=False,indent=2),encoding='utf-8')
font=ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',18)
files=sorted(render.glob('page-*.png'),key=lambda f:int(re.search(r'page-(\d+)',f.name)[1]))
for start in range(0,len(files),6):
    canvas=Image.new('RGB',(1260,1120),'#dedede');d=ImageDraw.Draw(canvas)
    for j,f in enumerate(files[start:start+6]):
        im=Image.open(f);im.thumbnail((400,515));x=10+(j%3)*420;y=32+(j//3)*555;canvas.paste(im,(x,y));d.text((x, y-26),f'第{start+j+1}页',font=font,fill='black')
    canvas.save(root/f'contact-{start//6+1}.png')
print(json.dumps({'pages':len(info),'layout':[{'p':p['page'],'chars':p['characters'],'title':p['titles']} for p in info]},ensure_ascii=False))
