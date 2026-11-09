#!/usr/bin/env bash
# BayiOS health report for the self-hosted server (infra/linux/README.md "Health checks").
#
#   bayios-healthcheck                 # units + API /health + web / on 127.0.0.1 (liveness)
#   bayios-healthcheck --ready         # also fail when API /health/ready is not 200 (DB / configuration)
#   bayios-healthcheck --via-nginx     # also go through Nginx + TLS for both server names (resolved to 127.0.0.1)
#
# Exit 0 = healthy, 1 = something failed. Does not need root. Suitable for cron/monitoring agents.
set -euo pipefail

script_dir="$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"
# shellcheck source-path=SCRIPTDIR
. "$script_dir/lib.sh"

want_ready=0
via_nginx=0
for arg in "$@"; do
  case "$arg" in
    --ready) want_ready=1 ;;
    --via-nginx) via_nginx=1 ;;
    -h | --help) sed -n '2,8p' "$0"; exit 0 ;;
    *) die "unknown option $arg" ;;
  esac
done

rc=0
echo "release: current=$(current_version || true) previous=$(previous_version || true)"
check_live || rc=1
if check_ready; then
  :
elif [[ $want_ready -eq 1 ]]; then
  rc=1
else
  echo "  (readiness is informational; pass --ready to fail on it)"
fi

if [[ $via_nginx -eq 1 ]]; then
  tls_args=()
  # Test mode (self-signed certificate) only; deploy.conf sets this from provision.sh BAYIOS_TEST_MODE=1.
  [[ "${BAYIOS_TLS_INSECURE:-0}" == 1 ]] && tls_args+=(--insecure)
  code="$(http_status "https://$BAYIOS_API_DOMAIN/health" --resolve "$BAYIOS_API_DOMAIN:443:127.0.0.1" "${tls_args[@]}")"
  echo "  nginx https://$BAYIOS_API_DOMAIN/health -> $code"
  [[ "$code" == 200 ]] || rc=1
  code="$(http_status "https://$BAYIOS_APP_DOMAIN/" --resolve "$BAYIOS_APP_DOMAIN:443:127.0.0.1" "${tls_args[@]}")"
  echo "  nginx https://$BAYIOS_APP_DOMAIN/ -> $code"
  [[ "$code" =~ ^[23][0-9][0-9]$ ]] || rc=1
  code="$(http_status "http://$BAYIOS_APP_DOMAIN/" --resolve "$BAYIOS_APP_DOMAIN:80:127.0.0.1")"
  echo "  nginx http://$BAYIOS_APP_DOMAIN/ -> $code (expect 301)"
  [[ "$code" == 301 ]] || rc=1
fi

if [[ $rc -eq 0 ]]; then echo "HEALTHY"; else echo "UNHEALTHY"; fi
exit "$rc"
