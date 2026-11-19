from pathlib import Path
import json,zipfile,hashlib,time,os,re
root=Path(__file__).resolve().parents[1]
source=Path(os.environ.get('LEVEL_PACKAGE_ROOT',root/'output/中文联调候选包_0.3.0-zh-test.1_r7'))
revision=re.search(r'_r[0-9]+$',source.name).group(0)
output=root/'output/releases'
output.mkdir(exist_ok=True)
manifest=json.loads((source/'package-manifest.json').read_text(encoding='utf-8-sig'))
date=os.environ.get('LEVEL_PACKAGE_DATE','20261029')
assert re.fullmatch(r'[0-9]{8}',date)
target=output/('液位管理系统_中文联调候选版_'+manifest['version']+'_'+date+revision+'.zip')
if target.exists():raise RuntimeError('不覆盖已有压缩包')
manifest=json.loads((source/'package-manifest.json').read_text(encoding='utf-8-sig'))
expected={x['path'].replace(chr(92),'/'):x for x in manifest['files']}
start=time.monotonic()
with zipfile.ZipFile(target,'x',compression=zipfile.ZIP_DEFLATED,compresslevel=6,allowZip64=True) as z:
 for item in sorted(source.rglob('*')):
  if item.is_file():z.write(item,item.relative_to(source).as_posix())
with zipfile.ZipFile(target) as z:
 assert len(z.infolist())==len(expected)+1
 assert len({e.filename for e in z.infolist()})==len(z.infolist())
 for entry in z.infolist():
  assert not entry.filename.startswith('/') and '..' not in Path(entry.filename).parts
  with z.open(entry) as stream:
   digest=hashlib.file_digest(stream,'sha256').hexdigest().upper()
  if entry.filename in expected:assert digest==expected[entry.filename]['sha256'],entry.filename
  else:assert entry.filename=='package-manifest.json'
with target.open('rb') as stream:digest=hashlib.file_digest(stream,'sha256').hexdigest().upper()
(target.with_suffix('.zip.sha256')).write_text(digest+'  '+target.name+'\n',encoding='utf-8')
report={'passed':True,'zipPath':str(target),'sha256':digest,'zipBytes':target.stat().st_size,'verifiedFiles':len(expected)+1,'seconds':round(time.monotonic()-start,2),'customerExpirationPreset':False,'containsSupplierSigningTool':False,'stage':manifest.get('stage','中文联调候选版，等待本次稳定性测试及现场验收')}
(output/('中文候选包'+revision[1:]+'_压缩校验.json')).write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False))
