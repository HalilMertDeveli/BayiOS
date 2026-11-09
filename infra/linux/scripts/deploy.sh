#!/usr/bin/env bash
# Deploys a BayiOS release on the server (infra/linux/README.md "Update and rollback").
#
#   sudo bayios-deploy <bayios-<version>.tar.gz | unpacked release dir> <version>
#
# 1. preflight (runtimes, env files, disk space, optional .sha256 next to the tarball)
# 2. unpack to /opt/bayios/releases/<version> (root-owned, read-only for the services)
# 3. atomically repoint /opt/bayios/current, restart bayios-api + bayios-web
# 4. health check (API /health 200, web / 2xx/3xx; with BAYIOS_REQUIRE_READY=1 also API /health/ready 200)
# 5. healthy: /opt/bayios/previous = the release that was current, old releases pruned
#    unhealthy: release marked .failed, current switched back to the previous release and restarted
#
# Exit codes: 0 deployed · 1 refused before switching (nothing changed) · 3 failed, rolled back to the previous release
#             · 4 failed and the rollback is unhealthy too / first deploy failed (services stopped).
# Database migrations are NOT run here: they run before the deploy (infra/deploy/apply-migrations.sh, bayios_migrator).
set -euo pipefail

script_dir="$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"
# shellcheck source-path=SCRIPTDIR
. "$script_dir/lib.sh"

[[ $# -eq 2 ]] || { echo "usage: $0 <artifact.tar.gz|release-dir> <version>" >&2; exit 2; }
artifact="$1"
version="$2"

require_root
valid_version "$version" || die "invalid version '$version' (allowed: [A-Za-z0-9][A-Za-z0-9._-]{0,63})"
[[ -d "$BAYIOS_RELEASES" ]] || die "$BAYIOS_RELEASES missing: run provision.sh first"
take_lock

dest="$BAYIOS_RELEASES/$version"
if [[ -e "$dest" ]]; then
  if [[ -f "$dest/.failed" && "$(current_version)" != "$version" ]]; then
    log "replacing previously failed release $version"
    rm -rf -- "$dest"
  else
    die "release $version already exists (use 'bayios-rollback $version' to switch to it, or deploy a new version)"
  fi
fi

# --- preflight -------------------------------------------------------------------------------------------------------
command -v dotnet >/dev/null || die "dotnet not installed (provision.sh installs aspnetcore-runtime-10.0)"
dotnet --list-runtimes | grep -q '^Microsoft\.AspNetCore\.App 10\.' || die "ASP.NET Core 10 runtime missing"
command -v node >/dev/null || die "node not installed"
node_major="$(node -p 'process.versions.node.split(".")[0]')"
((node_major >= 24)) || die "Node >= 24 required (found $(node --version))"
for f in api.env web.env; do
  [[ -f "$BAYIOS_ENV_DIR/$f" ]] || die "$BAYIOS_ENV_DIR/$f missing (provision.sh creates it from the template)"
done
avail_kb="$(df -Pk "$BAYIOS_RELEASES" | awk 'NR==2 {print $4}')"
((avail_kb > 1048576)) || die "less than 1 GiB free under $BAYIOS_RELEASES"

# --- unpack ----------------------------------------------------------------------------------------------------------
staging="$(mktemp -d "$BAYIOS_RELEASES/.staging.XXXXXX")"
trap 'rm -rf -- "$staging"' EXIT
if [[ -f "$artifact" ]]; then
  if [[ -f "$artifact.sha256" ]]; then
    (cd "$(dirname "$artifact")" && sha256sum --check --status "$(basename "$artifact").sha256") \
      || die "checksum mismatch for $artifact"
    log "checksum OK"
  fi
  log "unpacking $artifact"
  tar -xzf "$artifact" -C "$staging" --no-same-owner --no-same-permissions
elif [[ -d "$artifact" ]]; then
  log "copying $artifact"
  cp -a "$artifact/." "$staging/"
else
  die "artifact not found: $artifact"
fi
[[ -f "$staging/api/BayiOS.Api.dll" ]] || die "not a BayiOS release: api/BayiOS.Api.dll missing"
[[ -f "$staging/web/.next/BUILD_ID" ]] || die "not a BayiOS release: web/.next/BUILD_ID missing"
[[ -f "$staging/web/node_modules/next/dist/bin/next" ]] || die "not a BayiOS release: web/node_modules/next missing"
rm -f "$staging/.failed"
# Next.js writes its runtime cache (images, fetch cache) to .next/cache: point it at the unit's CacheDirectory so the
# release itself stays read-only for the service user.
rm -rf "$staging/web/.next/cache"
ln -s "$BAYIOS_WEB_CACHE" "$staging/web/.next/cache"
chown -R root:root "$staging"
chmod -R u=rwX,go=rX "$staging"
chmod 0755 "$staging"
mv -T "$staging" "$dest"
trap - EXIT
touch "$dest"
if [[ -f "$dest/RELEASE" ]]; then
  sed 's/^/  /' "$dest/RELEASE"
fi

# --- switch + verify -------------------------------------------------------------------------------------------------
prev="$(current_version)"
log "switching current: ${prev:-<none>} -> $version"
switch_current "$version"
restart_services

if report="$(wait_healthy)"; then
  echo "$report"
  if [[ -n "$prev" && "$prev" != "$version" ]]; then
    atomic_link "releases/$prev" "$BAYIOS_PREVIOUS"
  fi
  record "deploy $version ok (previous ${prev:-none})"
  prune_releases
  log "deployed $version"
  exit 0
fi

echo "$report"
warn "release $version failed its health check within ${BAYIOS_HEALTH_TIMEOUT}s"
journalctl -u bayios-api -u bayios-web -n 30 --no-pager 2>/dev/null || true
touch "$dest/.failed"

if [[ -z "$prev" || ! -d "$BAYIOS_RELEASES/$prev" ]]; then
  systemctl stop "${BAYIOS_SERVICES[@]}" || true
  rm -f "$BAYIOS_CURRENT"
  record "deploy $version FAILED (first deploy, services stopped)"
  warn "first deploy failed: services stopped, nothing is serving"
  exit 4
fi

log "automatic rollback to $prev"
switch_current "$prev"
restart_services
if report="$(wait_healthy)"; then
  echo "$report"
  record "deploy $version FAILED, rolled back to $prev"
  warn "deploy of $version failed; rolled back to $prev (healthy)"
  exit 3
fi
echo "$report"
record "deploy $version FAILED, rollback to $prev UNHEALTHY"
warn "deploy of $version failed AND $prev is unhealthy: manual intervention needed (README troubleshooting)"
exit 4
