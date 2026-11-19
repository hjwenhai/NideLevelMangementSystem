from pathlib import Path
from datetime import datetime, timezone
import sys,json,hashlib,shutil,zipfile
root=Path(__file__).resolve().parents[1]
base=root/'output/客户交付资料_r20_20261102/液位管理系统_r20_自动结果复位改进版_20261102.zip'
base_sha='464521C5A6380873E1C06DBD50583F6025187395462465BCED1AE6FC200C3E70'
assert hashlib.file_digest(base.open('rb'),'sha256').hexdigest().upper()==base_sha
build=root/'output/r21-feedback-build_20261106'
target=root/'output/客户交付资料_r21_20261107/液位管理系统_r21_客户反馈改进版_20261107'
archive=Path(str(target)+'.zip')
qa=root/'output/r21-feedback-20261106'
mode=sys.argv[1]
if mode=='--prepare':
 assert not target.exists() and not archive.exists(),'Never overwrite a sealed or prepared package'
 assert (build/'server/LevelManager.Host.exe').is_file() and (build/'maintenance/LevelManager.Maintenance.exe').is_file() and (build/'restore/LevelManager.RestoreDesk.exe').is_file()
 target.mkdir(parents=True)
 with zipfile.ZipFile(base) as z:
  original=json.loads(z.read('package-manifest.json').decode('utf-8-sig'))
  for item in z.infolist():
   n=item.filename
   if n.startswith(('app/server/','app/maintenance/')) or n in ['package-manifest.json','00_开始部署.txt']:continue
   dest=(target/n).resolve();assert dest.is_relative_to(target.resolve())
   if item.is_dir():dest.mkdir(parents=True,exist_ok=True)
   else:
    dest.parent.mkdir(parents=True,exist_ok=True)
    with z.open(item) as src,dest.open('xb') as dst:shutil.copyfileobj(src,dst)
 for name in ['server','maintenance','restore']:shutil.copytree(build/name,target/'app'/name)
 shutil.copy2(root/'deployment/docs/r21客户反馈修改说明.html',target/'docs/r21客户反馈修改说明.html')
 shutil.copy2(root/'output/r21-transfer-qa_20261107/复制及导入导出测试说明.txt',target/'docs/复制及导入导出测试说明.txt')
 shutil.copy2(root/'output/outputs/feedback-20261106/小数罐容表填写模板_含示例_20261106.xlsx',target/'docs/小数罐容表填写模板_含示例.xlsx')
 (target/'业务数据恢复工具.cmd').write_bytes(b'@echo off\r\nstart "" "%~dp0app\\restore\\LevelManager.RestoreDesk.exe"\r\n')
 (target/'00_开始部署.txt').write_text('r21 客户反馈改进版（2026-11-07封装）\n\n先阅读docs/r21客户反馈修改说明.html及复制及导入导出测试说明.txt。\n升级前备份业务并核对现场作业，关闭客户端和旧后台；沿用原安装、数据目录及端口运行安装.cmd。\n保留r20完整版本及升级前备份；数据库升级至17，旧程序不能降级打开。\n桌面侧栏应为r21，health.revision应为20261106-r21（内部修订标识保持不变）。\n新版业务恢复工具可从本目录运行；不能用旧恢复工具恢复数据库17。\n',encoding='utf-8-sig')
 manifest={**original,'revision':'r21','builtAtUtc':datetime.now(timezone.utc).isoformat(),'documentationRevision':'20261106-r21','businessSchema':17,'basedOnCustomerSuiteSha256':base_sha,'stage':'中文联调测试版，隔离回归通过，现场待验收','files':[]}
 for p in sorted(target.rglob('*')):
  if not p.is_file():continue
  n=p.relative_to(target).as_posix()
  assert p.suffix.lower() not in ['.pem','.key','.pfx','.db'] and not any(v in n for v in ['vendor-keys','installation.dat','clock.dat','mock-gateway','LicenseDesk'])
  manifest['files'].append({'path':n,'bytes':p.stat().st_size,'sha256':hashlib.file_digest(p.open('rb'),'sha256').hexdigest().upper()})
 (target/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
 print(json.dumps({'prepared':True,'target':str(target),'files':len(manifest['files'])},ensure_ascii=False))
elif mode=='--seal':
 assert target.is_dir() and not archive.exists()
 native=json.loads((qa/'native-package-final_20261107/result.json').read_text(encoding='utf-8'));assert native['passed'] and Path(native['packageRoot']).resolve()==target.resolve()
 summary=json.loads((qa/'verification-summary.json').read_text(encoding='utf-8'));assert summary['passed']
 manifest=json.loads((target/'package-manifest.json').read_text(encoding='utf-8'))
 for f in manifest['files']:assert hashlib.file_digest((target/f['path']).open('rb'),'sha256').hexdigest().upper()==f['sha256']
 with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
  for p in sorted(target.rglob('*')):
   if p.is_file():z.write(p,p.relative_to(target).as_posix())
 with zipfile.ZipFile(archive) as z:
  assert z.testzip() is None
  assert len(z.namelist())==len(manifest['files'])+1
  for f in manifest['files']:assert hashlib.sha256(z.read(f['path'])).hexdigest().upper()==f['sha256']
 sha=hashlib.file_digest(archive.open('rb'),'sha256').hexdigest().upper()
 Path(str(archive)+'.sha256').write_text(sha+'  '+archive.name+'\n',encoding='utf-8')
 result={'passed':True,'zip':str(archive),'suite':str(target),'bytes':archive.stat().st_size,'sha256':sha,'manifestFiles':len(manifest['files']),'r20OriginalUnchanged':True,'schema':17}
 (qa/'package.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
 print(json.dumps(result,ensure_ascii=False,indent=2))
else:raise ValueError('use --prepare or --seal')
