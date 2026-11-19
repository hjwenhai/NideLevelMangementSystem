from pathlib import Path
from datetime import datetime,timezone
import hashlib,json,zipfile,sys

root=Path(__file__).resolve().parents[1]
qa=root/'output/r22-latest-delivery-qa_20261107'
target=root/'output/客户交付资料_r22_最新版_20261107/液位管理系统_r22_客户交付版_20261107'
archive=Path(str(target)+'_发.zip');manifest_path=target/'package-manifest.json'
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
manifest=json.loads(manifest_path.read_text(encoding='utf-8-sig'))
assert manifest['build']=='20261107-r22-u2' and manifest['schemaVersion']==17
assert not archive.exists(),'Never modify sealed customer packages'
if sys.argv[1]=='--manifest':
    review=json.loads((qa/'documents/visual-review.json').read_text(encoding='utf-8'));assert review['passed'] and review['pagesReviewed']==10
    manifest['files']=[]
    for p in sorted(target.rglob('*')):
        if not p.is_file() or p==manifest_path:continue
        n=p.relative_to(target).as_posix();lower=n.lower()
        assert p.suffix.lower() not in ['.pem','.key','.pfx','.db']
        assert not any(s in lower for s in ['vendor-keys','licensedesk','levelmanager.tools','installation.dat','clock.dat','license.json','mock-gateway','node.exe','python.exe'])
        if p.suffix.lower() in ['.txt','.html']:
            text=p.read_text(encoding='utf-8-sig');assert not any(s in text for s in ['20261107-r22-u1','Preview-Isolated','previewqa','ChatGPT','Codex','大模型','AI生成'])
        manifest['files'].append({'path':n,'bytes':p.stat().st_size,'sha256':sha(p)})
    simulator=target/'可选工具/GT6757X本机模拟器_V1.0.3.zip'
    assert sha(simulator)=='D437B567E6381AAB65D9183EA9AD201286A6D162315EF4756762BE34D3667F6C'
    with zipfile.ZipFile(simulator) as z:
        inner=json.loads(z.read('package-manifest.json'));assert inner['version']=='1.0.3'
        for item in inner['files']:assert hashlib.sha256(z.read(item['path'])).hexdigest().upper()==item['sha256']
    manifest_path.write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'prepared':True,'files':len(manifest['files']),'includesSimulator':'1.0.3'},ensure_ascii=False))
elif sys.argv[1]=='--seal':
    native=json.loads((qa/'native-package/result.json').read_text(encoding='utf-8'));assert native['passed'] and Path(native['packageRoot']).resolve()==target.resolve()
    for f in manifest['files']:assert sha(target/f['path'])==f['sha256'],f['path']
    with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for p in sorted(target.rglob('*')):
            if p.is_file():z.write(p,p.relative_to(target).as_posix())
    with zipfile.ZipFile(archive) as z:
        assert z.testzip() is None and len(z.namelist())==len(manifest['files'])+1
        for f in manifest['files']:assert hashlib.sha256(z.read(f['path'])).hexdigest().upper()==f['sha256'],f['path']
    digest=sha(archive);Path(str(archive)+'.sha256').write_text(digest+'  '+archive.name+'\n',encoding='utf-8')
    result={'passed':True,'suite':str(target),'zip':str(archive),'bytes':archive.stat().st_size,'sha256':digest,'build':manifest['build'],'schema':17,'files':len(manifest['files']),'simulatorVersion':'1.0.3','wordDocuments':2,'reviewedPages':10,'nativeGroups':len(native['checks']),'newSixHourSoak':False,'businessCodeChangedThisTask':False,'r18UpgradeEvidenceReused':True,'containsCustomerData':False,'containsSupplierKeys':False,'atUtc':datetime.now(timezone.utc).isoformat()}
    (qa/'package.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(result,ensure_ascii=False,indent=2))
else:raise ValueError('Use --manifest or --seal')
