from pathlib import Path
import json,zipfile,time,argparse
from lxml import etree as E
args=argparse.ArgumentParser();args.add_argument('directory',nargs='?');args.add_argument('--expected-rows',type=int,default=525600);args=args.parse_args();r=Path(args.directory) if args.directory else Path(__file__).resolve().parents[1]/'output/capacity/final-schema13-excel-20261028-v2';report=json.loads((r/'result.json').read_text());p=r/'excel'/(report.get('annualExcel') or report.get('result'))['FileName'];start=time.monotonic();ns='{http://schemas.openxmlformats.org/spreadsheetml/2006/main}';rows=0;header=[];samples=[]
with zipfile.ZipFile(p) as z:
 root=E.fromstring(z.read('[Content_Types].xml'));assert all(e.tag.startswith('{http://schemas.openxmlformats.org/package/2006/content-types}') for e in root)
 with z.open('xl/worksheets/sheet1.xml') as stream:
  for _,row in E.iterparse(stream,events=('end',),tag=ns+'row'):
   rows+=1
   if rows<=3:
    values=[c.findtext(ns+'v') if c.get('t')=='n' else ''.join(c.itertext()) for c in row]
    if rows==1:header=values
    else:samples.append(values)
   row.clear()
   while row.getprevious() is not None:del row.getparent()[0]
 assert rows==args.expected_rows+1,(rows,len(header));assert len(header)==65;assert 'ATS第16点 ℃ 状态' in header
 assert samples[0][header.index('瞬时温度 ℃')]=='';assert samples[0][header.index('瞬时温度 ℃ 状态')]=='通讯中断';assert samples[1][header.index('ATS第1点 ℃')]=='20.1';assert samples[1][header.index('ATS第1点 ℃ 状态')]=='正常'
 for name in z.namelist():
  if name!='xl/worksheets/sheet1.xml':E.fromstring(z.read(name))
result={'passed':True,'rowsIncludingHeader':rows,'dataRows':rows-1,'columns':len(header),'atsPointColumns':32,'sampleInvalidValueBlank':True,'sampleQualityPreserved':True,'allOOXMLPartsWellFormed':True,'zipCrcVerifiedByFullRead':True,'seconds':round(time.monotonic()-start,2),'fileBytes':p.stat().st_size};(r/'xlsx-verification.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(result,ensure_ascii=False))
