"""Run installer workflows against isolated fake services; never install on hardware."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import shlex
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
WORK = ROOT / "tmp/install-flow-review"
WORK.mkdir(parents=True, exist_ok=True)
SHELL = Path.home() / ".cache/codex-runtimes/codex-primary-runtime/dependencies/native/git/usr/bin/sh.exe"
SOURCE = (ROOT / "deploy/install.sh").read_text(encoding="utf-8")
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--date',default='2026-10-19')
parser.add_argument('--version', default='0.4.0')
options = parser.parse_args()
checks = []


def posix(p):
    s = p.resolve().as_posix()
    return "/"+s[0].lower()+s[2:] if re.match(r"^[A-Za-z]:/", s) else s


def put(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8", newline="\n")


def fixture(name, existing=False, guided=False, allow_tty_fixture=False, bad_health=False):
    case = Path(tempfile.mkdtemp(prefix=name+"-", dir=WORK))
    base, pkg, bin_dir = case / "base", case / "package", case / "fake-bin"
    assert base.resolve().is_relative_to(WORK.resolve())
    source = SOURCE.replace("BASE=/home/level-gateway", "BASE="+shlex.quote(posix(base)), 1)
    if allow_tty_fixture:
        # Windows cannot supply a POSIX tty to this test. Only this fixture bypasses
        # the tty predicate; production keeps its tty requirement and real stty.
        assert source.count("[ -t 0 ]") == 1
        source = source.replace("[ -t 0 ]", "[ -r /dev/null ]")
    put(pkg / "deploy/install.sh", source)
    for name in ["supervise.sh", "rollback.sh", "S99levelgateway", "settings.example.env"]:
        put(pkg / "deploy" / name, (ROOT / "deploy" / name).read_text(encoding="utf-8"))
    put(pkg / "deploy/service.sh", "#!/bin/sh\nprintf '%s\\n' \"$1\" >> "+shlex.quote(posix(case / "service-calls"))+"\n")
    core = '''#!/bin/sh
set -eu
case "$1" in
 --network-base) test "$3" = --network-check ;;
 --check-license) test -f "$3" ;;
 --migrate-config) test -f "$3"; echo migrated ;;
 --check-config) test -f "$3"; echo 'valid config' ;;
 --check-listen) test -n "$2"; echo 'valid listen address' ;;
 --init-config) printf '{"fixture":true}\\n' > "$2" ;;
 --health-check) HEALTH_RESULT ;;
 --db) cp "$2" "$4" ;;
 *) echo 'unexpected fake-core command' >&2; exit 2 ;;
esac
'''.replace("HEALTH_RESULT", "exit 1" if bad_health else "echo healthy")
    put(pkg / "bin/gateway-core", core)
    put(pkg / "bin/gateway-web", '''#!/bin/sh
set -eu
IFS= read -r password
[ "${#password}" -ge 12 ] && [ "${#password}" -le 128 ] || exit 1
printf '{"fixture_credential":true}\\n' > "$3"
''')
    put(pkg / "bin/gateway-updater", "#!/bin/sh\nexit 0\n")
    put(pkg / "deploy/update-trust.json", '{"keys":{"fixture":"public"}}\n')
    put(pkg / "UPDATE_VERSION", "1\n")
    put(pkg / "config.default.json", '{"fixture_default":true}\n')
    put(pkg / "RELEASE", "fixture-new\n")
    put(pkg / "ARCH", "linux-arm64\n")
    put(pkg / "NORTH_VERSION", "3.2\n")
    entries = []
    for p in sorted(pkg.rglob("*")):
        if p.is_file():
            entries.append(hashlib.sha256(p.read_bytes()).hexdigest()+"  "+p.relative_to(pkg).as_posix())
    put(pkg / "SHA256SUMS", "\n".join(entries)+"\n")
    put(bin_dir / "uname", "#!/bin/sh\necho aarch64\n")
    hash_helper = case / "check_hash.py"
    put(hash_helper, "import hashlib,pathlib,sys\nfor line in pathlib.Path(sys.argv[2]).read_text().splitlines():\n digest,name=line.split('  ',1)\n assert hashlib.sha256(pathlib.Path(name).read_bytes()).hexdigest()==digest,name\n print(name+': OK')\n")
    put(bin_dir / "sha256sum", "#!/bin/sh\nexec "+shlex.quote(posix(Path(sys.executable)))+" "+shlex.quote(posix(hash_helper))+" \"$@\"\n")
    link_helper = case / "link.py"
    put(link_helper, "import os,sys\nos.symlink(sys.argv[-2],sys.argv[-1],target_is_directory=True)\n")
    put(bin_dir / "ln", "#!/bin/sh\nexec "+shlex.quote(posix(Path(sys.executable)))+" "+shlex.quote(posix(link_helper))+" \"$@\"\n")
    put(bin_dir / "chmod", "#!/bin/sh\nexit 0\n")
    put(bin_dir / "stty", "#!/bin/sh\nexit 0\n")
    put(bin_dir / "sleep", "#!/bin/sh\nexit 0\n")
    if existing:
        old = base / "releases/fixture-old"
        put(old / "deploy/service.sh", (pkg / "deploy/service.sh").read_text(encoding="utf-8"))
        put(old / "NORTH_VERSION", "3.2\n")
        put(base / "config/gateway.json", '{"site_binding":"keep"}\n')
        put(base / "config/service.env", "WEB_LISTEN=192.168.8.230:8088\nHARDWARE_WRITES=1\nALLOW_CLOCK_SET=0\nLEDS=0\nSITE_ONLY=keep\n")
        put(base / "config/license.json", '{"fixture_license":true}\n')
        put(base / "data/admin.json", '{"original_admin":true}\n')
        put(base / "data/gateway-v2.db", "original database\n")
        os.symlink(old.resolve(), base / "current", target_is_directory=True)
    env = os.environ.copy()
    env["PATH"] = str(bin_dir)+os.pathsep+str(SHELL.parent)+os.pathsep+env.get("PATH", "")
    args = [str(SHELL), posix(pkg / "deploy/install.sh")]
    if guided:
        args.append("--guided")
    args.append(posix(pkg))
    return case, base, pkg, args, env


def run(case, args, env, input=""):
    out = subprocess.run(args, env=env, input=input.encode("utf-8"), capture_output=True, timeout=30)
    out.stdout, out.stderr = out.stdout.decode("utf-8"), out.stderr.decode("utf-8")
    put(case / "result.log", out.stdout+out.stderr)
    return out


case, base, pkg, args, env = fixture("manual-first")
out = run(case, args, env)
assert out.returncode == 0, out.stdout+out.stderr
assert (base / "config/gateway.json").read_bytes() == (pkg / "config.default.json").read_bytes()
assert not (base / "data/admin.json").exists() and not (case / "service-calls").exists()
assert not (base / "data/install.lock").exists()
checks.append({"name":"manual_first_install_waits_for_admin", "passed":True})

case, base, pkg, args, env = fixture("upgrade", existing=True)
out = run(case, args, env)
assert out.returncode == 0, out.stdout+out.stderr
assert (base / "config/gateway.json").read_text() == '{"site_binding":"keep"}\n'
assert (base / "data/admin.json").read_text() == '{"original_admin":true}\n'
assert (base / "data/gateway-v2.db").read_text() == 'original database\n'
assert (base / "data/gateway.before-fixture-new.db").read_text() == 'original database\n'
service = (base / "config/service.env").read_text()
assert "RTC_DEVICE=/dev/rtc0" in service and "WEB_LISTEN=192.168.8.230:8088" in service
assert "HARDWARE_WRITES=1" in service and "ALLOW_CLOCK_SET=0" in service and "SITE_ONLY=keep" in service
assert "RTC_DEVICE=" not in (base / "config/service.before-fixture-new.env").read_text()
assert not (base / "data/install.lock").exists()
checks.append({"name":"upgrade_preserves_site_values_and_adds_missing_defaults_with_backup", "passed":True})

case, base, pkg, args, env = fixture("guided-first", guided=True, allow_tty_fixture=True)
password = "Fixture-Only-Strong-2026"
# First password pair deliberately differs; the installer must retry without echoing secrets.
out = run(case, args, env, "\n\n\n\n\n\ny\n"+password+"\ndifferent-password\n"+password+"\n"+password+"\n")
assert out.returncode == 0, out.stdout+out.stderr
assert (base / "data/admin.json").exists()
service = (base / "config/service.env").read_text()
assert "WEB_LISTEN=192.168.0.230:8088" in service and "ALLOW_CLOCK_SET=1" in service and "LEDS=0" in service
assert password not in out.stdout+out.stderr
assert "start" in (case / "service-calls").read_text()
assert not (base / "data/install.lock").exists()
checks.append({"name":"guided_first_prompts_config_password_retry_and_starts", "passed":True, "tty":"fixture predicate only; real terminal masking pending board test"})

case, base, pkg, args, env = fixture("guided-upgrade", existing=True, guided=True, allow_tty_fixture=True)
out = run(case, args, env, "y\n")
assert out.returncode == 0, out.stdout+out.stderr
assert "original_admin" in (base / "data/admin.json").read_text()
checks.append({"name":"guided_upgrade_keeps_existing_admin_and_config", "passed":True})

case, base, pkg, args, env = fixture("cancel", guided=True, allow_tty_fixture=True)
out = run(case, args, env, "\n\n\n\n\n\nn\n")
assert out.returncode == 0 and not base.exists(), out.stdout+out.stderr
checks.append({"name":"cancel_before_mutation", "passed":True})

case, base, pkg, args, env = fixture("non-tty", guided=True)
out = run(case, args, env)
assert out.returncode != 0 and not base.exists()
checks.append({"name":"production_guided_mode_requires_terminal", "passed":True})

case, base, pkg, args, env = fixture("rollback", existing=True, bad_health=True)
out = run(case, args, env)
assert out.returncode != 0
assert "RTC_DEVICE=" not in (base / "config/service.env").read_text()
assert "original database" in (base / "data/gateway-v2.db").read_text()
assert not (base / "data/install.lock").exists()
checks.append({"name":"failed_upgrade_restores_service_env_without_restoring_database", "passed":True})

for marker in ("3", "3.1"):
    case, base, pkg, args, env = fixture("old-no-auto-rollback", existing=True, bad_health=True)
    put(base / "releases/fixture-old/NORTH_VERSION", marker+"\n")
    out = run(case, args, env)
    assert out.returncode != 0 and "older runtime is blocked" in out.stdout
    assert (base / "current").resolve().name == "fixture-new"
    assert (case / "service-calls").read_text().splitlines() == ["stop", "start", "stop"]
    assert (base / "data/gateway.before-fixture-new.db").read_text() == "original database\n"
    checks.append({"name":"remote_level_upgrade_failure_blocks_old_marker_"+marker, "passed":True})

# Missing authorization must fail before stopping or switching the old release.
case, base, pkg, args, env = fixture("missing-license", existing=True)
(base / "config/license.json").unlink()
out = run(case, args, env)
assert out.returncode != 0 and (base / "current").resolve().name == "fixture-old"
assert not (case / "service-calls").exists()
checks.append({"name":"missing_license_aborts_before_old_service_stop", "passed":True})

# Exercise the real compiled configuration generator separately from shell-service fakes.
binary = ROOT / "output/windows-amd64/gateway-core.exe"
cfg_dir = Path(tempfile.mkdtemp(prefix="real-config-", dir=WORK))
for count in (1, 16, 24, 32, 48, 64):
    dest = cfg_dir / f"config-{count}.json"
    args = [str(binary), "--init-config", str(dest), "--meters", str(count), "--init-tcp-listen", "192.168.0.230:502", "--init-south-baud", "9600"]
    if count == 1:
        args += ["--init-first-address", "7"]
    out = subprocess.run(args, capture_output=True, text=True, encoding="utf-8")
    assert out.returncode == 0, out.stderr
    cfg = json.loads(dest.read_text())
    assert len(cfg["meters"]) == count and cfg["tcp"]["listen"] == "192.168.0.230:502"
    assert not cfg["allowed_actions"] and not cfg["tcp"]["allow_writes"]
    if count == 1:
        assert cfg["meters"][0]["address"] == 7
    checks.append({"name":f"real_generator_{count}_meters_read_only", "passed":True})
for address, expected in [("192.168.0.230:8088", True), ("999.1.1.1:8088", False), ("192.168.0.230:0", False), ("host;bad:80", False)]:
    out = subprocess.run([str(binary), "--check-listen", address], capture_output=True)
    assert (out.returncode == 0) == expected
checks.append({"name":"real_listen_address_validation", "passed":True})

report = {"date":options.date, "software":options.version, "passed":True,
          "hardware_executed":False, "production_script_modified_for_test":False,
          "scope":"Copies of install.sh run only under tmp with BASE redirected and fake runtime services. SHA256 and native Windows symlinks are real; chmod, guided tty predicate/stty are simulated. Real Windows core config generator verified separately.",
          "installer_sha256":hashlib.sha256(SOURCE.encode()).hexdigest(), "checks":checks}
(ROOT / f'tests/install-flow-verification-{options.version}-{options.date.replace(chr(45), str())}.json').write_text(json.dumps(report, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
print(json.dumps(report, ensure_ascii=False, indent=2))
