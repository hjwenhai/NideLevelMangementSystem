"""Exercise the actual service case block with fake role functions; never signal real processes."""
from pathlib import Path
import hashlib,json,os,shlex,subprocess,tempfile
ROOT=Path(__file__).resolve().parents[1]
source=(ROOT/'deploy/service.sh').read_text(encoding='utf-8')
case=source[source.index('case "${1:-}" in'):]
folder=Path(tempfile.mkdtemp(prefix='restart-shell-',dir=ROOT/'tmp'))
shell=Path.home()/'.cache/codex-runtimes/codex-primary-runtime/dependencies/native/git/usr/bin/sh.exe'
env=os.environ.copy();env['PATH']=str(shell.parent)+os.pathsep+env.get('PATH','')
def posix(path):s=path.resolve().as_posix();return '/'+s[0].lower()+s[2:]
checks=[]
for name,locked,failure,expected in [
 ('normal',False,'',['stop:web','stop:core','start:core','start:web']),
 ('install_locked',True,'',[]),
 ('web_stop_failure',False,'stop:web',['stop:web']),
 ('core_stop_failure',False,'stop:core',['stop:web','stop:core']),
 ('core_start_failure',False,'start:core',['stop:web','stop:core','start:core'])]:
 base=folder/name;(base/'data').mkdir(parents=True)
 if locked:(base/'data/install.lock').mkdir()
 calls=base/'calls';script=base/'case.sh'
 prefix='set -eu\nBASE='+shlex.quote(posix(base))+'\nCALLS='+shlex.quote(posix(calls))+'\nFAIL='+shlex.quote(failure)+'\n'
 prefix+='stoprole() { printf "stop:%s\\n" "$1" >> "$CALLS"; [ "$FAIL" != "stop:$1" ]; }\nstartrole() { printf "start:%s\\n" "$1" >> "$CALLS"; [ "$FAIL" != "start:$1" ]; }\n'
 script.write_text(prefix+case,encoding='utf-8',newline='\n')
 run=subprocess.run([str(shell),str(script),'restart-web'],capture_output=True,env=env,timeout=5)
 actual=calls.read_text().splitlines() if calls.exists() else []
 assert actual==expected and (run.returncode==0)==(name=='normal'),(name,actual,run.stderr)
 checks.append({'name':name,'passed':True})
result={'date':'2026-10-19','software':'0.4.0','passed':True,'checks':checks,'service_sha256':hashlib.sha256((ROOT/'deploy/service.sh').read_bytes()).hexdigest(),
 'hardware_executed':False,'scope':'Unchanged service.sh case block with fake stoprole/startrole. Checks ordering and refusal only; real PID/driver shutdown is not executed.'}
(ROOT/'tests/program-restart-shell-verification-0.4.0-20261019.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'passed':True,'checks':len(checks)}))
