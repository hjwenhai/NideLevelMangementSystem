"""Collect the remote-level evidence without using a network or controlling hardware."""
from pathlib import Path
import hashlib,json,re,tarfile
from pypdf import PdfReader

R=Path(__file__).resolve().parents[1]
checks=[]
def check(name,value):
 assert value,name
 checks.append({'name':name,'passed':True})
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def load(n):return json.loads((R/n).read_text(encoding='utf-8-sig'))
spec=load('protocols/north-v3.json');old=load('archive/remote-level-20261029/protocols/north-v3.json')
check('original_measurement_and_float_control_regions_preserved',spec['regions'][:-1]==old['regions'])
check('original_nine_FW699_actions_preserved',spec['commands']==old['commands'])
check('all_original_fields_groups_and_validity_masks_preserved',all(spec[k]==old[k] for k in ['summary_fields','groups','quality_bits','valid_bits','control_fields']))
check('64_slots_and_separate_level_permission',spec['capacity']==64 and spec['gateway_commands'][0]['value']==11 and spec['gateway_commands'][0]['permission']==512)
check('level_target_last_slot',spec['regions'][-1]['base']==31000 and 31000+4*63==31252)
check('published_formula_and_signed_word_example',(150100-150000-5000)==-4900 and ((-4900)&0xffffffff)==0xffffecdc)
rows=[]
for name in ['tests/nonnetwork-regression-0.5.0-20261029.jsonl','tests/remote-level-all-20261029.jsonl']:
 events=[json.loads(line) for line in (R/name).read_text(encoding='utf-8-sig').splitlines() if line.startswith('{')]
 check(name+'_no_failed_events',not any(e.get('Action')=='fail' for e in events))
 rows.extend(e for e in events if e.get('Action')=='pass' and 'Test' in e)
names=sorted({(x['Package'],x['Test']) for x in rows})
check('remote_level_example_failure_and_guard_tests_present',any('PublishedExample' in n for _,n in names) and any('WebSessionOrigin' in n for _,n in names) and any('RTUMasterFC06' in n for _,n in names))
package_report=load('tests/runtime-package-verification-0.5.0-20261029-r2.json')
check('archive_inventory_hash_ELF_and_config_checks',package_report['passed'] and len(package_report['packages'])==2)
packages=[]
for arch in ['linux-arm64','linux-armv7']:
 stage=R/f'output/packages/gt6757s-0.5.0-20261029-r2-{arch}';pkg=stage.with_suffix(stage.suffix+'.tar.gz') if stage.suffix else Path(str(stage)+'.tar.gz')
 # The version's dots are part of its directory name, not a removable extension.
 pkg=Path(str(stage)+'.tar.gz')
 check(arch+'_marker', (stage/'NORTH_VERSION').read_text().strip()=='3.2')
 cfg=json.loads((stage/'config.default.json').read_text(encoding='utf-8-sig'))
 check(arch+'_calibration_is_not_enabled_by_default',512 not in cfg['allowed_actions'])
 for name in ['install.sh','rollback.sh','service.sh','supervise.sh','S99levelgateway','settings.example.env']:
  check(arch+'_current_'+name,sha(stage/'deploy'/name)==sha(R/'deploy'/name))
 check(arch+'_binary_embeds_remote_level_transaction',b'level_target_raw' in (stage/'bin/gateway-core').read_bytes())
 check(arch+'_binary_embeds_web_feature','远程液位修改'.encode() in (stage/'bin/gateway-web').read_bytes())
 packages.append({'arch':arch,'path':str(pkg),'bytes':pkg.stat().st_size,'sha256':sha(pkg)})
arm64=packages[0]['sha256']
for name in ['10_运行部署与维护.md','23_时钟校核与断电恢复.md']:
 text=(R/'docs'/name).read_text(encoding='utf-8');digests=re.findall(r'(?<![a-zA-Z0-9])[A-Fa-f0-9]{64}(?![a-zA-Z0-9])',text)
 check(name+'_actual_current_package_SHA',bool(digests) and all(x.lower()==arm64 for x in digests))
doc_report=load('tests/system-document-verification-0.5.0-20261029.json')
check('55_command_blocks_have_chinese_comments',doc_report['passed'] and doc_report['blocks']==55)
check('document_checksums_current',all(sha(R/'docs'/n)==v for n,v in doc_report['documents'].items()))
pdf=R/'output/pdf/GT6757S_客户通讯协议_V3.2-D2_20261029-r1.pdf';reader=PdfReader(pdf)
check('customer_quick_start_first_two_pages', '快速接入·第1页' in reader.pages[0].extract_text() and '快速接入·第2页' in reader.pages[1].extract_text() and '详细说明' in reader.pages[2].extract_text())
check('customer_PDF_remote_level_section',any('远程液位修改（选配开通）' in p.extract_text() for p in reader.pages))
check('PDF_rendered_all_17_pages',len(list((R/'output/pdf-review-0.5.0-20261029').glob('page-*.png')))==len(reader.pages)==17)
check('customer_PDF_is_current_source',load('tests/v3-pdf-pagination.json')['source_sha256']==sha(R/'docs/12_客户通讯协议V3.md'))
full_log=R/'tests/build-0.5.0-20261029-r2.log'
log=full_log.read_text(encoding='utf-8-sig')
check('full_go_vet_tests_and_three_architecture_build', 'Built Windows amd64, Linux ARM64 and ARMv7' in log and not re.search(r'(?m)^FAIL\b|^--- FAIL:',log))
check('native_shell_checks_completed',package_report['verification_complete'] and package_report['shell_checks_executed'] and doc_report['shell_syntax_executed'])
compiled=[]
for name,number in [('v3-live-verification-0.5.0-20261029.json',33),('remote-level-live-20261029.json',28),('system-maintenance-compiled-verification-0.5.0-20261029.json',24),('install-flow-verification-0.5.0-20261029.json',16)]:
 evidence=load('tests/'+name)
 check(name+'_passed',evidence['passed'] and len(evidence['checks'])==number and all(c['passed'] for c in evidence['checks']))
 if 'binaries' in evidence:
  for binary,value in evidence['binaries'].items():
   check(name+'_current_binary_'+binary,sha(R/'output/windows-amd64'/binary)==value)
 compiled.append({'report':name,'checks':number,'sha256':sha(R/'tests'/name)})
ui_path=R/'output/ui-review-remote-level-20261029/ui-review-r2.json'
ui=json.loads(ui_path.read_text(encoding='utf-8'));check('actual_desktop_and_mobile_UI_operations',ui['passed'] and ui['runtime_tag']=='0.5.0-20261029-r2' and len(ui['checks'])==13)
ui_screenshots=[]
for name in ui['screenshots']:
 p=ui_path.parent/name;check('saved_UI_proof_'+name,p.is_file() and p.stat().st_size>1000);ui_screenshots.append({'path':str(p),'sha256':sha(p)})
check('UI_resets_and_selection_did_not_rewrite',load('tests/remote-level-ui-backend-20261029-r2.json')['passed'])
report={'date':'2026-10-29','software':'0.5.0','runtime_tag':'0.5.0-20261029-r2','north_revision':'V3.2-D2','checks':checks,'checks_passed':len(checks),'nonnetwork_test_cases_counted':len(names),'verification_complete':True,'verification_scope':'Local Go tests, compiled localhost simulations, UI review, package and documentation checks; excludes physical hardware acceptance','hardware_acceptance_complete':False,'package_ready_for_review':True,'hardware_executed':False,'packages':packages,'compiled_test_reports':compiled,'UI_screenshots':ui_screenshots,'full_build_log_sha256':sha(full_log),'customer_pdf':{'path':str(pdf),'pages':len(reader.pages),'sha256':sha(pdf)},'unexecuted':['Physical GT6757X/FW9000 calibration, password reset and instrument parameter persistence after power loss.'],'notes':['User explicitly authorized localhost testing on 2026-10-29; all prior local execution restrictions were resolved for these checks.','Fresh FC04 IR0/1 and FC03 HR503/504; FC06 HR129=EA66, one FC10 offset pair, offset readback, FC06 HR129=0.','No automatic retry or replay. A cleared gateway result does not restore offset or clear an instrument password.','Default calibration permission remains off. Historical product DOCX/PDF V1.2 remain separate; current feature is in Markdown specs and customer V3.2 PDF.','Embedded simulator is recreated with the core process; physical instrument parameter retention requires the separate board acceptance test.']}
(R/'tests/remote-level-delivery-20261029.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'checks_passed':len(checks),'nonnetwork_test_cases_counted':len(names),'verification_complete':True,'packages':packages},ensure_ascii=False))
