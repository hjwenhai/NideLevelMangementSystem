"""Seal r20 from the verified r19 customer suite and newly built server/client. No customer data or keys."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib,json,shutil,zipfile
root=Path(__file__).resolve().parent.parent
base=root/'output/客户交付资料_r19_20261102/液位管理系统_r19_通道状态与操作改进版_20261102.zip'
base_sha='7B68DECCBF61666273C6BE989EBCF71C2FD8B74EEC50834C1B6D07B8AADD29CF'
assert hashlib.file_digest(base.open('rb'),'sha256').hexdigest().upper()==base_sha
target=root/'output/客户交付资料_r20_20261102/液位管理系统_r20_自动结果复位改进版_20261102'
archive=Path(str(target)+'.zip')
assert not target.exists() and not archive.exists(), 'Never overwrite a sealed package'
build=root/'output/r20-auto-reset-build_20261102'
assert (build/'server/LevelManager.Host.exe').is_file() and (build/'client/LevelManager.Desktop.exe').is_file()
qa=root/'output/r20-auto-reset-qa_20261102'
summary=json.loads((qa/'verification-summary.json').read_text(encoding='utf-8'))
assert summary['passed'] and summary['unitTestsPassed']>=387 and summary['integrationGroupsPassed']>=12
target.mkdir(parents=True)
with zipfile.ZipFile(base) as z:
    original=json.loads(z.read('package-manifest.json').decode('utf-8-sig'))
    for item in z.infolist():
        name=item.filename
        if name.startswith(('app/server/','app/client/')) or name in ('package-manifest.json','Install.ps1','00_开始部署.txt'):continue
        dest=(target/name).resolve();assert dest.is_relative_to(target.resolve())
        if item.is_dir():dest.mkdir(parents=True,exist_ok=True)
        else:
            dest.parent.mkdir(parents=True,exist_ok=True)
            with z.open(item) as src,dest.open('xb') as out:shutil.copyfileobj(src,out)
shutil.copytree(build/'server',target/'app/server',ignore=lambda src,names:['assets'] if Path(src)==build/'server/wwwroot' else [])
shutil.copytree(root/'src/LevelManager.Host/wwwroot/assets',target/'app/server/wwwroot/assets')
shutil.copytree(build/'client',target/'app/client')
(target/'app/client/client.json').write_text('{"serverUrl":"http://127.0.0.1:8780/"}\n',encoding='utf-8-sig')
shutil.copy2(root/'deployment/Install.ps1',target/'Install.ps1')
shutil.copy2(root/'deployment/docs/r20自动结果复位与30秒默认说明.html',target/'docs/r20自动结果复位与30秒默认说明.html')
shutil.copy2(root/'deployment/docs/客户电脑模拟展示方案_讨论稿.html',target/'docs/客户电脑模拟展示方案_讨论稿.html')
(target/'docs/r20测试范围与结果.txt').write_text(summary['customerValidationText'],encoding='utf-8-sig')
(target/'00_开始部署.txt').write_text('r20 自动结果复位改进版（2026-11-02）\n\n先阅读 docs/r20自动结果复位与30秒默认说明.html。\n升级前备份业务、核对现场状态、关闭客户端并停止旧后台。完整解压后运行安装.cmd，沿用原安装、数据目录和端口。\n无需卸载、重新建罐或重新授权，不要删除原数据及授权目录。\n安装完成后从标准桌面快捷方式打开，侧栏应为r20；默认/api/health的revision为20261102-r20。\n本次没有代现场下发仪表动作，不自动复位或重发旧动作；测试范围见随包r20测试范围与结果.txt。\n原部署及操作手册继续适用，变化以r20说明为准。\n',encoding='utf-8-sig')
manifest={**original,'revision':'r20','stage':'中文测试版；隔离模拟回归通过，实际现场待验收','builtAtUtc':datetime.now(timezone.utc).isoformat(),'basedOnCustomerSuiteSha256':base_sha,'documentationRevision':'20261102-r20','files':[]}
for p in sorted(target.rglob('*')):
    if not p.is_file():continue
    name=p.relative_to(target).as_posix()
    assert p.suffix.lower() not in ('.pem','.pfx','.key','.db') and not any(v in name for v in ('LicenseDesk','LevelManager.Tools','mock-gateway','installation.dat','clock.dat','vendor-keys'))
    manifest['files'].append({'path':name,'bytes':p.stat().st_size,'sha256':hashlib.file_digest(p.open('rb'),'sha256').hexdigest().upper()})
(target/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for p in sorted(target.rglob('*')):
        if p.is_file():z.write(p,p.relative_to(target).as_posix())
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    assert len(z.namelist())==len(manifest['files'])+1
    for item in manifest['files']:
        b=z.read(item['path']);assert len(b)==item['bytes'] and hashlib.sha256(b).hexdigest().upper()==item['sha256']
sha=hashlib.file_digest(archive.open('rb'),'sha256').hexdigest().upper()
Path(str(archive)+'.sha256').write_text(sha+'  '+archive.name+'\n',encoding='utf-8')
result={'passed':True,'suite':str(target),'zip':str(archive),'bytes':archive.stat().st_size,'sha256':sha,'manifestFiles':len(manifest['files']),'privateKeysIncluded':False,'customerDataIncluded':False,'r19OriginalUnchanged':True}
(qa/'package.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result,ensure_ascii=False,indent=2))
