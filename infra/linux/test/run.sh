#!/usr/bin/env bash
# End-to-end test of the self-hosted Linux deployment WITHOUT a real VPS (infra/linux/README.md "Local test harness").
#
#   bash infra/linux/test/run.sh
#
# 1. shellcheck all scripts
# 2. build two images: a systemd-as-PID-1 Ubuntu 24.04 "VPS" and a .NET 10 SDK + Node 24 builder
# 3. build a REAL release tarball (dotnet publish + next build) with build-release.sh in the builder
# 4. boot the VPS container (privileged, private cgroup namespace), run provision.sh twice (idempotency) in test mode
#    (self-signed certificate, ufw rules written but not enabled, no certbot issuance)
# 5. deploy as the non-root deploy user via sudo, check API /health and the web home page THROUGH Nginx + TLS
# 6. deploy a second release, then a deliberately broken one -> automatic rollback; rollback.sh with/without version
# 7. crash recovery (kill -9), systemd-analyze verify + security
#
# Environment:
#   BAYIOS_LINUX_TEST_WORK  directory for the release tarball and npm/NuGet caches (default ~/.cache/bayios-linux-test)
#   REUSE_RELEASE=1         reuse an existing bayios-test-1.tar.gz in the work directory instead of rebuilding
#   KEEP=1                  leave the VPS container running for inspection (docker exec -it bayios-linux-test-vps bash)
# Everything Docker creates is named bayios-linux-test-*. Needs Docker with Linux containers (Docker Desktop is fine).
set -euo pipefail
export MSYS_NO_PATHCONV=1 # Git Bash on Windows: do not rewrite container paths

P=bayios-linux-test
VPS="$P-vps"
APP=app.bayios.test
API=api.bayios.test
test_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
linux_dir="$(cd "$test_dir/.." && pwd)"
repo_root="$(cd "$linux_dir/../.." && pwd)"
work="${BAYIOS_LINUX_TEST_WORK:-$HOME/.cache/bayios-linux-test}"
mkdir -p "$work/out" "$work/cache/npm" "$work/cache/nuget"

# Host path as Docker understands it (C:/... on Windows, unchanged elsewhere).
hostpath() { if command -v cygpath >/dev/null; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

step() { printf '\n\033[1;34m=== %s\033[0m\n' "$*"; }
pass() { printf '\033[32mPASS\033[0m %s\n' "$*"; }
fail() {
  printf '\033[31mFAIL\033[0m %s\n' "$*" >&2
  docker exec "$VPS" journalctl -u bayios-api -u bayios-web -u nginx -n 60 --no-pager >&2 2>/dev/null || true
  exit 1
}
expect() { # <description> <expected> <actual>
  if [[ "$3" == "$2" ]]; then pass "$1 ($3)"; else fail "$1: expected '$2', got '$3'"; fi
}
vexec() { docker exec "$VPS" "$@"; }
vsh() { docker exec "$VPS" bash -c "$1"; }
vlink() { local t; t="$(vexec readlink "/opt/bayios/$1")"; printf '%s' "${t##*/}"; }
cleanup() {
  if [[ "${KEEP:-0}" == 1 ]]; then
    echo "KEEP=1: container $VPS left running"
  else
    docker rm -f "$VPS" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

step "1. shellcheck"
docker run --rm --name "$P-shellcheck" -v "$(hostpath "$linux_dir"):/mnt:ro" -w /mnt \
  koalaman/shellcheck:v0.11.0@sha256:61862eba1fcf09a484ebcc6feea46f1782532571a34ed51fedf90dd25f925a8d -x \
  scripts/lib.sh scripts/deploy.sh scripts/rollback.sh scripts/healthcheck.sh scripts/provision.sh \
  scripts/build-release.sh test/run.sh test/static-checks.sh
pass "shellcheck: no findings"

step "2. images"
docker build -q -t "$P-vps" -f "$(hostpath "$test_dir/vps.Dockerfile")" "$(hostpath "$test_dir")"
docker build -q -t "$P-builder" -f "$(hostpath "$test_dir/builder.Dockerfile")" "$(hostpath "$test_dir")"

step "3. release build (dotnet publish + next build)"
tarball="$work/out/bayios-test-1.tar.gz"
if [[ "${REUSE_RELEASE:-0}" == 1 && -s "$tarball" ]]; then
  echo "reusing $tarball"
else
  commit="$(git -C "$repo_root" rev-parse HEAD 2>/dev/null || echo unknown)"
  docker run --rm --name "$P-build" \
    -e BUILD_COMMIT="$commit" \
    -e NEXT_PUBLIC_API_BASE_URL="https://$API" \
    -e NEXT_PUBLIC_SUPABASE_URL=https://placeholder.supabase.test \
    -e NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY=sb_publishable_placeholder-not-a-real-key \
    -v "$(hostpath "$repo_root"):/repo:ro" \
    -v "$(hostpath "$work/out"):/out" \
    -v "$(hostpath "$work/cache/npm"):/root/.npm" \
    -v "$(hostpath "$work/cache/nuget"):/root/.nuget/packages" \
    "$P-builder" bash /repo/infra/linux/scripts/build-release.sh test-1 /out
fi
ls -l "$tarball"

step "4. boot the test VPS (systemd as PID 1)"
docker rm -f "$VPS" >/dev/null 2>&1 || true
docker run -d --name "$VPS" --hostname bayios-vps --privileged --cgroupns=private \
  --tmpfs /run --tmpfs /run/lock "$P-vps" >/dev/null
state="$(timeout 120 docker exec "$VPS" systemctl is-system-running --wait 2>/dev/null || true)"
echo "systemd: $state"
[[ "$state" == running || "$state" == degraded ]] || fail "systemd did not boot ($state)"
vexec mkdir -p /root/infra-linux
docker cp "$(hostpath "$linux_dir")/." "$VPS:/root/infra-linux/"
docker cp "$(hostpath "$tarball")" "$VPS:/root/bayios-test-1.tar.gz"
docker cp "$(hostpath "$tarball.sha256")" "$VPS:/root/bayios-test-1.tar.gz.sha256"
vexec ssh-keygen -q -t ed25519 -N '' -C test-deploy-key -f /root/test-deploy-key
pubkey="$(vexec cat /root/test-deploy-key.pub)"

provision() {
  docker exec -e BAYIOS_TEST_MODE=1 -e BAYIOS_APP_DOMAIN="$APP" -e BAYIOS_API_DOMAIN="$API" \
    -e BAYIOS_DEPLOY_USER=deploy -e BAYIOS_DEPLOY_PUBKEY="$pubkey" -e BAYIOS_HARDEN_SSH=1 \
    "$VPS" bash /root/infra-linux/scripts/provision.sh
}
step "5a. provision.sh (first run)"
provision
step "5b. provision.sh (second run: idempotent)"
provision >/dev/null
pass "provision.sh re-run succeeded"

step "5c. server state after provisioning"
vexec nginx -t
expect "ufw rules (not enabled in test mode)" 3 "$(vsh "ufw show added | grep -cE 'limit OpenSSH|allow 80/tcp|allow 443/tcp'")"
expect "api.env owner/mode" "root:bayios 640" "$(vexec stat -c '%U:%G %a' /etc/bayios/api.env)"
expect "web.env owner/mode" "root:bayios 640" "$(vexec stat -c '%U:%G %a' /etc/bayios/web.env)"
expect "bayios user shell" /usr/sbin/nologin "$(vsh 'getent passwd bayios | cut -d: -f7')"
expect "units enabled" "enabled enabled" "$(vsh 'systemctl is-enabled bayios-api bayios-web | xargs')"
expect "certbot renewal timer" enabled "$(vexec systemctl is-enabled certbot.timer)"
expect "sshd: password auth" "passwordauthentication no" "$(vsh 'sshd -T | grep -i "^passwordauthentication"')"
expect "sshd: root login" "permitrootlogin no" "$(vsh 'sshd -T | grep -i "^permitrootlogin"')"
expect "deploy user sudo rights" 0 "$(vsh 'sudo -l -U deploy | grep -c journalctl || true')"
vexec node --version
vexec dotnet --list-runtimes

step "6. first deploy (test-1) as the deploy user via sudo"
vsh 'cp /root/bayios-test-1.tar.gz* /home/deploy/artifacts/ && chown deploy: /home/deploy/artifacts/*'
docker exec -u deploy "$VPS" sudo -n /usr/local/sbin/bayios-deploy /home/deploy/artifacts/bayios-test-1.tar.gz test-1
expect "current release" test-1 "$(vlink current)"

curl_vps() { vexec curl --silent --show-error --insecure --max-time 10 "$@"; }
step "7. through Nginx + TLS"
body="$(curl_vps --resolve "$API:443:127.0.0.1" "https://$API/health")"
echo "GET https://$API/health -> $body"
expect "API /health via nginx" '{"status":"ok"}' "$body"
expect "API /health/ready via nginx (no DB configured: fail-closed)" 503 \
  "$(curl_vps -o /dev/null -w '%{http_code}' --resolve "$API:443:127.0.0.1" "https://$API/health/ready")"
expect "web / via nginx" 200 "$(curl_vps -o /dev/null -w '%{http_code}' --resolve "$APP:443:127.0.0.1" "https://$APP/")"
title="$(curl_vps --resolve "$APP:443:127.0.0.1" "https://$APP/" | grep -o '<title>[^<]*</title>' | head -1)"
echo "web home page: $title"
[[ -n "$title" ]] || fail "web home page has no <title>"
headers="$(curl_vps -D - -o /dev/null --resolve "$APP:443:127.0.0.1" "https://$APP/")"
expect "exactly one CSP header (app-owned, nginx adds none)" 1 "$(grep -ci '^content-security-policy:' <<<"$headers")"
expect "HTTP/2" 1 "$(grep -c '^HTTP/2 200' <<<"$headers")"
expect "http -> https redirect" "301 https://$APP/giris" \
  "$(curl_vps -o /dev/null -w '%{http_code} %{redirect_url}' --resolve "$APP:80:127.0.0.1" "http://$APP/giris")"
expect "ACME challenge path served over http" 404 \
  "$(curl_vps -o /dev/null -w '%{http_code}' --resolve "$APP:80:127.0.0.1" "http://$APP/.well-known/acme-challenge/x")"
expect "unknown Host is dropped (444)" 000 \
  "$(vexec curl -s -o /dev/null -w '%{http_code}' -H 'Host: unknown.test' http://127.0.0.1/ || true)"
expect "unknown SNI: TLS handshake rejected" 000 \
  "$(vexec curl -sk -o /dev/null -w '%{http_code}' --resolve unknown.test:443:127.0.0.1 https://unknown.test/ || true)"
expect "API listens on loopback only" "127.0.0.1:5280" "$(vsh "ss -Hltn 'sport = :5280' | awk '{print \$4}'")"
expect "web listens on loopback only" "127.0.0.1:3200" "$(vsh "ss -Hltn 'sport = :3200' | awk '{print \$4}'")"
expect "API runs as bayios" bayios "$(vexec ps -o user= -p "$(vexec systemctl show -p MainPID --value bayios-api)")"

step "8. second deploy (test-2)"
vsh 'cp /root/bayios-test-1.tar.gz /root/bayios-test-2.tar.gz'
vexec bayios-deploy /root/bayios-test-2.tar.gz test-2
expect "current" test-2 "$(vlink current)"
expect "previous" test-1 "$(vlink previous)"
rc=0
vexec bayios-deploy /root/bayios-test-2.tar.gz test-2 >/dev/null 2>&1 || rc=$?
expect "re-deploying an existing version is refused" 1 "$rc"

step "9. broken release (corrupted API assembly) -> automatic rollback"
vsh 'rm -rf /root/broken && mkdir /root/broken && tar -xzf /root/bayios-test-1.tar.gz -C /root/broken \
  && echo "not a .NET assembly" > /root/broken/api/BayiOS.Api.dll'
rc=0
docker exec -e BAYIOS_HEALTH_TIMEOUT=30 "$VPS" bayios-deploy /root/broken test-3-broken || rc=$?
expect "deploy exit code (3 = failed, rolled back)" 3 "$rc"
expect "current after automatic rollback" test-2 "$(vlink current)"
expect "broken release marked failed" yes "$(vsh 'test -f /opt/bayios/releases/test-3-broken/.failed && echo yes')"
expect "API healthy again" '{"status":"ok"}' "$(curl_vps --resolve "$API:443:127.0.0.1" "https://$API/health")"

step "10. rollback.sh"
vexec bayios-rollback
expect "rollback (no arg) -> previous" test-1 "$(vlink current)"
expect "previous after rollback" test-2 "$(vlink previous)"
rc=0
vexec bayios-rollback test-3-broken >/dev/null 2>&1 || rc=$?
expect "rollback to a failed release is refused" 1 "$rc"
docker exec -u deploy "$VPS" sudo -n /usr/local/sbin/bayios-rollback test-2
expect "rollback test-2 (deploy user)" test-2 "$(vlink current)"

step "11. crash recovery (kill -9 the API)"
old_pid="$(vexec systemctl show -p MainPID --value bayios-api)"
vexec kill -9 "$old_pid"
for _ in $(seq 1 30); do
  sleep 1
  code="$(curl_vps -o /dev/null -w '%{http_code}' --resolve "$API:443:127.0.0.1" "https://$API/health" || true)"
  [[ "$code" == 200 ]] && break
done
new_pid="$(vexec systemctl show -p MainPID --value bayios-api)"
expect "API restarted by systemd and healthy" 200 "$code"
[[ "$new_pid" != "$old_pid" ]] || fail "API PID unchanged"
pass "API PID $old_pid -> $new_pid"

step "12. healthcheck.sh"
docker exec -u deploy "$VPS" bayios-healthcheck --via-nginx
rc=0
vexec bayios-healthcheck --ready >/dev/null || rc=$?
expect "healthcheck --ready fails without a database (fail-closed)" 1 "$rc"

step "13. systemd-analyze"
vexec systemd-analyze verify /etc/systemd/system/bayios-api.service /etc/systemd/system/bayios-web.service
pass "systemd-analyze verify: no findings"
for unit in bayios-api bayios-web; do
  vsh "systemd-analyze security --no-pager $unit | tail -1"
done

step "14. logs"
vexec cat /opt/bayios/deploy.log
vexec journalctl -u bayios-api -n 5 --no-pager -o cat
vexec ls -1 /var/log/nginx/

printf '\n\033[1;32mALL CHECKS PASSED\033[0m\n'
