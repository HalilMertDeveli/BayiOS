# BayiOS on a self-hosted Linux VPS (Ubuntu 24.04)

Self-hosted alternative to the Fly.io + Vercel path in `docs/operations/deploy-and-rollback.md` (#194): one small
Ubuntu 24.04 VPS runs the API and the web app behind Nginx; the database stays on managed Supabase Postgres.

> **Status: nothing has been provisioned.** No VPS has been rented and no DNS record exists. Renting a server, buying
> a domain, changing DNS and any production deploy need the product owner's explicit approval (`AUTOPILOT.md`).
> Everything here was verified only in local containers (see [Local test harness](#local-test-harness)).

## Architecture

```
                 ┌───────────────────────────── VPS (Ubuntu 24.04) ─────────────────────────────┐
 browser ──443──►│ Nginx  app.example.com ──► 127.0.0.1:3200  bayios-web  (Next.js, Node 24)    │
 mobile  ──443──►│        api.example.com ──► 127.0.0.1:5280  bayios-api  (ASP.NET Core, .NET 10)│──TLS──► Supabase
 ACME    ───80──►│        :80 → 301 https, /.well-known/acme-challenge/                         │        (Postgres, Auth)
                 └───────────────────────────────────────────────────────────────────────────────┘
```

- Two server names (placeholders `app.example.com`, `api.example.com`). The browser calls the API cross-origin, exactly
  like the Fly/Vercel setup: `NEXT_PUBLIC_API_BASE_URL=https://api.example.com`, `Cors__AllowedOrigins__0=https://app.example.com`.
- Both services listen on loopback only; only Nginx (80/443) and SSH are reachable from outside.
- The VPS is **stateless**: data lives in Supabase. A local Postgres on the VPS is out of scope.
- Migrations are **not** run on the VPS. They run before a deploy with `infra/deploy/apply-migrations.sh` as
  `bayios_migrator` (scripts from `infra/ci/ef-migrations.sh`); the API only ever gets the `bayios_app` connection.

## Files

| Path | Purpose |
|---|---|
| `scripts/provision.sh` | Idempotent server setup: packages, runtimes, users, directories, units, Nginx, TLS, ufw, SSH hardening |
| `scripts/build-release.sh` | Builds `bayios-<version>.tar.gz` (API publish + web build) on Linux x64 (CI or a build container) |
| `scripts/deploy.sh` | `bayios-deploy`: unpack → atomic symlink switch → restart → health check → automatic rollback |
| `scripts/rollback.sh` | `bayios-rollback [version]`: switch back to a release still on disk |
| `scripts/healthcheck.sh` | `bayios-healthcheck [--ready] [--via-nginx]` |
| `scripts/lib.sh` | Shared helpers (sourced) |
| `systemd/bayios-api.service`, `systemd/bayios-web.service` | Hardened units (user `bayios`, read-only system, loopback) |
| `nginx/bayios.conf`, `nginx/snippets/*.conf` | TLS site: redirects, ACME, proxy headers, WebSocket upgrade, body limits, gzip |
| `nginx/bayios-bootstrap.conf` | HTTP-only site used until the first certificate exists |
| `env/api.env.example`, `env/web.env.example` | Variable names for `/etc/bayios/*.env` (no values) |
| `test/run.sh` | End-to-end harness without a VPS (systemd container, real release, deploy/rollback) |
| `test/static-checks.sh` | nginx -t + systemd-analyze, used by `.github/workflows/ci-linux.yml` |

## Server requirements

- x86_64 VPS, Ubuntu 24.04 LTS, 2 vCPU / 2–4 GB RAM / 20 GB+ disk (each release is ~500 MB unpacked; up to 5 are kept).
  The unit memory caps (API `MemoryMax=768M`, web `512M`) fit a 2 GB machine; raise them on larger ones.
- DNS `A` (and `AAAA` if the VPS has IPv6) records for both names pointing at the server **before** the first certbot run.
- Outbound HTTPS to Supabase, `deb.nodesource.com`, Ubuntu mirrors, Let's Encrypt.

## 1. Server preparation

1. Create the VPS with **your SSH public key** (no password login). Log in as the provider's initial user/root.
2. Copy this directory to the server (or `git clone` the public repo) — e.g. `scp -r infra/linux root@<ip>:/root/infra-linux`.
3. Run provisioning (all variables optional except the domains; values below are placeholders):

   ```bash
   sudo BAYIOS_APP_DOMAIN=app.example.com BAYIOS_API_DOMAIN=api.example.com \
        BAYIOS_CERTBOT_EMAIL=ops@example.com \
        BAYIOS_DEPLOY_USER=deploy BAYIOS_DEPLOY_PUBKEY="ssh-ed25519 AAAA... ci-or-laptop" \
        BAYIOS_HARDEN_SSH=1 \
        bash /root/infra-linux/scripts/provision.sh
   ```

4. **Before closing your session**, open a second terminal and check `ssh deploy@<ip>` works. With
   `BAYIOS_HARDEN_SSH=1`, root login and password login are off (`/etc/ssh/sshd_config.d/00-bayios.conf`, named `00-`
   so it wins over cloud-init's `50-cloud-init.conf`). The script refuses to harden when the deploy user has no key.

What `provision.sh` does (safe to re-run; it never overwrites `/etc/bayios/*.env`):

| Area | Result |
|---|---|
| Packages | `nginx ufw unattended-upgrades certbot python3-certbot-nginx logrotate curl gnupg openssl` |
| .NET | `aspnetcore-runtime-10.0` from the **Ubuntu archive** (`noble-updates`/`noble-security`, so unattended-upgrades patches it) |
| Node | `nodejs` 24.x from the NodeSource apt repo (key fingerprint checked, apt-pinned over Ubuntu's Node 18) |
| Updates | `20auto-upgrades` on; automatic reboot off (reboot in a planned window when `/var/run/reboot-required` exists) |
| Users | system user `bayios` (no shell, no home) runs both services; optional `deploy` user (SSH key, member of `systemd-journal`/`adm` to read logs) with sudo for **only** `bayios-deploy` and `bayios-rollback` |
| Layout | see below; tooling in `/opt/bayios/bin`, symlinked as `/usr/local/sbin/bayios-{deploy,rollback,healthcheck}` |
| systemd | units installed + enabled (they start only once a release exists: `ConditionPathExists`), journald capped at 1 GB / 30 days |
| Nginx | snippets + site, default site removed, unknown hosts dropped (444 / TLS handshake rejected) |
| TLS | certificate `bayios` via certbot (see [TLS](#4-tls-lets-encrypt)) |
| Firewall | ufw: deny incoming; allow `OpenSSH` (rate-limited), `80/tcp`, `443/tcp` |

## 2. Directory layout

```
/opt/bayios/
  bin/                       deploy tooling (root, 0755)
  releases/<version>/        immutable releases (root-owned, read-only for bayios)
    RELEASE                  version, git commit, build time, baked API URL
    api/BayiOS.Api.dll …     dotnet publish output
    web/.next  web/node_modules  web/package.json  web/next.config.ts
    web/.next/cache -> /var/cache/bayios-web
  current  -> releases/<version>     what systemd runs (switched atomically)
  previous -> releases/<version>     last good release (target of `bayios-rollback`)
  deploy.log                 one line per deploy/rollback
/etc/bayios/api.env          root:bayios 0640  API secrets/config
/etc/bayios/web.env          root:bayios 0640  web runtime config
/etc/bayios/deploy.conf      domains for the tooling (no secrets)
/var/lib/bayios-api/         API state dir (HOME; ASP.NET Data Protection keys), 0700 bayios
/var/cache/bayios-web/       Next.js runtime cache, emptied on every deploy/rollback
```

## 3. Configuration

Fill in `/etc/bayios/api.env` and `/etc/bayios/web.env` on the server (`sudoedit`). Values come from the password
manager / Supabase dashboard and are **never** committed, pasted into issues or echoed in CI logs. Names and meanings:
`env/*.env.example` and the root `.env.example`.

- `ConnectionStrings__Default`: **only** `bayios_app` (NOBYPASSRLS), TLS (`SSL Mode=Require`). Never `postgres`,
  `service_role` or the migrator.
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (template default): the API trusts `X-Forwarded-For/-Proto`. Safe because
  Kestrel listens on 127.0.0.1 and Nginx **overwrites** `X-Forwarded-For` with the real peer address. If you ever put
  a CDN/proxy (e.g. Cloudflare) in front, restore the client IP in Nginx first (`real_ip_header` + the CDN's ranges).
- `ASPNETCORE_URLS`/`ASPNETCORE_ENVIRONMENT` default in the unit (`http://127.0.0.1:5280`, `Production`).
- `NEXT_PUBLIC_*` are **baked in at build time** (bundle and the CSP in `next.config.ts`); `web.env` repeats them for
  server-side code. Changing them means building and deploying a new release.

After editing: `sudo systemctl restart bayios-api bayios-web && bayios-healthcheck --ready`.

## 4. TLS (Let's Encrypt)

- First run: with no certificate yet, `provision.sh` serves `nginx/bayios-bootstrap.conf` (HTTP only, ACME path,
  everything else 503). With `BAYIOS_CERTBOT_EMAIL` set it then runs
  `certbot certonly --nginx --cert-name bayios -d <app> -d <api>` and switches to the full `bayios.conf`.
  `certonly` keeps certbot from editing our Nginx files; the certificate lives in `/etc/letsencrypt/live/bayios/`.
  Without the e-mail it stops at the bootstrap site: point DNS, then re-run provisioning with the e-mail set.
- Renewal: the `certbot.timer` systemd timer (twice a day); `/etc/letsencrypt/renewal-hooks/deploy/bayios-reload-nginx.sh`
  reloads Nginx after a renewal. Check with `systemctl list-timers certbot.timer` and `sudo certbot renew --dry-run`.
- Protocols TLS 1.2/1.3 (Mozilla intermediate), HTTP/2, no OCSP stapling (Let's Encrypt ended OCSP in 2025).
- HSTS and all other security headers come from the apps (web: CSP, HSTS, X-Frame-Options, Referrer-Policy,
  Permissions-Policy, COOP; API: nosniff, HSTS, `Cache-Control: no-store`). Nginx adds none, so nothing is duplicated
  or contradicts the app CSP.

## 5. Build a release

On Linux x64 with .NET 10 SDK + Node 24 (CI runner or `infra/linux/test/builder.Dockerfile`):

```bash
NEXT_PUBLIC_API_BASE_URL=https://api.example.com \
NEXT_PUBLIC_SUPABASE_URL=https://<project-ref>.supabase.co \
NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY=<publishable key> \
  infra/linux/scripts/build-release.sh v0.3.0 dist/
# -> dist/bayios-v0.3.0.tar.gz (+ .sha256), ~115 MB
```

The script copies the sources to a temp dir (the checkout is not modified), runs `dotnet publish -c Release
-p:UseAppHost=false`, `npm ci && npm run build`, prunes dev dependencies and drops `.next/cache`. A release is tied
to one environment because of the baked `NEXT_PUBLIC_*` values. The web build needs outbound HTTPS to
`fonts.googleapis.com` (`next/font/google` downloads and self-hosts the fonts at build time; a failed fetch fails the build).

## 6. Deploy

Order is fixed (deploy-and-rollback.md §5): **migrations → API → web**. The kit restarts API and web together.

```bash
# 1. migrations (from CI or an operator machine with the bayios_migrator connection — never from the VPS env files)
# 2. ship and deploy the release
scp dist/bayios-v0.3.0.tar.gz* deploy@<ip>:artifacts/
ssh deploy@<ip> sudo bayios-deploy artifacts/bayios-v0.3.0.tar.gz v0.3.0
```

`bayios-deploy <tarball|dir> <version>`:

1. Preflight: ASP.NET Core 10 runtime, Node ≥ 24, env files, ≥ 1 GiB free, `.sha256` verified when present;
   refuses a version that already exists (a `.failed` one may be replaced). One deploy/rollback at a time (`flock`).
2. Unpacks into `releases/<version>` (root-owned, `go=rX`), links `.next/cache` to the cache dir.
3. Atomically repoints `current` (`rename(2)`), clears the web cache, restarts both units.
4. Health check for up to `BAYIOS_HEALTH_TIMEOUT` (90 s): both units active, `GET /health` = 200, web `/` = 2xx/3xx.
   `BAYIOS_REQUIRE_READY=1` (environment or `/etc/bayios/deploy.conf`) also requires `GET /health/ready` = 200 —
   recommended once the database is configured.
5. Healthy → `previous` = old release, keeps the newest 5 releases. Unhealthy → release marked `.failed`, `current`
   switched back, services restarted and re-checked.

Exit codes: `0` deployed · `1` refused, nothing changed · `3` failed and rolled back (previous release healthy) ·
`4` failed and the rollback is unhealthy too / first deploy failed (services stopped). Every action is appended to
`/opt/bayios/deploy.log`.

## 7. Rollback

```bash
sudo bayios-rollback              # to /opt/bayios/previous (running it twice toggles)
sudo bayios-rollback v0.2.1       # to any release still in /opt/bayios/releases
sudo bayios-rollback v0.3.0 --force   # even if it is marked .failed
```

Health-checked like a deploy; if the target is unhealthy it switches back. The database is never rolled back
(expand → migrate → contract keeps the previous API compatible). Afterwards: incident note + root-cause issue
(`docs/operations/runbooks/rollback.md`).

## 8. Health checks and monitoring

| Check | Meaning |
|---|---|
| `GET https://api.example.com/health` | Liveness, no dependencies (200 `{"status":"ok"}`) — use for external uptime monitoring |
| `GET https://api.example.com/health/ready` | Readiness: DB, configuration, job queue lag. 200 `ready`/`degraded`, 503 `unavailable` (also 503 when unconfigured: fail-closed) |
| `GET https://app.example.com/` | Web renders |
| `bayios-healthcheck [--ready] [--via-nginx]` | All of the above from the server (loopback and through Nginx/TLS); exit 0/1, cron-friendly |

## 9. Logs

| What | Where |
|---|---|
| API (structured JSON) | `journalctl -u bayios-api -f` (`-o cat` for raw JSON) |
| Web | `journalctl -u bayios-web -f` |
| Nginx | `/var/log/nginx/bayios-{app,api}.{access,error}.log` (+ `access.log`/`error.log` for dropped hosts) |
| Deploys | `/opt/bayios/deploy.log` |
| certbot | `/var/log/letsencrypt/letsencrypt.log` |
| unattended-upgrades | `/var/log/unattended-upgrades/` |

Rotation: journald is capped (1 GB, 30 days, `/etc/systemd/journald.conf.d/50-bayios.conf`); Nginx logs rotate daily
and are kept 14 days compressed (Ubuntu's `/etc/logrotate.d/nginx`, which covers `/var/log/nginx/*.log`). Access logs
hold client IPs (personal data under KVKK/GDPR): keep the retention short; `/health` is not logged.

## 10. Maintenance

- OS security updates install automatically; reboot manually when `/var/run/reboot-required` exists.
- .NET runtime updates arrive through `noble-security`; if `needrestart` (present on Ubuntu cloud images) did not restart the API,
  `sudo systemctl restart bayios-api`.
- Node: `sudo apt-get install --only-upgrade nodejs && sudo systemctl restart bayios-web` (24.x line only; a new major
  means editing `NODE_MAJOR` in `provision.sh` and re-running it).
- Backups: the VPS holds no business data. Keep `/etc/bayios/*.env` values in the password manager; DB backups are
  Supabase's (`docs/operations/backup-and-restore.md`). A rebuilt VPS = provision + env files + deploy.

## 11. Troubleshooting

| Symptom | Check / fix |
|---|---|
| `bayios-deploy` exit 3 | New release unhealthy, previous one serving. `journalctl -u bayios-api -u bayios-web -n 200`; fix, deploy a new version |
| API `Failed to create CoreCLR, HRESULT: 0x8007054F` | A syscall needed by CoreCLR is filtered. Do not add `~@resources` (or `MemoryDenyWriteExecute`) to the API unit |
| API exits at start with `OptionsValidationException` | A required setting in `/etc/bayios/api.env` is missing or invalid (e.g. `Audit__IpHashKey` shorter than 32 bytes) |
| `/health` 200 but `/health/ready` 503 | DB unreachable / wrong `ConnectionStrings__Default` / Supabase paused; see the `health` log lines in the API journal |
| Web 500, log says `NEXT_PUBLIC_SUPABASE_URL … must be configured` | The release was built without the `NEXT_PUBLIC_*` values: rebuild with them |
| Browser CORS errors | `Cors__AllowedOrigins__0` must equal the web origin exactly (`https://app.example.com`, no trailing slash) |
| Unit `start-limit-hit` | Crash loop; `systemctl reset-failed bayios-api` after fixing (deploy/rollback do this themselves) |
| 502 from Nginx | Upstream down: `systemctl status bayios-api bayios-web`, `ss -ltn` should show 127.0.0.1:5280 / :3200 |
| 413 Request Entity Too Large | Import > 25 MB (API limit is 20 MB) or form post > 2 MB on the web host |
| certbot fails | DNS not pointing here yet, port 80 blocked, or rate limit; `certbot certificates`, `/var/log/letsencrypt/` |
| Locked out of SSH | Provider's web console; remove `/etc/ssh/sshd_config.d/00-bayios.conf` or fix `authorized_keys` |
| `release … already exists` | Versions are immutable: use a new version, or `bayios-rollback <version>` to switch to it |

## Local test harness

Proves the kit end to end without a VPS (Docker with Linux containers; Docker Desktop works):

```bash
BAYIOS_LINUX_TEST_WORK=/path/with/space bash infra/linux/test/run.sh   # REUSE_RELEASE=1, KEEP=1 optional
```

It runs shellcheck, builds a systemd-as-PID-1 Ubuntu 24.04 image and a .NET 10 + Node 24 builder image, builds a
**real** release with `build-release.sh`, runs `provision.sh` twice in test mode (`BAYIOS_TEST_MODE=1`: self-signed
certificate, no certbot issuance, ufw rules written but not enabled), deploys as the `deploy` user via sudo, checks
API `/health`, `/health/ready` (503 without a DB) and the web home page **through Nginx + TLS**, redirects and dropped
unknown hosts, deploys a second release, then a deliberately broken one (corrupted `BayiOS.Api.dll`) and asserts the
automatic rollback, exercises `rollback.sh` with and without a version, kills the API to prove systemd restarts it, and
prints `systemd-analyze verify`/`security` (API exposure 1.2, web 1.1 — "OK"). Containers/images are named
`bayios-linux-test-*`. Not covered locally: real Let's Encrypt issuance/renewal, an enabled ufw, real DNS/SSH from
outside, a real database.

CI (`.github/workflows/ci-linux.yml`, on changes under `infra/linux/**`): LF check, shellcheck, `nginx -t` for both
sites and `systemd-analyze verify` + `security --offline` (fails above exposure 2.0) in an Ubuntu 24.04 container.
