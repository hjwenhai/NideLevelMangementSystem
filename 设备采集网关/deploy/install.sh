#!/bin/sh
# Stage a verified, local, unpacked release. First install does not enable init.
set -eu
umask 077
BASE=/home/level-gateway
GUIDED=0
if [ "${1:-}" = --guided ]; then GUIDED=1; shift; fi
PKG=${1:?Usage: sh install.sh /absolute/unpacked/package}
case "$PKG" in /*) ;; *) echo "Package path must be absolute"; exit 2 ;; esac
[ -f "$PKG/RELEASE" ] && [ -f "$PKG/ARCH" ] && [ -f "$PKG/SHA256SUMS" ]
[ -f "$PKG/UPDATE_VERSION" ] && [ "$(cat "$PKG/UPDATE_VERSION")" = 1 ] || { echo "Upgrade protocol marker is missing"; exit 2; }
[ -f "$PKG/NORTH_VERSION" ] && [ "$(cat "$PKG/NORTH_VERSION")" = 3.2 ] || { echo "A verified V3 runtime-only package is required"; exit 2; }
# New runtime releases use a small marker. Legacy V3 releases kept north-v3.json.
is_v3_release() {
 if [ -f "$1/NORTH_VERSION" ]; then
  case "$(cat "$1/NORTH_VERSION")" in 3|3.1|3.2) return 0 ;; *) return 1 ;; esac
 else
  [ -f "$1/north-v3.json" ]
 fi
}
# Refuse concurrent manual installation while a durable network trial is active.
chmod 755 "$PKG/bin/gateway-core"
"$PKG/bin/gateway-core" --network-base "$BASE" --network-check
# Existing gateways must be licensed before any stop/switch. New gateways may
# start their management page only and import a factory license afterwards.
if [ -L "$BASE/current" ]; then
 "$PKG/bin/gateway-core" --check-license --license-file "$BASE/config/license.json"
fi
VERSION=$(cat "$PKG/RELEASE")
case "$VERSION" in ''|[!a-zA-Z0-9]*|*[!a-zA-Z0-9._-]*) echo "Invalid release"; exit 2 ;; esac
ARCH=$(cat "$PKG/ARCH")
case "$(uname -m)" in aarch64) EXPECTED=linux-arm64 ;; armv7l|armv8l) EXPECTED=linux-armv7 ;; *) echo "Unsupported architecture; run probe.sh"; exit 2 ;; esac
[ "$ARCH" = "$EXPECTED" ] || { echo "Wrong package architecture"; exit 2; }
mv --help 2>&1 | grep -F -- '-T' >/dev/null || { echo "mv -T is required for atomic symlink switching"; exit 2; }
[ ! -e "$BASE/current.new" ] && [ ! -L "$BASE/current.new" ] || { echo "Stale current.new; inspect before installing"; exit 2; }
(cd "$PKG" && sha256sum -c SHA256SUMS)
DEST="$BASE/releases/$VERSION"
[ ! -e "$DEST" ] && [ ! -L "$DEST" ] || { echo "Release already exists; do not overwrite a release"; exit 2; }
terminal_hidden=0
install_locked=0
cleanup() {
 [ "$terminal_hidden" = 0 ] || stty echo 2>/dev/null || true
 [ "$install_locked" = 0 ] || rmdir "$BASE/data/install.lock" 2>/dev/null || true
}
trap cleanup 0
trap 'exit 130' INT
trap 'exit 143' TERM HUP
prompt() {
 printf '%s [%s]: ' "$1" "$2"
 IFS= read -r answer || { echo "输入结束，已停止"; exit 2; }
 [ -n "$answer" ] || answer=$2
}
if [ "$GUIDED" = 1 ]; then
 [ -t 0 ] || { echo "交互式向导须在SSH或Console交互终端运行；手动安装请去掉--guided"; exit 2; }
 # Windows-created tar archives can unpack binaries without executable mode.
 chmod 755 "$PKG/bin/gateway-core" "$PKG/bin/gateway-web"
 echo "GT6757X 安装与升级向导：$VERSION ($ARCH)"
 if [ -e "$BASE/config/gateway.json" ] || [ -L "$BASE/current" ] || [ -f "$BASE/data/gateway-v2.db" ]; then
  echo "检测到已有设备数据：按升级处理，保留账号、仪表绑定、网络监听及现有参数值。"
  echo "升级会中断采集；请先完成或现场核查控制操作。"
  [ -f "$BASE/config/gateway.json" ] && [ -f "$BASE/config/service.env" ] || { echo "已有部署不完整，请先按手动步骤核查，不能生成配置覆盖。"; exit 2; }
 else
  echo "首次安装：生成只读配置，默认1台；真实控制及SYS灯暂不启用。"
  prompt "管理网页监听IP:端口（填写设备已有IP）" "192.168.0.230:8088"; guided_web=$answer
  "$PKG/bin/gateway-core" --check-listen "$guided_web"
  prompt "Modbus TCP监听IP:端口（填写设备已有IP）" "${guided_web%:*}:502"; guided_tcp=$answer
  "$PKG/bin/gateway-core" --check-listen "$guided_tcp"
  prompt "实际已接液位计台数，1～64" "1"; guided_count=$answer
  case "$guided_count" in ''|*[!0-9]*) echo "台数必须是整数"; exit 2 ;; esac
  [ "$guided_count" -ge 1 ] && [ "$guided_count" -le 64 ] || { echo "台数须1～64"; exit 2; }
  guided_address=1
  if [ "$guided_count" = 1 ]; then prompt "COM1首台液位计站址，1～243" "1"; guided_address=$answer; fi
  prompt "液位计波特率，1200/2400/4800/9600（8E1）" "4800"; guided_baud=$answer
  case "$guided_baud" in 1200|2400|4800|9600) ;; *) echo "首次向导只使用FW协议列出的四种速率"; exit 2 ;; esac
  prompt "允许管理员网页校时及保存RTC，1允许/0关闭" "1"; guided_clock=$answer
  case "$guided_clock" in 0|1) ;; *) echo "须填写0或1"; exit 2 ;; esac
  echo "网页=$guided_web；TCP=$guided_tcp；仪表=$guided_count；南向=$guided_baud/8E1。"
  echo "多台按COM1起每口16台、每口地址1～16分配；其余现场差异安装后在网页配置。"
 fi
 prompt "确认按上述方案继续，输入y" "n"
 [ "$answer" = y ] || { echo "已取消，未修改现有安装"; exit 0; }
fi
mkdir -p "$BASE/releases" "$BASE/config" "$BASE/data" "$BASE/logs"
mkdir "$BASE/data/install.lock" 2>/dev/null || { echo "Another install is active; inspect stale install.lock before retrying"; exit 1; }
install_locked=1
# Recheck after acquiring the shared installation lock to close the check/use race.
"$PKG/bin/gateway-core" --network-base "$BASE" --network-check
[ ! -e "$DEST" ] && [ ! -L "$DEST" ] || { echo "Release appeared during preparation; refusing to overwrite"; exit 2; }
# Copy an explicit runtime allowlist even if the unpack directory has unrelated files.
mkdir -p "$DEST/bin" "$DEST/deploy"
for runtime_file in bin/gateway-core bin/gateway-web deploy/install.sh deploy/service.sh deploy/supervise.sh deploy/rollback.sh deploy/S99levelgateway deploy/settings.example.env deploy/update-trust.json bin/gateway-updater config.default.json RELEASE ARCH NORTH_VERSION UPDATE_VERSION SHA256SUMS; do
 cp "$PKG/$runtime_file" "$DEST/$runtime_file"
done
(cd "$DEST" && sha256sum -c SHA256SUMS)
chmod 755 "$DEST"/bin/* "$DEST"/deploy/*.sh "$DEST"/deploy/S99levelgateway
"$DEST/bin/gateway-core" --check-config --config "$DEST/config.default.json"
if [ ! -f "$BASE/config/update-trust.json" ]; then cp "$DEST/deploy/update-trust.json" "$BASE/config/update-trust.json"; fi
 if [ ! -f "$BASE/config/gateway.json" ]; then
 if [ "$GUIDED" = 1 ]; then
  "$DEST/bin/gateway-core" --init-config "$BASE/config/gateway.json" --meters "$guided_count" --init-tcp-listen "$guided_tcp" --init-first-address "$guided_address" --init-south-baud "$guided_baud"
 else
  cp "$DEST/config.default.json" "$BASE/config/gateway.json"
 fi
fi
if [ ! -f "$BASE/config/service.env" ]; then
 cp "$DEST/deploy/settings.example.env" "$BASE/config/service.env"
 if [ "$GUIDED" = 1 ]; then
  sed -i "s|^WEB_LISTEN=.*|WEB_LISTEN=$guided_web|;s/^ALLOW_CLOCK_SET=.*/ALLOW_CLOCK_SET=$guided_clock/;s/^LEDS=.*/LEDS=0/" "$BASE/config/service.env"
 fi
fi
"$DEST/bin/gateway-core" --check-config --config "$BASE/config/gateway.json"
OLD=
[ ! -L "$BASE/current" ] || OLD=$(readlink "$BASE/current")
if [ -n "$OLD" ]; then "$BASE/current/deploy/service.sh" stop; fi
if [ -f "$BASE/data/gateway-v2.db" ]; then
 "$DEST/bin/gateway-core" --db "$BASE/data/gateway-v2.db" --backup-db "$BASE/data/gateway.before-$VERSION.db"
fi
if [ -f "$BASE/config/license.json" ]; then cp "$BASE/config/license.json" "$BASE/config/license.before-$VERSION.json"; fi
cp "$BASE/config/gateway.json" "$BASE/config/gateway.before-$VERSION.json"
"$DEST/bin/gateway-core" --migrate-config --config "$BASE/config/gateway.json"
# Preserve existing values, and append only missing supported defaults atomically.
cp "$BASE/config/service.env" "$BASE/config/service.before-$VERSION.env"
env_merge="$BASE/config/.service-$VERSION.env"
[ ! -e "$env_merge" ] && [ ! -L "$env_merge" ] || { echo "Stale environment merge file; inspect it before retrying"; exit 2; }
cp "$BASE/config/service.env" "$env_merge"
while IFS= read -r env_line || [ -n "$env_line" ]; do
 case "$env_line" in ''|\#*) continue ;; esac
 env_key=${env_line%%=*}
 case "$env_key" in WEB_LISTEN|HARDWARE_WRITES|ALLOW_CLOCK_SET|RTC_DEVICE|WATCHDOG|TLS_CERT|TLS_KEY|LEDS) ;; *) continue ;; esac
 if ! grep -Eq "^[[:space:]]*(export[[:space:]]+)?${env_key}=" "$env_merge"; then
  printf '\n%s\n' "$env_line" >> "$env_merge"
  echo "Added missing service setting: $env_key (existing values preserved)"
 fi
done < "$DEST/deploy/settings.example.env"
mv -Tf "$env_merge" "$BASE/config/service.env"
ln -s "$DEST" "$BASE/current.new"
mv -Tf "$BASE/current.new" "$BASE/current"
if [ ! -f "$BASE/data/admin.json" ]; then
 if [ "$GUIDED" = 1 ]; then
  while :; do
   echo "设置网页管理员密码：12～128字符；输入时不显示。"
   stty -echo; terminal_hidden=1
   printf '密码: '; IFS= read -r guided_password
   printf '\n再次输入: '; IFS= read -r guided_confirm
   stty echo; terminal_hidden=0; printf '\n'
   if [ "$guided_password" != "$guided_confirm" ]; then echo "两次输入不同，请重试"; continue; fi
   if printf '%s\n' "$guided_password" | "$BASE/current/bin/gateway-web" --init-admin --auth-file "$BASE/data/admin.json"; then break; fi
  done
  unset guided_password guided_confirm
 else
 echo "Release installed; initialize the administrator from stdin before starting:"
 echo "$BASE/current/bin/gateway-web --init-admin --auth-file $BASE/data/admin.json"
 echo "Then verify service.env and run: $BASE/current/deploy/service.sh start"
 exit 0
 fi
fi
"$BASE/current/deploy/service.sh" start
OK=0
for i in 1 2 3 4 5; do
 sleep 3
 if "$BASE/current/bin/gateway-core" --health-check --ipc unix:/home/level-gateway/data/core.sock; then OK=1; break; fi
done
if [ "$OK" = 1 ]; then
 echo "New release core healthy; verify Web, permanent license and both north interfaces"
 if [ "$GUIDED" = 1 ]; then
  echo "程序已启动；网页地址见config/service.env的WEB_LISTEN。"
  echo "若启用RTC校时，请登录网页校核一次并确认RTC已保存。"
  echo "安装不会修改系统IP，也不会自动启用开机自启；自启按说明10第8节验证后设置。"
 fi
 exit 0
fi
echo "New release failed core health; attempting rollback"
"$BASE/current/deploy/service.sh" stop
if [ -n "$OLD" ]; then
 if ! is_v3_release "$OLD" || [ ! -f "$OLD/NORTH_VERSION" ] || [ "$(cat "$OLD/NORTH_VERSION")" != 3.2 ]; then
  echo "Remote-level command 11 requires NORTH_VERSION=3.2. Automatic rollback to an older runtime is blocked. New release stopped; preserve current DB/backups and inspect on site."
  exit 1
 fi
 cp "$BASE/config/gateway.before-$VERSION.json" "$BASE/config/gateway.json"
 cp "$BASE/config/service.before-$VERSION.env" "$BASE/config/service.env"
 ln -s "$OLD" "$BASE/current.new"
 mv -Tf "$BASE/current.new" "$BASE/current"
 "$BASE/current/deploy/service.sh" start
else
 echo "No previous release; installed files retained for diagnosis"
fi
exit 1
