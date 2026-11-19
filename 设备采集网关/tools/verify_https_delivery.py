"""Check optional HTTPS deliverables while preserving HTTP defaults. No commands are deployed."""
from pathlib import Path
import hashlib,json,os,re,subprocess
ROOT=Path(__file__).resolve().parents[1]
load=lambda p:json.loads((ROOT/p).read_text(encoding='utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
checks=[]
def check(name,condition):
 assert condition,name
 checks.append({'name':name,'passed':True})
manifest=load('output/https/GT6757S-20261019-r1/manifest.json')
check('HTTP_default_and_no_deployment_or_trust',manifest['default_web_transport']=='http' and not manifest['https_enabled'] and not manifest['trust_installed'] and not manifest['deployed_to_gateway'])
template=(ROOT/'deploy/settings.example.env').read_text(encoding='utf-8')
check('source_template_TLS_values_empty',all(re.search('(?m)^'+key+r'=$',template) for key in ['TLS_CERT','TLS_KEY']))
release=load('tests/runtime-package-verification-0.3.6-20261018-r1.json')
for package in release['packages']:
 check('existing_runtime_package_unchanged_'+package['architecture'],sha(ROOT/'output/packages'/package['archive'])==package['sha256'])
 check('packaged_TLS_defaults_still_HTTP_'+package['architecture'],all(re.search('(?m)^'+key+r'=$',(ROOT/'output/packages'/('gt6757s-0.3.6-20261018-r1-'+package['architecture'])/'deploy/settings.example.env').read_text(encoding='utf-8')) for key in ['TLS_CERT','TLS_KEY']))
https=load('tests/https-certificate-verification-20261019.json')
check('certificate_and_compiled_HTTPS_checks',https['passed'] and len(https['checks'])==12 and not https['trust_installed'] and not https['hardware_accessed'])
check('tested_compiled_web_unchanged',sha(ROOT/'output/windows-amd64/gateway-web.exe')==https['compiled_web_sha256'])
acl=load('tmp/https-doc-syntax-20261019/acl.json')
check('private_key_access_restricted',acl['passed'] and acl['root_inheritance_disabled'] and all(item['passed'] for item in acl['items']))
syntax=load('tmp/https-doc-syntax-20261019/manifest.json')
check('Chinese_comments_and_PowerShell_parse',syntax['chinese_comments'] and len(syntax['blocks'])==4 and not syntax['commands_executed'])
shell=Path.home()/'.cache/codex-runtimes/codex-primary-runtime/dependencies/native/git/usr/bin/sh.exe'
env=os.environ.copy();env['PATH']=str(shell.parent)+os.pathsep+env.get('PATH','')
for block in syntax['blocks']:
 if block['syntax']!='sh':continue
 result=subprocess.run([str(shell),'-n',block['path']],capture_output=True,env=env)
 check('shell_syntax_'+Path(block['path']).name,result.returncode==0)
paths=['docs/25_HTTPS证书与可选启用说明.md','output/https/GT6757S-20261019-r1/README.md','progress.md','00_index.md']
for name in paths:
 p=ROOT/name
 for link in re.findall(r'\]\((<[^>]+>|[^)]+)\)',p.read_text(encoding='utf-8-sig')):
  url=link.strip('<>').split('#')[0]
  if not url or re.match(r'^(https?://|app:|codex:)',url):continue
  check('local_link_'+name+':'+url,(p.parent/url).resolve().exists())
result={'date':'2026-10-19','passed':True,'default_web_transport':'http','software':'0.3.6','release_unchanged':'0.3.6-20261018-r1',
 'trust_installed':False,'deployed_to_gateway':False,'device_commands_executed':False,'checks':checks,
 'documents':{name:sha(ROOT/name) for name in paths},'certificate_manifest':'output/https/GT6757S-20261019-r1/manifest.json'}
(ROOT/'tests/https-delivery-verification-20261019.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'passed':True,'default':'http','checks':len(checks)},ensure_ascii=False))
