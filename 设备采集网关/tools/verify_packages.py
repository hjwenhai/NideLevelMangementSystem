"""Read-only release validation: SHA256, ELF architecture and tar contents."""
from pathlib import Path
import argparse, hashlib, json, struct, tarfile, zipfile
root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser()
parser.add_argument('--version',default='0.3.1-20261014-r1')
parser.add_argument('--customer-revision',default='20261014-r1')
args=parser.parse_args()
reports=[]
for arch,cls,machine in [('linux-arm64',2,183),('linux-armv7',1,40)]:
    package=root/'output/packages'/f'gt6757s-{args.version}-{arch}'
    assert package.is_dir(),package
    checked=0
    for line in (package/'SHA256SUMS').read_text(encoding='utf-8').splitlines():
        expected,name=line.split('  ',1)
        p=(package/name).resolve();p.relative_to(package.resolve())
        assert p.is_file() and hashlib.sha256(p.read_bytes()).hexdigest()==expected,name
        checked+=1
    assert (package/'ARCH').read_bytes()==(arch+'\n').encode()
    assert (package/'RELEASE').read_bytes()==(args.version+'\n').encode()
    spec=json.loads((package/'north-v3.json').read_text(encoding='utf-8'))
    assert spec['version']==3 and spec['capacity']==32
    assert (package/'protocols/north-v3.json').read_bytes()==(package/'north-v3.json').read_bytes()
    assert (package/'output/pdf/GT6757S_客户通讯协议_V3.0-D2.pdf').read_bytes()==(root/'output/pdf/GT6757S_客户通讯协议_V3.0-D2.pdf').read_bytes()
    for name in ['gateway-core','gateway-web']:
        b=(package/'bin'/name).read_bytes()
        assert b[:4]==b'\x7fELF' and b[4]==cls and b[5]==1
        assert struct.unpack_from('<H',b,18)[0]==machine
        phoff=struct.unpack_from('<Q' if cls==2 else '<I',b,32 if cls==2 else 28)[0]
        entsize,num=struct.unpack_from('<HH',b,54 if cls==2 else 42)
        assert not any(struct.unpack_from('<I',b,phoff+i*entsize)[0]==3 for i in range(num)), 'dynamic interpreter present'
    archive=Path(str(package)+'.tar.gz')
    with tarfile.open(archive,'r:gz') as t:
        members=t.getmembers()
        for m in members:
            assert m.name.split('/')[0]==package.name and '..' not in m.name.split('/')
            assert m.isdir() or m.isfile(),m.name
            if m.isfile():
                assert t.extractfile(m).read()==(package.parent/m.name).read_bytes(),m.name
    reports.append(dict(architecture=arch,files_checked=checked,elf_static=True,archive=archive.name,archive_bytes=archive.stat().st_size,sha256=hashlib.sha256(archive.read_bytes()).hexdigest()))
customer_name='gt6757s-customer-v3.0-d2-'+args.customer_revision
customer=root/'output/customer-docs'/(customer_name+'.zip')
with zipfile.ZipFile(customer) as z:
    files={Path(n).name:z.read(n) for n in z.namelist() if not n.endswith('/')}
    assert set(files)=={'README.md','SHA256SUMS','12_客户通讯协议V3.md','GT6757S_客户通讯协议_V3.0-D2.pdf'},set(files)
    for line in files['SHA256SUMS'].decode('utf-8').splitlines():
        expected,name=line.split('  ',1)
        assert hashlib.sha256(files[name]).hexdigest()==expected,name
    assert files['12_客户通讯协议V3.md']==(root/'docs/12_客户通讯协议V3.md').read_bytes()
    assert files['GT6757S_客户通讯协议_V3.0-D2.pdf']==(root/'output/pdf/GT6757S_客户通讯协议_V3.0-D2.pdf').read_bytes()
result=dict(passed=True,version=args.version,packages=reports,customer=dict(archive=customer.name,sha256=hashlib.sha256(customer.read_bytes()).hexdigest(),whitelist=True),hardware_executed=False)
(root/'tests/v3-package-verification.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(result,ensure_ascii=False,indent=2))
