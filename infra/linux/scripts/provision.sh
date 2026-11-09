#!/usr/bin/env bash
# Idempotent setup of a fresh Ubuntu 24.04 VPS for BayiOS (API + web behind Nginx). See infra/linux/README.md.
# Run as root from a checkout (or a copy) of infra/linux:
#
#   sudo BAYIOS_APP_DOMAIN=app.example.com BAYIOS_API_DOMAIN=api.example.com \
#        BAYIOS_CERTBOT_EMAIL=ops@example.com \
#        BAYIOS_DEPLOY_USER=deploy BAYIOS_DEPLOY_PUBKEY="ssh-ed25519 AAAA... you@laptop" BAYIOS_HARDEN_SSH=1 \
#        bash infra/linux/scripts/provision.sh
#
# Environment:
#   BAYIOS_APP_DOMAIN / BAYIOS_API_DOMAIN  server names (DNS A/AAAA records must point here before certbot runs)
#   BAYIOS_CERTBOT_EMAIL   when set and no certificate exists yet: issue one with `certbot certonly --nginx`
#   BAYIOS_DEPLOY_USER     optional non-root deploy user, allowed to run only bayios-deploy/-rollback/-healthcheck via sudo
#   BAYIOS_DEPLOY_PUBKEY   optional SSH public key added to that user's authorized_keys
#   BAYIOS_HARDEN_SSH=1    key-only SSH, no root login (refused unless the deploy user has an authorized key)
#   BAYIOS_TEST_MODE=1     local test harness only: self-signed certificate, no certbot, ufw rules written but not enabled
#
# Never touches: databases (managed Supabase), DNS, paid resources. Re-running is safe; existing env files are kept.
set -euo pipefail

here="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/.." && pwd)" # infra/linux
APP_DOMAIN="${BAYIOS_APP_DOMAIN:-app.example.com}"
API_DOMAIN="${BAYIOS_API_DOMAIN:-api.example.com}"
TEST_MODE="${BAYIOS_TEST_MODE:-0}"
CERTBOT_EMAIL="${BAYIOS_CERTBOT_EMAIL:-}"
DEPLOY_USER="${BAYIOS_DEPLOY_USER:-}"
DEPLOY_PUBKEY="${BAYIOS_DEPLOY_PUBKEY:-}"
HARDEN_SSH="${BAYIOS_HARDEN_SSH:-0}"
NODE_MAJOR=24
# NodeSource repository signing key (https://github.com/nodesource/distributions).
NODESOURCE_KEY_FPR="6F71F525282841EEDAF851B42F59B5F99B1BE0B4"
CERT_NAME=bayios
LE_CERT_DIR="/etc/letsencrypt/live/$CERT_NAME"
TEST_CERT_DIR="/etc/ssl/bayios-selfsigned"

log() { printf '\n==> %s\n' "$*"; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "run as root"
for d in "$APP_DOMAIN" "$API_DOMAIN"; do
  [[ "$d" =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$ ]] || die "invalid domain '$d'"
done
[[ -z "$DEPLOY_USER" || "$DEPLOY_USER" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] || die "invalid deploy user name"
# shellcheck source=/dev/null
. /etc/os-release
if [[ "${ID:-}" != ubuntu || "${VERSION_ID:-}" != 24.04 ]]; then
  echo "WARNING: written for Ubuntu 24.04, this is ${PRETTY_NAME:-unknown}" >&2
fi
export DEBIAN_FRONTEND=noninteractive

# --- packages --------------------------------------------------------------------------------------------------------
log "base packages"
apt-get update -q
apt-get install -y -q --no-install-recommends \
  ca-certificates curl gnupg nginx ufw unattended-upgrades logrotate openssl sudo \
  certbot python3-certbot-nginx

log ".NET 10 ASP.NET Core runtime (Ubuntu archive, patched via noble-security)"
apt-get install -y -q --no-install-recommends aspnetcore-runtime-10.0
dotnet --list-runtimes

log "Node.js $NODE_MAJOR (NodeSource apt repository, pinned to the $NODE_MAJOR.x line)"
install -d -m 0755 /etc/apt/keyrings
if [[ ! -s /etc/apt/keyrings/nodesource.gpg ]]; then
  tmpkey="$(mktemp)"
  curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key -o "$tmpkey"
  fpr="$(gpg --show-keys --with-colons "$tmpkey" | awk -F: '$1 == "fpr" {print $10; exit}')"
  [[ "$fpr" == "$NODESOURCE_KEY_FPR" ]] || { rm -f "$tmpkey"; die "NodeSource key fingerprint mismatch: $fpr"; }
  gpg --dearmor <"$tmpkey" >/etc/apt/keyrings/nodesource.gpg
  rm -f "$tmpkey"
  chmod 0644 /etc/apt/keyrings/nodesource.gpg
fi
cat >/etc/apt/sources.list.d/nodesource.sources <<EOF
Types: deb
URIs: https://deb.nodesource.com/node_$NODE_MAJOR.x
Suites: nodistro
Components: main
Signed-By: /etc/apt/keyrings/nodesource.gpg
EOF
# Prefer NodeSource's nodejs over Ubuntu's older one.
cat >/etc/apt/preferences.d/nodesource <<'EOF'
Package: nodejs
Pin: origin deb.nodesource.com
Pin-Priority: 600
EOF
apt-get update -q
apt-get install -y -q nodejs
node_major="$(node -p 'process.versions.node.split(".")[0]')"
[[ "$node_major" == "$NODE_MAJOR" ]] || die "expected Node $NODE_MAJOR, got $(node --version)"
echo "node $(node --version)"

log "unattended security upgrades"
cat >/etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
EOF
# Reboots stay manual (planned window); check /var/run/reboot-required. Node (NodeSource) is upgraded by hand.
cat >/etc/apt/apt.conf.d/52bayios-unattended-upgrades <<'EOF'
Unattended-Upgrade::Automatic-Reboot "false";
Unattended-Upgrade::Remove-Unused-Dependencies "true";
EOF
systemctl enable --now unattended-upgrades.service >/dev/null 2>&1 || true

# --- users and directories -------------------------------------------------------------------------------------------
log "service user 'bayios' and directories"
getent group bayios >/dev/null || groupadd --system bayios
id -u bayios >/dev/null 2>&1 \
  || useradd --system --gid bayios --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin bayios
install -d -o root -g root -m 0755 /opt/bayios /opt/bayios/releases /opt/bayios/bin /var/www/letsencrypt
touch /opt/bayios/deploy.log
chmod 0644 /opt/bayios/deploy.log
install -d -o root -g root -m 0755 /etc/bayios
for f in api web; do
  if [[ ! -f "/etc/bayios/$f.env" ]]; then
    sed -e "s/app\.example\.com/$APP_DOMAIN/g" -e "s/api\.example\.com/$API_DOMAIN/g" \
      "$here/env/$f.env.example" >"/etc/bayios/$f.env"
    echo "created /etc/bayios/$f.env from the template: fill in the values"
  fi
  chown root:bayios "/etc/bayios/$f.env"
  chmod 0640 "/etc/bayios/$f.env"
done
tls_insecure=0
[[ "$TEST_MODE" == 1 ]] && tls_insecure=1
cat >/etc/bayios/deploy.conf <<EOF
# Written by provision.sh; read (not executed) by bayios-deploy/-rollback/-healthcheck.
BAYIOS_APP_DOMAIN=$APP_DOMAIN
BAYIOS_API_DOMAIN=$API_DOMAIN
BAYIOS_TLS_INSECURE=$tls_insecure
EOF
chmod 0644 /etc/bayios/deploy.conf

log "deploy tooling -> /opt/bayios/bin, /usr/local/sbin/bayios-*"
for s in lib deploy rollback healthcheck; do
  install -o root -g root -m 0755 "$here/scripts/$s.sh" "/opt/bayios/bin/$s.sh"
done
for s in deploy rollback healthcheck; do
  ln -sfn "/opt/bayios/bin/$s.sh" "/usr/local/sbin/bayios-$s"
done

if [[ -n "$DEPLOY_USER" ]]; then
  log "deploy user '$DEPLOY_USER'"
  id -u "$DEPLOY_USER" >/dev/null 2>&1 || useradd --create-home --shell /bin/bash "$DEPLOY_USER"
  install -d -o "$DEPLOY_USER" -g "$DEPLOY_USER" -m 0700 "/home/$DEPLOY_USER/.ssh"
  akeys="/home/$DEPLOY_USER/.ssh/authorized_keys"
  touch "$akeys"
  if [[ -n "$DEPLOY_PUBKEY" ]] && ! grep -qxF "$DEPLOY_PUBKEY" "$akeys"; then
    printf '%s\n' "$DEPLOY_PUBKEY" >>"$akeys"
  fi
  chown "$DEPLOY_USER:$DEPLOY_USER" "$akeys"
  chmod 0600 "$akeys"
  install -d -o "$DEPLOY_USER" -g "$DEPLOY_USER" -m 0750 "/home/$DEPLOY_USER/artifacts"
  # Logs without sudo (sudo journalctl would hand out a root pager = root shell).
  usermod -aG systemd-journal,adm "$DEPLOY_USER"
  sudoers="$(mktemp)"
  cat >"$sudoers" <<EOF
# BayiOS: the deploy user may run only the deploy tooling as root (provision.sh). Note: deploying a release means
# running that release's code as the unprivileged 'bayios' user — treat this account's SSH key accordingly.
$DEPLOY_USER ALL=(root) NOPASSWD: /usr/local/sbin/bayios-deploy, /usr/local/sbin/bayios-rollback
EOF
  visudo -cqf "$sudoers" || { rm -f "$sudoers"; die "generated sudoers file is invalid"; }
  install -o root -g root -m 0440 "$sudoers" /etc/sudoers.d/bayios-deploy
  rm -f "$sudoers"
fi

# --- systemd ---------------------------------------------------------------------------------------------------------
log "systemd units"
install -o root -g root -m 0644 "$here/systemd/bayios-api.service" /etc/systemd/system/bayios-api.service
install -o root -g root -m 0644 "$here/systemd/bayios-web.service" /etc/systemd/system/bayios-web.service
install -d -m 0755 /etc/systemd/journald.conf.d
cat >/etc/systemd/journald.conf.d/50-bayios.conf <<'EOF'
# Bound the journal on a small disk; application logs are structured JSON on stdout (observability.md).
[Journal]
SystemMaxUse=1G
MaxRetentionSec=30day
EOF
systemctl restart systemd-journald
systemctl daemon-reload
systemctl enable bayios-api.service bayios-web.service
systemd-analyze verify /etc/systemd/system/bayios-api.service /etc/systemd/system/bayios-web.service

# --- nginx + TLS -----------------------------------------------------------------------------------------------------
render_site() { # <template> <cert-dir>
  sed -e "s/app\.example\.com/$APP_DOMAIN/g" -e "s/api\.example\.com/$API_DOMAIN/g" \
    -e "s#/etc/letsencrypt/live/bayios#$2#g" "$1" >/etc/nginx/sites-available/bayios.conf
}

log "nginx"
install -o root -g root -m 0644 "$here/nginx/snippets/bayios-tls.conf" /etc/nginx/snippets/bayios-tls.conf
install -o root -g root -m 0644 "$here/nginx/snippets/bayios-proxy.conf" /etc/nginx/snippets/bayios-proxy.conf
rm -f /etc/nginx/sites-enabled/default
ln -sfn /etc/nginx/sites-available/bayios.conf /etc/nginx/sites-enabled/bayios.conf
install -d -m 0755 /etc/letsencrypt/renewal-hooks/deploy
cat >/etc/letsencrypt/renewal-hooks/deploy/bayios-reload-nginx.sh <<'EOF'
#!/bin/sh
# Pick up renewed certificates (certbot certonly does not reload Nginx by itself).
systemctl reload nginx
EOF
chmod 0755 /etc/letsencrypt/renewal-hooks/deploy/bayios-reload-nginx.sh

if [[ "$TEST_MODE" == 1 ]]; then
  if [[ ! -s "$TEST_CERT_DIR/fullchain.pem" ]]; then
    echo "TEST MODE: self-signed certificate in $TEST_CERT_DIR (never use in production)"
    install -d -m 0755 "$TEST_CERT_DIR"
    openssl req -x509 -newkey rsa:2048 -nodes -days 7 -subj "/CN=$APP_DOMAIN" \
      -addext "subjectAltName=DNS:$APP_DOMAIN,DNS:$API_DOMAIN" \
      -keyout "$TEST_CERT_DIR/privkey.pem" -out "$TEST_CERT_DIR/fullchain.pem" 2>/dev/null
    chmod 0600 "$TEST_CERT_DIR/privkey.pem"
  fi
  render_site "$here/nginx/bayios.conf" "$TEST_CERT_DIR"
elif [[ -s "$LE_CERT_DIR/fullchain.pem" ]]; then
  render_site "$here/nginx/bayios.conf" "$LE_CERT_DIR"
else
  render_site "$here/nginx/bayios-bootstrap.conf" "$LE_CERT_DIR"
fi
nginx -t
systemctl enable nginx >/dev/null
systemctl reload-or-restart nginx

if [[ "$TEST_MODE" != 1 && ! -s "$LE_CERT_DIR/fullchain.pem" ]]; then
  if [[ -n "$CERTBOT_EMAIL" ]]; then
    log "certbot: issuing '$CERT_NAME' for $APP_DOMAIN, $API_DOMAIN"
    certbot certonly --nginx --non-interactive --agree-tos --no-eff-email --email "$CERTBOT_EMAIL" \
      --cert-name "$CERT_NAME" -d "$APP_DOMAIN" -d "$API_DOMAIN"
    render_site "$here/nginx/bayios.conf" "$LE_CERT_DIR"
    nginx -t
    systemctl reload nginx
  else
    echo "No certificate yet and BAYIOS_CERTBOT_EMAIL is unset: Nginx serves the HTTP-only bootstrap site."
    echo "Point DNS at this server, then re-run with BAYIOS_CERTBOT_EMAIL=<address>."
  fi
fi
systemctl enable --now certbot.timer >/dev/null 2>&1 || true

# --- firewall --------------------------------------------------------------------------------------------------------
log "ufw: deny incoming except SSH (rate-limited), 80, 443"
ufw default deny incoming >/dev/null
ufw default allow outgoing >/dev/null
ufw limit OpenSSH >/dev/null
ufw allow 80/tcp >/dev/null
ufw allow 443/tcp >/dev/null
if [[ "$TEST_MODE" == 1 ]]; then
  echo "TEST MODE: rules written, firewall NOT enabled"
  ufw show added
else
  ufw --force enable
  ufw status verbose
fi

# --- ssh -------------------------------------------------------------------------------------------------------------
if [[ "$HARDEN_SSH" == 1 ]]; then
  log "ssh hardening"
  [[ -n "$DEPLOY_USER" && -s "/home/$DEPLOY_USER/.ssh/authorized_keys" ]] \
    || die "BAYIOS_HARDEN_SSH=1 needs BAYIOS_DEPLOY_USER with an authorized key (otherwise you lock yourself out)"
  install -d -m 0755 /etc/ssh/sshd_config.d
  # 00- so it wins over cloud-init's 50-cloud-init.conf (sshd uses the first value it reads).
  cat >/etc/ssh/sshd_config.d/00-bayios.conf <<'EOF'
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
PubkeyAuthentication yes
X11Forwarding no
MaxAuthTries 3
EOF
  # Ubuntu 24.04 socket-activates sshd, so its runtime directory may not exist yet; `sshd -t` needs it.
  install -d -m 0755 /run/sshd
  sshd -t
  systemctl try-reload-or-restart ssh.service
  echo "sshd: key-only, no root login. Keep this session open and test a new login as $DEPLOY_USER first."
fi

log "done"
cat <<EOF
Next steps:
  1. Fill in /etc/bayios/api.env and /etc/bayios/web.env (values from the password manager; never commit them).
  2. Build a release (scripts/build-release.sh) and copy it here, then: sudo bayios-deploy <tarball> <version>
  3. bayios-healthcheck --via-nginx --ready
EOF
