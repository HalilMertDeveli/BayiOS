#!/usr/bin/env bash
# BayiOS — build and deploy to a Linux VPS prepared by bootstrap.sh (infra/linux/README.md, step 6).
#
# Run from the repo on a Linux / WSL / CI machine (x64, .NET 10 SDK, Node 24, rsync, ssh):
#
#   DEPLOY_HOST=deploy@bayios.example.com WEB_ENV_FILE=infra/linux/env/web.env bash infra/linux/deploy.sh
#   DEPLOY_HOST=deploy@bayios.example.com bash infra/linux/deploy.sh rollback
#   DEPLOY_HOST=deploy@bayios.example.com bash infra/linux/deploy.sh list
#
# deploy (default):
#   1. dotnet publish the API (framework-dependent, the server has the ASP.NET Core 10 runtime)
#   2. npm ci + next build the web app with the NEXT_PUBLIC_* values from WEB_ENV_FILE (public values only)
#   3. rsync both into /opt/bayios/releases/<UTC timestamp>-<git sha>/ on the server
#   4. remote-activate.sh: npm ci --omit=dev, switch /opt/bayios/current, restart the systemd services, health check,
#      automatic switch back to the previous release when the new one is unhealthy
#
# Database migrations are NOT run here: they go through infra/deploy/apply-migrations.sh with the bayios_migrator
# role, before the API (order: migration -> API -> web, docs/operations/deploy-and-rollback.md).
set -euo pipefail

MODE="${1:-deploy}"
DEPLOY_HOST="${DEPLOY_HOST:?DEPLOY_HOST is required, e.g. deploy@bayios.example.com}"
REMOTE_ROOT="${REMOTE_ROOT:-/opt/bayios}"
KEEP_RELEASES="${KEEP_RELEASES:-5}"
SSH_OPTS="${SSH_OPTS:-}"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"

say() { printf '\n==> %s\n' "$*"; }
die() { printf 'deploy: %s\n' "$*" >&2; exit 1; }

# Runs remote-activate.sh on the server: the script goes over stdin, the arguments are quoted with %q.
remote() {
  local cmd
  cmd="$(printf 'BAYIOS_ROOT=%q KEEP_RELEASES=%q bash -s --' "$REMOTE_ROOT" "$KEEP_RELEASES")$(printf ' %q' "$@")"
  # SSH_OPTS is word-split on purpose (e.g. "-p 2222 -i ~/.ssh/bayios"); cmd is expanded here on purpose.
  # shellcheck disable=SC2086,SC2029
  ssh $SSH_OPTS "$DEPLOY_HOST" "$cmd" < "$here/remote-activate.sh"
}

case "$MODE" in
  rollback) remote rollback; exit ;;
  list) remote list; exit ;;
  deploy) ;;
  *) die "usage: deploy.sh [deploy|rollback|list]" ;;
esac

WEB_ENV_FILE="${WEB_ENV_FILE:?WEB_ENV_FILE is required (NEXT_PUBLIC_* values, see infra/linux/env/web.env.example)}"
[ -f "$WEB_ENV_FILE" ] || die "WEB_ENV_FILE '$WEB_ENV_FILE' not found"
for tool in dotnet npm rsync ssh git; do command -v "$tool" >/dev/null || die "'$tool' is not installed"; done
if [ -n "$(git -C "$repo" status --porcelain)" ] && [ "${ALLOW_DIRTY:-0}" != "1" ]; then
  die "working tree has uncommitted changes; commit them or set ALLOW_DIRTY=1"
fi

release_id="$(date -u +%Y%m%d%H%M%S)-$(git -C "$repo" rev-parse --short=12 HEAD)"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT
say "release $release_id"

say "1/4 API: dotnet publish"
dotnet publish "$repo/services/api/src/BayiOS.Api/BayiOS.Api.csproj" \
  --configuration Release --output "$out/api" -p:UseAppHost=false
rm -f "$out/api/appsettings.Development.json"

say "2/4 web: npm ci + next build"
(
  set -a
  # shellcheck source=/dev/null
  . "$WEB_ENV_FILE"
  set +a
  [ -n "${NEXT_PUBLIC_API_BASE_URL:-}" ] || die "NEXT_PUBLIC_API_BASE_URL is empty in $WEB_ENV_FILE"
  cd "$repo/apps/web"
  npm ci --no-audit --no-fund
  NEXT_TELEMETRY_DISABLED=1 npm run build
)
mkdir -p "$out/web"
rsync -a --exclude cache "$repo/apps/web/.next" "$out/web/"
cp "$repo/apps/web/package.json" "$repo/apps/web/package-lock.json" "$repo/apps/web/next.config.ts" "$out/web/"
[ -d "$repo/apps/web/public" ] && rsync -a "$repo/apps/web/public" "$out/web/"

say "3/4 upload to $DEPLOY_HOST:$REMOTE_ROOT/releases/$release_id"
# shellcheck disable=SC2086
rsync -az --delete -e "ssh $SSH_OPTS" "$out/" "$DEPLOY_HOST:$REMOTE_ROOT/releases/$release_id/"

say "4/4 activate (restart + health check, automatic rollback on failure)"
remote activate "$release_id"
