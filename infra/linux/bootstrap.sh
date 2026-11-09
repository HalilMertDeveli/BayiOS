#!/usr/bin/env bash
# BayiOS — one-time setup of a fresh Ubuntu 24.04 VPS. Idempotent: safe to run again.
#
# Run as root ON THE SERVER, from a copy of infra/linux (see infra/linux/README.md, step 3):
#
#   sudo DOMAIN=bayios.example.com DEPLOY_SSH_PUBKEY="ssh-ed25519 AAAA... you@laptop" bash bootstrap.sh
#
# What it does:
#   1. apt upgrade + base packages, unattended security upgrades
#   2. users: $DEPLOY_USER (SSH, limited sudo), bayios-api and bayios-web (system users, no shell, no home)
#   3. SSH: key-only login, no passwords (only after the deploy key is in place)
#   4. firewall (ufw: SSH + 80/443 only) and fail2ban (sshd jail)
#   5. runtimes: ASP.NET Core 10 runtime (Ubuntu archive), Node.js (NodeSource apt repository), nginx, certbot
#   6. /opt/bayios/releases, /etc/bayios/*.env (from the examples, root-only), systemd units, nginx site, sudoers
#
# It does NOT start the services (there is no release yet: run deploy.sh) and does NOT request a TLS certificate
# (needs DNS pointing at the server first: README step 5).
set -euo pipefail

DOMAIN="${DOMAIN:?DOMAIN is required, e.g. DOMAIN=bayios.example.com}"
DEPLOY_USER="${DEPLOY_USER:-deploy}"
DEPLOY_SSH_PUBKEY="${DEPLOY_SSH_PUBKEY:-}"
SSH_PORT="${SSH_PORT:-22}"
# The web app declares "engines": { "node": ">=24" } (apps/web/package.json) and CI uses Node 24.
NODE_MAJOR="${NODE_MAJOR:-24}"
DOTNET_PACKAGE="${DOTNET_PACKAGE:-aspnetcore-runtime-10.0}"
BAYIOS_ROOT="/opt/bayios"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

log() { printf '\n==> %s\n' "$*"; }
die() { printf 'bootstrap: %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "run as root (sudo)"
# shellcheck source=/dev/null
. /etc/os-release
[ "${ID:-}" = "ubuntu" ] && [ "${VERSION_ID:-}" = "24.04" ] \
  || [ "${BOOTSTRAP_ALLOW_ANY_OS:-0}" = "1" ] \
  || die "tested on Ubuntu 24.04 only (found ${PRETTY_NAME:-unknown}); set BOOTSTRAP_ALLOW_ANY_OS=1 to try anyway"
[[ "$DOMAIN" =~ ^[A-Za-z0-9.-]+$ ]] || die "DOMAIN must be a plain host name"
[[ "$DEPLOY_USER" =~ ^[a-z_][a-z0-9_-]*$ ]] || die "DEPLOY_USER is not a valid user name"
[[ "$SSH_PORT" =~ ^[0-9]+$ ]] || die "SSH_PORT must be a number"
for f in systemd/bayios-api.service systemd/bayios-web.service nginx/bayios.conf sudoers/bayios-deploy \
         env/api.env.example env/web.env.example; do
  [ -f "$here/$f" ] || die "missing $here/$f (copy the whole infra/linux directory to the server)"
done

export DEBIAN_FRONTEND=noninteractive

log "1/6 packages and unattended upgrades"
apt-get update -q
apt-get upgrade -yq
apt-get install -yq --no-install-recommends \
  ca-certificates curl gnupg rsync \
  ufw fail2ban unattended-upgrades nginx certbot python3-certbot-nginx
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
EOF

log "2/6 users"
if ! id "$DEPLOY_USER" >/dev/null 2>&1; then
  useradd --create-home --shell /bin/bash "$DEPLOY_USER"
fi
usermod -aG systemd-journal "$DEPLOY_USER"   # read service logs with journalctl, no sudo
for svc in bayios-api bayios-web; do
  id "$svc" >/dev/null 2>&1 || useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin "$svc"
done
deploy_home="$(getent passwd "$DEPLOY_USER" | cut -d: -f6)"
install -d -m 0700 -o "$DEPLOY_USER" -g "$DEPLOY_USER" "$deploy_home/.ssh"
auth_keys="$deploy_home/.ssh/authorized_keys"
touch "$auth_keys"
if [ -n "$DEPLOY_SSH_PUBKEY" ] && ! grep -qxF "$DEPLOY_SSH_PUBKEY" "$auth_keys"; then
  printf '%s\n' "$DEPLOY_SSH_PUBKEY" >> "$auth_keys"
fi
chown "$DEPLOY_USER:$DEPLOY_USER" "$auth_keys"
chmod 0600 "$auth_keys"

log "3/6 SSH hardening"
if [ -s "$auth_keys" ]; then
  cat > /etc/ssh/sshd_config.d/60-bayios.conf <<EOF
# Managed by infra/linux/bootstrap.sh
Port ${SSH_PORT}
PasswordAuthentication no
KbdInteractiveAuthentication no
# Administration stays possible with the provider-installed root key; passwords are never accepted.
# The deploy user only has the narrow sudo rights in /etc/sudoers.d/bayios-deploy.
PermitRootLogin prohibit-password
EOF
  sshd -t
  # Ubuntu 24.04 starts sshd through ssh.socket; a Port change only applies after the socket is regenerated.
  systemctl daemon-reload
  if systemctl is-active --quiet ssh.socket; then systemctl restart ssh.socket; fi
  systemctl reload-or-restart ssh
else
  # Never lock the operator out: without a key for the deploy user, password/root login stays as it was.
  echo "WARNING: no SSH key for ${DEPLOY_USER}; SSH hardening skipped. Re-run with DEPLOY_SSH_PUBKEY=..."
fi

log "4/6 firewall and fail2ban"
ufw default deny incoming
ufw default allow outgoing
ufw allow "${SSH_PORT}/tcp"
ufw allow 'Nginx Full'
ufw --force enable
systemctl enable --now ufw
cat > /etc/fail2ban/jail.d/bayios.local <<EOF
# Managed by infra/linux/bootstrap.sh
[sshd]
enabled  = true
port     = ${SSH_PORT}
backend  = systemd
maxretry = 5
findtime = 10m
bantime  = 1h
EOF
systemctl enable --now fail2ban
systemctl restart fail2ban

log "5/6 runtimes: ${DOTNET_PACKAGE}, Node.js ${NODE_MAJOR}.x"
# ASP.NET Core 10 comes from the regular Ubuntu 24.04 archive (noble-updates / noble-security): no extra repository.
# Node.js comes from the NodeSource apt repository (Ubuntu 24.04 itself ships Node 18).
install -d -m 0755 /etc/apt/keyrings
curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key \
  | gpg --dearmor --yes -o /etc/apt/keyrings/nodesource.gpg
echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_${NODE_MAJOR}.x nodistro main" \
  > /etc/apt/sources.list.d/nodesource.list
apt-get update -q
apt-get install -yq "$DOTNET_PACKAGE" nodejs
dotnet --list-runtimes
node --version

log "6/6 BayiOS directories, env files, systemd units, nginx, sudoers"
install -d -m 0755 -o "$DEPLOY_USER" -g "$DEPLOY_USER" "$BAYIOS_ROOT" "$BAYIOS_ROOT/releases"
# Env files: root-only. systemd reads them as root before dropping privileges, so the service users never can.
install -d -m 0700 -o root -g root /etc/bayios
for name in api web; do
  if [ ! -f "/etc/bayios/${name}.env" ]; then
    install -m 0600 -o root -g root "$here/env/${name}.env.example" "/etc/bayios/${name}.env"
    echo "created /etc/bayios/${name}.env from the example: fill in the values before the first deploy"
  fi
done

install -m 0644 "$here/systemd/bayios-api.service" /etc/systemd/system/bayios-api.service
install -m 0644 "$here/systemd/bayios-web.service" /etc/systemd/system/bayios-web.service
systemctl daemon-reload
systemctl enable bayios-api.service bayios-web.service

sudoers_tmp="$(mktemp)"
sed "s/__DEPLOY_USER__/${DEPLOY_USER}/g" "$here/sudoers/bayios-deploy" > "$sudoers_tmp"
visudo -cf "$sudoers_tmp"
install -m 0440 -o root -g root "$sudoers_tmp" /etc/sudoers.d/bayios-deploy
rm -f "$sudoers_tmp"

sed "s/__DOMAIN__/${DOMAIN}/g" "$here/nginx/bayios.conf" > /etc/nginx/sites-available/bayios
ln -sfn /etc/nginx/sites-available/bayios /etc/nginx/sites-enabled/bayios
rm -f /etc/nginx/sites-enabled/default
nginx -t
systemctl enable --now nginx
systemctl reload nginx

cat <<EOF

Bootstrap finished.
Next steps (infra/linux/README.md):
  4. fill in /etc/bayios/api.env and /etc/bayios/web.env (as root)
  5. point DNS for ${DOMAIN} at this server, then:  certbot --nginx -d ${DOMAIN} --redirect
  6. from your build machine:  DEPLOY_HOST=${DEPLOY_USER}@${DOMAIN} WEB_ENV_FILE=... bash infra/linux/deploy.sh
EOF
