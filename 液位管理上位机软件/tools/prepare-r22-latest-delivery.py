from pathlib import Path
from datetime import datetime,timezone
import hashlib,json,zipfile,shutil
root=Path(__file__).resolve().parents[1]
qa=root/'output/r22-latest-delivery-qa_20261107';qa.mkdir(exist_ok=True)
target=root/'output/客户交付资料_r22_最新版_20261107/液位管理系统_r22_客户交付版_20261107'
base=root/'output/客户交付资料_r22_导入修复版_20261107/液位管理系统_r22_导入恢复修复版_20261107_发.zip'
sim=root/'output/GT6757X本机模拟器_r22_20261107_V1.0.3.zip'
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
assert sha(base)=='7C2263FBF498FC4391821E9F9B6433ADE226B02A78CBE80202001D97D07D0FFE'
assert sha(sim)=='D437B567E6381AAB65D9183EA9AD201286A6D162315EF4756762BE34D3667F6C'
assert not target.exists();target.mkdir(parents=True)
with zipfile.ZipFile(base) as z:
    manifest=json.loads(z.read('package-manifest.json').decode('utf-8-sig'))
    for item in z.infolist():
        if item.is_dir() or item.filename in ['00_本次修复与升级说明.txt','00_开始部署.txt']:continue
        p=(target/item.filename).resolve();assert p.is_relative_to(target.resolve());p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(z.read(item))
extras=target/'可选工具';extras.mkdir()
shutil.copy2(sim,extras/'GT6757X本机模拟器_V1.0.3.zip')
simdocs=root/'output/GT6757X本机模拟器_r22_20261107_V1.0.3/docs/11个模拟罐与1台真实液位计部署说明.html'
shutil.copy2(simdocs,target/'docs/模拟展示部署说明.html')
(target/'00_先阅读部署说明.txt').write_text('''液位管理系统 r22 最新客户交付资料
程序构建 20261107-r22-u2    模拟器 V1.0.3
适用 Windows 10或11 64位    本次开通平均温度测量

本目录为本次唯一客户交付资料。包含完整安装程序、中文安装升级指南、操作说明、业务恢复工具、罐容表模板，以及可选的本机模拟器。完整解压后使用，不在ZIP内部运行。

客户原来安装r18，或已经安装较早r22：
1 先阅读 docs/客户安装升级指南_r22.docx。可以直接升级，不必先安装中间版本。
2 在原系统创建业务备份，下载并另存，记录原程序目录、业务目录和端口。配置Excel不能代替业务备份。
3 结束现场仪表作业，退出所有旧客户端，正常停止“液位管理系统后台”服务。
4 双击 安装.cmd，沿用原安装与业务目录；不要卸载后删除数据。标准桌面快捷方式随安装更新。
5 从标准快捷方式打开，以原账号登录。默认浏览器地址为 http://127.0.0.1:8780/。
6 检查 http://127.0.0.1:8780/api/health：revision=20261107-r22，build=20261107-r22-u2。自定义端口沿用原端口。
7 核对罐号、通讯、罐容表、授权及历史，再按现场条件启动采集。升级不重新续跑旧仪表动作。通常无需重启电脑。

全新安装：阅读同一安装指南，安装后自行建立管理员账号，再按“软件授权”页面导出本机请求并向供方申请授权。没有通用客户账号或演示密码。

日常使用：docs/用户操作说明书_r22.docx。内容已同步本构建，包含配置Excel、罐组关联、删除后按原编号恢复、密度/体积计算、平均测温、趋势、打印、授权及备份恢复。

模拟展示（可选）：
完整解压 可选工具/GT6757X本机模拟器_V1.0.3.zip，在管理系统后台所在电脑运行其中“启动模拟器.cmd”。详细见 docs/模拟展示部署说明.html。默认127.0.0.1:15020、Unit ID 1、11个模拟罐。真实液位计继续原RS-485通道及地址1；不要照抄其他电脑的COM口。
模拟器不自动新建管理系统储罐；按说明创建“模拟展示”罐组和SIM-01至SIM-11。模拟值会保存历史、参与混合合计，真实库存只看真实罐组。该工具用于通讯与画面展示，不代表完整无线浮子V1.3测温流程或现场仪表验收。

导入模板：请先在客户本机创建实际通道、介质、罐组并导入适用罐容表，再在软件“储罐配置”下载模板。模板含6个可见页签，“选择列表”为隐藏辅助页，不填写、不删除。不要使用其他电脑带本机参考编号的测试表代替现场配置。

需要恢复业务数据时，先停止后台，再运行根目录“业务数据恢复工具.cmd”。恢复会回到备份时刻，须按提示核对并确认。

本版为中文联调测试版，三类报表入口仍为暂未启用；未确定项目及英文后续开发。程序、数据与授权分开存放，不要删除业务目录或授权目录。本包不包含客户数据、供方私钥或授权签发工具。
''',encoding='utf-8-sig')
manifest.update(build='20261107-r22-u2',deliveryRevision='20261107-r22-u2-final',builtAtUtc=datetime.now(timezone.utc).isoformat(),documentationRevision='20261107-r22-u2',optionalSimulatorVersion='1.0.3',files=[])
(target/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
(qa/'prepared.json').write_text(json.dumps({'target':str(target),'sourceBuild':'20261107-r22-u2','simulatorVersion':'1.0.3'},ensure_ascii=False,indent=2),encoding='utf-8')
print(str(target))
