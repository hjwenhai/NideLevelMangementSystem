#!/bin/sh
set -eu
BASE=/home/level-gateway
ROLE=${1:?core or web}
case "$ROLE" in core|web) ;; *) exit 2 ;; esac
umask 077
cd "$BASE"
[ -f config/service.env ] && . ./config/service.env
mkdir -p data logs
child=
finish() { [ -z "$child" ] || kill -TERM "$child" 2>/dev/null || true; [ -z "$child" ] || wait "$child" 2>/dev/null || true; exit 0; }
trap finish TERM INT
while :; do
 log="logs/$ROLE.log"
 [ ! -f "$log" ] || [ "$(wc -c < "$log")" -lt 2097152 ] || mv "$log" "$log.1"
 if [ "$ROLE" = core ]; then
  # Only one supervisor per role is permitted by service.sh.
  rm -f data/core.sock data/ipc.token
  set -- --network-base "$BASE" --config config/gateway.json --db data/gateway-v2.db --ipc unix:/home/level-gateway/data/core.sock --token-file data/ipc.token --clock-state data/clock-state.json --rtc-device "${RTC_DEVICE:-/dev/rtc0}"
  [ "${HARDWARE_WRITES:-0}" != 1 ] || set -- "$@" --hardware-writes
  [ "${ALLOW_CLOCK_SET:-0}" != 1 ] || set -- "$@" --allow-clock-set
  # Legacy TIME_TRUSTED is ignored; restore requires a verified RTC and calibration record.
  [ "${LEDS:-1}" != 0 ] || set -- "$@" --leds=false
  [ -z "${WATCHDOG:-}" ] || set -- "$@" --watchdog "$WATCHDOG"
  set -- "$@" --service-script "$BASE/current/deploy/service.sh" --upgrade-base "$BASE" --upgrade-web-listen "${WEB_LISTEN:-127.0.0.1:8088}" --upgrade-tls-cert "${TLS_CERT:-}"
  current/bin/gateway-core "$@" >> "$log" 2>&1 &
 else
  set -- --network-base "$BASE" --listen "${WEB_LISTEN:-127.0.0.1:8088}" --ipc unix:/home/level-gateway/data/core.sock --token-file data/ipc.token --auth-file data/admin.json --upgrade-base "$BASE"
  [ -z "${TLS_CERT:-}" ] || set -- "$@" --tls-cert "$TLS_CERT" --tls-key "${TLS_KEY:?TLS_KEY required}"
  current/bin/gateway-web "$@" >> "$log" 2>&1 &
 fi
 child=$!
 wait "$child" || true
 child=
 sleep 2
done
