#!/usr/bin/env bash
# BayiOS — server side of a deploy. deploy.sh streams this file over SSH (`ssh host bash -s -- ...`);
# it can also be run by hand on the server as the deploy user.
#
#   remote-activate.sh activate <release-id>   switch /opt/bayios/current to releases/<id>, restart, health check;
#                                              on failure switch back to the previous release automatically
#   remote-activate.sh rollback                switch to the release before the current one, restart, health check
#   remote-activate.sh list                    show releases (* = current)
#
# Release ids sort by time: <UTC yyyymmddHHMMSS>-<git sha>. The previous releases are kept (KEEP_RELEASES).
# Overridable for tests (infra/linux/test.sh): BAYIOS_ROOT, SYSTEMCTL, API_HEALTH_URL, WEB_HEALTH_URL,
# HEALTH_TRIES, HEALTH_SLEEP, SKIP_NPM, WEB_CACHE_DIR.
set -euo pipefail

BAYIOS_ROOT="${BAYIOS_ROOT:-/opt/bayios}"
SYSTEMCTL="${SYSTEMCTL:-sudo -n /usr/bin/systemctl}"
API_HEALTH_URL="${API_HEALTH_URL:-http://127.0.0.1:5080/health}"
WEB_HEALTH_URL="${WEB_HEALTH_URL:-http://127.0.0.1:3000/}"
HEALTH_TRIES="${HEALTH_TRIES:-30}"
HEALTH_SLEEP="${HEALTH_SLEEP:-2}"
KEEP_RELEASES="${KEEP_RELEASES:-5}"
SKIP_NPM="${SKIP_NPM:-0}"
WEB_CACHE_DIR="${WEB_CACHE_DIR:-/var/cache/bayios-web}"

releases="$BAYIOS_ROOT/releases"
current="$BAYIOS_ROOT/current"

say() { printf '[remote] %s\n' "$*"; }
die() { printf '[remote] ERROR: %s\n' "$*" >&2; exit 1; }

current_id() { if [ -L "$current" ]; then basename "$(readlink "$current")"; fi; }

# All release ids, oldest first.
release_ids() { find "$releases" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | sort; }

# Atomic switch: a new symlink is renamed over the old one, so `current` is never missing.
switch_to() {
  ln -sfn "$releases/$1" "$current.tmp"
  mv -Tf "$current.tmp" "$current"
  say "current -> $1"
}

restart() {
  $SYSTEMCTL restart bayios-api.service
  $SYSTEMCTL restart bayios-web.service
}

status_of() { curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$1" || true; }

# API /health must be 200; the web root may answer 200 or redirect to the sign-in page (3xx).
healthy() {
  local i api="" web=""
  for ((i = 1; i <= HEALTH_TRIES; i++)); do
    api="$(status_of "$API_HEALTH_URL")"
    web="$(status_of "$WEB_HEALTH_URL")"
    if [ "$api" = "200" ] && [[ "$web" =~ ^[23][0-9][0-9]$ ]]; then
      say "healthy: api=$api web=$web"
      return 0
    fi
    sleep "$HEALTH_SLEEP"
  done
  say "unhealthy after $HEALTH_TRIES tries: api=$api web=$web"
  return 1
}

prepare() { # install production node_modules and link the writable Next.js cache
  local web="$releases/$1/web"
  [ -f "$releases/$1/api/BayiOS.Api.dll" ] || die "release $1 has no api/BayiOS.Api.dll"
  [ -f "$web/package.json" ] || die "release $1 has no web/package.json"
  if [ "$SKIP_NPM" != "1" ]; then
    (cd "$web" && npm ci --omit=dev --ignore-scripts --no-audit --no-fund)
  fi
  mkdir -p "$web/.next"
  ln -sfn "$WEB_CACHE_DIR" "$web/.next/cache"
}

prune() { # keep the newest KEEP_RELEASES, never the current one
  local keep_from cur id
  cur="$(current_id)"
  keep_from=$(( $(release_ids | wc -l) - KEEP_RELEASES ))
  [ "$keep_from" -gt 0 ] || return 0
  release_ids | head -n "$keep_from" | while read -r id; do
    [ "$id" = "$cur" ] && continue
    rm -rf "${releases:?}/$id"
    say "pruned $id"
  done
}

activate() {
  local id="$1" previous
  [[ "$id" =~ ^[0-9]{14}-[0-9a-f]+$ ]] || die "invalid release id '$id'"
  [ -d "$releases/$id" ] || die "release $id not uploaded"
  previous="$(current_id)"
  prepare "$id"
  switch_to "$id"
  restart
  if healthy; then
    prune
    say "deployed $id"
    return 0
  fi
  if [ -n "$previous" ] && [ "$previous" != "$id" ]; then
    say "rolling back to $previous"
    switch_to "$previous"
    restart
    healthy || die "rollback to $previous is unhealthy too: check journalctl -u bayios-api -u bayios-web"
  fi
  die "release $id failed its health check (now on: ${previous:-nothing})"
}

rollback() {
  local cur target
  cur="$(current_id)"
  [ -n "$cur" ] || die "no current release"
  target="$(release_ids | awk -v cur="$cur" '$0 == cur { print prev; exit } { prev = $0 }')"
  [ -n "$target" ] || die "no release older than $cur"
  switch_to "$target"
  restart
  healthy || die "rolled back to $target but it is unhealthy: check journalctl -u bayios-api -u bayios-web"
  say "rolled back $cur -> $target"
}

list() {
  local cur id
  cur="$(current_id)"
  release_ids | while read -r id; do
    if [ "$id" = "$cur" ]; then echo "* $id"; else echo "  $id"; fi
  done
}

case "${1:-}" in
  activate) [ $# -eq 2 ] || die "usage: $0 activate <release-id>"; activate "$2" ;;
  rollback) rollback ;;
  list) list ;;
  *) die "usage: $0 activate <release-id> | rollback | list" ;;
esac
