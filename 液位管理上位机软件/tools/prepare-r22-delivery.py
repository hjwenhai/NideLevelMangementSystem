from pathlib import Path
from datetime import datetime,timezone
import shutil,json,hashlib,zipfile
root=Path(__file__).resolve().parents[1]
base=root/'output/客户交付资料_r20_20261102/液位管理系统_r20_自动结果复位改进版_20261102.zip'
assert hashlib.file_digest(base.open('rb'),'sha256').hexdigest().upper()=='464521C5A6380873E1C06DBD50583F6025187395462465BCED1AE6FC200C3E70'
target=root/'output/客户交付资料_r22_20261107/液位管理系统_r22_中文测试版_20261107'
assert not target.exists()
target.mkdir(parents=True)
# Retain only prerequisites, the installer entry and third party notices from
# the sealed r20 suite. Do not carry contradictory old version manuals.
with zipfile.ZipFile(base) as z:
    original=json.loads(z.read('package-manifest.json').decode('utf-8-sig'))
    for item in z.infolist():
        n=item.filename
        if not (n.startswith('prerequisites/') or n.startswith('docs/第三方许可/') or n=='安装.cmd'):continue
        if item.is_dir():continue
        p=(target/n).resolve();assert p.is_relative_to(target.resolve());p.parent.mkdir(parents=True,exist_ok=True)
        p.write_bytes(z.read(item))
source=root/'output/r22-preview-runtime_20261107-u1/app/server'
shutil.copytree(source,target/'app/server',ignore=lambda p,n: ['assets'] if Path(p)==source/'wwwroot' else [])
shutil.copytree(root/'src/LevelManager.Host/wwwroot/assets',target/'app/server/wwwroot/assets')
for name in ['client','maintenance','restore']:
    shutil.copytree(root/'output/r22-delivery-build_20261107'/name,target/'app'/name)
(target/'app/client/client.json').write_text('{"serverUrl":"http://127.0.0.1:8780/"}\n',encoding='utf-8-sig')
install=(root/'deployment/Install.ps1').read_text(encoding='utf-8-sig')
old=" [Windows.Forms.MessageBox]::Show('安装完成。请从桌面打开液位管理系统，建立管理员并按说明导入供方授权。请先核对通讯和储罐配置。新安装默认不启动采集；升级保留原来配置的只读采集启动策略。','液位管理系统','OK','Information')|Out-Null"
new=" $taskFinished=if($service){'r22升级完成。原账号、业务数据与本机授权继续使用。请从标准桌面快捷方式打开，核对侧栏r22、通讯和储罐配置。升级保留原来的只读采集启动策略；不会续跑旧仪表动作。'}else{'r22安装完成。请从桌面打开液位管理系统，建立管理员并按说明导入供方授权。新安装默认不启动采集，请先核对通讯和储罐配置。'};[Windows.Forms.MessageBox]::Show($taskFinished,'液位管理系统','OK','Information')|Out-Null"
assert old in install
install=install.replace(old,new)
(root/'deployment/Install.ps1').write_text(install,encoding='utf-8-sig')
(target/'Install.ps1').write_text(install,encoding='utf-8-sig')
(target/'业务数据恢复工具.cmd').write_bytes(b'@echo off\r\nstart "" "%~dp0app\\restore\\LevelManager.RestoreDesk.exe"\r\n')
(target/'00_开始部署.txt').write_text('液位管理系统 r22 中文测试版\n2026年11月7日\n\n客户当前为r18：先阅读 docs/客户安装升级指南_r18到r22.docx，再执行升级。\n1 在原系统创建业务备份并下载保存，记录原安装、数据目录和端口。\n2 结束现场作业、关闭所有旧客户端，正常停止“液位管理系统后台”服务。\n3 完整解压本包，双击 安装.cmd，沿用默认安装与数据位置，无需卸载原系统。\n4 以原账号登录，核对侧栏r22与 http://127.0.0.1:8780/api/health 的revision=20261107-r22、build=20261107-r22-u1。\n5 核对储罐、通讯、授权、计量及历史。不要以重新建罐或重新导入配置代替升级。\n\n新装和自定义安装见指南。无需先安装r20/r21。无需常规重启电脑；遇到文件占用须先关闭旧客户端和后台。\n数据库首次启动由16升级至17，旧r18不能直接打开已升级库。保留升级前完整业务备份，回退由技术人员使用升级前数据进行，不用r18打开r22数据。\n\n本包含完整自包含程序、微软WebView2离线环境、新版桌面恢复工具、中文操作说明和模板。没有预设客户密码，没有客户数据、供方私钥或签发工具。平均测温功能按客户已有授权和设备条件使用。\n',encoding='utf-8-sig')
manifest={**original,'revision':'r22','build':'20261107-r22-u1','schemaVersion':17,'businessSchema':17,'documentationRevision':'20261107-r22','builtAtUtc':datetime.now(timezone.utc).isoformat(),'stage':'中文联调测试版，待现场验收','plannedSoakSeconds':None,'upgradeFrom':['r18','r20','r21'],'averageResources':True,'files':[]}
(target/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
(root/'output/r22-delivery-build_20261107/prepared.json').write_text(json.dumps({'target':str(target)},ensure_ascii=False),encoding='utf-8')
print(str(target))
