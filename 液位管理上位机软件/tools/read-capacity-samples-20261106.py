from pathlib import Path
import json
import pdfplumber
root=Path(__file__).resolve().parents[2]
out=root/'液位管理上位机软件/output/feedback-review-20261106'
names=['客户发过来的技术协议/罐容表/罐容表示例/61-T-710.pdf','客户发过来的技术协议/罐容表/立式金属罐容量表.pdf','客户发过来的技术协议/罐容表/球形罐容量表.pdf','客户发过来的技术协议/罐容表/浮顶容量表.pdf']
for i,name in enumerate(names):
    doc=pdfplumber.open(root/name)
    text='\n'.join(f'PAGE {n+1}\n'+(p.extract_text() or '') for n,p in enumerate(doc.pages))
    (out/f'capacity-{i}.txt').write_text(text,encoding='utf-8')
    print(name, 'PAGES',len(doc.pages),'TEXT',text[:8500] if i else text[:1800])
    for n,p in enumerate(doc.pages):
        t=p.extract_text() or ''
        if ('厘' in t and '毫' in t) or not t.strip() and n<2:
            p.to_image(resolution=125).save(out/f'capacity-{i}-page-{n+1}.png')
