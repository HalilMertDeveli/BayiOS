#!/usr/bin/env bash
# Shared helpers for deploy.sh, rollback.sh and healthcheck.sh. Sourced, never executed.
# Every path/URL can be overridden through the environment. /etc/bayios/deploy.conf (written by provision.sh) supplies
# BAYIOS_* values that are not already set in the environment; it is parsed as KEY=VALUE lines, never executed.

BAYIOS_CONF="${BAYIOS_CONF:-/etc/bayios/deploy.conf}"
if [[ -r "$BAYIOS_CONF" ]]; then
  while IFS='=' read -r _key _value; do
    [[ "$_key" =~ ^BAYIOS_[A-Z_]+$ ]] || continue
    [[ -n "${!_key+x}" ]] || printf -v "$_key" '%s' "$_value"
  done <"$BAYIOS_CONF"
  unset _key _value
fi

BAYIOS_ROOT="${BAYIOS_ROOT:-/opt/bayios}"
BAYIOS_RELEASES="${BAYIOS_RELEASES:-$BAYIOS_ROOT/releases}"
BAYIOS_CURRENT="${BAYIOS_CURRENT:-$BAYIOS_ROOT/current}"
BAYIOS_PREVIOUS="${BAYIOS_PREVIOUS:-$BAYIOS_ROOT/previous}"
BAYIOS_DEPLOY_LOG="${BAYIOS_DEPLOY_LOG:-$BAYIOS_ROOT/deploy.log}"
BAYIOS_LOCK="${BAYIOS_LOCK:-/run/lock/bayios-deploy.lock}"
BAYIOS_WEB_CACHE="${BAYIOS_WEB_CACHE:-/var/cache/bayios-web}"
BAYIOS_ENV_DIR="${BAYIOS_ENV_DIR:-/etc/bayios}"
BAYIOS_APP_DOMAIN="${BAYIOS_APP_DOMAIN:-app.example.com}"
BAYIOS_API_DOMAIN="${BAYIOS_API_DOMAIN:-api.example.com}"
BAYIOS_API_URL="${BAYIOS_API_URL:-http://127.0.0.1:5280}"
BAYIOS_WEB_URL="${BAYIOS_WEB_URL:-http://127.0.0.1:3200}"
BAYIOS_HEALTH_TIMEOUT="${BAYIOS_HEALTH_TIMEOUT:-90}"
BAYIOS_KEEP_RELEASES="${BAYIOS_KEEP_RELEASES:-5}"
BAYIOS_SERVICES=(bayios-api bayios-web)

log() { printf '%s [bayios] %s\n' "$(date -u +%H:%M:%S)" "$*"; }
warn() { printf '%s [bayios] WARNING: %s\n' "$(date -u +%H:%M:%S)" "$*" >&2; }
die() { printf '%s [bayios] ERROR: %s\n' "$(date -u +%H:%M:%S)" "$*" >&2; exit 1; }

require_root() { [[ $EUID -eq 0 ]] || die "run as root (sudo $0 ...)"; }

valid_version() { [[ "$1" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]]; }

# Version the given symlink points at ("" if missing).
link_version() {
  local target
  target="$(readlink "$1" 2>/dev/null || true)"
  [[ -n "$target" ]] && basename "$target"
  return 0
}

current_version() { link_version "$BAYIOS_CURRENT"; }
previous_version() { link_version "$BAYIOS_PREVIOUS"; }

# Atomically repoint a symlink (rename(2) over the old link; there is never a moment without one).
atomic_link() {
  local target="$1" link="$2" tmp
  tmp="$(dirname "$link")/.$(basename "$link").tmp.$$"
  ln -sfn "$target" "$tmp"
  mv -Tf "$tmp" "$link"
}

switch_current() { atomic_link "releases/$1" "$BAYIOS_CURRENT"; }

record() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >>"$BAYIOS_DEPLOY_LOG"; }

# One deploy/rollback at a time. The lock is released when the process exits.
take_lock() {
  exec 9>"$BAYIOS_LOCK"
  flock -n 9 || die "another deploy/rollback is running ($BAYIOS_LOCK)"
}

restart_services() {
  log "restarting ${BAYIOS_SERVICES[*]}"
  # A crash-looping release may have hit StartLimitBurst; clear that so the next release/rollback can start.
  systemctl reset-failed "${BAYIOS_SERVICES[@]}" 2>/dev/null || true
  systemctl stop bayios-web || true
  # The Next.js runtime cache (.next/cache -> $BAYIOS_WEB_CACHE) belongs to one build; never reuse it across releases.
  if [[ -d "$BAYIOS_WEB_CACHE" ]]; then
    find "$BAYIOS_WEB_CACHE" -mindepth 1 -delete
  fi
  systemctl restart bayios-api
  systemctl start bayios-web
}

# HTTP status of a URL (000 when unreachable). Extra curl args may follow.
http_status() {
  local url="$1"
  shift
  curl --silent --output /dev/null --write-out '%{http_code}' --max-time 5 "$@" "$url" || true
}

# Liveness of both services: units active, API /health = 200, web / = 2xx/3xx. Prints one line per check.
check_live() {
  local ok=0 unit code
  for unit in "${BAYIOS_SERVICES[@]}"; do
    if systemctl is-active --quiet "$unit"; then
      echo "  unit $unit: active"
    else
      echo "  unit $unit: $(systemctl is-active "$unit" 2>/dev/null || true)"
      ok=1
    fi
  done
  code="$(http_status "$BAYIOS_API_URL/health")"
  echo "  api  $BAYIOS_API_URL/health -> $code"
  [[ "$code" == 200 ]] || ok=1
  code="$(http_status "$BAYIOS_WEB_URL/")"
  echo "  web  $BAYIOS_WEB_URL/ -> $code"
  [[ "$code" =~ ^[23][0-9][0-9]$ ]] || ok=1
  return "$ok"
}

# Readiness of the API (DB + configuration): 200 = ready/degraded, 503 = unavailable.
check_ready() {
  local code
  code="$(http_status "$BAYIOS_API_URL/health/ready")"
  echo "  api  $BAYIOS_API_URL/health/ready -> $code"
  [[ "$code" == 200 ]]
}

# Poll until healthy or BAYIOS_HEALTH_TIMEOUT seconds pass. BAYIOS_REQUIRE_READY=1 also gates on /health/ready.
wait_healthy() {
  local deadline=$((SECONDS + BAYIOS_HEALTH_TIMEOUT)) report ready
  while :; do
    if report="$(check_live)"; then
      if [[ "${BAYIOS_REQUIRE_READY:-0}" != 1 ]]; then
        echo "$report"
        return 0
      fi
      if ready="$(check_ready)"; then
        printf '%s\n%s\n' "$report" "$ready"
        return 0
      fi
      report+=$'\n'"$ready"
    fi
    if ((SECONDS >= deadline)); then
      echo "$report"
      return 1
    fi
    sleep 2
  done
}

# Keep the newest BAYIOS_KEEP_RELEASES release directories; never delete current or previous.
prune_releases() {
  local cur prev dir name
  cur="$(current_version)"
  prev="$(previous_version)"
  local -a releases=()
  while IFS= read -r dir; do releases+=("$dir"); done < <(ls -1dt "$BAYIOS_RELEASES"/*/ 2>/dev/null || true)
  local i
  for ((i = BAYIOS_KEEP_RELEASES; i < ${#releases[@]}; i++)); do
    name="$(basename "${releases[$i]}")"
    [[ "$name" == "$cur" || "$name" == "$prev" ]] && continue
    log "pruning old release $name"
    rm -rf -- "${BAYIOS_RELEASES:?}/$name"
  done
}
