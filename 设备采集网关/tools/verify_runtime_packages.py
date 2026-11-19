"""Check runtime-only archives using the standard library; never install or contact hardware."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import struct
import subprocess
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[1]
FILES = {
    "bin/gateway-core", "bin/gateway-web", "bin/gateway-updater", "deploy/update-trust.json", "UPDATE_VERSION",
    "deploy/install.sh", "deploy/service.sh", "deploy/supervise.sh",
    "deploy/rollback.sh", "deploy/S99levelgateway", "deploy/settings.example.env",
    "config.default.json", "RELEASE", "ARCH", "NORTH_VERSION", "SHA256SUMS",
}
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--version", default="0.7.0-20261108-r3")
parser.add_argument("--shell", help="Optional sh executable used only for syntax and read-only marker checks")
parser.add_argument("--skip-shell-checks", action="store_true", help="Explicitly record shell checks as unexecuted when the OS blocks MSYS; still verify every package file")
args = parser.parse_args()
if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}", args.version):
    parser.error("Invalid version")
sha = lambda b: hashlib.sha256(b).hexdigest()
packages = []
for arch, cls, machine in [("linux-arm64", 2, 183), ("linux-armv7", 1, 40)]:
    stage = ROOT / "output/packages" / f"gt6757s-{args.version}-{arch}"
    actual = {p.relative_to(stage).as_posix() for p in stage.rglob("*") if p.is_file()}
    assert actual == FILES, (arch, "unexpected/missing runtime files", actual ^ FILES)
    assert not any(p.is_symlink() for p in stage.rglob("*"))
    entries = {}
    for line in (stage / "SHA256SUMS").read_text(encoding="utf-8").splitlines():
        digest, name = line.split("  ", 1)
        assert name in FILES and name not in entries and name != "SHA256SUMS"
        assert sha((stage / name).read_bytes()) == digest, (arch, name)
        entries[name] = digest
    assert set(entries) == FILES - {"SHA256SUMS"}
    assert (stage / "RELEASE").read_bytes() == (args.version + "\n").encode()
    assert (stage / "ARCH").read_bytes() == (arch + "\n").encode()
    assert (stage / "NORTH_VERSION").read_bytes() == b"3.2\n"
    assert (stage / "UPDATE_VERSION").read_bytes() == b"1\n"
    binary_hashes = {}
    for name in ("gateway-core", "gateway-web", "gateway-updater"):
        binary = (stage / "bin" / name).read_bytes()
        assert binary == (ROOT / "output" / arch / name).read_bytes()
        assert binary[:4] == b"\x7fELF" and binary[4:6] == bytes([cls, 1])
        assert struct.unpack_from("<H", binary, 18)[0] == machine
        phoff = struct.unpack_from("<Q" if cls == 2 else "<I", binary, 32 if cls == 2 else 28)[0]
        entsize, count = struct.unpack_from("<HH", binary, 54 if cls == 2 else 42)
        assert not any(struct.unpack_from("<I", binary, phoff + i * entsize)[0] == 3 for i in range(count))
        binary_hashes[name] = sha(binary)
    for name in FILES:
        if name.startswith("deploy/"):
            source = (ROOT / name).read_bytes()
            assert (stage / name).read_bytes() == source
            assert b"\r" not in source, ("non-LF script", name)
    cfg = json.loads((stage / "config.default.json").read_text(encoding="utf-8"))
    assert cfg["schema_version"] == 3 and len(cfg["slot_versions"]) == 64
    assert not cfg["allowed_actions"] and not cfg["tcp"]["allow_writes"]
    assert all(not p["allow_writes"] for p in cfg["ports"])
    checked = subprocess.run(
        [str(ROOT / "output/windows-amd64/gateway-core.exe"), "--check-config", "--config", str(stage / "config.default.json")],
        capture_output=True, text=True, encoding="utf-8",
    )
    assert checked.returncode == 0 and "valid config" in checked.stdout, checked.stderr
    archive = Path(str(stage) + ".tar.gz")
    with tarfile.open(archive, "r:gz") as tar:
        archived = set()
        for member in tar.getmembers():
            assert member.isdir() or member.isfile(), member.name
            pieces = member.name.split("/")
            assert pieces[0] == stage.name and ".." not in pieces
            if member.isfile():
                name = "/".join(pieces[1:])
                assert name in FILES and name not in archived, name
                assert tar.extractfile(member).read() == (stage / name).read_bytes()
                archived.add(name)
        assert archived == FILES
    previous = ROOT / "output/packages" / f"gt6757s-0.3.1-20261014-r1-{arch}"
    previous_archive = Path(str(previous) + ".tar.gz")
    packages.append({
        "architecture": arch, "files": sorted(FILES), "file_count": len(FILES),
        "sha256_entries": len(entries), "elf_static": True, "config_valid": True,
        "archive": archive.name, "archive_bytes": archive.stat().st_size,
        "installed_payload_bytes": sum((stage / p).stat().st_size for p in FILES),
        "sha256": sha(archive.read_bytes()), "binaries": binary_hashes,
        "binaries_match_previous_release": all(
            (previous / "bin" / n).is_file() and sha((previous / "bin" / n).read_bytes()) == h
            for n, h in binary_hashes.items()
        ),
        "previous_archive_bytes": previous_archive.stat().st_size if previous_archive.exists() else None,
    })

# Ensure the installer itself copies only the same allowlist.
installer = (ROOT / "deploy/install.sh").read_text(encoding="utf-8")
copy_list = re.search(r"for runtime_file in (.+?); do", installer)[1].split()
assert set(copy_list) == FILES and len(copy_list) == len(FILES)
assert 'cp -R "$PKG" "$DEST"' not in installer
shell = args.shell
if not shell and os.name == "nt":
    candidate = Path.home() / ".cache/codex-runtimes/codex-primary-runtime/dependencies/native/git/usr/bin/sh.exe"
    if candidate.is_file():
        shell = str(candidate)
syntax = []
marker_checks = []
if shell and not args.skip_shell_checks:
    shell_env = os.environ.copy()
    # The bundled Windows sh is invoked without a login profile; expose its companion coreutils.
    shell_env["PATH"] = str(Path(shell).resolve().parent) + os.pathsep + shell_env.get("PATH", "")
    for name in sorted(FILES):
        if name.endswith(".sh") or name == "deploy/S99levelgateway":
            run = subprocess.run([shell, "-n", str(ROOT / name)], capture_output=True)
            assert run.returncode == 0, (name, run.stderr)
            syntax.append(name)
    test_root = ROOT / "tmp/runtime-package-review"
    test_root.mkdir(parents=True, exist_ok=True)
    fixtures = Path(tempfile.mkdtemp(prefix="v3-markers-", dir=test_root)).resolve()
    assert fixtures.is_relative_to(test_root.resolve())
    cases = [
        ("runtime-v3", "3\n", False, True),
        ("runtime-v3.2", "3.2\n", False, True),
        ("wrong-marker", "2\n", False, False),
        ("empty-marker", "", False, False),
        ("legacy-v3", None, True, True),
        ("legacy-v2", None, False, False),
        ("invalid-marker-plus-legacy", "2\n", True, False),
    ]
    for name, marker, legacy, allowed in cases:
        case = fixtures / name
        case.mkdir()
        if marker is not None:
            (case / "NORTH_VERSION").write_text(marker, encoding="utf-8")
        if legacy:
            (case / "north-v3.json").write_text("{}", encoding="utf-8")
    for script in ("install.sh", "rollback.sh"):
        source = (ROOT / "deploy" / script).read_text(encoding="utf-8")
        function = re.search(r"is_v3_release\(\) \{\n.*?\n\}", source, re.S)[0]
        for name, marker, legacy, allowed in cases:
            # Invoke the actual release-compatibility function only. No install/service command is executed.
            run = subprocess.run([shell, "-c", function + '\nis_v3_release "$1"\n', "marker-check", (fixtures / name).as_posix()], capture_output=True, env=shell_env)
            assert (run.returncode == 0) == allowed, (script, name, run.stderr)
            marker_checks.append({"script": script, "case": name, "allowed": allowed, "passed": True})
release_date = re.search(r"-(\d{4})(\d{2})(\d{2})-", args.version)
result = {
    "passed": True, "date": "-".join(release_date.groups()) if release_date else None, "version": args.version, "software": args.version.split("-")[0],
    "package_kind": "runtime-only", "packages": packages,
    "installer_allowlist_matches_package": True, "shell_syntax": syntax,
    "verification_complete": bool(syntax), "shell_checks_executed": bool(syntax),
    "release_guard_checks": marker_checks, "hardware_executed": False,
    "scope": "Archive hashes/allowlist/static ELF/config validation and read-only compatibility guards; no device installation or controls.",
}
report = ROOT / "tests" / f"runtime-package-verification-{args.version}.json"
report.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps(result, ensure_ascii=False, indent=2))
