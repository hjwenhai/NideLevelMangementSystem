from pathlib import Path
import shutil,json,hashlib
r=Path(__file__).resolve().parents[1]
runtime=r/'output/r22-preview-runtime_20261107'
assert not runtime.exists(),'Existing runtime must be inspected; never overwrite a running preview'
source=r/'output/客户交付资料_r21_20261107/液位管理系统_r21_客户反馈改进版_20261107/app/client'
build=r/'output/r22-review-build_20261107/server'
assert (source/'LevelManager.Desktop.exe').is_file() and (build/'LevelManager.Host.exe').is_file()
shutil.copytree(source,runtime/'app/client')
shutil.copytree(build,runtime/'app/server')
files=[]
for p in runtime.rglob('*'):
 if p.is_file():
  assert p.suffix.lower() not in ['.pem','.key','.pfx','.db']
  files.append({'path':p.relative_to(runtime).as_posix(),'bytes':p.stat().st_size,'sha256':hashlib.file_digest(p.open('rb'),'sha256').hexdigest()})
(runtime/'package-manifest.json').write_text(json.dumps({'revision':'r22','previewOnly':True,'releaseInstaller':False,'files':files},ensure_ascii=False,indent=2),encoding='utf-8')
p=r/'tools/Start-R22Preview.cjs';assert not p.exists()
s=(r/'tools/Start-R21Preview.cjs').read_text(encoding='utf-8-sig')
s=s.replace('r21','r22').replace('R21','R22').replace('20261106-r22','20261107-r22')
s=s.replace('output/客户交付资料_r22_20261107/液位管理系统_r22_客户反馈改进版_20261107','output/r22-preview-runtime_20261107')
s=s.replace("hostBuild:'sealed-r22'","hostBuild:'preview-snapshot-r22'")
s=s.replace('// Independent r22 local preview. Never opens r20 or completed test databases.','// Independent r22 preview snapshot. Never opens r20/r21 or completed test databases.')
p.write_text(s,encoding='utf-8')
p=r/'output/client-viewing/Start-R22Preview.ps1';assert not p.exists()
s=(r/'output/client-viewing/Start-R21Preview.ps1').read_text(encoding='utf-8-sig').replace('r21','r22').replace('20261106-r22','20261107-r22')
s=s.replace('output\\客户交付资料_r22_20261107\\液位管理系统_r22_客户反馈改进版_20261107','output\\r22-preview-runtime_20261107')
p.write_text(s,encoding='utf-8-sig')
(r/'output/client-viewing/打开r22客户端.cmd').write_bytes(b'@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-R22Preview.ps1"\r\nif errorlevel 1 pause\r\n')
print(json.dumps({'runtime':str(runtime),'files':len(files),'previewOnly':True},ensure_ascii=False))
