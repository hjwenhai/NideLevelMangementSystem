"""Verify retained files and measure the result, without following directory links."""
from pathlib import Path
import os,json,hashlib
R=Path(__file__).resolve().parents[1];C=R/'output/cleanup-records/20261113-r22-r23-only'
plan=json.loads((C/'plan.json').read_text(encoding='utf-8'));result=json.loads((C/'result.json').read_text(encoding='utf-8'));assert result['passed']
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
# Documentation edited after the verified deletion phase is deliberately listed.
intentional={'README.md','docs/project_memory/progress.md','docs/project_memory/00_index.md'}
mismatch=[]
for f in plan['protected']:
 if f['path'].casefold() not in {p.casefold() for p in intentional}:
  p=R/f['path']
  if not p.is_file() or sha(p)!=f['sha256']:mismatch.append(f['path'])
assert not mismatch,mismatch
remaining=[];errors=[];links=[]
def scan(d):
 try:entries=list(os.scandir(d))
 except OSError as e:errors.append(str(e));return
 for e in entries:
  st=e.stat(follow_symlinks=False)
  if st.st_file_attributes & 0x400:links.append(str(Path(e.path).relative_to(R)));continue
  if e.is_dir(follow_symlinks=False):scan(e.path)
  else:remaining.append(dict(path=Path(e.path).relative_to(R).as_posix(),bytes=st.st_size))
scan(R);assert not errors,errors
still=[]
for t in plan['targets']:
 if t['relative']=='output/r23-feedback_20261113/qa':continue
 if Path(t['path']).exists():still.append(t['relative'])
assert not still,still
for rel in ['output/r23-feedback_20261113/payload','output/confirmation-build_20261113/payload']:
 base=R/rel;m=json.loads((base/'package-manifest.json').read_text(encoding='utf-8-sig'))
 for f in m['files']:assert sha(base/f['path'])==f['sha256'],f['path']
releases=[]
for rel in ['output/客户安装向导_r22_20261113','output/客户安装向导_r23_20261113']:
 base=R/rel;exe=next(base.glob('*.exe'));saved=exe.with_name(exe.name+'.sha256').read_text(encoding='utf-8-sig').split()[0].upper();assert sha(exe)==saved
 releases.append(dict(path=str(exe.relative_to(R)),sha256=saved))
assert sha(Path(plan['archive']['path']))==plan['archive']['sha256']
final=dict(passed=True,deletedFiles=result['deletedFiles'],deletedBytes=result['deletedBytes'],remainingBytes=sum(f['bytes'] for f in remaining),remainingFiles=len(remaining),protectedRechecked=len(plan['protected'])-len(intentional),intentionalDocumentationChanges=sorted(intentional),errors=errors,releases=releases,targetsStillPresent=still,linksNotFollowed=len(links),sourceAndKeysPreserved=True,r22LauncherCheckOnlyPassed=True)
(C/'after-verification.json').write_text(json.dumps(final,ensure_ascii=False,indent=2),encoding='utf-8')
(C/'after-inventory.json').write_text(json.dumps(dict(files=remaining,links=links),ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(passed=True,deletedGiB=round(result['deletedBytes']/2**30,3),remainingGiB=round(final['remainingBytes']/2**30,3),remainingFiles=len(remaining),checkedFiles=final['protectedRechecked'],releaseHashesOk=True),ensure_ascii=False))
