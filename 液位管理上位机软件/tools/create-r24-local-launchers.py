"""生成本机r24入口；已有数据及端口设置保持，不重新初始化。"""
from pathlib import Path
import json
R=Path(__file__).resolve().parents[1]
L=R/'output/client-viewing'; D=R/'output/r24-preview_20261118'
L.mkdir(exist_ok=True); D.mkdir(exist_ok=True)
marker=D/'preview-identity.json'
if marker.exists():
    identity=json.loads(marker.read_text(encoding='utf-8-sig'))
    assert identity['revision']=='r24' and identity['projectRoot']==str(R)
    assert identity['dataRoot']==str(D/'data') and identity['profileRoot']==str(D/'client-profile')
else:
    identity=dict(revision='r24',projectRoot=str(R),url='http://127.0.0.1:18425/',gatewayPort=18424,tanks=16,dataRoot=str(D/'data'),profileRoot=str(D/'client-profile'),headlessSimulator=True,realDevices=False)
    marker.write_text(json.dumps(identity,ensure_ascii=False,indent=2),encoding='utf-8')
for source in (R/'tools/local-preview-r24').glob('*.ps1'):
    (L/source.name).write_text(source.read_text(encoding='utf-8-sig'),encoding='utf-8-sig')
for title,script in [('启动r24后台.cmd','Start-R24Backend.ps1'),('打开r24客户端.cmd','Start-R24Client.ps1'),('启动r24模拟网关.cmd','Start-R24Simulator.ps1')]:
    (L/title).write_bytes(('@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0'+script+'"\r\nif errorlevel 1 pause\r\n').encode('utf-8'))
print(json.dumps(dict(url=identity['url'],gatewayPort=identity['gatewayPort'],preservedExistingData=True)))
