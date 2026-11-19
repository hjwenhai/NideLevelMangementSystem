from pathlib import Path
import html, re, json, hashlib
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.lib import colors
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, PageBreak, LongTable, TableStyle
from pypdf import PdfReader

root=Path(__file__).resolve().parents[1]
source=root/'docs/12_客户通讯协议V3.md'
out=root/'output/pdf/GT6757S_客户通讯协议_V3.2-D2_20261117-r1.pdf'
out.parent.mkdir(parents=True,exist_ok=True)
pdfmetrics.registerFont(TTFont('CN','C:/Windows/Fonts/simhei.ttf'))
color=colors.HexColor('#173b57')
styles={
 'body':ParagraphStyle('body',fontName='CN',fontSize=10,leading=14.4,wordWrap='CJK',spaceAfter=6),
 'title':ParagraphStyle('title',fontName='CN',fontSize=19,leading=25,textColor=color,spaceAfter=9,keepWithNext=True),
 'h2':ParagraphStyle('h2',fontName='CN',fontSize=13.2,leading=19,textColor=color,spaceBefore=8,spaceAfter=7,keepWithNext=True),
 'h3':ParagraphStyle('h3',fontName='CN',fontSize=11.2,leading=16,textColor=color,spaceBefore=6,spaceAfter=5,keepWithNext=True),
 'cell':ParagraphStyle('cell',fontName='CN',fontSize=9.2,leading=12.7,wordWrap='CJK'),
 'head':ParagraphStyle('head',fontName='CN',fontSize=9.3,leading=12.8,wordWrap='CJK',textColor=colors.white),
}
def markup(t):
 t=t.replace('−','-').replace('³','3').replace('≥','>=').replace('～','~')
 t=html.escape(t)
 t=re.sub(r'\*\*(.+?)\*\*',r'<b>\1</b>',t)
 t=re.sub(r'`([^`]+)`',r'<font color="#234961">\1</font>',t)
 t=re.sub(r'\[([^\]]+)\]\([^)]+\)',r'\1',t)
 return t
width=A4[0]-80
lines=source.read_text(encoding='utf-8').splitlines()
story=[];i=0
while i<len(lines):
 line=lines[i].strip()
 if not line:i+=1;continue
 if line=='<!-- PAGEBREAK -->':story.append(PageBreak());i+=1;continue
 if line.startswith('|'):
  rows=[]
  while i<len(lines) and lines[i].strip().startswith('|'):
   cells=[x.strip() for x in lines[i].strip().split('|')[1:-1]]
   if not all(re.fullmatch(r':?-+:?',x) for x in cells):rows.append(cells)
   i+=1
  cols=len(rows[0]);assert all(len(row)==cols for row in rows)
  if cols==3:fractions=[.20,.25,.55]
  elif cols==4:fractions=[.14,.18,.13,.55]
  elif cols==5:fractions=[.19,.16,.13,.14,.38]
  elif cols==6:fractions=[.17,.16,.08,.08,.20,.31]
  elif cols==7:fractions=[.10,.15,.15,.15,.15,.15,.15]
  elif cols==8:fractions=[.075,.14,.12,.065,.18,.13,.15,.14]
  else:fractions=[1/cols]*cols
  if any('请求字节' in h for h in rows[0]):fractions=[.16,.58,.26]
  data=[[Paragraph(markup(x),styles['head' if n==0 else 'cell']) for x in row] for n,row in enumerate(rows)]
  table=LongTable(data,colWidths=[width*f for f in fractions],repeatRows=1,hAlign='LEFT')
  table.setStyle(TableStyle([
    ('BACKGROUND',(0,0),(-1,0),color),('ROWBACKGROUNDS',(0,1),(-1,-1),[colors.white,colors.HexColor('#f1f5f7')]),
    ('GRID',(0,0),(-1,-1),.35,colors.HexColor('#ccd7df')),('VALIGN',(0,0),(-1,-1),'TOP'),
    ('LEFTPADDING',(0,0),(-1,-1),5),('RIGHTPADDING',(0,0),(-1,-1),5),('TOPPADDING',(0,0),(-1,-1),5),('BOTTOMPADDING',(0,0),(-1,-1),5)]))
  story += [table,Spacer(1,7)];continue
 if line.startswith('# '):kind='title';line=line[2:]
 elif line.startswith('## '):kind='h2';line=line[3:]
 elif line.startswith('### '):kind='h3';line=line[4:]
 else:kind='body'
 story.append(Paragraph(markup(line),styles[kind]));i+=1
def footer(c,doc):
 c.saveState();c.setStrokeColor(colors.HexColor('#ccd7df'));c.line(40,34,A4[0]-40,34)
 c.setFont('CN',8);c.setFillColor(colors.HexColor('#526675'))
 c.drawString(40,22,'GT6757S  |  V3.2-D2  |  软件0.8.3')
 c.drawRightString(A4[0]-40,22,f'{doc.page}');c.restoreState()
doc=SimpleDocTemplate(str(out),pagesize=A4,leftMargin=40,rightMargin=40,topMargin=35,bottomMargin=46,
 title='GT6757S客户通讯协议 V3.2-D2（软件实施版）',author='液位采集网关项目')
doc.build(story,onFirstPage=footer,onLaterPages=footer)
r=PdfReader(out)
result={'pages':len(r.pages),'first_page_title':'快速接入·第1页' in r.pages[0].extract_text(),'second_page_title':'快速接入·第2页' in r.pages[1].extract_text(),'third_page_details':'详细说明' in r.pages[2].extract_text(),'output':str(out),'source_sha256':hashlib.sha256(source.read_bytes()).hexdigest(),'pdf_sha256':hashlib.sha256(out.read_bytes()).hexdigest(),'render_substitutions':{'−':'-','³':'3','≥':'>=','～':'~'}}
(root/'tests/customer-protocol-pdf-20261117.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(result,ensure_ascii=False))
assert all(result[k] for k in ['first_page_title','second_page_title','third_page_details']), 'Quick-start overflow; revise layout/content'
