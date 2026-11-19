"""Parse documented commands only. Never execute deployment or control examples."""
from pathlib import Path
import argparse,hashlib,json,os,re,subprocess,tempfile
ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--version',default='0.4.2')
parser.add_argument('--date',default='2026-10-27')
parser.add_argument('--skip-shell-checks',action='store_true',help='Record shell syntax as unexecuted when the execution environment blocks MSYS')
args=parser.parse_args()
assert re.fullmatch(r'\d+\.\d+\.\d+',args.version)
assert re.fullmatch(r'\d{4}-\d{2}-\d{2}',args.date)
folder=Path(tempfile.mkdtemp(prefix='system-doc-syntax-',dir=ROOT/'tmp'))
files=['10_运行部署与维护.md','22_电脑演示构建与客户资料管理.md','23_时钟校核与断电恢复.md','24_手机访问与网页操作说明.md','25_HTTPS证书与可选启用说明.md','26_系统维护与控制写入操作说明.md','27_十六台每口扩容与升级说明.md','28_远程液位修改与验收说明.md','29_网页上传升级操作说明.md','31_网络设置与失联恢复.md','33_设备永久授权与厂家发放说明.md']
blocks=[];bad=[]
for name in files:
 for match in re.finditer(r'(?ms)^(~~~|```)(powershell|sh|bash)\s*\n(.*?)^\1',(ROOT/'docs'/name).read_text(encoding='utf-8')):
  language,body=match[2],match[3];path=folder/(str(len(blocks))+('.ps1' if language=='powershell' else '.sh'));path.write_text(body,encoding='utf-8',newline='\n')
  blocks.append({'source':name,'language':language,'path':str(path)})
  if name.startswith(('10_','25_','26_','27_','29_','31_','33_')):
   previous=''
   for line in body.splitlines():
    if not line.strip():continue
    if not line.lstrip().startswith('#') and line.strip() not in ['(',')','}'] and (not previous.startswith('#') or not re.search(r'[\u4e00-\u9fff]',previous)):bad.append((name,line))
    previous=line.strip()
assert not bad,bad
dependencies=Path.home()/'.cache/codex-runtimes/codex-primary-runtime/dependencies'
shell=dependencies/'native/git/usr/bin/sh.exe';ps=dependencies/'native/powershell/pwsh.exe'
env=os.environ.copy();env['PATH']=str(shell.parent)+os.pathsep+env.get('PATH','')
for block in blocks:
 if block['language']!='powershell' and args.skip_shell_checks:continue
 if block['language']=='powershell':
  command='$e=$null;$t=$null;[void][System.Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$t,[ref]$e);if($e){throw ($e|Out-String)}'
  # A saved parser wrapper avoids PowerShell -Command argument parsing ambiguity.
  wrapper=folder/'parse.ps1'
  if not wrapper.exists():wrapper.write_text(command,encoding='utf-8')
  run=subprocess.run([str(ps),'-NoProfile','-File',str(wrapper),block['path']],capture_output=True)
 else:run=subprocess.run([str(shell),'-n',block['path']],capture_output=True,env=env)
 assert run.returncode==0,(block,run.stderr.decode(errors='replace'))
result={'date':args.date,'software':args.version,'passed':True,'commands_executed':False,'blocks':len(blocks),
 'verification_complete':not args.skip_shell_checks,'shell_syntax_executed':not args.skip_shell_checks,
 'powershell_blocks':sum(b['language']=='powershell' for b in blocks),'shell_blocks':sum(b['language']!='powershell' for b in blocks),
 'deployment_HTTPS_system_commands_have_Chinese_comments':True,'documents':{name:hashlib.sha256((ROOT/'docs'/name).read_bytes()).hexdigest() for name in files}}
(ROOT/f'tests/system-document-verification-{args.version}-{args.date.replace("-", "")}.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(result,ensure_ascii=False))
