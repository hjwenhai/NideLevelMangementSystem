"""Build and verify the ARM64 web migration release using the signed-off 0.7 core."""
import hashlib
import os
import shutil
import subprocess
import tarfile
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PACKAGES = ROOT / "output/packages"
BASE = "gt6757s-0.7.0-20261108-r3-linux-arm64"
BASE_SHA = "ee562b061b3e30dd82bc4dfa426d5e99950c2620b30012a47bcf42e4052eab90"
RELEASE = "0.7.0-20261109-r2"
NAME = "gt6757s-" + RELEASE + "-linux-arm64"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    original = ROOT / "tests/fixtures/license-bridge-build" / (BASE + ".tar.gz")
    if digest(original) != BASE_SHA:
        raise ValueError("Original 0.7 ARM64 archive does not match its published digest")
    output = PACKAGES / (NAME + ".gwup")
    sums = PACKAGES / ("SHA256SUMS-" + RELEASE + ".txt")
    if output.exists() or sums.exists():
        raise FileExistsError("This migration release already exists")
    go = ROOT / ".build-tools/go/bin/go.exe"
    signer = ROOT / "output/windows-amd64/gateway-release.exe"
    env = dict(os.environ, GOTOOLCHAIN="local", GOCACHE=str(ROOT / ".build-cache/go"),
               GOMODCACHE=str(ROOT / ".module-cache"), GOOS="linux", GOARCH="arm64", CGO_ENABLED="0")
    subprocess.run(["node", "src/web/node_modules/typescript/bin/tsc", "-p", "src/web"],
                   cwd=ROOT, check=True)
    with tempfile.TemporaryDirectory(dir=ROOT / "tmp", prefix="bridge-build-") as temp:
        work = Path(temp)
        stage = work / NAME
        with tarfile.open(original, "r:gz") as archive:
            for entry in archive:
                if entry.isdir():
                    continue
                if not entry.isfile():
                    raise ValueError(entry.name)
                dest = stage / Path(entry.name).relative_to(BASE)
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes(archive.extractfile(entry).read())
        for role in ["gateway-web", "gateway-updater"]:
            subprocess.run([go, "build", "-buildvcs=false", "-trimpath", "-tags", "licensebridge",
                            "-ldflags", "-s -w", "-o", stage / "bin" / role, "./src/cmd/" + role],
                           cwd=ROOT, env=env, check=True)
        (stage / "RELEASE").write_bytes((RELEASE + "\n").encode())
        files = sorted(p for p in stage.rglob("*") if p.is_file() and p.name != "SHA256SUMS")
        (stage / "SHA256SUMS").write_bytes("".join(
            f"{digest(p)}  {p.relative_to(stage).as_posix()}\n" for p in files).encode())
        payload = work / (NAME + ".tar.gz")
        with tarfile.open(payload, "w:gz") as archive:
            archive.add(stage, arcname=NAME)
        bundle = work / output.name
        subprocess.run([signer, "sign", "--payload", payload, "--stage", stage, "--output", bundle,
                        "--private-key", ROOT / "private/update-signing.key", "--notes",
                        "0.6.0样机网页授权过渡版。安装后按原配置采集。在系统维护下载本机申请，导入厂家永久授权，再安装0.8.1。"],
                       cwd=ROOT, check=True)
        # Use the trust that the customer's old release actually carries.
        for version in ("0.6.0-20261029-r3", "0.7.0-20261108-r3"):
            trust = ROOT / "tests/fixtures/license-bridge-build" / f"update-trust-{version}.json"
            subprocess.run([signer, "verify", "--input", bundle, "--public-key", trust,
                            "--arch", "linux-arm64"], check=True)
        with bundle.open("rb") as src, output.open("xb") as dst:
            shutil.copyfileobj(src, dst)
        with sums.open("x", encoding="ascii", newline="\n") as f:
            f.write(f"{digest(output)}  {output.name}\n")
    print(output.name, digest(output))


if __name__ == "__main__":
    main()
