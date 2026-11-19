"""Cross-check final 0.4.2 web reset artifacts and software evidence; no hardware access."""
from pathlib import Path
import hashlib,json,re,zipfile
from pypdf import PdfReader

ROOT=Path(__file__).resolve().parents[1];VERSION='0.4.2-20261027-r1';checks=[]
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def text(p):
 raw=p.read_bytes();return raw.decode('utf-16' if raw.startswith(b'\xff\xfe') else 'utf-8-sig')
def check(name,condition):assert condition,name;checks.append({'name':name,'passed':True})
reports={}
for name in ['web-control-reset-compiled-20261027.json','web-control-reset-browser-20261027.json','v3-live-verification-0.4.2-20261027.json','system-maintenance-compiled-verification-0.4.2-20261027.json','system-document-verification-0.4.2-20261027.json','runtime-package-verification-'+VERSION+'.json']:
 p=ROOT/'tests'/name;report=json.loads(text(p));check('passed_'+name,report['passed'] and report['software']=='0.4.2');reports[name]={'sha256':sha(p),'checks':len(report.get('checks',[]))}
 for binary,digest in report.get('binaries',{}).items():check('current_binary_'+name+'_'+binary,sha(ROOT/'output/windows-amd64'/binary)==digest)
ui=json.loads(text(ROOT/'tests/web-control-reset-browser-20261027.json'))
for name,digest in ui['screenshots'].items():check('browser_proof_'+name,sha(ROOT/'output/ui-review-web-reset-20261027'/name)==digest)
check('mobile_does_not_overflow',ui['mobile_scroll_width']<=ui['mobile_viewport'][0])
units=[json.loads(line) for line in text(ROOT/'tests/web-control-reset-unit-20261027.jsonl').splitlines() if line]
check('focused_reset_tests_passed',units[-1]['Action']=='pass' and not any(e['Action']=='fail' for e in units))
check('eight_state_channel_slot_combinations_passed',sum(e['Action']=='pass' and e.get('Test','').startswith('TestWebResetFailedAndUncertainBothChannels/') for e in units)==8)
check('non_resettable_states_and_store_failure_test_passed',any(e['Action']=='pass' and e.get('Test')=='TestWebResetRejectsNonResettableStatesAndStoreFailure' for e in units))
build=text(ROOT/'tests/build-0.4.2-20261027.log');check('complete_build_and_tests_passed','Built Windows amd64, Linux ARM64 and ARMv7' in build and 'level-gateway-web@0.4.2 build' in build and 'FAIL' not in build)
command=text(ROOT/'src/internal/command/command.go');old=text(ROOT/'archive/web-control-reset-20261027/src/internal/command/command.go')
expected=old.replace('c.State != Uncertain || c.Request.ID != id','(c.State != Failed && c.State != Uncertain) || id == "" || c.Request.ID != id')
check('engine_diff_only_extends_guard_no_dispatch_changes',command==expected)
check('IR51_mode_gate_stays_removed','states[0]&0x8000' not in command)
app=text(ROOT/'src/web/app.ts');html=text(ROOT/'src/internal/web/static/index.html')
check('UI_includes_states_4_and_5','cycle.state!==4&&cycle.state!==5' in app)
check('UI_has_confirmation_and_no_auto_reset','req.site_verified' in app and 'control/maintenance-reset' in app and '确认复位（不重发动作）' in html)
check('UI_does_not_hide_unconfigured_slots','if(!commands.control_error)for(const slot of commands.controls??[])' in app)
check('panel_contains_old_failure_explanation','旧版本保留的失败记录' in html and '复位失败结果' in app and '核查并复位' in app)
package=json.loads(text(ROOT/'tests'/('runtime-package-verification-'+VERSION+'.json')))
for item in package['packages']:
 check(item['architecture']+'_archive_hash',sha(ROOT/'output/packages'/item['archive'])==item['sha256'])
 check(item['architecture']+'_13_runtime_files',item['file_count']==13)
 for name,digest in item['binaries'].items():check(item['architecture']+'_current_'+name,sha(ROOT/'output'/item['architecture']/name)==digest)
arm64=next(p for p in package['packages'] if p['architecture']=='linux-arm64')
for name in ['10_运行部署与维护.md','23_时钟校核与断电恢复.md']:
 contents=text(ROOT/'docs'/name);digests=re.findall(r'(?i)\b[0-9a-f]{64}\b',contents)
 check('document_digest_'+name,bool(digests) and all(d.lower()==arm64['sha256'] for d in digests))
 check('current_package_'+name,VERSION in contents and '0.4.1-20261020-r1' not in contents)
deploy=text(ROOT/'docs/10_运行部署与维护.md')
check('correct_compressed_and_payload_bytes',all(f'{arm64[k]:,}字节' in deploy for k in ['archive_bytes','installed_payload_bytes']))
doccheck=json.loads(text(ROOT/'tests/system-document-verification-0.4.2-20261027.json'))
for name,digest in doccheck['documents'].items():check('current_document_evidence_'+name,sha(ROOT/'docs'/name)==digest)
pdf=ROOT/'output/pdf/GT6757S_客户通讯协议_V3.1-D2_20261027.pdf';pagination=json.loads(text(ROOT/'tests/v3-pdf-pagination.json'))
check('PDF_and_source_hashes',sha(pdf)==pagination['pdf_sha256'] and sha(ROOT/'docs/12_客户通讯协议V3.md')==pagination['source_sha256'])
pages=PdfReader(pdf).pages;check('16_pages_quick_start_layout',len(pages)==16 and all(pagination[k] for k in ['first_page_title','second_page_title','third_page_details']))
check('all_PDF_pages_rendered',all((ROOT/f'output/pdf-review-0.4.2-20261027/page-{n:02}.png').is_file() for n in range(1,17)))
check('customer_PDF_web_reset_documented','网页也提供状态4、5' in ''.join(p.extract_text() for p in pages).replace('\n','').replace(' ',''))
customer=ROOT/'output/customer-docs/gt6757s-customer-v3.1-d2-20261027-r1.zip'
with zipfile.ZipFile(customer) as z:
 for p in [pdf,ROOT/'docs/12_客户通讯协议V3.md']:
  entries=[n for n in z.namelist() if n.endswith('/'+p.name)];check('customer_zip_matches_'+p.suffix,len(entries)==1 and z.read(entries[0])==p.read_bytes())
result={'date':'2026-10-27','software':'0.4.2','release':VERSION,'passed':True,'hardware_executed':False,'checks_count':len(checks),'checks':checks,'evidence':reports,'packages':package['packages'],'customer_zip_sha256':sha(customer),'pdf_visual_review':'16-page contact sheet and changed pages 9/16 reviewed; quick-start first two pages preserved.','boundary':'Isolated simulation only. Existing site controls are preserved until an authorized person installs the update and manually resets the selected result.'}
(ROOT/'tests/web-control-reset-delivery-20261027.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({'passed':True,'checks':len(checks),'arm64_sha256':arm64['sha256'],'evidence':reports},ensure_ascii=False))
