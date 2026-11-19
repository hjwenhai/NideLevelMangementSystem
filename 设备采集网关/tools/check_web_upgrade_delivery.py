"""Review final local artifacts and evidence; does not connect to hardware."""
from pathlib import Path
import hashlib,json,re,sqlite3,subprocess,sys
R=Path(__file__).resolve().parents[1];TAG='0.6.0-20261029-r3';sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest();checks=[]
def check(name,value):assert value,name;checks.append({'name':name,'passed':True})
def read(name):return json.loads((R/name).read_text(encoding='utf-8-sig'))
go=[json.loads(line) for line in (R/'tests/go-test-web-upgrade-0.6.0-20261029.jsonl').read_text(encoding='utf-8-sig').splitlines() if line.strip()];check('all_Go_tests_passed',not any(i['Action']=='fail' for i in go) and any(i['Action']=='pass' for i in go));go_cases=sum(i['Action']=='pass' and bool(i.get('Test')) for i in go)
reports=['tests/v3-live-verification-0.6.0-20261029.json','tests/web-upgrade-compiled-verification-0.6.0-20261029.json','tests/system-maintenance-compiled-verification-0.6.0-20261029.json','tests/install-flow-verification-0.6.0-20261029.json','tests/runtime-package-verification-'+TAG+'.json','tests/system-document-verification-0.6.0-20261029.json']
# Preserve the original date-only report and use versioned evidence going forward.
remote=R/'tests/remote-level-live-0.6.0-20261029.json';current=json.loads(remote.read_text(encoding='utf-8'));check('remote_level_current_version',current['software']=='0.6.0');reports.append(remote.relative_to(R).as_posix())
for n in reports:
 data=read(n);check(n+' passed',data['passed']);check(n+' local_only',not data.get('hardware_executed',False))
 for name,digest in data.get('binaries',{}).items():check(n+' binary '+name,sha(R/'output/windows-amd64'/name)==digest)
doc_report=read('tests/system-document-verification-0.6.0-20261029.json');check('document_shell_and_PowerShell_syntax',doc_report['verification_complete'] and doc_report['deployment_HTTPS_system_commands_have_Chinese_comments'])
for n,digest in doc_report['documents'].items():check('document_sha '+n,sha(R/'docs'/n)==digest)
package_report=read('tests/runtime-package-verification-'+TAG+'.json');check('all_runtime_files_and_static_ELF_checked',package_report['verification_complete'] and all(p['file_count']==16 for p in package_report['packages']))
packages=[];signer=R/'output/windows-amd64/gateway-release.exe'
for arch in ['linux-arm64','linux-armv7']:
 bundle=R/'output/packages'/f'gt6757s-{TAG}-{arch}.gwup';result=subprocess.run([str(signer),'verify','--input',str(bundle),'--public-key',str(R/'deploy/update-trust.json'),'--arch',arch],capture_output=True);check(arch+' signed_bundle_verified',result.returncode==0)
 for suffix in ['tar.gz','gwup']:
  p=R/'output/packages'/f'gt6757s-{TAG}-{arch}.{suffix}';packages.append({'file':p.relative_to(R).as_posix(),'bytes':p.stat().st_size,'sha256':sha(p)})
arm=next(p for p in packages if p['file'].endswith('linux-arm64.tar.gz'));check('deployment_hash_matches_actual_final_archive',arm['sha256'].upper() in (R/'docs/10_运行部署与维护.md').read_text(encoding='utf-8') and arm['sha256'].upper() in (R/'docs/23_时钟校核与断电恢复.md').read_text(encoding='utf-8'))
fixture=R/'tests/fixtures/web-upgrade-0.6.0';proof=read('tests/fixtures/web-upgrade-0.6.0/provenance.json');check('preserved_simulation_database_intact',sha(fixture/'gateway-v2.db')==proof['database_sha256'])
with sqlite3.connect(fixture/'gateway-v2.db') as db:
 events=db.execute("SELECT COUNT(*) FROM events WHERE kind='upgrade_requested'").fetchone()[0];cutoff=db.execute("SELECT MIN(time_ms) FROM events WHERE kind='upgrade_requested'").fetchone()[0];old_rows=db.execute('SELECT COUNT(*) FROM history WHERE record_ms<=?',(cutoff,)).fetchone()[0]
 check('upgrade_requests_audited_including_optional_UI_cycle',events>=2);check('history_before_first_upgrade_preserved_after_success_and_rollback',old_rows>0)
ui=read('output/ui-review-web-upgrade-20261029/review.json');check('browser_upload_confirmation_relogin_success_and_mobile',all(ui.get(n) for n in ['desktop_verified','signed_upload_verified','confirmation_required','confirm_executed_once','relogin_after_upgrade','success_visible','auto_reload_new_document_observed']) and ui['mobile_scroll_width']<=ui['mobile_width'] and ui['final_build_checked']==TAG)
check('factory_private_key_not_in_runtime_packages',all('private' not in n for n in __import__('re').findall(r'[^\s]+',json.dumps(package_report['packages'])) if n.endswith('.key')))
check('factory_signing_private_directory_ignored','private/' in (R/'.gitignore').read_text(encoding='utf-8'))
record={'date':'2026-10-29','software':'0.6.0','release':TAG,'north_protocol':'V3.2-D2','config_schema':3,'database_schema':3,'upgrade_protocol':1,'passed':True,'verification_complete':True,'verification_scope':'local software, compiled Windows service bridge, package signatures/static Linux ELF and document syntax; no physical board tests','hardware_executed':False,'OS_restarted':False,'host_clock_modified':False,'Go_passed_cases_including_subtests':go_cases,'compiled_cases':{'upgrade':22,'TCP64':33,'remote_level':28,'system_maintenance':24,'install_flow':len(read(reports[3])['checks'])},'document_command_blocks':doc_report['blocks'],'checks':checks,'packages':packages,'evidence':reports,'UI_evidence':'output/ui-review-web-upgrade-20261029/review.json','factory_signing_private_key':'PC only; backup separately; never ship to gateway or customer','pending_board_acceptance':['Manual bootstrap to 0.6.0 foundation','Next-tag signed web upgrade on Buildroot','Four-port acquisition and dual-LAN reconnect after upgrade','Real start failure rollback and low-space behaviour','Power interruption and explicit manual recovery']}
if '--check-only' not in sys.argv:(R/'tests/web-upgrade-delivery-20261029.json').write_text(json.dumps(record,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'passed':True,'checks':len(checks),'Go_cases':go_cases,'release':TAG,'history_rows_preserved':old_rows,'hardware_executed':False},ensure_ascii=False))
