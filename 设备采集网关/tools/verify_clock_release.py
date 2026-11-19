"""Exercise compiled core/web on loopback in simulation; never access board clocks."""
from pathlib import Path
import argparse
import hashlib
import http.cookiejar
import json
import os
import secrets
import socket
import struct
import subprocess
import tempfile
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--date',default='2026-10-19')
parser.add_argument('--version', default='0.3.7')
args = parser.parse_args()
EXE = ROOT / "output/windows-amd64"
WORK = ROOT / "tmp/rtc-release-check"
WORK.mkdir(parents=True, exist_ok=True)
run_dir = Path(tempfile.mkdtemp(prefix="run-", dir=WORK))
checks = []
children = []
logs = []
flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)


def check(name, condition):
    assert condition, name
    checks.append({"name": name, "passed": True})


def port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def launch(name, args):
    stream = (run_dir / f"{name}-{len(logs)}.log").open("wb")
    logs.append(stream)
    p = subprocess.Popen([str(EXE / f"{name}.exe"), *map(str, args)], cwd=run_dir,
                         stdin=subprocess.DEVNULL, stdout=stream, stderr=stream, creationflags=flags)
    children.append(p)
    return p


def stop():
    for p in reversed(children):
        if p.poll() is None:
            p.terminate()
            p.wait(timeout=10)
    children.clear()
    for f in logs:
        f.close()
    logs.clear()


def wait_for(fn):
    deadline = time.monotonic() + 12
    last = None
    while time.monotonic() < deadline:
        try:
            value = fn()
            if value:
                return value
        except (OSError, ValueError) as exc:
            last = exc
        time.sleep(0.1)
    raise AssertionError(f"timeout: {last}")


def mb_read(tcp_port):
    with socket.create_connection(("127.0.0.1", tcp_port), timeout=3) as s:
        s.sendall(struct.pack(">HHHBBHH", 7, 0, 6, 1, 4, 1000, 24))
        def exact(n):
            b = b""
            while len(b) < n:
                part = s.recv(n-len(b))
                assert part, "closed Modbus socket"
                b += part
            return b
        header = exact(7)
        body = exact(struct.unpack_from(">H", header, 4)[0]-1)
        assert body[:2] == bytes([4, 48]), body
        return struct.unpack(">24H", body[2:])


try:
    cfg = json.loads((ROOT / "deploy/config.demo.json").read_text(encoding="utf-8"))
    cfg["meters"] = cfg["meters"][:1]
    cfg["fast_ms"] = 1000
    cfg["tcp"]["listen"] = f"127.0.0.1:{port()}"
    cfg_file = run_dir / "gateway.json"
    cfg_file.write_text(json.dumps(cfg), encoding="utf-8")
    password = secrets.token_urlsafe(24)
    auth = run_dir / "admin.json"
    init = subprocess.run([str(EXE / "gateway-web.exe"), "--init-admin", "--auth-file", str(auth)],
                          input=password+"\n", text=True, capture_output=True, creationflags=flags)
    assert init.returncode == 0, init.stderr
    check("isolated_admin_initialization", auth.is_file())
    for attempt in range(2):
        ipc_port, web_port = port(), port()
        token = run_dir / f"ipc-{attempt}.token"
        clock_file = run_dir / "clock-state.json"
        core_args = ["--simulate", "--leds=false", "--config", cfg_file, "--db", run_dir / "gateway.db",
                     "--clock-state", clock_file, "--token-file", token, "--ipc", f"127.0.0.1:{ipc_port}"]
        if attempt:
            core_args += ["--time-trusted"]
        launch("gateway-core", core_args)
        wait_for(lambda: token.is_file())
        launch("gateway-web", ["--listen", f"127.0.0.1:{web_port}", "--ipc", f"127.0.0.1:{ipc_port}",
                               "--token-file", token, "--auth-file", auth])
        base = f"http://127.0.0.1:{web_port}"
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                  urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        csrf = ""
        def request(path, data=None):
            req = urllib.request.Request(base+path, data=None if data is None else json.dumps(data).encode(),
                     headers={"Content-Type":"application/json", "X-CSRF-Token":csrf, "Origin":base})
            return opener.open(req, timeout=3).read()
        html = wait_for(lambda: request("/"))
        if not attempt:
            check("embedded_clock_diagnostics", b'id="clock-detail"' in html)
            js = request("/app.js")
            check("embedded_ui_handles_clock_result", b"result.clock" in js and b"rtc_saved" in js)
        login = json.loads(request("/api/login", {"password":password}))
        csrf = login["csrf"]
        status = json.loads(request("/api/status"))
        check("legacy_flag_does_not_bypass_checks" if attempt else "startup_unconfirmed",
              status["version"] == args.version and status["map_version"] == 3 and not status["time_trusted"])
        if attempt:
            break
        result = json.loads(request("/api/time", {"utc_ms":int(time.time()*1000)}))
        check("web_proxy_calibration_result", result["ok"] and result["clock"]["trusted"]
              and not result["clock"]["rtc_saved"] and bool(result["clock"]["warning"]))
        def fresh():
            data = json.loads(request("/api/data"))[0]
            return data if all(g["time_trusted"] for g in data["groups"][:3]) else None
        data = wait_for(fresh)
        check("new_fast_samples_trusted", data["valid"] == 127 and not data["quality"] & 128)
        summary = mb_read(int(cfg["tcp"]["listen"].split(":")[1]))
        check("north_summary_contract_unchanged", len(summary) == 24 and summary[1] == 127 and summary[22] == 1)
        check("simulation_does_not_persist_real_rtc_trust", not clock_file.exists())
        stop()
finally:
    stop()

report = {"date":args.date, "software":args.version, "passed":True,
          "hardware_executed":False, "host_clock_modified":False, "checks":checks,
          "binaries":{n:hashlib.sha256((EXE/n).read_bytes()).hexdigest()
                      for n in ("gateway-core.exe","gateway-web.exe")}, "logs":str(run_dir.relative_to(ROOT))}
dest = ROOT / "tests" / ('rtc-compiled-integration-20261016.json' if args.version == '0.3.2' else f'rtc-compiled-integration-{args.version}-{args.date.replace(chr(45), str())}.json')
dest.write_text(json.dumps(report, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
print(json.dumps(report, ensure_ascii=False, indent=2))
