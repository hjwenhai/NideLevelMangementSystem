"""Create a reviewable, explicit cleanup plan; never delete files here."""
from pathlib import Path
import json,hashlib,shutil,zipfile,time
R=Path(__file__).resolve().parents[1];O=R/'output';C=O/'cleanup-records/20261113-r22-r23-only'
before=json.loads((C/'before-inventory.json').read_text(encoding='utf-8'))
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
# Put packaging prerequisites in source-owned locations before retiring the obsolete tool.
old=O/'供方专用授权工具_0.3.0-zh-test.1_20261028'
notices=R/'deployment/third-party';notices.mkdir(exist_ok=True)
for n in ['dotnet-LICENSE.txt','dotnet-ThirdPartyNotices.txt']:
 shutil.copy2(old/'第三方许可'/n,notices/n)
shutil.copy2(old/'更新安装快捷方式图标.ps1',R/'deployment/vendor/更新安装快捷方式图标.ps1')
p=R/'tools/package-license-desk.ps1';s=p.read_text(encoding='utf-8-sig').replace("output/供方专用授权工具_0.3.0-zh-test.1_20261028/更新安装快捷方式图标.ps1","deployment/vendor/更新安装快捷方式图标.ps1").replace("output/供方专用授权工具_0.3.0-zh-test.1_20261028/第三方许可/","deployment/third-party/");p.write_text(s,encoding='utf-8-sig')
p=R/'tests/r23-feedback-check.cjs';s=p.read_text(encoding='utf-8').replace('output/r23-feedback_20261113/server/LevelManager.Host.exe','output/r23-feedback_20261113/payload/app/server/LevelManager.Host.exe');p.write_text(s,encoding='utf-8')
# Preserve the useful r22 manuals and templates, without retaining its obsolete binaries.
oldDelivery=O/'客户交付资料_r22_最新版_20261107/液位管理系统_r22_客户交付版_20261107'
newDelivery=O/'客户安装向导_r22_20261113'
shutil.copytree(oldDelivery/'docs',newDelivery/'操作手册与模板',dirs_exist_ok=True)
(newDelivery/'操作手册与模板/00_当前版本说明.txt').write_text('这里保留r22操作手册和模板；安装时以父目录20261113安装程序、安装说明和本次修订与超时设置说明为准。旧手册中的20261107压缩包名称为历史引用，不需要寻找或安装旧包。\n',encoding='utf-8-sig')
# r23 payload is the one unpacked runtime copy used by future QA/builds.
b=O/'r23-feedback_20261113'
for n in ['server','client','restore','maintenance']:
 for f in (b/n).rglob('*'):
  if f.is_file():
   twin=b/'payload/app'/n/f.relative_to(b/n)
   assert twin.is_file() and sha(f)==sha(twin),f
for f in (b/'vendor/app').rglob('*'):
 if f.is_file():assert sha(f)==sha(O/'供方授权工具_r23_20261113/app'/f.relative_to(b/'vendor/app')),f
whole=[
 'output/供方专用授权工具_0.3.0-zh-test.1_20261028',
 'output/客户安装向导_r22_20261110','output/客户安装向导_r22_20261111',
 'output/客户交付资料_r20_20261102','output/客户交付资料_r22_最新版_20261107',
 'output/control-flow-build_20261111','output/control-flow-installer-qa_20261111','output/control-flow-qa_20261111',
 'output/wizard-installer-qa_20261110','output/ifx-editor-qa_20261111','output/independent-channel-qa',
 'output/r20-auto-reset-build_20261102','output/r20-auto-reset-qa_20261102','output/r20-comparison-preview-1794021879129','output/r20-preview-1793628950711',
 'output/r22-import-restore-qa_20261107','output/r22-latest-delivery-qa_20261107','output/confirmation-qa_20261113',
 'output/outputs','output/confirmation-build_20261113/server',
 'output/r23-feedback_20261113/client','output/r23-feedback_20261113/server','output/r23-feedback_20261113/maintenance','output/r23-feedback_20261113/restore','output/r23-feedback_20261113/vendor','output/r23-feedback_20261113/qa',
 'tmp','.build-tools/pnpm-cache',
 'output/00_r20测试版使用说明.txt','output/r20版本保留标记_20261106.json',
]
for p in (O/'client-viewing').iterdir():
 if p.name.lower().find('r20')>=0 or p.name.lower().find('r21')>=0 or p.name.startswith(('r19-to-r20','r22-update-','stop-old-keep-r22')) or p.name=='stop-old-keep-r22-latest.json':whole.append(p.relative_to(R).as_posix())
# Cache/build outputs are reconstructible; tests/source themselves stay intact.
for base in ['src','tools','tests']:
 for proj in (R/base).iterdir():
  if not proj.is_dir():continue
  for n in ['bin','obj','__pycache__','TestResults']:
   p=proj/n
   if p.exists():whole.append(p.relative_to(R).as_posix())
for base in ['tools','tests','web']:
 p=R/base/'__pycache__'
 if p.exists():whole.append(p.relative_to(R).as_posix())
# Keep top-level verification documents even when disposable raw data is removed.
keep=[
 'output/客户安装向导_r22_20261113','output/客户安装向导_r23_20261113',
 'output/GT6757X本机模拟器_r22_20261107_V1.0.3','output/GT6757X本机模拟器_r22_20261107_V1.0.3.zip','output/GT6757X本机模拟器_r22_20261107_V1.0.3.zip.sha256',
 'output/供方桌面授权工具_20261030','output/供方桌面授权工具_20261030.zip','output/供方桌面授权工具_20261030.zip.sha256',
 'output/供方授权工具_r23_20261113','output/供方授权工具_r23_20261113.zip','output/供方授权工具_r23_20261113.zip.sha256',
 'output/r22-preview-runtime_20261107-u2','output/r22-preview-1794033145639',
 'output/confirmation-build_20261113/payload','output/r23-feedback_20261113/payload',
 '.build-tools/vendor-keys','data','archive','assets','deployment','docs','web/src','web/node_modules'
]
targets=sorted(set(whole))
for rel in targets:
 p=R/rel;assert p.is_relative_to(R) and p!=R
 for k in keep:
  q=R/k
  assert not (p==q or p in q.parents or q in p.parents),(rel,k)
def selected(rel):return any(rel==t or rel.startswith(t+'/') for t in targets)
# Reports/logs/screenshots only: exclude raw databases, browser profiles, installed apps, licenses and data payloads.
evidence=[]
extensions={'.log','.trx','.png','.jpg','.jpeg','.html','.txt','.md','.svg'}
for f in before['files']:
 rel=f['path'];p=Path(rel);parts=p.parts
 if not selected(rel):continue
 if any(x.lower() in ['app','server','client','maintenance','restore','prerequisites','node_modules','bin','obj','isolated-data','data','client-profile','keys','.authorization','业务数据','业务数据.authorization','完整安装','仅客户端'] for x in parts):continue
 if p.suffix.lower() in extensions or (p.suffix=='.json' and not p.name.endswith('.license.json') and any(x in p.name.lower() for x in ['result','verification','summary','layout','failure','checks','snapshot','manifest','session'])):evidence.append(f)
archive=C/'验证记录归档_20261113.zip';manifest=[]
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
 for f in evidence:
  p=R/f['path'];data=p.read_bytes();z.writestr(f['path'],data);manifest.append(dict(path=f['path'],bytes=len(data),sha256=hashlib.sha256(data).hexdigest().upper()))
 z.writestr('归档文件校验清单.json',json.dumps(manifest,ensure_ascii=False,indent=2))
with zipfile.ZipFile(archive) as z:
 assert z.testzip() is None
 for f in manifest:assert hashlib.sha256(z.read(f['path'])).hexdigest().upper()==f['sha256']
# Preserve the latest r23 test evidence at its existing lookup paths after the disposable QA tree is removed.
copy=b/'qa-kept-records';copy.mkdir(exist_ok=True)
for rel in ['r23-final.trx','installer/package-result.json','vendor-ui/result.json','integration-1794579532812/result.json','integration-1794579532812/layout.json']:
 p=copy/rel;p.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(b/'qa'/rel,p)
for p in (b/'qa/integration-1794579532812').glob('*.png'):
 d=copy/'integration-1794579532812'/p.name;shutil.copy2(p,d)
protected=[]
for p in R.rglob('*'):
 # Do not traverse using this iterator outside project; only hash selected source/current retention leaves.
 if not p.is_file() or p.is_symlink():continue
 rel=p.relative_to(R).as_posix()
 if selected(rel) or rel.startswith('output/cleanup-records/'):continue
 if rel.startswith(('src/','tools/','tests/','assets/','deployment/','docs/','.build-tools/vendor-keys/','data/')) or any(rel==k or rel.startswith(k+'/') for k in keep if k.startswith('output/')):
  protected.append(dict(path=rel,bytes=p.stat().st_size,sha256=sha(p)))
plan=dict(root=str(R),created=time.time(),targets=[dict(path=str(R/t),relative=t,knownBytes=sum(f['bytes'] for f in before['files'] if f['path']==t or f['path'].startswith(t+'/')),knownFiles=sum(1 for f in before['files'] if f['path']==t or f['path'].startswith(t+'/'))) for t in targets],keep=keep,archive=dict(path=str(archive),sha256=sha(archive),files=len(manifest)),protected=protected,accessRestricted=before['errors'],protectedFiles=len(protected))
(C/'plan.json').write_text(json.dumps(plan,ensure_ascii=False,indent=2),encoding='utf-8')
(C/'保留与删除清单.md').write_text('# r22/r23清理清单\n\n保留：\n\n'+''.join('- '+k+'\n' for k in keep)+'\n删除（含旧安装、测试数据库、重复发布与编译缓存）：\n\n'+''.join('- '+t+'\n' for t in targets)+f'\n验证记录{len(manifest)}份已逐文件SHA校验压缩归档。源代码、测试源码、授权密钥及开发依赖保留。\n',encoding='utf-8')
print(json.dumps(dict(targets=len(targets),estimatedGiB=round(sum(t['knownBytes'] for t in plan['targets'])/2**30,3),evidenceFiles=len(manifest),archiveMiB=round(archive.stat().st_size/2**20,2),protectedFiles=len(protected)),ensure_ascii=False))
