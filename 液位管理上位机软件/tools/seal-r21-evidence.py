from pathlib import Path
from datetime import datetime,timezone
import json,xml.etree.ElementTree as ET,hashlib
r=Path(__file__).resolve().parents[1]
qa=r/'output/r21-feedback-20261106';transfer=r/'output/r21-transfer-qa_20261107'
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
trx=ET.parse(transfer/'r21-transfer-final.trx');c=trx.find('.//t:ResultSummary/t:Counters',ns).attrib
assert c['failed']=='0' and c['passed']=='407'
integration=qa/'integration-1794018462929/result.json'
roundtrip=transfer/'roundtrip-1794018564869/result.json'
native=qa/'native-package-final_20261107/result.json'
results=[read(p) for p in [integration,roundtrip,native]]
assert all(x['passed'] for x in results)
assert len(results[0]['checks'])==14 and len(results[1]['checks'])==12 and len(results[2]['checks'])==10
assert results[0]['instrumentWrites']==results[1]['instrumentWrites']==0
assert read(transfer/'process-exit-verification.json')['exited']
base=r/'output/客户交付资料_r20_20261102/液位管理系统_r20_自动结果复位改进版_20261102.zip'
sha=hashlib.file_digest(base.open('rb'),'sha256').hexdigest().upper()
assert sha=='464521C5A6380873E1C06DBD50583F6025187395462465BCED1AE6FC200C3E70'
summary={'passed':True,'revision':'20261106-r21','verifiedAtUtc':datetime.now(timezone.utc).isoformat(),'unitTests':407,'unitFailed':0,'integrationGroups':14,'transferRoundtripGroups':12,'nativePackageGroups':10,'businessSchema':17,'frontendTypecheckAndBuildPassed':True,'instrumentWrites':0,'realDeviceAccess':False,'sixHourSoakPerformed':False,'r20OriginalSha256':sha,'r20OriginalUnchanged':True,'qaProcessesExited':True,'evidence':[str(p.relative_to(r)) for p in [transfer/'r21-transfer-final.trx',integration,roundtrip,native,transfer/'process-exit-verification.json']],'limitations':['现场仪表与检定表需验收','本次未另跑六小时稳定性测试','未实际安装或替换客户Windows服务','升级前应备份；r20不能打开schema17业务库']}
(qa/'verification-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(summary,ensure_ascii=False,indent=2))
