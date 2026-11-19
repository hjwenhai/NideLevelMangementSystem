#!/bin/sh
# Read-only board inventory. Does not open serial/watchdog or change networking.
set -eu
echo "=== CPU/kernel ==="
uname -a
getconf LONG_BIT 2>/dev/null || true
echo "=== binary architecture ==="
command -v file >/dev/null 2>&1 && file /bin/busybox /bin/sh || true
echo "=== resources ==="
cat /proc/meminfo
df -h /home /tmp
echo "=== serial nodes ==="
ls -l /dev/ttyS* 2>/dev/null || true
echo "=== indicator metadata (no write) ==="
ls -l /dev/led /dev/led2 /dev/led3 2>/dev/null || true
echo "=== network ==="
ip -brief address 2>/dev/null || ifconfig -a
echo "=== clock ==="
date -u
hwclock -r 2>/dev/null || true
echo "=== startup ==="
ls -ld /etc/init.d /etc/rc.local 2>/dev/null || true
echo "=== watchdog metadata (does not arm) ==="
ls -l /dev/watchdog* 2>/dev/null || true
cat /sys/class/watchdog/watchdog0/timeout 2>/dev/null || true
cat /sys/class/watchdog/watchdog0/nowayout 2>/dev/null || true
echo "=== required utilities ==="
for cmd in sha256sum readlink wget setsid stty tar; do command -v "$cmd" || true; done
echo "=== vendor startup services that may also drive SYS1 ==="
ls -l /etc/htset/bootset 2>/dev/null || true
ps 2>/dev/null | grep -E 'led|sysled|htset|htservice' || true
