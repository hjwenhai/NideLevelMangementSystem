"""Build the customer specification and comparison from one maintained content source."""
from pathlib import Path
import json
import argparse
from docx import Document
from docx.shared import Inches, Pt, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.enum.table import WD_TABLE_ALIGNMENT, WD_CELL_VERTICAL_ALIGNMENT
from docx.oxml import OxmlElement
from docx.oxml.ns import qn

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'output' / 'customer-materials'
OUT.mkdir(parents=True, exist_ok=True)

def p(text): return {'type': 'p', 'text': text}
def h(text): return {'type': 'h', 'text': text}
def t(headers, rows, widths): return {'type': 'table', 'headers': headers, 'rows': rows, 'widths': widths}

SPEC = {
    'title': 'GT6757X液位采集网关规格书',
    'file': 'GT6757X_液位采集网关规格书_V1.5',
    'source': '18_GT6757S液位采集网关客户规格书.md',
    'subtitle': '客户资料 V1.5  2026年11月8日',
    'pages': [
        [
            p('GT6757X液位采集网关用于将FW-9000系列液位计接入PLC、DCS或上位机，采集程序、客户点表和维护页面由我们开发和维护。当前采用GT6757S硬件，配备512MB内存。客户按Modbus协议读取网关缓存，无需逐台与液位计通信。具体仪表型号及RS485选件由厂家核对。'),
            h('设备用途'),
            p('网关提供液位、平均温度、当前位置密度、平均密度、液位变化率、仪表状态和故障数据。每项数据带有效性标识，另提供采集时间与数据年龄，便于上位机区分正常数据、过期数据和通信异常。'),
            p('客户连接业务网口即可读取数据。厂家通过另一网口连接电脑，用浏览器查看当前值、配置采集参数和导出历史记录。设备不配置显示屏，现场维护无需另装专用显示面板。'),
            h('接入容量'),
            t(['交付配置', '南向液位计连接', '客户接入方式'], [
                ['最多32台', '2路RS485，每路最多16台\n其余口按项目配置', 'Modbus TCP\n可配置北向RS485 RTU'],
                ['最多48台', '3路RS485，每路最多16台', 'Modbus TCP\n余下1路可配置北向RTU'],
                ['最多64台', '4路RS485，每路最多16台', 'Modbus TCP'],
            ], [1.02, 2.85, 2.93]),
            p('COM1～COM4由厂家配置为仪表采集口、上位机RTU口或停用口。一个端口只承担一种角色；配置北向RTU后，可用南向采集口相应减少。'),
            h('客户通信接口'),
            t(['项目', '规格'], [
                ['液位计接口', 'RS485 Modbus RTU Master；南向默认4800bps、8E1'],
                ['网口', 'Modbus TCP Server；默认端口502、Unit ID为1'],
                ['RS485接口', 'Modbus RTU Slave；默认站号1、38400bps、8E1'],
                ['数据读取', 'FC03或FC04；TCP与RTU使用同一套点表'],
                ['客户控制', '按授权开放；三寄存器控制，另设独立停止入口'],
                ['远程液位修改', '支持按现场核实值修正仪表液位偏移；上位机或网页操作，回读核对，异常不自动重写'],
                ['通信协议', 'GT6757S客户通讯协议V3.2-D2'],
            ], [1.25, 5.55]),
        ],
        [
            h('硬件规格'),
            t(['项目', '参数'], [
                ['硬件平台', 'GT6757S工业嵌入式计算机'],
                ['处理器', '双核Cortex-A35，主频800MHz'],
                ['内存与存储', '512MB DDR3；板载8GB MLC eMMC'],
                ['以太网', '2路10/100Mbps自适应网口'],
                ['串行接口', '4路独立电气隔离RS485；COM3、COM4兼有RS232端子，与对应RS485复用'],
                ['辅助接口', '1路USB HOST、1个TF卡槽、1路Type-C Console调试接口'],
                ['输入电源', 'DC 9～48V或AC 12～30V；项目建议采用DC 24V供电'],
                ['功耗', '硬件原厂标称整机最大平均功耗不大于5W'],
                ['外形尺寸', '128.8 × 117 × 28mm，含挂耳'],
                ['重量', '约450g'],
                ['安装方式', '挂耳固定；导轨卡扣为选配件'],
                ['硬件工作温度', '原厂标称－40～85℃'],
                ['硬件工作湿度', '20%～90% RH，无凝露'],
                ['时钟', '内置RTC及后备电池；采集时间使用前由厂家校准并确认'],
                ['接口浪涌防护', '原厂标称：RS485为4kV，网口为2kV，电源输入为4kV；标准GB/T 17626.5-2019'],
            ], [1.35, 5.45]),
            p('RS485和网口标称采用10/700μs－5/320μs组合波，电源采用1.2/50μs－8/20μs组合波。数值为原厂浪涌防护参数，不是耐压或直击雷等级；现场按工程要求接地并配置外部防雷器。'),
            p('硬件参数依据GT-230710-V1.1。8GB包含系统占用；环境与防护为硬件标称值，完整系统运行条件以项目验收为准。'),
            h('状态指示'),
            p('PWR为电源指示。SYS1表示程序心跳；SYS2通过亮灭和闪烁表示采集正常、异常或暂停；SYS3表示仪表、采集、存储、控制或时间告警。TX1～TX4、RX1～RX4显示各串口收发。'),
        ],
        [
            h('数据采集与刷新'),
            p('各采集端口并行运行，同一总线顺序轮询。常用数据优先，多点温度、多点密度和参数快照按较长周期读取。'),
            t(['项目', '默认设置及说明'], [
                ['常用数据采集', '快速周期设为3秒；这是调度目标，不是16台满载刷新承诺；4800bps每口16台仅快速通信预算约4.74秒，实际还受响应、详细采集和超时影响'],
                ['详细数据采集', '周期30秒；多点温度及多点密度须具备相应仪表选件'],
                ['参数快照采集', '周期10分钟；用于查看，除远程液位修改外，普通参数写入不在本版功能范围'],
                ['数据有效窗口', '快速周期的3倍，默认9秒；按各字段所属采集组最后成功时间判断'],
            ], [1.4, 5.4]),
            p('采集失败时保留最后成功值并标记警告；过期后对应有效位清零，该值不再作为当前有效数据。有效位说明采集时效，不代表仪表无故障，也不表示机械动作或密度测量已完成。'),
            h('上位机读取与控制'),
            p('仪表统一编号1～64，使用同一个网关Unit ID。每台常用数据占24个寄存器，一次取得数值、质量和有效性。64台液位、平均温度、当前位置密度及质量和有效位，可用四次各96字批量读取；多次响应不构成严格同步快照。'),
            p('控制采用指令、状态和错误码三个寄存器，写入授权指令后查询结果；独立停止另设入口。控制按仪表型号和现场条件开通。“回读确认”表示仪表接受指令，不等于机械动作完成。独立停止为通信指令，不能代替硬接线急停。'),
            h('记录与现场维护'),
            p('设备保存历史并支持CSV导出。默认每60秒记录一次，按30天或256MiB上限保留，先达到者触发清理；一次导出最多7天。电脑断开后采集和记录继续。'),
            p('厂家通过浏览器设置四口、仪表绑定、采集和历史参数及控制授权，查看实时值和操作记录。客户只需IP或RTU参数、仪表编号表及通讯协议。'),
            p('支持在管理网页上传厂家签名的应用包，校验后确认安装，保留配置、账号、历史和控制记录。升级需维护窗口，通讯短暂中断；不升级Linux系统，首次启用及实际板端验收由厂家完成。'),
            h('交付与使用条件'),
            p('供货包括网关、项目配置表和客户通讯协议。外部电源、导轨卡扣、线缆及现场服务按报价提供。维护电脑接有线网口；平板需兼容的有线网卡或外置无线接入设备，网关不内置Wi-Fi。'),
            p('现场须核对液位计接口、站址和串口参数，按总线规范接线。本网关点表不直接兼容IFX地址。交付IP、权限、实际刷新周期及系统运行条件以项目技术协议和验收记录为准。'),
        ],
    ],
}

COMPARE = {
    'title': 'GT6757X液位采集网关与IFX接口单元比较',
    'file': 'GT6757X_液位采集网关与IFX接口单元比较_V1.5',
    'source': '19_液位采集网关与IFX接口单元比较.md',
    'subtitle': '客户资料 V1.5  2026年11月8日',
    'pages': [
        [
            p('GT6757X液位采集网关是我们面向液位管理项目开发的产品，当前采用GT6757S硬件。客户通过Modbus TCP直接读取液位计数据，现场配置、程序维护和功能调整由我们负责。与客户现用IFX相比，重点改善网络接入、现场维护和后续服务，并降低设备采购费用。'),
            p('IFX对照型号为东京计装IFX30000 / IFX3W011，产品号L25-75015。网络协议、价格和服务情况按本项目客户反馈比较；不代表其他IFX型号或选件。'),
            h('客户关心的区别'),
            t(['项目', 'GT6757X液位采集网关', '客户现用IFX接口单元'], [
                ['Modbus TCP', '支持，可直接接入PLC、DCS及上位机', '客户确认现用配置不支持'],
                ['客户串口接入', 'RS485 Modbus RTU\n按容量配置北向端口', 'RS232C Modbus RTU'],
                ['仪表连接', '4路独立隔离RS485\n每路16台，总计64台', '2路现场线路\n每路16台，总计32台'],
                ['现场维护', '电脑浏览器查看数据、告警和历史，厂家设置参数', '所提供资料未列相同的浏览器维护功能'],
                ['程序升级', '可由我方经维护网口安排应用升级；支持网页上传厂家签名应用包，板端验收待完成', '由日本方协调软件升级，客户反馈处理较慢'],
                ['开发与售后', '采集程序、客户点表和维护页面由我方开发维护，直接对接现场问题', '日本方开发；客户反馈升级及售后响应较慢'],
                ['设备价格', '据本项目反馈，低于现用IFX', '据本项目反馈，高于本网关'],
            ], [1.03, 2.88, 2.89]),
            h('接入和使用更方便'),
            p('已有Modbus TCP驱动的上位机可直接通过以太网接入本网关，减少串口转网络设备及接线。业务网口用于上位机，维护网口用于电脑调试，现场查数据和改配置不需要专用显示面板，也不依赖云平台。'),
            p('单机容量由IFX的32台提高到64台；占一路北向RTU仍可采集48台。四路采集可按罐区或线路分组，一路异常时便于缩小排查范围。应用程序由我们维护，现场新增点位、修改数据展示或排查通讯问题，可由我们直接处理，减少跨供应方协调。价格优势按本项目反馈列示，最终按同一供货和服务范围确认报价。'),
        ],
        [
            h('数据使用和控制'),
            p('本网关每台24字摘要包含数值、质量和有效性。64台液位、平均温度、当前位置密度及质量和有效位，可用四次各96字批量读取。通信失败时保留旧值并报警，过期后有效位清零，上位机能够区分旧值与当前可用值。'),
            p('密度输出保留0.0001g/cm³的仪表原始步长，IFX资料为0.001g/cm³；这是数值输出差别，不是测量准确度提高。客户控制只需写指令、读状态和错误码，另有独立停止入口；历史数据可在设备内保留并导出CSV。'),
            h('接口防护和安装'),
            t(['项目', 'GT6757X硬件配置的原厂标称参数'], [
                ['RS485', '四路独立电气隔离；4kV浪涌防护，10/700μs－5/320μs组合波'],
                ['以太网', '2kV浪涌防护，10/700μs－5/320μs组合波'],
                ['电源输入', '4kV浪涌防护，1.2/50μs－8/20μs组合波；DC 9～48V或AC 12～30V'],
            ], [1.13, 5.67]),
            p('上述防护依据GB/T 17626.5-2019及硬件原厂规格，现场仍按工程要求完成接地和外部防雷。本网关128.8×117×28mm、约450g，IFX为151×200×61mm、约800g，本网关便于安排控制柜安装位置。'),
            h('网页应用升级'),
            p('0.6.0已增加网页升级入口：上传厂家签名包，在维护窗口安装并查询结果。保留配置、账号、历史和控制记录；启动检查失败时恢复校验通过的兼容原程序，不恢复旧数据库。首次启用及真实板端验收由厂家完成，不更新Linux系统。'),
            h('项目接入条件'),
            p('须确认仪表具备匹配的RS485 Modbus接口，原IFX现场线路不能未经核对直接接入。上位机需修改地址、倍率、有效性和控制流程。64台配置仅支持TCP；配置北向RTU时最多采集48台。'),
            p('IFX资料有任务完成标志；本网关“回读确认”仅表示仪表接受指令。独立停止不代替硬接线急停，多次批量读取不是同步快照，刷新周期以实测为准。'),
            p('依据：本项目客户反馈（2026年10月11日）及容量修订（2026年11月8日）；IFX指定型号规格POR2-L25024-1/2-R0；GT6757硬件规格GT-230710-V1.1；客户通讯协议V3.2-D2。资料出现LAN口不表示客户现用配置支持Modbus TCP；其他IFX型号功能另行核对。'),
        ],
    ],
}

def set_font(run, size=None, bold=None, color='000000'):
    run.font.name = 'Arial'
    run._element.get_or_add_rPr().rFonts.set(qn('w:eastAsia'), '宋体')
    if size is not None: run.font.size = Pt(size)
    if bold is not None: run.bold = bold
    run.font.color.rgb = RGBColor.from_string(color)

def field(paragraph, code):
    run = paragraph.add_run()
    elem = OxmlElement('w:fldSimple'); elem.set(qn('w:instr'), code)
    run._r.addnext(elem)

def add_table(doc, block):
    table = doc.add_table(rows=1, cols=len(block['headers']))
    table.alignment = WD_TABLE_ALIGNMENT.CENTER
    table.autofit = False
    pr = table._tbl.tblPr
    borders = OxmlElement('w:tblBorders')
    for name in ('top','left','bottom','right','insideH','insideV'):
        b = OxmlElement('w:'+name)
        for k,v in {'val':'single','sz':'4','color':'D9D9D9'}.items(): b.set(qn('w:'+k),v)
        borders.append(b)
    pr.append(borders)
    for col,width in zip(table.columns,block['widths']): col.width = Inches(width)
    headprops = table.rows[0]._tr.get_or_add_trPr()
    repeat = OxmlElement('w:tblHeader'); headprops.append(repeat)
    for i,text in enumerate(block['headers']): table.rows[0].cells[i].text = text
    for rowdata in block['rows']:
        cells = table.add_row().cells
        for cell,text in zip(cells,rowdata): cell.text = text
    for ri,row in enumerate(table.rows):
        cant = OxmlElement('w:cantSplit'); row._tr.get_or_add_trPr().append(cant)
        for ci,cell in enumerate(row.cells):
            cell.width = Inches(block['widths'][ci])
            cell.vertical_alignment = WD_CELL_VERTICAL_ALIGNMENT.CENTER
            cp = cell._tc.get_or_add_tcPr()
            marg = OxmlElement('w:tcMar')
            for side,value in [('top','75'),('bottom','75'),('left','105'),('right','105')]:
                m=OxmlElement('w:'+side); m.set(qn('w:w'),value); m.set(qn('w:type'),'dxa'); marg.append(m)
            cp.append(marg)
            shade = OxmlElement('w:shd'); shade.set(qn('w:fill'), 'DCE6EE' if ri==0 else 'FFFFFF'); cp.append(shade)
            for para in cell.paragraphs:
                para.paragraph_format.space_after = Pt(0)
                para.paragraph_format.space_before = Pt(0)
                para.paragraph_format.line_spacing = 1.12
                para.paragraph_format.keep_with_next = False
                para.alignment = WD_ALIGN_PARAGRAPH.CENTER if ci==0 else WD_ALIGN_PARAGRAPH.LEFT
                for run in para.runs: set_font(run,10.5,bold=ri==0)
    spacer=doc.add_paragraph(); spacer.paragraph_format.space_after=Pt(0); spacer.paragraph_format.space_before=Pt(0)
    spacer.paragraph_format.line_spacing=Pt(3); set_font(spacer.add_run(''),3)

def build(data):
    doc = Document()
    sec=doc.sections[0]; sec.page_width=Inches(8.5); sec.page_height=Inches(11)
    sec.left_margin=Inches(.85); sec.right_margin=Inches(.85)
    sec.top_margin=Inches(.7); sec.bottom_margin=Inches(.65)
    sec.header_distance=Inches(.25); sec.footer_distance=Inches(.25)
    for style_name in ('Normal','Title','Subtitle','Heading 1','Heading 2','Header','Footer'):
        style=doc.styles[style_name]; style.font.name='Arial'; style.font.color.rgb=RGBColor(0,0,0)
        style.element.get_or_add_rPr().rFonts.set(qn('w:eastAsia'),'宋体')
    for border in doc.styles.element.xpath('.//w:pBdr'):
        border.getparent().remove(border)
    normal=doc.styles['Normal']; normal.font.size=Pt(11)
    normal.paragraph_format.line_spacing=1.22; normal.paragraph_format.space_after=Pt(6)
    title=doc.styles['Title']; title.font.size=Pt(21); title.font.bold=True
    title.paragraph_format.space_after=Pt(8)
    doc.styles['Subtitle'].font.size=Pt(10); doc.styles['Subtitle'].font.italic=False
    doc.styles['Subtitle'].paragraph_format.space_after=Pt(12)
    hs=doc.styles['Heading 1']; hs.font.size=Pt(13); hs.font.bold=True
    hs.paragraph_format.space_before=Pt(10); hs.paragraph_format.space_after=Pt(6)
    hp=sec.header.paragraphs[0]; hp.text='GT6757X液位采集网关'; hp.style='Header'
    for r in hp.runs: set_font(r,9)
    fp=sec.footer.paragraphs[0]; fp.alignment=WD_ALIGN_PARAGRAPH.RIGHT
    set_font(fp.add_run('V1.5  |  '),9); field(fp,'PAGE')
    doc.core_properties.title=data['title']; doc.core_properties.subject='客户产品资料'
    doc.core_properties.author=''; doc.core_properties.last_modified_by=''
    doc.add_paragraph(data['title'],'Title'); doc.add_paragraph(data['subtitle'],'Subtitle')
    for page_i,blocks in enumerate(data['pages']):
        for block_i,block in enumerate(blocks):
            if block['type']=='table': add_table(doc,block)
            else:
                para=doc.add_paragraph(block['text'],'Heading 1' if block['type']=='h' else 'Normal')
                if page_i and block_i==0: para.paragraph_format.page_break_before=True
                if block['type']=='p' and block['text'].startswith('依据：'):
                    for r in para.runs: set_font(r,9)
    dest=OUT/(data['file']+'.docx'); doc.save(dest)
    md=['# '+data['title'],'',data['subtitle'],'']
    for page in data['pages']:
        for b in page:
            if b['type']=='table':
                md.append('| '+' | '.join(b['headers'])+' |')
                md.append('| '+' | '.join(['---']*len(b['headers']))+' |')
                for row in b['rows']: md.append('| '+' | '.join(x.replace('\n','<br>') for x in row)+' |')
                md.append('')
            else: md.extend([('## ' if b['type']=='h' else '')+b['text'],''])
    if WRITE_MARKDOWN: (ROOT/'docs'/data['source']).write_text('\n'.join(md),encoding='utf-8')
    print(dest)

WRITE_MARKDOWN=False
if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--write-markdown',action='store_true',help='显式重建Markdown；默认保护已有维护源文档')
    WRITE_MARKDOWN=parser.parse_args().write_markdown
    for item in (SPEC,COMPARE):
        item['pages'].append([h('多网关接入与网络维护'),p('多台网关经交换机接入上位机，各自使用不同固定IP。网页可修改两个网口IP、子网掩码和一个默认网关，TCP监听同步调整，端口、站号和控制许可保留。'),p('改地址先试用，再通过新地址人工确认；180秒未确认或试用期间断电，恢复修改前参数。原厂FUN功能正常时可临时恢复默认IP，密码、绑定和历史保留。修改需维护窗口，真实网口、FUN及断电保持以交付验收记录为准。')])
    for data in (SPEC,COMPARE): build(data)
    evidence_dir=ROOT/'tmp/customer-materials-v1.5'
    evidence_dir.mkdir(parents=True,exist_ok=True)
    (evidence_dir/'content.json').write_text(json.dumps([SPEC,COMPARE],ensure_ascii=False,indent=2),encoding='utf-8')
