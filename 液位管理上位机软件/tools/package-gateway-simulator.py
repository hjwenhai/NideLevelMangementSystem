from pathlib import Path
import json,hashlib,zipfile
from datetime import datetime,timezone
root=Path(__file__).resolve().parents[1]
target=root/'output/GT6757X本机模拟器_r22_20261107_V1.0.3'
qa=root/'output/r22-simulator-qa_20261107/v103'
assert (target/'app/LevelManager.GatewaySimulator.exe').is_file()
integration=json.loads((qa/'integration-latest.json').read_text(encoding='utf-8'));assert integration['passed'] and integration['slots']==11
native=json.loads((qa/'native-window.json').read_text(encoding='utf-8'));assert native['passed'] and native['rows']==11 and native['stopped']
layout=json.loads((qa/'native-layout.json').read_text(encoding='utf-8'));assert layout['passed'] and len(layout['cases'])==4
refresh=json.loads((qa/'native-refresh.json').read_text(encoding='utf-8'));assert refresh['passed'] and len(refresh['cases'])==3 and all(c['refreshes']==12 and c['sizeEvents']==0 and not c['changes'] for c in refresh['cases'])
unit=(qa/'simulator-final-unit.trx').read_text(encoding='utf-8');assert 'total="424" executed="424" passed="424" failed="0"' in unit
(target/'启动模拟器.cmd').write_bytes(b'@echo off\r\nstart "" "%~dp0app\\LevelManager.GatewaySimulator.exe"\r\n')
steps='''GT6757X本机模拟器 V1.0.3  r22配套工具

本版修正右侧滚动条反复闪烁：数据刷新只更新变化值，不反复调整窗口宽高；表格采用双缓冲。仍保留文字自适应、换行和小窗口滚动。若旧版正在展示，请先在旧窗口停止模拟并关闭，再打开本版。已有管理系统通道和11罐配置无需修改，两边端口保持一致即可。

用途：同一台采集电脑上，11个模拟罐走本机Modbus TCP，客户1台真实液位计仍走原RS-485通道、地址1。无需安装Node.js、Python或单独.NET。模拟器不打开串口，不连接真实仪表，不修改管理系统配置、数据库或授权。

1 完整解压本ZIP到普通本地文件夹，双击“启动模拟器.cmd”。不要只复制EXE。
2 保持模拟罐数量11、端口15020，点击“启动模拟”，确认显示正在监听127.0.0.1:15020。Unit ID固定1。模拟器应与管理系统后台运行在同一台电脑。
3 管理系统保留客户原真实仪表通道与原罐配置，通讯地址仍是1，原实际COM口、波特率等不改。
4 新增通道“独立模拟网关（本机）”：GT6757X Modbus TCP，主机127.0.0.1，端口15020，Unit ID 1，启用。无需添加虚拟串口。也可改用55406，但模拟器与管理系统必须填同一个端口。
5 新增“模拟展示”罐组，只放模拟罐。可先导入docs/模拟主罐容表_3000m3.xlsx，表类型选主表，预览后启用。这是展示用线性表，不能用于真实客户罐。
6 新建SIM-01，名称“模拟罐01”，绑定模拟通道，内部编号1；罐高12000mm、公称容量3000m³；启用。温度可选ATS、16点，高度0、700、1400……10500mm。计量启用并关联SIM-CT-3000主表，密度选仪表实测，水体积0。如需操作演示，仅在模拟罐开放所需动作。
7 保存第一个模拟罐后，从SIM-01复制10次，罐号分别SIM-02至SIM-11，内部编号分别2至11，核对它们全部绑定“独立模拟网关（本机）”。不要复制或改绑客户真实罐。r22复制会保留模拟罐分组、罐容表、启用及温度配置。
8 最终为11个SIM罐加1个真实罐，共12罐。模拟内部编号1与真实RS-485地址1属于不同采集通道，不冲突。保存配置后按原方式采集。

演示：在模拟器选中一行，改液位、温度、密度后“应用”；也可模拟离线、模拟故障、恢复正常。管理系统可查看图形、温度、ATS实时平均、体积质量、趋势和报警。上升/下降演示时位置会简单变化，模式回到测量Ⅰ后再看液位。远程液位修改目标应在0～12000mm内。

注意：模拟量会和真实量一起保存历史、参与总览合计。看真实库存时只选真实罐组；不要把“模拟展示”合计当成真实库存。需要看历史，按默认60秒归档应等至少一分钟。

模拟器只演示网关通讯、常用模式和简单位置变化，不是完整无线浮子V1.3测温过程或真实机械停稳模型。ATS实时平均与无线浮子一次任务平均不是同一功能。

结束演示：在管理系统停用11个SIM罐和模拟通道，再停止/关闭模拟器；真实罐与真实通道保持启用，不要为了关闭演示而停止总采集后台。关闭或重新启动模拟器会重置模拟数据，不清除管理系统已有历史，也不改变真实仪表。窗口可最小化，但运行演示时不能关闭。

端口冲突：改为15021等未占用端口，同时修改管理系统模拟通道。8780是管理系统后台端口，不用于模拟网关。连接失败时先看模拟器是否“正在监听”，再核对IP、端口、Unit ID和内部编号1～11。只新增通讯通道而没有启动模拟器不会产生数据。

本工具不需要签发授权；管理系统继续使用客户本机有效授权。r18到r22的升级仍按r22完整包内指南操作，模拟器无需安装成Windows服务。此ZIP是配套工具，不替代r22完整升级包。
'''
(target/'先读我.txt').write_text(steps,encoding='utf-8-sig')
html='''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>11个模拟罐与1台真实液位计同时使用</title><style>body{font:16px/1.85 "Microsoft YaHei",sans-serif;color:#263f49;max-width:1000px;margin:32px auto;padding:0 24px}h1{font-size:29px;color:#122f39}h2{font-size:21px;color:#122f39;margin-top:30px}table{border-collapse:collapse;width:100%;margin:15px 0}th,td{border:1px solid #d9e1e6;padding:10px 13px;text-align:left}th{background:#ecf5f2}code{background:#f0f5f5;padding:2px 4px}li{margin:9px 0}strong{color:#116f65}</style><body>
<h1>11个模拟罐与1台真实液位计同时使用</h1><p><strong>模拟器V1.0.3：修正右侧滚动条闪烁。</strong>刷新数据时保留滚动位置，不反复改变窗口布局；文字仍自动撑开，小窗口可滚动。更新时先停止并关闭旧模拟器，再从本文件夹启动。管理系统通道与11罐配置保留，端口两边一致即可。</p>
<p>先完成客户管理系统r18到r22升级。模拟器作为额外的独立程序运行，客户原来的真实液位计仍使用原RS-485接线与地址1。<strong>不需要创建虚拟串口，不需要再次升级管理系统，不需要给模拟器签发授权。</strong></p>
<h2>一 在客户采集电脑上启动模拟器</h2>
<ol><li>完整解压本ZIP到本地文件夹，例如D盘的“液位演示模拟器”。双击根目录的“启动模拟器.cmd”，保留整个app文件夹。</li><li>数量保持11，端口保持15020，点击“启动模拟”。应看到“正在监听127.0.0.1:15020 · Unit ID 1 · 模拟槽位1～11”。</li><li>运行中可最小化，不能关闭。模拟器必须与管理系统<strong>后台</strong>在同一台电脑，127.0.0.1表示本电脑。</li></ol>
<h2>二 两条通讯通道分别配置</h2>
<table><tr><th>项目</th><th>11个模拟罐</th><th>客户真实液位计</th></tr><tr><td>名称</td><td>独立模拟网关（本机）</td><td>保留原真实通道名称</td></tr><tr><td>协议</td><td>GT6757X Modbus TCP</td><td>直连RS-485 Modbus RTU</td></tr><tr><td>连接</td><td>127.0.0.1，端口15020</td><td>原实际COM口及原通讯参数</td></tr><tr><td>站号或编号</td><td>通道Unit ID为1；每罐内部编号1～11</td><td>仪表通讯地址1</td></tr><tr><td>罐号与分组</td><td>SIM-01～SIM-11，分组“模拟展示”</td><td>保留原罐号与真实分组</td></tr></table>
<p>两边的1不冲突，因为属于不同采集通道。不要照搬本测试电脑的COM2，客户真实通道沿用他电脑原有的配置。截图中的55406也可以使用，只要模拟器与管理系统两边端口一致；本工具默认15020。8780是管理系统后台端口，不是网关端口。</p>
<h2>三 先做好第一个模拟罐 再复制十次</h2>
<ol><li>在“罐组管理”新增“模拟展示”。在“基础数据 → 罐容表管理”新增主表，导入本包docs中的“模拟主罐容表_3000m3.xlsx”，预览核对后启用。这张线性表只用于模拟展示，不能关联客户真实罐。</li><li>新增SIM-01，绑定新建的模拟通道，内部编号1，罐高12000mm、公称容量3000m³，加入“模拟展示”组并启用。罐型可按需要选择立式、球罐或浮顶。</li><li>计量启用，关联SIM-CT-3000主表，计算密度选仪表实测密度，人工水体积0。这样能看到模拟体积与质量。</li><li>如需温度计画面，选ATS、16点，高度依次0、700、1400、2100、2800、3500、4200、4900、5600、6300、7000、7700、8400、9100、9800、10500mm。模拟器提供正常16点温度。</li><li>如需操作演示，在此模拟罐开放需要的操作，按页面提示确认。不要为演示去修改真实罐的允许操作。</li><li>先应用并保存SIM-01，再复制为SIM-02～SIM-11，分别填内部编号2～11。复制会保留分组、启用、三表和温度等参数；每次核对绑定的仍是本机模拟通道。</li><li>最终共12罐：11个SIM罐加客户原有1个真实罐。保存后按原方式采集，等待模拟罐上线。</li></ol>
<h2>四 怎样展示效果</h2>
<p>可在模拟器中选择一个槽位，修改液位、温度和密度并应用，或选择模拟离线、模拟故障、恢复正常。液位默认缓慢变化；管理系统可演示三种罐图、IR26温度、ATS实时平均、压力、体积质量、趋势和报警。需要历史数据时，按默认60秒归档应等至少一分钟。</p>
<p>上升、下降、停止、测量等只改变模拟模式和简单位置；位置变化可在单罐详情的P值看到。回到测量Ⅰ再看液位。远程液位修改的演示目标限制在0～12000mm内。模拟器不模拟完整无线浮子V1.3测温过程，也不能代表真实机械动作时间或计量检定。</p>
<p><strong>模拟数据会保存历史并参与总览合计。</strong>看真实库存时只选真实罐组；不要使用混合合计作为实际库存。真实罐的计量表、接线和操作权限保持原配置。</p>
<h2>五 结束演示和常见问题</h2>
<p>结束时先在管理系统停用11个SIM罐和模拟通道，再关闭模拟器。真实罐与真实通道保持启用，不要停掉总采集后台。重启模拟器会生成初始模拟值，管理系统历史及真实仪表不被清除。</p>
<table><tr><th>情况</th><th>处理</th></tr><tr><td>端口已占用</td><td>模拟器与管理系统的模拟通道同时改为15021等未占用端口，不要停止真实后台来腾端口。</td></tr><tr><td>模拟罐离线</td><td>先看模拟器是否正在监听，再核对IP、端口、Unit ID、通道启用和每罐内部编号1～11。</td></tr><tr><td>温度有值但没有体积质量</td><td>核对模拟罐计量是否启用、是否关联展示主表、所选密度是否有效。</td></tr><tr><td>只有浮子位置没有液位</td><td>先看仪表模式，模拟上升或下降时不能把P值直接当液面。需要液面时恢复测量Ⅰ。</td></tr><tr><td>电脑重启后模拟罐离线</td><td>重新打开模拟器并启动监听。管理系统后台可继续采集真实仪表，模拟器不自动注册系统服务。</td></tr></table>
<p>此配套工具无需Node.js、Python或开发工具，不打开串口、不建立到真实设备的连接、不读写管理系统数据库或授权。仅支持本机回环监听，关闭时只影响模拟罐。</p></body></html>'''
(target/'docs/11个模拟罐与1台真实液位计部署说明.html').write_text(html,encoding='utf-8')
manifest={'product':'GT6757X本机模拟器','version':'1.0.3','compatibleHostBuild':'20261107-r22-u1','builtAtUtc':datetime.now(timezone.utc).isoformat(),'defaultPort':15020,'unitId':1,'defaultSlots':11,'loopbackOnly':True,'serialPortsOpened':0,'includesRuntime':True,'customerDataIncluded':False,'supplierKeysIncluded':False,'files':[]}
for p in sorted(target.rglob('*')):
    if not p.is_file() or p.name=='package-manifest.json':continue
    relative=p.relative_to(target).as_posix()
    assert p.suffix.lower() not in ['.pem','.key','.pfx','.db'] and not any(x in relative for x in ['vendor-keys','installation.dat','clock.dat','license.json'])
    with p.open('rb') as f:sha=hashlib.file_digest(f,'sha256').hexdigest().upper()
    manifest['files'].append({'path':relative,'bytes':p.stat().st_size,'sha256':sha})
(target/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
archive=Path(str(target)+'.zip');assert not archive.exists(),'Never overwrite a sealed simulator'
with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for p in sorted(target.rglob('*')):
        if p.is_file():z.write(p,p.relative_to(target).as_posix())
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    for f in manifest['files']:assert hashlib.sha256(z.read(f['path'])).hexdigest().upper()==f['sha256']
with archive.open('rb') as f:sha=hashlib.file_digest(f,'sha256').hexdigest().upper()
Path(str(archive)+'.sha256').write_text(sha+'  '+archive.name+'\n',encoding='utf-8')
result={'passed':True,'zip':str(archive),'bytes':archive.stat().st_size,'sha256':sha,'files':len(manifest['files']),'unitTests':424,'integrationGroups':len(integration['checks']),'nativeWindowVerified':True,'layoutCases':len(layout['cases']),'refreshCases':len(refresh['cases']),'refreshGeometryChanges':0,'mainR22NotRepacked':True,'serialPortsOpened':0,'defaultPort':15020,'slots':11}
(qa/'package.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(result,ensure_ascii=False,indent=2))
