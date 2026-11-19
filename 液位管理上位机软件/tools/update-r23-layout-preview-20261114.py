from pathlib import Path
import shutil,json,hashlib,datetime
R=Path(__file__).resolve().parents[1];B=R/'output/r23-layout-fix_20261114';P=R/'output/r23-feedback_20261113/payload';src=R/'src/LevelManager.Host/wwwroot';dst=P/'app/server/wwwroot'
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
qa=json.loads((B/'qa/result.json').read_text(encoding='utf-8'));assert qa['passed']
manifest=json.loads((P/'package-manifest.json').read_text(encoding='utf-8'))
protected=[]
for f in manifest['files']:
 if not f['path'].startswith('app/server/wwwroot/'):
  p=P/f['path'];assert sha(p)==f['sha256'],f['path'];protected.append(f)
before=dict(atUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),protected=protected,oldWeb=[f for f in manifest['files'] if f['path'].startswith('app/server/wwwroot/')])
if not (B/'before-payload.json').exists():(B/'before-payload.json').write_text(json.dumps(before,ensure_ascii=False,indent=2),encoding='utf-8')
shutil.copytree(src,dst,dirs_exist_ok=True)
# Existing opened pages can finish loading their prior hashed chunks until the client is refreshed.
print(json.dumps(dict(updated=True,runtimeRestarted=False,oldHashAssetsTemporarilyKept=True,protectedFiles=len(protected)),ensure_ascii=False))
