"""Only parse documented commands. Never install or run example device commands."""
from pathlib import Path
import json,os,subprocess
ROOT=Path(__file__).resolve().parents[1]
folder=ROOT/'tmp/manual-reset-doc-syntax'
manifest=json.loads((folder/'manifest.json').read_text(encoding='utf-8'))
ps=json.loads((folder/'powershell-result.json').read_text(encoding='utf-8-sig'))
assert ps['passed'] and not manifest['main_doc_missing_comments']
shell=Path.home()/'.cache/codex-runtimes/codex-primary-runtime/dependencies/native/git/usr/bin/sh.exe'
env=os.environ.copy();env['PATH']=str(shell.parent)+os.pathsep+env.get('PATH','')
checks=[]
for block in manifest['blocks']:
 if block['syntax']=='powershell':continue
 r=subprocess.run([str(shell),'-n',block['path']],capture_output=True,env=env)
 assert r.returncode==0,(block,r.stderr.decode(errors='replace'))
 checks.append({'source':block['source'],'block':Path(block['path']).name,'passed':True})
result={'date':'2026-10-18','software':'0.3.6','passed':True,'commands_executed':False,'blocks':len(manifest['blocks']),
 'powershell_blocks':ps['checked'],'shell_blocks':len(checks),'main_deployment_commands_have_chinese_comments':True,'checks':checks}
(ROOT/'tests/analog-document-syntax-20261018.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(result,ensure_ascii=False))
