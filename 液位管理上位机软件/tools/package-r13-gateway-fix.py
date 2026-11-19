"""Build a new customer suite from sealed r12 content, replacing only the server and release notes."""
from pathlib import Path
import argparse, hashlib, json, shutil, zipfile
from datetime import datetime, timezone

parser=argparse.ArgumentParser(); parser.add_argument('--revision',choices=['r13','r14','r15','r16','r17','r18'],default='r13'); parser.add_argument('--candidate',default=''); args=parser.parse_args(); revision=args.revision; candidate=args.candidate; assert not candidate or candidate.isalnum()
fixkind={'r13':'status','r14':'control','r15':'detail-capacity','r16':'serial','r17':'direct-control','r18':'all-actions'}[revision]
label={'r13':'远程液位修复版','r14':'网关操作修复版','r15':'界面与罐容表改进版','r16':'串口选择修复版','r17':'直连控制修复版','r18':'控制与说明修复版'}[revision]
note={'r13':'r13网关远程液位修改修复说明.html','r14':'r14网关操作修复说明.html','r15':'r15画面与罐容表改进说明.html','r16':'r16串口选择与控制检查说明.html','r17':'r17直连普通动作修复说明.html','r18':'r18控制与来源说明修复说明.html'}[revision]
root=Path(__file__).resolve().parent.parent
base=root/'output/客户交付资料_r12_20261030/液位管理系统_r12_客户交付套件_20261030.zip'
release=root/f'output/客户交付资料_{revision}_20261030'
target=release/(f'液位管理系统_{revision}_{label}_20261030'+ ('_'+candidate if candidate else ''))
server=root/(f'output/r18-all-actions-build_20261030/server' if revision=='r18' else f'output/r17-direct-control-build_20261030/server' if revision=='r17' else f'output/r16-serial-control-build_20261030/server' if revision=='r16' else f'output/{revision}-detail-capacity-build_20261030/server' if revision=='r15' else f'output/{revision}-gateway-{fixkind}-build_20261030/server')
assert not target.exists(), 'Use a new output directory; do not overwrite a sealed suite.'
assert hashlib.file_digest(base.open('rb'),'sha256').hexdigest().upper()=='DB5C114DF66887BDEE14BA1A992B0B76D4DD248CCE4399DBEEC515FC9AC9D586'
assert (server/'LevelManager.Host.exe').exists()
target.mkdir(parents=True)
with zipfile.ZipFile(base) as z:
    for item in z.infolist():
        name=item.filename
        if name.startswith('app/server/') or name=='package-manifest.json' or revision in ('r15','r16','r17','r18') and name.startswith('app/client/'): continue
        dest=(target/name).resolve()
        assert dest.is_relative_to(target.resolve())
        if item.is_dir(): dest.mkdir(parents=True, exist_ok=True)
        else:
            dest.parent.mkdir(parents=True, exist_ok=True)
            with z.open(item) as src, dest.open('xb') as out: shutil.copyfileobj(src,out)
    original=json.loads(z.read('package-manifest.json').decode('utf-8-sig'))
shutil.copytree(server,target/'app/server')
if revision in ('r15','r16','r17','r18'):
    # The sealed r18 client is byte-identical to r15 reviewed; the old test packages were retired.
    client=(root/'output/客户交付资料_r18_20261030/液位管理系统_r18_控制与说明修复版_20261030/app/client') if revision in ('r16','r17','r18') else server.parent/'client'
    shutil.copytree(client,target/'app/client')
    with zipfile.ZipFile(base) as z: (target/'app/client/client.json').write_bytes(z.read('app/client/client.json'))
    shutil.copytree(root/'assets/capacity-templates',target/'docs/Excel导入模板',ignore=shutil.ignore_patterns('*.ndjson'))
shutil.copy2(root/'deployment/docs'/note,target/'docs'/note)
if revision in ('r16','r17','r18'): shutil.copy2(root/'deployment/docs/r15画面与罐容表改进说明.html',target/'docs/r15画面与罐容表改进说明.html')
(target/'00_开始部署.txt').write_text(f'{revision}{label}（2026-10-30）\n\n已安装旧版：先阅读 docs/{note}，备份业务、确认现场作业结束、关闭客户端并停止“液位管理系统后台”服务，再运行安装.cmd，沿用原安装和数据目录。\n首次安装：阅读客户部署指南。\n升级不需要重新建罐或重新授权，不删除业务及授权目录。\n原r12部署与操作手册仍适用，新增差异以本版修复说明为准。\n\n本版是中文现场修复版，模拟回归已通过；六小时结果另附，真实设备需现场复核。\n',encoding='utf-8-sig')
manifest={**original,'revision':revision,'stage':'中文现场修复版；六小时结果另附，真实网关联调待确认','builtAtUtc':datetime.now(timezone.utc).isoformat(),'softwareBytesUnchanged':False,'basedOnCustomerSuiteSha256':'DB5C114DF66887BDEE14BA1A992B0B76D4DD248CCE4399DBEEC515FC9AC9D586','documentationRevision':'20261030-customer-'+revision+'-hotfix','files':[]}
for p in sorted(target.rglob('*')):
    if not p.is_file(): continue
    name=p.relative_to(target).as_posix()
    assert p.suffix.lower() not in ('.pem','.pfx','.key','.db') and not any(s in name for s in ('LicenseDesk','LevelManager.Tools','mock-gateway','installation.dat','clock.dat'))
    manifest['files'].append({'path':name,'bytes':p.stat().st_size,'sha256':hashlib.file_digest(p.open('rb'),'sha256').hexdigest().upper()})
(target/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
archive=target.with_suffix('.zip')
assert not archive.exists()
with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for p in sorted(target.rglob('*')):
        if p.is_file(): z.write(p,p.relative_to(target).as_posix())
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    assert len(z.namelist())==len(manifest['files'])+1
    for item in manifest['files']:
        data=z.read(item['path']); assert len(data)==item['bytes']; assert hashlib.sha256(data).hexdigest().upper()==item['sha256']
sha=hashlib.file_digest(archive.open('rb'),'sha256').hexdigest().upper()
Path(str(archive)+'.sha256').write_text(sha+'  '+archive.name+'\n',encoding='utf-8')
qa=root/(f'output/r18-all-actions-qa_20261030' if revision=='r18' else f'output/r17-direct-control-qa_20261030' if revision=='r17' else f'output/r16-serial-control-qa_20261030' if revision=='r16' else f'output/{revision}-detail-capacity-qa_20261030' if revision=='r15' else f'output/{revision}-gateway-{fixkind}-qa_20261030');qa.mkdir(exist_ok=True)
result={'passed':True,'suite':str(target),'zip':str(archive),'bytes':archive.stat().st_size,'sha256':sha,'manifestFiles':len(manifest['files']),'oldCustomerSuiteUnchanged':True,'privateKeysIncluded':False,'customerDataIncluded':False}
(qa/'package.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(result,ensure_ascii=False,indent=2))
