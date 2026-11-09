#!/usr/bin/env bash
# BayiOS — offline checks for infra/linux, inside a throw-away Ubuntu 24.04 container (removed with --rm).
#
#   bash infra/linux/test.sh            # needs Docker; works from Linux, macOS, WSL and Git Bash on Windows
#
# Checks:
#   1. bash -n and shellcheck on every script
#   2. nginx -t with the real site file (the stock Ubuntu nginx.conf includes it)
#   3. visudo -cf on the sudoers drop-in
#   4. systemd-analyze verify + systemd-analyze security (exposure score) on both units
#   5. behaviour of remote-activate.sh: activate, automatic rollback on a failed health check, manual rollback,
#      pruning of old releases, rejection of a malformed release id
set -euo pipefail

IMAGE="${IMAGE:-ubuntu:24.04}"

if [ "${1:-}" != "--inside" ]; then
  here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  # Git Bash on Windows: hand Docker a Windows path and stop MSYS from rewriting /src.
  if command -v cygpath >/dev/null 2>&1; then here="$(cygpath -w "$here")"; fi
  MSYS_NO_PATHCONV=1 exec docker run --rm -v "$here:/src:ro" "$IMAGE" bash /src/test.sh --inside
fi

fail=0
pass() { printf 'PASS  %s\n' "$*"; }
bad() { printf 'FAIL  %s\n' "$*"; fail=1; }
section() { printf '\n--- %s\n' "$*"; }

section "install test tools"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -yqq --no-install-recommends shellcheck nginx sudo systemd curl python3 ca-certificates >/dev/null
echo "shellcheck $(shellcheck --version | awk '/^version/ {print $2}'), $(nginx -v 2>&1), systemd $(systemctl --version | awk 'NR==1 {print $2}')"

section "1. bash -n + shellcheck"
for s in /src/*.sh; do
  if bash -n "$s"; then pass "bash -n $(basename "$s")"; else bad "bash -n $(basename "$s")"; fi
done
if shellcheck -x /src/*.sh; then pass "shellcheck (all scripts, default severity)"; else bad "shellcheck"; fi

section "2. nginx -t"
sed 's/__DOMAIN__/bayios.example.com/g' /src/nginx/bayios.conf > /etc/nginx/sites-available/bayios
ln -sfn /etc/nginx/sites-available/bayios /etc/nginx/sites-enabled/bayios
rm -f /etc/nginx/sites-enabled/default
if nginx -t 2>&1; then pass "nginx -t"; else bad "nginx -t"; fi

section "3. sudoers"
sed 's/__DEPLOY_USER__/deploy/g' /src/sudoers/bayios-deploy > /tmp/bayios-deploy
if visudo -cf /tmp/bayios-deploy; then pass "visudo -cf"; else bad "visudo -cf"; fi

section "4. systemd units"
# verify checks that ExecStart binaries exist; the real ones come from bootstrap.sh, so stand-ins are enough here.
for bin in /usr/bin/dotnet /usr/bin/node; do [ -x "$bin" ] || install -m 0755 /dev/null "$bin"; done
for u in bayios-api bayios-web; do
  id "$u" >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin "$u"
  install -m 0644 "/src/systemd/$u.service" /etc/systemd/system/  # like bootstrap.sh
  out="$(systemd-analyze verify "/etc/systemd/system/$u.service" 2>&1)" && rc=0 || rc=$?
  # Inside a container without a running systemd, verify may complain about the missing manager bus / cgroup tree;
  # anything that mentions the unit file itself is a real finding.
  findings="$(printf '%s\n' "$out" | grep -E "$u\.service" || true)"
  if [ "$rc" -eq 0 ] && [ -z "$findings" ]; then pass "systemd-analyze verify $u.service"
  else printf '%s\n' "$out"; bad "systemd-analyze verify $u.service (rc=$rc)"; fi
  score="$(systemd-analyze security --offline=true "/etc/systemd/system/$u.service" 2>/dev/null | tail -n 1)"
  echo "      exposure: $score"
done

section "5. remote-activate.sh behaviour"
root="$(mktemp -d)"
mkdir -p "$root/releases"
mkrel() { # $1 id, $2 healthy|broken
  local d="$root/releases/$1"
  mkdir -p "$d/api" "$d/web"
  : > "$d/api/BayiOS.Api.dll"
  echo '{}' > "$d/web/package.json"
  [ "$2" = healthy ] && echo ok > "$d/api/health"
  return 0
}
# A tiny web server over $root: /current/api/health exists only while `current` points at a healthy release.
(cd "$root" && exec python3 -m http.server 18080 --bind 127.0.0.1 >/dev/null 2>&1) &
srv=$!
trap 'kill $srv 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do curl -s -o /dev/null http://127.0.0.1:18080/ && break; sleep 0.1; done

export BAYIOS_ROOT="$root" SYSTEMCTL="echo systemctl" SKIP_NPM=1 HEALTH_TRIES=2 HEALTH_SLEEP=0 KEEP_RELEASES=3 \
  WEB_CACHE_DIR="$root/cache" API_HEALTH_URL="http://127.0.0.1:18080/current/api/health" \
  WEB_HEALTH_URL="http://127.0.0.1:18080/current/web/package.json"
ra() { bash /src/remote-activate.sh "$@"; }
cur() { basename "$(readlink "$root/current")"; }
expect_cur() { if [ "$(cur)" = "$2" ]; then pass "$1 (current=$2)"; else bad "$1: current=$(cur), expected $2"; fi; }

R1=20261101000000-aaaa01 R2=20261102000000-aaaa02 R3=20261103000000-aaaa03
R4=20261104000000-aaaa04 R5=20261105000000-aaaa05
mkrel "$R1" healthy; mkrel "$R2" healthy; mkrel "$R3" broken

ok_then() { # $1 label, $2 expected current, rest: remote-activate.sh args; the call itself must succeed
  local label="$1" want="$2"
  shift 2
  if ra "$@" >/dev/null; then expect_cur "$label" "$want"; else bad "$label: remote-activate.sh $* failed"; fi
}

ok_then "first deploy" "$R1" activate "$R1"
if [ "$(readlink "$root/releases/$R1/web/.next/cache")" = "$root/cache" ]; then
  pass "Next.js cache linked to the writable cache dir"
else bad "Next.js cache link"; fi
ok_then "second deploy" "$R2" activate "$R2"
if ra activate "$R3" >/dev/null 2>&1; then bad "broken release reported success"
else expect_cur "broken release rolled back automatically" "$R2"; fi
ok_then "manual rollback" "$R1" rollback
if ra activate "not-a-release" >/dev/null 2>&1; then bad "malformed id accepted"; else pass "malformed release id rejected"; fi
mkrel "$R4" healthy
mkrel "$R5" healthy
ok_then "deploy after rollback" "$R4" activate "$R4"
ok_then "next deploy" "$R5" activate "$R5"
left="$(find "$root/releases" -mindepth 1 -maxdepth 1 -printf '%f ')"
if [ ! -d "$root/releases/$R1" ] && [ ! -d "$root/releases/$R2" ] && [ -d "$root/releases/$R3" ]; then
  pass "pruned to the newest $KEEP_RELEASES releases"
else bad "prune left: $left"; fi
if ra list | grep -qx "\\* $R5"; then pass "list marks the current release"; else bad "list output"; fi

section "result"
if [ "$fail" -eq 0 ]; then echo "ALL CHECKS PASSED"; else echo "SOME CHECKS FAILED"; fi
exit "$fail"
