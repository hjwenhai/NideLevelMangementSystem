#!/bin/sh
# BusyBox/Buildroot startup wrapper. Verify setsid/readlink/wget on the board first.
set -eu
BASE=/home/level-gateway
mkdir -p "$BASE/data"
mkdir "$BASE/data/service.lock" 2>/dev/null || { echo "Another service operation is active; stale lock must be checked manually"; exit 1; }
trap 'rmdir "$BASE/data/service.lock" 2>/dev/null || true' 0
owned() {
 pidfile="$BASE/data/$1-supervisor.pid"
 [ -f "$pidfile" ] || return 1
 owner=$(cat "$pidfile")
 case "$owner" in ''|*[!0-9]*) return 1 ;; esac
 [ -r "/proc/$owner/cmdline" ] || return 1
 tr '\000' ' ' < "/proc/$owner/cmdline" | grep -F "$BASE/current/deploy/supervise.sh $1" >/dev/null
}
startrole() {
 owned "$1" && return 0
 setsid sh "$BASE/current/deploy/supervise.sh" "$1" </dev/null >/dev/null 2>&1 &
 echo "$!" > "$BASE/data/$1-supervisor.pid"
}
stoprole() {
 if owned "$1"; then
  owner=$(cat "$BASE/data/$1-supervisor.pid")
  kill -TERM "$owner"
  count=0
  while kill -0 "$owner" 2>/dev/null; do
   count=$((count+1))
   [ "$count" -lt 30 ] || { echo "Process still stopping; refusing to switch release"; return 1; }
   sleep 1
  done
 fi
 rm -f "$BASE/data/$1-supervisor.pid"
}
case "${1:-}" in
 start)
  if ! owned core; then
   "$BASE/current/bin/gateway-core" --network-base "$BASE" --network-recover
  fi
  startrole core; startrole web ;;
 network-stop) stoprole web; stoprole core ;;
 network-start) startrole core; startrole web ;;
 stop) stoprole web; stoprole core ;;
 restart)
  stoprole web; stoprole core
  "$BASE/current/bin/gateway-core" --network-base "$BASE" --network-recover
  startrole core; startrole web ;;
 restart-web)
  [ ! -d "$BASE/data/install.lock" ] || { echo "Installation or upgrade is active; refusing Web restart"; exit 1; }
  stoprole web; stoprole core; startrole core; startrole web
  ;;
 status) owned core && echo "core supervisor running"; owned web && echo "web supervisor running" ;;
 *) echo "Usage: $0 start|stop|restart|status"; exit 2 ;;
esac
