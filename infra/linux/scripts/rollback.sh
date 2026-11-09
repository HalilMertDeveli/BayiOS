#!/usr/bin/env bash
# Switches BayiOS back to an earlier release that is still on disk (infra/linux/README.md "Rollback").
#
#   sudo bayios-rollback              # to /opt/bayios/previous (running it twice toggles between two releases)
#   sudo bayios-rollback <version>    # to /opt/bayios/releases/<version>
#   sudo bayios-rollback <version> --force   # even if that release is marked .failed
#
# The database is never rolled back: schema changes are expand -> migrate -> contract, so the previous API works
# with the current schema (docs/operations/deploy-and-rollback.md section 5).
# Exit codes: 0 switched and healthy · 1 refused (nothing changed) · 3 target unhealthy, switched back.
set -euo pipefail

script_dir="$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"
# shellcheck source-path=SCRIPTDIR
. "$script_dir/lib.sh"

force=0
target=""
for arg in "$@"; do
  case "$arg" in
    --force) force=1 ;;
    -h | --help) sed -n '2,10p' "$0"; exit 0 ;;
    -*) die "unknown option $arg" ;;
    *) [[ -z "$target" ]] || die "only one version may be given"; target="$arg" ;;
  esac
done

require_root
take_lock

cur="$(current_version)"
if [[ -z "$target" ]]; then
  target="$(previous_version)"
  if [[ -z "$target" ]]; then
    die "no previous release recorded; pass a version: $(find "$BAYIOS_RELEASES" -mindepth 1 -maxdepth 1 -type d ! -name '.*' -printf '%f ')"
  fi
fi
valid_version "$target" || die "invalid version '$target'"
[[ -d "$BAYIOS_RELEASES/$target" ]] || die "release $target not found in $BAYIOS_RELEASES"
[[ "$target" != "$cur" ]] || die "$target is already the current release"
if [[ -f "$BAYIOS_RELEASES/$target/.failed" && $force -ne 1 ]]; then
  die "release $target is marked failed (it did not pass a health check); use --force to switch anyway"
fi

log "rollback: ${cur:-<none>} -> $target"
switch_current "$target"
restart_services
if report="$(wait_healthy)"; then
  echo "$report"
  if [[ -n "$cur" ]]; then
    atomic_link "releases/$cur" "$BAYIOS_PREVIOUS"
  fi
  rm -f "$BAYIOS_RELEASES/$target/.failed"
  record "rollback ${cur:-none} -> $target ok"
  log "now running $target"
  exit 0
fi

echo "$report"
journalctl -u bayios-api -u bayios-web -n 30 --no-pager 2>/dev/null || true
if [[ -n "$cur" && -d "$BAYIOS_RELEASES/$cur" ]]; then
  warn "$target is unhealthy; switching back to $cur"
  switch_current "$cur"
  restart_services
  wait_healthy || warn "$cur is unhealthy as well: manual intervention needed"
fi
record "rollback ${cur:-none} -> $target FAILED (back on ${cur:-none})"
exit 3
