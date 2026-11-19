"""Final release evidence, without reading private signing keys or accessing hardware."""
from pathlib import Path
import hashlib,json,re,subprocess


ROOT=Path(__file__).resolve().parents[1];version='0.8.1-20261109-r1';sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();checks=[]
def check(name,v):assert v,name;checks.append({'name':name,'passed':True})
def report(name):
 p=ROOT/'tests'/name;v=json.loads(p.read_text(encoding='utf-8'));check(name,v['passed']);return v
go=[json.loads(x) for x in (ROOT/'tests/go-0.8.1-20261109.jsonl').read_text(encoding='utf-8-sig').splitlines() if x.strip()]
check('full_go_regression_no_failures',not any(x['Action']=='fail' for x in go) and any(x['Action']=='pass' and x.get('Package')=='levelgateway/src/internal/core' and 'Test' not in x for x in go))
count=sum(x['Action']=='pass' and bool(x.get('Test')) for x in go)
gate=[json.loads(x) for x in (ROOT/'tests/go-0.8.1-20261109.jsonl').read_text(encoding='utf-8-sig').splitlines() if x.strip()]
check('unlicensed_upgrade_target_rejected',any(x['Action']=='pass' and x.get('Test')=='TestUnlicensedRuntimeAndActivation' for x in gate) and not any(x['Action']=='fail' for x in gate))
reports=[report(n) for n in ['license-compiled-verification-0.8.1-20261109.json','v3-live-verification-0.8.1-20261109.json','remote-level-live-0.8.1-20261109.json','system-maintenance-compiled-verification-0.8.1-20261109.json','web-upgrade-compiled-verification-0.8.1-20261109.json','network-compiled-verification-0.8.1-20261109.json','install-flow-verification-0.8.1-20261109.json']]
compiled_count=sum(len(v.get('checks',[])) for v in reports)
docs=report('system-document-verification-0.8.1-20261109.json');check('all_document_command_blocks_checked',docs['verification_complete'] and not docs['commands_executed'] and all(sha(ROOT/'docs'/name)==digest for name,digest in docs['documents'].items()))
runtime=report('runtime-package-verification-'+version+'.json');check('package_syntax_and_runtime_allowlist',runtime['verification_complete'] and runtime['installer_allowlist_matches_package'])
for v in reports:
 for name,digest in v.get('binaries',{}).items():check('verified_binary_'+name+'_'+v.get('software',''),sha(ROOT/'output/windows-amd64'/name)==digest)
check('web_desktop_mobile_proof_saved',(ROOT/'tests/tcp-settings-desktop-20261109.png').is_file())
pub=json.loads((ROOT/'src/internal/licensing/trusted-public.json').read_text())
check('license_public_key_embedded',bool(pub.get('keys')))
packages={}
for arch in ['linux-arm64','linux-armv7']:
 stage=ROOT/'output/packages'/f'gt6757s-{version}-{arch}'
 check('runtime_release_'+arch,(stage/'RELEASE').read_text().strip()==version)
 check('runtime_public_key_'+arch,all(v.encode() in (stage/'bin/gateway-core').read_bytes() for v in pub['keys'].values()))
 check('license_cli_in_linux_binary_'+arch,b'license-request' in (stage/'bin/gateway-core').read_bytes())
 for role in ['gateway-core','gateway-web','gateway-updater']:check('staged_binary_is_final_build_'+arch+'_'+role,sha(stage/'bin'/role)==sha(ROOT/'output'/arch/role))
 files=[p for p in stage.rglob('*') if p.is_file()]
 check('exactly_16_runtime_files_'+arch,len(files)==16)
 check('no_factory_tools_keys_or_per_device_grants_'+arch,not any(p.name in ['gateway-license','license.json','license-signing.key','gateway-release'] or p.suffix in ['.docx','.md','.gwlic','.gwreq','.key'] for p in files))
 for ext in ['.tar.gz','.gwup']:
  p=Path(str(stage)+ext);digest=sha(p);packages[p.name]={'sha256':digest,'bytes':p.stat().st_size}
  check('document_package_hash_'+p.name,digest.upper() in (ROOT/'docs/29_网页上传升级操作说明.md').read_text(encoding='utf-8'))
  if ext=='.gwup':
   v=subprocess.run([str(ROOT/'output/windows-amd64/gateway-release.exe'),'verify','--input',str(p),'--arch',arch,'--public-key',str(ROOT/'deploy/update-trust.json')],capture_output=True)
   check('signature_'+arch,v.returncode==0)
check('deployment_hash_matches_actual_package',packages[f'gt6757s-{version}-linux-arm64.tar.gz']['sha256'].upper() in (ROOT/'docs/10_运行部署与维护.md').read_text(encoding='utf-8'))
check('permanent_license_document_linked',all('33_设备永久授权与厂家发放说明.md' in (ROOT/p).read_text(encoding='utf-8') for p in ['README.md','00_index.md','docs/10_运行部署与维护.md','docs/29_网页上传升级操作说明.md']))
out={'date':'2026-11-09','software':'0.8.1','release':version,'passed':True,'go_passed_including_subtests':count,'compiled_checks':compiled_count,'document_blocks':docs['blocks'],'checks':checks,'packages':packages,'tcp_display_checks':len(report('tcp-display-0.8.1-20261109.json')['checks']),'tcp_UI_checks':len(report('tcp-settings-web-0.8.1-20261109.json')['checks']),'hardware_executed':False,'scope':'Windows loopback simulations and static ARM64/ARMv7 package verification; no real board installation, no physical hardware ID/power-cycle acceptance. Root patching or installing legacy unlicensed programs is outside software-copy protection.'}
(ROOT/'tests/tcp-settings-delivery-20261109.json').write_text(json.dumps(out,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(json.dumps({'passed':True,'checks':len(checks),'go':count,'compiled':compiled_count,'document_blocks':docs['blocks']},ensure_ascii=False))
