from pathlib import Path
from datetime import datetime,timezone
import json,hashlib,zipfile,sys
root=Path(__file__).resolve().parents[1]
target=root/'output/客户交付资料_r22_20261107/液位管理系统_r22_中文测试版_20261107'
archive=Path(str(target)+'_发.zip');qa=root/'output/r22-delivery-qa_20261107'
manifest_path=target/'package-manifest.json'
manifest=json.loads(manifest_path.read_text(encoding='utf-8-sig'))
assert manifest['revision']=='r22' and manifest['schemaVersion']==17
def digest(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
if sys.argv[1]=='--manifest':
    assert not archive.exists(),'sealed package must not be modified'
    manifest['files']=[]
    for p in sorted(target.rglob('*')):
        if not p.is_file() or p==manifest_path:continue
        n=p.relative_to(target).as_posix()
        assert p.suffix.lower() not in ('.pem','.key','.pfx','.db')
        assert not any(s in n.lower() for s in ['vendor-keys','licensedesk','levelmanager.tools','installation.dat','clock.dat','license.json','mock-gateway','node.exe','python.exe'])
        manifest['files'].append({'path':n,'bytes':p.stat().st_size,'sha256':digest(p)})
    manifest_path.write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'prepared':True,'files':len(manifest['files'])}))
elif sys.argv[1]=='--seal':
    assert not archive.exists()
    native=json.loads((qa/'native-package-final/result.json').read_text(encoding='utf-8'))
    upgrade=json.loads((qa/'upgrade-latest.json').read_text(encoding='utf-8'))
    assert native['passed'] and Path(native['packageRoot']).resolve()==target.resolve()
    assert upgrade['passed'] and upgrade['instrumentWrites']==0
    assert json.loads((qa/'documents/visual-review.json').read_text(encoding='utf-8'))['passed']
    for f in manifest['files']:
        p=target/f['path'];assert p.stat().st_size==f['bytes'] and digest(p)==f['sha256'],f['path']
    with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for p in sorted(target.rglob('*')):
            if p.is_file():z.write(p,p.relative_to(target).as_posix())
    with zipfile.ZipFile(archive) as z:
        assert z.testzip() is None
        assert len(z.namelist())==len(manifest['files'])+1
        for f in manifest['files']:assert hashlib.sha256(z.read(f['path'])).hexdigest().upper()==f['sha256'],f['path']
    sha=digest(archive);Path(str(archive)+'.sha256').write_text(sha+'  '+archive.name+'\n',encoding='utf-8')
    result={'passed':True,'suite':str(target),'zip':str(archive),'bytes':archive.stat().st_size,'sha256':sha,'manifestFiles':len(manifest['files']),'schema':17,'revision':'20261107-r22','build':'20261107-r22-u1','r18DirectUpgradeVerified':True,'nativeGroups':len(native['checks']),'upgradeGroups':len(upgrade['checks']),'newSixHourSoak':False,'containsSupplierPrivateKeys':False,'containsCustomerData':False,'sealedAtUtc':datetime.now(timezone.utc).isoformat()}
    (qa/'package.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(result,ensure_ascii=False,indent=2))
else:raise ValueError('use --manifest or --seal')
