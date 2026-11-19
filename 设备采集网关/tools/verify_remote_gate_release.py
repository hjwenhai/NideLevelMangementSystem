"""Audit the 0.4.1 mode-gate change and its immutable delivery artifacts."""
from pathlib import Path
import hashlib
import json
import re
import zipfile
from pypdf import PdfReader

ROOT = Path(__file__).resolve().parents[1]
VERSION = '0.4.1-20261020-r1'
BACKUP = ROOT / 'archive/no-remote-gate-20261020'
checks = []

def text(path):
    raw = path.read_bytes()
    return raw.decode('utf-16' if raw.startswith(b'\xff\xfe') else 'utf-8-sig')

def check(name, condition):
    assert condition, name
    checks.append({'name': name, 'passed': True})

sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
command = text(ROOT / 'src/internal/command/command.go')
old = text(BACKUP / 'src/internal/command/command.go')
gate = '\tif states[0]&0x8000 == 0 {\n\t\te.update(r, Failed, 2, "仪表非远程，未发送", states)\n\t\treturn\n\t}\n'
expected = old.replace(gate, '\t// IR51 bit15 is reported as mode information, not a gateway control gate.\n').replace(' && states[0]&0x8000 != 0', '')
check('production_command_change_is_only_two_mode_conditions_and_comment', command == expected)
check('no_remaining_command_mode_bit_gate', '0x8000' not in command and 'Failed, 2,' not in command)
check('fresh_state_fault_auth_single_action_and_readback_retained', all(s in command for s in [
    'read(4, 51, 2)', 'if states[1] != 0', 'write(129, 0x10d9)',
    'if !e.claimSend(r)', 'writeErr := write(699, r.Action)',
    'back[0]&r.Action != 0 && states[1] == 0', 'e.update(r, Uncertain, code']))
old_spec = json.loads(text(BACKUP / 'protocols/north-v3.json'))
new_spec = json.loads(text(ROOT / 'protocols/north-v3.json'))
check('old_error_2_kept_as_reserved_legacy_code', old_spec['control_errors'].pop('not_remote_unsent') == new_spec['control_errors'].pop('legacy_not_remote_unsent') == 2)
check('register_contract_and_other_error_values_unchanged', old_spec == new_spec)
check('64_slots_and_16_per_port_preserved', new_spec['capacity'] == 64)
design = json.loads(text(ROOT / 'docs/附件/north-v3-design.json'))
check('runtime_and_design_errors_agree', design['errors'] == json.loads(text(ROOT / 'protocols/north-v3.json'))['control_errors'])
unit = [json.loads(s) for s in text(ROOT / 'tests/remote-gate-unit-20261020.jsonl').splitlines() if s]
passes = [e['Test'] for e in unit if e['Action'] == 'pass' and 'Test' in e]
check('60_mode_and_action_combinations_pass', len([n for n in passes if n.startswith('TestControlDoesNotRequireRemoteBit/')]) == 60)
check('fault_auth_rejection_readback_negative_cases_pass', all('TestFreshPreflightFaultAuthRejectAndReadback/' + n in passes for n in ['local_fault', 'fault', 'auth', 'reject', 'readback']))
check('per_action_authentication_order_pass', 'TestEveryFloatOperationAuthenticatesBeforeAction' in passes)
check('focused_test_run_passed', unit[-1]['Action'] == 'pass' and not any(e['Action'] == 'fail' for e in unit))
build = text(ROOT / 'tests/build-0.4.1-20261020.log')
check('full_go_vet_test_typescript_and_three_architecture_build_passed', 'Built Windows amd64, Linux ARM64 and ARMv7' in build and 'level-gateway-web@0.4.1 build' in build and 'FAIL' not in build)
for package in ['command', 'core', 'web', 'storage', 'config', 'platform', 'clockstate', 'cache', 'modbus']:
    check('full_test_package_' + package, bool(re.search(r'^ok\s+levelgateway/src/internal/' + package + r'\s', build, re.M)))
reports = {}
for name in ['v3-live-verification-0.4.1-20261020.json', 'runtime-package-verification-' + VERSION + '.json', 'system-document-verification-0.4.1-20261020.json']:
    report = json.loads(text(ROOT / 'tests' / name))
    check('verified_report_' + name, report['passed'] and report['software'] == '0.4.1')
    reports[name] = sha(ROOT / 'tests' / name)
live = json.loads(text(ROOT / 'tests/v3-live-verification-0.4.1-20261020.json'))
check('33_compiled_live_checks_passed', len(live['checks']) == 33 and all(c['passed'] for c in live['checks']))
for name, digest in live['binaries'].items():
    check('live_test_current_binary_' + name, digest == sha(ROOT / 'output/windows-amd64' / name))
package_report = json.loads(text(ROOT / 'tests' / ('runtime-package-verification-' + VERSION + '.json')))
for package in package_report['packages']:
    archive = ROOT / 'output/packages' / package['archive']
    check(package['architecture'] + '_archive_digest', sha(archive) == package['sha256'])
    check(package['architecture'] + '_13_runtime_files', package['file_count'] == 13)
    for name, digest in package['binaries'].items():
        check(package['architecture'] + '_current_binary_' + name, sha(ROOT / 'output' / package['architecture'] / name) == digest)
arm64 = next(p for p in package_report['packages'] if p['architecture'] == 'linux-arm64')
for name in ['10_运行部署与维护.md', '23_时钟校核与断电恢复.md']:
    contents = text(ROOT / 'docs' / name)
    digests = re.findall(r'(?i)\b[0-9a-f]{64}\b', contents)
    check('documented_digest_matches_final_package_' + name, bool(digests) and all(d.lower() == arm64['sha256'] for d in digests))
    check('current_package_in_' + name, VERSION in contents and '0.4.0-20261019-r2' not in contents)
deploy = text(ROOT / 'docs/10_运行部署与维护.md')
check('documented_archive_and_payload_size_exact', all(f'{arm64[k]:,}字节' in deploy for k in ['archive_bytes', 'installed_payload_bytes']))
pdf = ROOT / 'output/pdf/GT6757S_客户通讯协议_V3.1-D2_20261020.pdf'
pagination = json.loads(text(ROOT / 'tests/v3-pdf-pagination.json'))
check('customer_pdf_source_and_output_hashes', pagination['source_sha256'] == sha(ROOT / 'docs/12_客户通讯协议V3.md') and pagination['pdf_sha256'] == sha(pdf))
pages = PdfReader(pdf).pages
check('customer_pdf_16_pages_and_quick_start_first_two', len(pages) == 16 and pagination['first_page_title'] and pagination['second_page_title'] and pagination['third_page_details'])
normalized = ''.join(p.extract_text() for p in pages).replace('\n', '').replace(' ', '')
check('customer_pdf_new_policy_and_legacy_error', '不以bit15拦截控制或判定回读失败' in normalized and '旧版“非远程未发送”，保留码' in normalized and '0.4.1不再产生' in normalized)
check('pdf_all_pages_rendered', all((ROOT / f'output/pdf-review-0.4.1-20261020/page-{i:02}.png').is_file() for i in range(1, 17)))
customer = ROOT / 'output/customer-docs/gt6757s-customer-v3.1-d2-20261020-r1.zip'
with zipfile.ZipFile(customer) as zipped:
    for source in [pdf, ROOT / 'docs/12_客户通讯协议V3.md']:
        entries = [name for name in zipped.namelist() if name.endswith('/' + source.name)]
        check('customer_zip_current_' + source.suffix, len(entries) == 1 and zipped.read(entries[0]) == source.read_bytes())
result = {'date': '2026-10-20', 'software': '0.4.1', 'release': VERSION, 'passed': True,
          'hardware_executed': False, 'checks_count': len(checks), 'checks': checks,
          'evidence_sha256': reports, 'packages': package_report['packages'],
          'customer_zip_sha256': sha(customer), 'customer_pdf_sha256': sha(pdf),
          'pdf_visual_review': '16-page contact sheet plus changed pages 4,10,16 inspected; layout passed.',
          'boundary': 'Software and simulation checks only. Install new ARM64 package and verify real instrument acceptance and motion on site.'}
(ROOT / 'tests/remote-gate-removal-delivery-20261020.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'passed': True, 'checks': len(checks), 'arm64_sha256': arm64['sha256']}, ensure_ascii=False))
