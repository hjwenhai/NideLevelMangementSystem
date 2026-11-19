from pathlib import Path
import re, json
from PIL import Image, ImageDraw, ImageFont
from docx import Document
from docx.shared import Inches, Pt, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH, WD_TAB_ALIGNMENT
from docx.enum.table import WD_TABLE_ALIGNMENT, WD_CELL_VERTICAL_ALIGNMENT
from docx.oxml import OxmlElement
from docx.oxml.ns import qn

ROOT=Path(__file__).resolve().parents[1]
WORK=ROOT/'output/r24-user-manual_20261118'
ASSETS=WORK/'assets'
DEST=ROOT/'output/客户安装向导_r24_20261118/使用说明书'
DEST.mkdir(parents=True,exist_ok=True)
TITLE='智能伺服液位管理系统用户使用说明书_r24'

def diagram():
    im=Image.new('RGB',(1500,690),'white'); d=ImageDraw.Draw(im)
    font=lambda n:ImageFont.truetype('C:/Windows/Fonts/msyh.ttc',n)
    def box(x,y,w,h,lines):
        d.rounded_rectangle((x,y,x+w,y+h),radius=15,fill='#F3F7F7',outline='#78969B',width=3)
        for i,s in enumerate(lines):d.text((x+w/2,y+22+i*45),s,font=font(31 if i==0 else 26),anchor='mt',fill='black')
    def arrow(a,b):
        import math
        d.line((a,b),fill='#365B63',width=4);t=math.atan2(b[1]-a[1],b[0]-a[0])
        d.polygon([b,(b[0]-17*math.cos(t-.45),b[1]-17*math.sin(t-.45)),(b[0]-17*math.cos(t+.45),b[1]-17*math.sin(t+.45))],fill='#365B63')
    box(30,30,355,115,['采集主机本地客户端','查看  查询  按权限操作'])
    box(515,30,470,115,['后台服务与业务数据','采集  计量  报警  记录'])
    box(1115,30,355,115,['其他电脑的客户端','连接采集主机IP和端口'])
    arrow((385,87),(515,87));arrow((1115,87),(985,87))
    d.text((750,177),'一个后台  多个独立通讯通道',font=font(28),anchor='mt',fill='black')
    d.line((750,145,750,168),fill='#365B63',width=4);d.line((750,218,750,240),fill='#365B63',width=4);d.line((280,240,1220,240),fill='#365B63',width=4)
    for x in [280,750,1220]:arrow((x,240),(x,300))
    box(65,300,430,130,['GT6757X网关TCP或RTU','按槽位号绑定仪表'])
    box(535,300,430,130,['直连RTU通道','电脑串口  实际仪表地址'])
    box(1005,300,430,130,['IFX L25通道','电脑串口  站号与表地址'])
    for x in [280,750,1220]:arrow((x,430),(x,495))
    box(65,495,430,125,['网关南向液位计','槽位对应COM口及真实地址'])
    box(535,495,430,125,['直连液位计','状态  故障  液位  温度'])
    box(1005,495,430,125,['IFX设备','按协议提供数据和操作'])
    d.text((750,650),'通讯连接和可用操作以设备能力及现场配置为准',font=font(25),anchor='mt',fill='#444444')
    im.save(ASSETS/'network.png')

def font_set(style,size,bold=False):
    style.font.name='Microsoft YaHei';style.font.size=Pt(size);style.font.bold=bold;style.font.italic=False;style.font.underline=False;style.font.color.rgb=RGBColor(0,0,0)
    for el in style.element.xpath('./w:pPr/w:pBdr'):el.getparent().remove(el)
    rp=style.element.get_or_add_rPr();rf=rp.find(qn('w:rFonts'))
    if rf is None:rf=OxmlElement('w:rFonts');rp.insert(0,rf)
    for k in ['ascii','hAnsi','eastAsia','cs']:rf.set(qn('w:'+k),'Microsoft YaHei')
    for k in ['asciiTheme','hAnsiTheme','eastAsiaTheme','cstheme']:
        if qn('w:'+k) in rf.attrib:del rf.attrib[qn('w:'+k)]

def inline(p,text):
    for s in re.split(r'(\*\*.*?\*\*|`[^`]+`)',text):
        if not s:continue
        r=p.add_run(s[2:-2] if s.startswith('**') else s[1:-1] if s.startswith('`') else s)
        if s.startswith('**'):r.bold=True
        if s.startswith('`'):
            r.font.name='Consolas';r.font.size=Pt(10)
            r._element.get_or_add_rPr().rFonts.set(qn('w:eastAsia'),'Microsoft YaHei')

def field(p,name):
    r=p.add_run();f=OxmlElement('w:fldSimple');f.set(qn('w:instr'),name);r._r.addnext(f)

def table(doc,rows):
    n=len(rows[0]);tb=doc.add_table(rows=1,cols=n);tb.alignment=WD_TABLE_ALIGNMENT.CENTER;tb.autofit=False
    if n==4:widths=[1.1,1.65,1.15,2.9]
    elif n==3:widths=[1.28,2.25,3.27]
    else:widths=[1.5,5.3]
    if rows[0][0]=='章节':widths=[1.05,4.65,1.1]
    if rows[0][0] in ['IFX故障位','FW故障位']:widths=[1.0,1.62,1.0,3.18]
    if rows[0][0]=='接入方式':widths=[1.35,1.15,4.3]
    if rows[0]==['协议','故障字','展开后的含义','表头码']:widths=[1.8,1.0,3.2,.8]
    if rows[0]==['参数','当前规则']:widths=[2.25,4.55]
    for c,w in zip(tb.columns,widths):c.width=Inches(w)
    for ri,row in enumerate(rows):
        cells=tb.rows[0].cells if ri==0 else tb.add_row().cells
        trpr=tb.rows[ri]._tr.get_or_add_trPr();cant=OxmlElement('w:cantSplit');trpr.append(cant)
        if ri==0:repeat=OxmlElement('w:tblHeader');repeat.set(qn('w:val'),'true');trpr.append(repeat)
        for ci,(cell,txt) in enumerate(zip(cells,row)):
            cell.width=Inches(widths[ci]);cell.vertical_alignment=WD_CELL_VERTICAL_ALIGNMENT.CENTER
            pr=cell._tc.get_or_add_tcPr();sh=OxmlElement('w:shd');sh.set(qn('w:fill'),'DCE8EB' if ri==0 else 'FFFFFF');pr.append(sh)
            mar=OxmlElement('w:tcMar')
            for side,val in [('top',85),('bottom',85),('left',95),('right',95)]:
                el=OxmlElement('w:'+side);el.set(qn('w:w'),str(val));el.set(qn('w:type'),'dxa');mar.append(el)
            pr.append(mar);bd=OxmlElement('w:tcBorders')
            for side in ['top','bottom','left','right']:
                el=OxmlElement('w:'+side);el.set(qn('w:val'),'single');el.set(qn('w:sz'),'5');el.set(qn('w:color'),'D9D9D9');bd.append(el)
            pr.append(bd)
            p=cell.paragraphs[0];p.paragraph_format.space_after=Pt(0);p.paragraph_format.space_before=Pt(0);p.paragraph_format.line_spacing=1.15
            if ci==0 or rows[0][ci] in ['页码','对应表头码','故障位','网关槽位','南向端口','液位计实际地址','平台应填槽位']:p.alignment=WD_ALIGN_PARAGRAPH.CENTER
            inline(p,txt)
            for r in p.runs:r.font.size=Pt(10.2);r.bold=ri==0
    aft=doc.add_paragraph();aft.paragraph_format.space_after=Pt(2);aft.paragraph_format.line_spacing=Pt(3);aft.add_run().font.size=Pt(3)

def build():
    diagram();doc=Document();s=doc.sections[0];s.page_width=Inches(8.5);s.page_height=Inches(11)
    s.top_margin=Inches(.7);s.bottom_margin=Inches(.65);s.left_margin=Inches(.85);s.right_margin=Inches(.85);s.header_distance=Inches(.28);s.footer_distance=Inches(.28)
    for name,sz,bold in [('Normal',11.3,False),('Title',29,True),('Subtitle',22,False),('Heading 1',17,True),('Heading 2',13,True),('Heading 3',11.5,True),('Caption',9.3,False)]:font_set(doc.styles[name],sz,bold)
    normal=doc.styles['Normal'].paragraph_format;normal.space_after=Pt(5);normal.line_spacing=1.22
    for name in ['Heading 1','Heading 2']:
        pf=doc.styles[name].paragraph_format;pf.keep_with_next=True;pf.space_before=Pt(10 if name=='Heading 2' else 0);pf.space_after=Pt(9);pf.line_spacing=1.15
    hp=s.header.paragraphs[0];hp.paragraph_format.tab_stops.add_tab_stop(Inches(6.8),WD_TAB_ALIGNMENT.RIGHT);hp.text='智能伺服液位管理系统  用户使用说明书\tr24'
    for r in hp.runs:r.font.name='Microsoft YaHei';r.font.size=Pt(8);r.font.color.rgb=RGBColor(0,0,0)
    fp=s.footer.paragraphs[0];fp.alignment=WD_ALIGN_PARAGRAPH.CENTER;fp.add_run('第 ');field(fp,'PAGE');fp.add_run(' 页  共 ');field(fp,'NUMPAGES');fp.add_run(' 页')
    for r in fp.runs:r.font.size=Pt(8.5)
    doc.core_properties.title='智能伺服液位管理系统用户使用说明书 r24';doc.core_properties.subject='客户安装配置操作与维护说明';doc.core_properties.author='耐德东京计装';doc.core_properties.keywords='r24 用户说明书 安装 通讯 计量 操作 备份'
    blocks=(WORK/'manual.md').read_text(encoding='utf-8').split('<!-- page -->')
    headings=[]
    for bi,block in enumerate(blocks):
        lines=block.strip().splitlines();i=0
        while i<len(lines):
            line=lines[i].strip();i+=1
            if not line:continue
            if line.startswith('|'):
                rows=[]
                while True:
                    if not re.match(r'^\|[\s|:\-]+\|$',line):rows.append([x.strip() for x in line.strip('|').split('|')])
                    if i>=len(lines) or not lines[i].strip().startswith('|'):break
                    line=lines[i].strip();i+=1
                table(doc,rows);continue
            if line.startswith('!['):
                match=re.match(r'!\[(.*?)\]\((.*?)\)',line);name=match.group(2)
                width={'network.png':6.8,'overview.png':6.15,'detail.png':6.8,'detail-metering.png':6.8,'history.png':6.15,'history-query.png':6.8,'overview-card.png':1.5}[name]
                p=doc.add_paragraph();p.alignment=WD_ALIGN_PARAGRAPH.CENTER;p.paragraph_format.space_after=Pt(3);p.paragraph_format.keep_with_next=True;p.add_run().add_picture(str(ASSETS/name),width=Inches(width));continue
            if line.startswith('# '):
                title=line[2:];headings.append({'intendedPage':bi+1,'title':title})
                p=doc.add_paragraph(title,'Title' if bi==0 else 'Heading 1')
                if bi:p.paragraph_format.page_break_before=True
                if bi==0:p.paragraph_format.space_before=Pt(78);p.paragraph_format.space_after=Pt(20)
                continue
            if line.startswith('## '):
                p=doc.add_paragraph(line[3:],'Subtitle' if bi==0 else 'Heading 2')
                if bi==0:p.paragraph_format.space_after=Pt(42)
                continue
            p=doc.add_paragraph()
            if re.match(r'^\d+\. ',line):
                p.paragraph_format.left_indent=Pt(15);p.paragraph_format.first_line_indent=Pt(-15);p.paragraph_format.space_after=Pt(5)
            if line.startswith('图'):
                p.style='Caption';p.paragraph_format.space_after=Pt(8)
            if bi==0 and re.match(r'适用版本|软件标识|文档版本|编制日期',line):p.paragraph_format.space_after=Pt(10)
            inline(p,line)
    out=DEST/(TITLE+'.docx');doc.save(out)
    (WORK/'structure.json').write_text(json.dumps({'chapters':headings,'intendedPages':len(blocks),'docx':str(out),'characters':len((WORK/'manual.md').read_text(encoding='utf-8'))},ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'docx':str(out),'intendedPages':len(blocks)},ensure_ascii=False))

if __name__=='__main__':build()
