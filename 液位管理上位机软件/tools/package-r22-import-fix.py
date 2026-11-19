from pathlib import Path
from datetime import datetime,timezone
import hashlib,json,shutil,zipfile,sys

root=Path(__file__).resolve().parents[1]
qa=root/'output/r22-import-restore-qa_20261107'
base=root/'output/客户交付资料_r22_20261107/液位管理系统_r22_中文测试版_20261107_发.zip'
target=root/'output/客户交付资料_r22_导入修复版_20261107/液位管理系统_r22_导入恢复修复版_20261107'
archive=Path(str(target)+'_发.zip')
def digest(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()

assert digest(base)=='8347A353191D454A25FC6617A6327E6710D9ED27AF2FB0A4E64700F092E701A1'
assert json.loads((qa/'actual-file-latest.json').read_text(encoding='utf-8'))['passed']
if sys.argv[1]=='--prepare':
    assert not target.exists() and not archive.exists(),'Do not overwrite sealed suites'
    target.mkdir(parents=True)
    with zipfile.ZipFile(base) as z:
        manifest=json.loads(z.read('package-manifest.json').decode('utf-8-sig'))
        for item in z.infolist():
            if item.is_dir():continue
            p=(target/item.filename).resolve();assert p.is_relative_to(target.resolve())
            p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(z.read(item))
    shutil.copytree(root/'output/r22-import-restore-build_20261107/server',target/'app/server',dirs_exist_ok=True)
    for p in target.rglob('*'):
        if p.is_file() and p.suffix.lower() in ['.txt','.html']:
            text=p.read_text(encoding='utf-8-sig');changed=text.replace('20261107-r22-u1','20261107-r22-u2')
            if text!=changed:p.write_text(changed,encoding='utf-8-sig' if p.suffix.lower()=='.txt' else 'utf-8')
    note='''液位管理系统 r22 导入恢复修复版
构建标识：20261107-r22-u2

本包为完整安装包，可直接从r18升级，也可覆盖升级已安装的r22。保留原账号、业务数据、授权和通讯配置。不要先删除储罐，不要卸载后重建数据。

本次修复：原系统导出Excel后，删除储罐并保存，再导入原Excel时不再错误提示“内部编号不存在”。恢复时保留原内部编号，使历史记录继续关联原罐；多罐组关联一并恢复。重复编号、同罐号属于另一编号、错误通道/罐容表引用和预览版本冲突仍会拒绝。

安装升级：
1 阅读docs/客户安装升级指南_r18到r22.docx。先在原系统创建并保存业务备份，结束现场仪表作业，退出旧客户端并正常停止后台服务。
2 完整解压本包，双击安装.cmd，沿用原安装及数据目录；标准桌面快捷方式将更新。无需先安装旧r22或常规重启电脑。
3 使用原账号登录。在浏览器打开http://127.0.0.1:8780/api/health，检查revision=20261107-r22、build=20261107-r22-u2。自定义端口沿用现场端口。
4 本包沿用r22操作说明与r18升级指南。原Word指南内提到的build=20261107-r22-u1，在本次修复包应以本说明的u2为准，其余安装、备份和回退步骤不变。

已删除储罐后如何恢复：
重新上传原来导出的Excel，保留“内部编号”列；检查预览中的罐号、通道、地址、计量及罐组关联，勾选确认后“确认导入并保存”。Excel不包含罐容表内容、通讯通道定义、账号和授权，关联的资料须已在本系统中。后台若处于停止采集，保存后再启动采集。

不要为了避开旧版错误而清空原内部编号；这可能新建身份，使原历史在新罐下无法连续查询。真正全新的罐才将内部编号留空；跨安装迁移请核对目标系统引用资料。

本包不预置客户数据、客户密码或供方私钥。数据库结构仍为17，本次未改变授权和现场控制协议。新增修复已通过删除/部分删除后恢复、多罐组和历史连续性测试，不代表已完成现场硬件验收。本次未新增六小时长测。
'''
    (target/'00_本次修复与升级说明.txt').write_text(note,encoding='utf-8-sig')
    start=target/'00_开始部署.txt';start.write_text('请先阅读同目录00_本次修复与升级说明.txt。\n\n'+start.read_text(encoding='utf-8-sig'),encoding='utf-8-sig')
    manifest.update(build='20261107-r22-u2',builtAtUtc=datetime.now(timezone.utc).isoformat(),hotfix='储罐配置删除后恢复导入',files=[])
    for p in sorted(target.rglob('*')):
        if not p.is_file() or p.name=='package-manifest.json':continue
        n=p.relative_to(target).as_posix()
        assert p.suffix.lower() not in ['.pem','.key','.pfx','.db']
        assert not any(x in n.lower() for x in ['vendor-keys','licensedesk','levelmanager.tools','installation.dat','clock.dat','license.json','mock-gateway','node.exe','python.exe'])
        manifest['files'].append({'path':n,'bytes':p.stat().st_size,'sha256':digest(p)})
    (target/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'prepared':True,'target':str(target),'files':len(manifest['files'])},ensure_ascii=False))
elif sys.argv[1]=='--seal':
    assert not archive.exists()
    native=json.loads((qa/'native-package/result.json').read_text(encoding='utf-8'));assert native['passed'] and Path(native['packageRoot']).resolve()==target.resolve()
    manifest=json.loads((target/'package-manifest.json').read_text(encoding='utf-8'))
    for item in manifest['files']:assert digest(target/item['path'])==item['sha256'],item['path']
    with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for p in sorted(target.rglob('*')):
            if p.is_file():z.write(p,p.relative_to(target).as_posix())
    with zipfile.ZipFile(archive) as z:
        assert z.testzip() is None and len(z.namelist())==len(manifest['files'])+1
        for item in manifest['files']:assert hashlib.sha256(z.read(item['path'])).hexdigest().upper()==item['sha256']
    sha=digest(archive);Path(str(archive)+'.sha256').write_text(sha+'  '+archive.name+'\n',encoding='utf-8')
    result={'passed':True,'zip':str(archive),'bytes':archive.stat().st_size,'sha256':sha,'build':manifest['build'],'schema':17,'nativeChecks':len(native['checks']),'actualUserWorkbookVerified':True,'sourceOriginalR22Untouched':True,'unitTests':428,'newSixHourSoak':False,'r18UpgradeEvidence':'Existing sealed r22 direct-upgrade result; schema and upgrade mechanism unchanged; not rerun this patch'}
    (qa/'package.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(result,ensure_ascii=False,indent=2))
else:raise ValueError('Use --prepare or --seal')
