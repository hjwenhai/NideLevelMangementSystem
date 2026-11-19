"""Apply the user-authorized, same-version presentation update and reseal r22."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib, json, os, subprocess, sys, zipfile

root = Path(__file__).resolve().parents[1]
qa = root / 'output/r22-latest-delivery-qa_20261107'
assert sys.argv[1:] in [[], ['--pending-records']]
pending_update = bool(sys.argv[1:])
work = qa / ('pending-records_20261108' if pending_update else 'inventory-layout_20261108')
web = work / 'web'
suite = root / 'output/客户交付资料_r22_最新版_20261107/液位管理系统_r22_客户交付版_20261107'
archive = Path(str(suite) + '_发.zip')
# Preserve an existing user-renamed delivery rather than creating a duplicate.
if not archive.exists():
    renamed = Path(str(suite) + '.zip')
    assert renamed.exists(), 'The previously delivered customer archive is missing'
    archive = renamed
result_path = qa / 'package.json'
manifest_path = suite / 'package-manifest.json'
review = json.loads((work / ('record-result.json' if pending_update else 'layout-result.json')).read_text(encoding='utf-8'))
assert review['passed'] and len(review['checks']) == (11 if pending_update else 8) and review['writes'] == []
assert review['backendAccessed'] is False
if pending_update:
    assert len(review['mockReviewRequests']) == 1
    assert review['mockReviewRequests'][0]['path'].endswith('/old-r18-unresolved/review')
assert not (work / 'published.json').exists(), 'This presentation update is already published'

def sha(p):
    with p.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()

def safe(p):
    assert p.resolve().is_relative_to(root), str(p)
    current = p
    while current != root:
        assert not current.is_symlink() and not current.is_junction(), str(current)
        current = current.parent

def write_atomic(p, data):
    safe(p)
    p.parent.mkdir(parents=True, exist_ok=True)
    temp = p.with_name(p.name + '.layout-updating')
    assert not temp.exists(), str(temp)
    safe(temp)
    temp.write_bytes(data)
    os.replace(temp, p)

for p in [work, web, suite, archive, result_path, manifest_path]:
    safe(p)
before = json.loads(result_path.read_text(encoding='utf-8-sig'))
assert before['passed'] and before['build'] == '20261107-r22-u2'
assert sha(archive) == before['sha256']
manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
assert manifest['revision'] == 'r22' and manifest['build'] == before['build']
original_files = {f['path']: f for f in manifest['files']}
for name, f in original_files.items():
    assert sha(suite / name) == f['sha256'], name
(work / 'manifest-before.json').write_bytes(manifest_path.read_bytes())
(work / 'package-before.json').write_bytes(result_path.read_bytes())

# Generate new compressed variants as well as plain files; no stale index.gz/br.
compression = r"""
const fs=require('fs'),path=require('path'),z=require('zlib'),base=process.argv[1];
function visit(dir){for(const e of fs.readdirSync(dir,{withFileTypes:true})){
 const p=path.join(dir,e.name);if(e.isDirectory())visit(p);
 else if(/\.(js|css|html|ico)$/.test(e.name)){
  const data=fs.readFileSync(p);fs.writeFileSync(p+'.gz',z.gzipSync(data,{level:9}));
  fs.writeFileSync(p+'.br',z.brotliCompressSync(data,{params:{[z.constants.BROTLI_PARAM_QUALITY]:9}}));
 }
}}visit(base);
"""
subprocess.run(['D:/Program Files/nodejs/node.exe', '-e', compression, str(web)], check=True)
new_files = {p.relative_to(web).as_posix(): p for p in web.rglob('*') if p.is_file()
             and (p.relative_to(web).parts[0] == 'assets' or p.name in ['index.html', 'index.html.gz', 'index.html.br'])}
assert 'index.html' in new_files and any(n.endswith('.css') for n in new_files)
destinations = {
    'source': root / 'src/LevelManager.Host/wwwroot',
    'runtime': root / 'output/r22-preview-runtime_20261107-u2/app/server/wwwroot',
    'suite': suite / 'app/server/wwwroot',
}
with zipfile.ZipFile(work / 'wwwroot-before.zip', 'x', zipfile.ZIP_DEFLATED) as backup:
    for label, destination in destinations.items():
        safe(destination)
        for p in destination.iterdir():
            if p.is_file() and p.name in ['index.html', 'index.html.gz', 'index.html.br']:
                safe(p); backup.write(p, label + '/' + p.name)
        for p in (destination / 'assets').iterdir():
            safe(p); assert p.is_file(), str(p)
            backup.write(p, label + '/assets/' + p.name)

for label, destination in destinations.items():
    # New assets land first. Index replacement is atomic and requires no Host restart.
    for name, p in sorted(new_files.items(), key=lambda pair: pair[0].startswith('index.html')):
        write_atomic(destination / name, p.read_bytes())
    if label != 'runtime':
        for p in (destination / 'assets').iterdir():
            safe(p); assert p.is_file(), str(p)
            if 'assets/' + p.name not in new_files:
                p.unlink()
    # Keep old hashed runtime assets for already-open pages' delayed imports.
    # These are compatibility assets, not a second runtime or customer version.
    for name, p in new_files.items():
        assert sha(destination / name) == sha(p)

note_path = suite / '00_先阅读部署说明.txt'
note = note_path.read_text(encoding='utf-8-sig')
if pending_update:
    note += '\n2026年11月8日旧操作记录查找修正（仍为r22-u2）：操作记录与回读现在优先显示执行中/待核查项，可“显示更多操作记录”。较早的待核查记录会单独读取，避免被近期历史遮挡。记录总条数不是待处理条数，窗口上方另列待处理数。\n'
    note += '客户从r18升级后新增通讯通道，如果提示存在执行中或待核查操作：先记下新增通道参数，点击页面“重新载入”放弃本次未保存的改动，进入原罐“操作记录与回读”，逐项核对真实浮子运动、位置和仪表模式，对已核实的未知结果填写“人工核查记录”。仍在执行的操作应先按现场情况结束。处理后再新增通道并保存。人工核查只记录结论，不停止、不复位、不重发仪表动作。网关非空闲结果是否需要复位，应按实时状态另行判断。平均测温若也有待核查任务，须在平均温度测量页面处理。\n'
else:
    note += '\n2026年11月8日画面调整（仍为r22-u2）：罐区总览卡片的仪表密度、人工工况密度、净体积及质量统一分为名称、单位、数值三行。已安装客户使用本包按同一升级步骤更新；原数据和授权继续保留。\n'
write_atomic(note_path, note.encode('utf-8-sig'))
now = datetime.now(timezone.utc).isoformat()
manifest['presentationUpdatedAtUtc'] = now
change = '旧待核查操作优先展示并支持展开，保留配置重开保护' if pending_update else '罐区总览数据项统一三行对齐，版本保持r22-u2'
manifest['presentationChange'] = manifest.get('presentationChange', '') + ('；' if manifest.get('presentationChange') else '') + change
files = []
for p in sorted(suite.rglob('*')):
    if not p.is_file() or p == manifest_path:
        continue
    safe(p)
    name = p.relative_to(suite).as_posix()
    if not name.startswith('app/server/wwwroot/') and name != note_path.name:
        assert name in original_files and sha(p) == original_files[name]['sha256'], name
    assert p.suffix.lower() not in ['.pem', '.key', '.pfx', '.db']
    files.append({'path': name, 'bytes': p.stat().st_size, 'sha256': sha(p)})
for name in original_files:
    if not name.startswith('app/server/wwwroot/'):
        assert (suite / name).exists(), name
manifest['files'] = files
write_atomic(manifest_path, json.dumps(manifest, ensure_ascii=False, indent=2).encode('utf-8'))

# Explicitly authorized by the user to replace the existing r22 customer package.
# The original remains intact until a full temporary archive passes all hashes.
temporary = archive.with_name(archive.name + '.updating')
safe(temporary); assert not temporary.exists()
with zipfile.ZipFile(temporary, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for p in sorted(suite.rglob('*')):
        if p.is_file():
            z.write(p, p.relative_to(suite).as_posix())
with zipfile.ZipFile(temporary) as z:
    assert z.testzip() is None and len(z.namelist()) == len(files) + 1
    assert json.loads(z.read('package-manifest.json'))['build'] == before['build']
    for item in files:
        assert hashlib.sha256(z.read(item['path'])).hexdigest().upper() == item['sha256'], item['path']
assert sha(archive) == before['sha256'], 'Original package changed concurrently'
digest = sha(temporary)
safe(archive); os.replace(temporary, archive)
write_atomic(Path(str(archive) + '.sha256'), (digest + '  ' + archive.name + '\n').encode('utf-8'))
result = {**before, 'zip': str(archive), 'bytes': archive.stat().st_size, 'sha256': digest, 'files': len(files),
          'presentationUpdatedAtUtc': now, 'presentationChecks': len(review['checks']),
          'updatedInPlacePerUser': True, 'atUtc': now}
if pending_update:
    result['operationRecordReviewFix'] = True
write_atomic(result_path, json.dumps(result, ensure_ascii=False, indent=2).encode('utf-8'))
published = {'passed': True, 'sameVersion': True, 'build': before['build'],
             'zip': str(archive), 'previousSha256': before['sha256'], 'sha256': digest,
             'bytes': result['bytes'], 'files': len(files), 'backendBinariesUnchanged': True,
             'layoutChecks': len(review['checks']), 'hostRestarted': False,
             'dataOrAuthorizationModified': False, 'presentationChange': change, 'atUtc': now}
(work / 'published.json').write_text(json.dumps(published, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(published, ensure_ascii=False))
