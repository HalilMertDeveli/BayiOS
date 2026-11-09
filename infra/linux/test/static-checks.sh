#!/usr/bin/env bash
# Static checks of the Linux deploy kit in a throwaway Ubuntu 24.04 container (the same Nginx/systemd versions as the
# target VPS). Used by .github/workflows/ci-linux.yml; locally:
#
#   docker run --rm -v "$PWD/infra/linux:/src:ro" ubuntu:24.04 bash /src/test/static-checks.sh
#
# - nginx -t for the TLS site and the HTTP-only bootstrap site (dummy self-signed certificate at the production path)
# - systemd-analyze verify for both units (real dotnet/node binaries installed, so ExecStart is checked too)
# - systemd-analyze security --offline: fails when a unit's exposure rises above BAYIOS_MAX_EXPOSURE (default 2.0)
set -euo pipefail

src="${1:-/src}"
max_exposure="${BAYIOS_MAX_EXPOSURE:-2.0}"
export DEBIAN_FRONTEND=noninteractive

echo "==> packages"
apt-get update -q >/dev/null
apt-get install -y -q --no-install-recommends ca-certificates openssl nginx systemd aspnetcore-runtime-10.0 nodejs >/dev/null
nginx -v
systemctl --version | head -1

echo "==> nginx -t"
install -m 0644 "$src/nginx/snippets/bayios-tls.conf" "$src/nginx/snippets/bayios-proxy.conf" /etc/nginx/snippets/
install -d /etc/letsencrypt/live/bayios /var/www/letsencrypt
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj "/CN=app.example.com" \
  -keyout /etc/letsencrypt/live/bayios/privkey.pem -out /etc/letsencrypt/live/bayios/fullchain.pem 2>/dev/null
rm -f /etc/nginx/sites-enabled/default
ln -sfn /etc/nginx/sites-available/bayios.conf /etc/nginx/sites-enabled/bayios.conf
for site in bayios.conf bayios-bootstrap.conf; do
  install -m 0644 "$src/nginx/$site" /etc/nginx/sites-available/bayios.conf
  echo "--- $site"
  nginx -t
done

echo "==> systemd-analyze verify"
getent group bayios >/dev/null || groupadd --system bayios
id -u bayios >/dev/null 2>&1 || useradd --system --gid bayios --no-create-home --shell /usr/sbin/nologin bayios
install -m 0644 "$src/systemd/bayios-api.service" "$src/systemd/bayios-web.service" /etc/systemd/system/
systemd-analyze verify /etc/systemd/system/bayios-api.service /etc/systemd/system/bayios-web.service
echo "no findings"

echo "==> systemd-analyze security (offline, threshold $max_exposure)"
threshold="$(awk -v x="$max_exposure" 'BEGIN { printf "%d", x * 10 }')"
rc=0
for unit in bayios-api bayios-web; do
  systemd-analyze security --offline=yes --threshold="$threshold" --no-pager \
    "/etc/systemd/system/$unit.service" | tail -1 || rc=1
done
exit "$rc"
