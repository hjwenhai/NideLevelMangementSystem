#!/bin/sh
# Explicit application rollback; only a locally installed verified release.
set -eu
BASE=/home/level-gateway
# Never fall back to the legacy marker when an explicit new marker is invalid.
is_v3_release() {
 if [ -f "$1/NORTH_VERSION" ]; then
  case "$(cat "$1/NORTH_VERSION")" in 3|3.1|3.2) return 0 ;; *) return 1 ;; esac
 else
  [ -f "$1/north-v3.json" ]
 fi
}
VERSION=${1:?Usage: sh rollback.sh release-version}
case "$VERSION" in ''|[!a-zA-Z0-9]*|*[!a-zA-Z0-9._-]*) exit 2 ;; esac
mv --help 2>&1 | grep -F -- '-T' >/dev/null || exit 2
DEST="$BASE/releases/$VERSION"
[ -d "$DEST" ] && [ -f "$DEST/SHA256SUMS" ]
[ -f "$DEST/NORTH_VERSION" ] && [ "$(cat "$DEST/NORTH_VERSION")" = 3.2 ] && is_v3_release "$DEST" || { echo "Rollback to a release without remote-level transactions is blocked (schema3 required). Preserve current DB and verify unresolved cycles on site."; exit 2; }
(cd "$DEST" && sha256sum -c SHA256SUMS)
"$DEST/bin/gateway-core" --check-config --config "$BASE/config/gateway.json"
"$BASE/current/deploy/service.sh" stop
ln -s "$DEST" "$BASE/current.new"
mv -Tf "$BASE/current.new" "$BASE/current"
"$BASE/current/deploy/service.sh" start
sleep 3
"$BASE/current/bin/gateway-core" --health-check --ipc unix:/home/level-gateway/data/core.sock
