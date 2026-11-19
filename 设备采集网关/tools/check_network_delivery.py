"""Verify the immutable network release and durable evidence; no hardware operations."""
from pathlib import Path
import hashlib,json,re,subprocess
R=Path(__file__).resolve().parents[1];TAG='0.7.0-20261108-r3';checks=[]
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def check(name,value):assert value,name;checks.append({'name':name,'passed':True})
def read(p):return json.loads((R/p).read_text(encoding='utf-8-sig'))
rows=[json.loads(x) for x in (R/'tests/go-test-network-0.7.0-20261108.jsonl').read_text(encoding='utf-8-sig').splitlines() if x.strip()]
check('full_Go_regression_passed',not any(x.get('Action')=='fail' for x in rows) and sum(x.get('Action')=='pass' and 'Test' in x for x in rows)>=331)
reports=['tests/network-compiled-verification-0.7.0-20261108.json','tests/v3-live-verification-0.7.0-20261108.json','tests/remote-level-live-0.7.0-20261108.json','tests/system-maintenance-compiled-verification-0.7.0-20261108.json','tests/web-upgrade-compiled-verification-0.7.0-20261108.json','tests/install-flow-verification-0.7.0-20261108.json','tests/runtime-package-verification-'+TAG+'.json','tests/system-document-verification-0.7.0-20261108.json']
compiled={}
for name in reports:
 d=read(name);check(name,d['passed'] and not d.get('hardware_executed',False))
 for binary,digest in d.get('binaries',{}).items():check(name+' final binary '+binary,sha(R/'output/windows-amd64'/binary)==digest)
 if 'checks' in d:compiled[name]=len(d['checks'])
check('host_network_untouched_in_fixture',read(reports[0])['host_network_modified'] is False)
check('production_180_second_window_observed',any(c['name']=='production_trial_window_180_seconds' and c['passed'] for c in read(reports[0])['checks']))
doc=read(reports[-1]);check('all_document_commands_syntax_checked_and_annotated',doc['verification_complete'] and doc['deployment_HTTPS_system_commands_have_Chinese_comments'])
for name,digest in doc['documents'].items():check('document current '+name,sha(R/'docs'/name)==digest)
package=read(reports[-2]);check('runtime_16_files_static_ELF_both_arches',package['verification_complete'] and all(x['file_count']==16 and x['elf_static'] for x in package['packages']))
check('actual_installer_source_tested',sha(R/'deploy/install.sh')==read(reports[5])['installer_sha256'])
manifest={}
for line in (R/'output/packages'/('SHA256SUMS-'+TAG+'.txt')).read_text().splitlines():digest,name=line.split('  ',1);manifest[name]=digest
packages=[]
for arch in ['linux-arm64','linux-armv7']:
 for ext in ['tar.gz','gwup']:
  p=R/'output/packages'/('gt6757s-'+TAG+'-'+arch+'.'+ext);check('final digest '+p.name,sha(p)==manifest[p.name]);packages.append({'file':p.relative_to(R).as_posix(),'bytes':p.stat().st_size,'sha256':sha(p)})
 bundle=R/'output/packages'/('gt6757s-'+TAG+'-'+arch+'.gwup')
 result=subprocess.run([str(R/'output/windows-amd64/gateway-release.exe'),'verify','--input',str(bundle),'--public-key',str(R/'deploy/update-trust.json'),'--arch',arch],capture_output=True)
 check('Ed25519 signature '+arch,result.returncode==0)
arm=next(p for p in packages if p['file'].endswith('linux-arm64.tar.gz'))
check('deployment_archive_hash_matches',arm['sha256'].upper() in (R/'docs/10_运行部署与维护.md').read_text(encoding='utf-8'))
ui=read('output/ui-review-network-20261108/review.json')
check('desktop_mobile_manual_apply_and_confirm',ui['passed'] and ui['manual_UI_apply'] and ui['manual_UI_confirm'] and ui['trial_confirmation_default_unchecked'] and ui['new_login_after_service_restart'] and ui['mobile_scroll_width']<=ui['mobile_width'] and ui['desktop_scroll_width']<=ui['desktop_width'])
check('source_reference_and_manual_recovery_documented',(R/'source_notes/GT6757S网络与FUN恢复依据_V1.8.md').exists() and (R/'docs/31_网络设置与失联恢复.md').exists())
check('signing_private_key_not_deployed','private/' in (R/'.gitignore').read_text() and all(not any('private' in f or f.endswith('.key') for f in x['files']) for x in package['packages']))
record={'date':'2026-11-08','software':'0.7.0','release':TAG,'passed':True,'hardware_executed':False,'host_network_modified':False,'north_protocol':'V3.2-D2','config_schema':3,'database_schema':3,'upgrade_protocol':1,'Go_cases_including_subtests':sum(x.get('Action')=='pass' and 'Test' in x for x in rows),'compiled_checks':compiled,'document_command_blocks':doc['blocks'],'checks':checks,'packages':packages,'evidence':reports,'UI_evidence':'output/ui-review-network-20261108/review.json','pending_board_acceptance':['Vendor Buildroot net.conf/net2.conf permissions and boot order','Two real NIC address/mask/default-route changes and TCP/Web reconnect','Confirmed settings survive physical power interruption','Unconfirmed trial times out and recovers after physical power interruption','Manufacturer sysrst/FUN and Console recovery on this unit','Real four-port acquisition and RTU/TCP recovery after service switch']}
(R/'tests/network-delivery-20261108.json').write_text(json.dumps(record,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'passed':True,'release':TAG,'checks':len(checks),'Go_cases':record['Go_cases_including_subtests'],'compiled_checks':sum(compiled.values()),'document_blocks':doc['blocks'],'hardware_executed':False}))
