# infra/linux — BayiOS'u tek bir Linux VPS'e kurmak (Nginx + systemd)

> **Durum:** betikler ve unit dosyaları yazıldı ve **Docker içindeki Ubuntu 24.04'te gerçekten çalıştırıldı**
> (aşağıda [Nasıl doğrulandı](#nasıl-doğrulandı)). **Gerçek bir VPS'e henüz kurulmadı**: sunucu kiralanınca
> adımlar aynen uygulanacak. Ana dağıtım yolu hâlâ Fly.io şablonlarıdır ([`infra/deploy`](../deploy/README.md));
> bu klasör "konteyner ve PaaS olmadan, düz Linux üzerinde de çalıştırabiliyorum" alternatifidir.

## Ne kuruluyor

```text
                     İnternet
                        │ 80/443 (ufw yalnızca 22, 80, 443'e izin verir)
                ┌───────▼────────┐
                │     nginx      │  TLS (certbot / Let's Encrypt), gzip, per-IP limit, eksik güvenlik header'ları
                └──┬──────────┬──┘
    /api/*, /health│          │ /*
        ┌──────────▼─────┐ ┌──▼─────────────┐
        │ bayios-api     │ │ bayios-web     │   systemd servisleri, ayrı sistem kullanıcıları,
        │ Kestrel        │ │ Next.js        │   Restart=always, sertleştirilmiş (exposure 1.5 "OK")
        │ 127.0.0.1:5080 │ │ 127.0.0.1:3000 │
        └──────┬─────────┘ └────────────────┘
               │ bayios_app rolü (NOBYPASSRLS), sslmode=require
        ┌──────▼──────────────────────────────┐
        │ Supabase Postgres + Auth            │  yönetilen servis, VPS'te DEĞİL
        └─────────────────────────────────────┘
```

| Dosya | Görev |
|---|---|
| [`bootstrap.sh`](bootstrap.sh) | Boş Ubuntu 24.04'ü bir kez hazırlar (idempotent): paket güncelleme + unattended-upgrades, `deploy` / `bayios-api` / `bayios-web` kullanıcıları, yalnızca anahtarla SSH, ufw, fail2ban, ASP.NET Core 10 runtime, Node.js 24, nginx, certbot, unit'ler, nginx sitesi, sudoers |
| [`systemd/bayios-api.service`](systemd/bayios-api.service) | API: `dotnet BayiOS.Api.dll`, yalnızca `127.0.0.1:5080`, `EnvironmentFile=/etc/bayios/api.env` |
| [`systemd/bayios-web.service`](systemd/bayios-web.service) | Web: `next start`, yalnızca `127.0.0.1:3000`, `EnvironmentFile=/etc/bayios/web.env` |
| [`nginx/bayios.conf`](nginx/bayios.conf) | Reverse proxy: `/api/*` ve `/health*` → Kestrel, `/` → Next.js; gzip, 25 MB upload, `/api` için IP başına 20 istek/sn |
| [`deploy.sh`](deploy.sh) | Derleme makinesinde: `dotnet publish` + `next build` → `rsync` → uzakta `remote-activate.sh` |
| [`remote-activate.sh`](remote-activate.sh) | Sunucuda: `npm ci --omit=dev`, `current` symlink'ini atomik değiştirme, `systemctl restart`, health check, **başarısızsa otomatik geri dönüş**, eski sürümleri budama |
| [`env/*.env.example`](env/) | Yalnızca değişken **adları** (gerçek değer sunucuda `/etc/bayios/*.env`, root:root 0600) |
| [`sudoers/bayios-deploy`](sudoers/bayios-deploy) | `deploy` kullanıcısı yalnızca bu iki servisi yeniden başlatabilir, başka hiçbir şey |
| [`test.sh`](test.sh) | Docker'da Ubuntu 24.04 ile tüm statik kontroller + deploy/rollback davranış testleri |

**Dizin düzeni (sunucu):**

```text
/opt/bayios/releases/20261109114211-3661199e45d1/{api,web}   # her deploy yeni klasör (UTC zaman-git sha)
/opt/bayios/current -> releases/<son sağlıklı sürüm>          # unit'ler hep buradan çalışır
/etc/bayios/api.env, /etc/bayios/web.env                      # sırlar; yalnızca root okur, systemd enjekte eder
/var/lib/bayios-api  (StateDirectory)   /var/cache/bayios-web (CacheDirectory, .next/cache buraya bağlı)
```

## Adım adım kurulum

### 1. VPS kirala
- Ubuntu **24.04 LTS**, x64, en az **2 vCPU / 2 GB RAM** (derleme sunucuda yapılmaz, yalnızca çalıştırılır).
- Örnek: Hetzner Cloud (Almanya, aylık birkaç €; fiyatlar için [hosting-options.md](../../docs/operations/hosting-options.md)).
  Ücretli kaynak açmak ürün sahibinin onayını gerektirir ([AUTOPILOT.md](../../AUTOPILOT.md)).
- Oluştururken kendi SSH açık anahtarını ekle (root için). Parola ile giriş açma.

### 2. SSH anahtarı
```bash
ssh-keygen -t ed25519 -C "bayios-deploy" -f ~/.ssh/bayios_deploy   # deploy kullanıcısı için ayrı anahtar
ssh root@<sunucu-ip> 'cat /etc/os-release | head -2'                 # erişimi dene
```

### 3. Bootstrap (sunucuda bir kez, root)
```bash
scp -r infra/linux root@<sunucu-ip>:/root/bayios-linux
ssh root@<sunucu-ip>
DOMAIN=bayios.example.com DEPLOY_SSH_PUBKEY="$(cat /root/.ssh/authorized_keys | head -1)" \
  bash /root/bayios-linux/bootstrap.sh
```
`DEPLOY_SSH_PUBKEY` için 2. adımdaki `~/.ssh/bayios_deploy.pub` içeriğini de verebilirsin. Anahtar yoksa betik SSH
sertleştirmesini **atlar** (kendini kilitlememek için) ve uyarır. Sonrasında: parola ile giriş kapalı, root yalnızca
anahtarla, ufw açık (22/80/443), fail2ban `sshd` jail'i aktif.
Farklı SSH portu için `SSH_PORT=2222`; Node sürümü için `NODE_MAJOR` (varsayılan 24, çünkü
`apps/web/package.json` → `"engines": { "node": ">=24" }`).

### 4. Ortam değişkenleri (sunucuda, root)
```bash
sudoedit /etc/bayios/api.env   # ConnectionStrings__Default = bayios_app rolü (NOBYPASSRLS), sslmode=require
sudoedit /etc/bayios/web.env   # NEXT_PUBLIC_API_BASE_URL=https://bayios.example.com (aynı origin; nginx /api'yi API'ye yollar)
```
Değişken listesi: [`env/api.env.example`](env/api.env.example), [`env/web.env.example`](env/web.env.example),
anlamları [docs/operations/environments.md](../../docs/operations/environments.md). API **asla** `postgres` /
service-role ile bağlanmaz.

### 5. DNS + TLS
```bash
# DNS: bayios.example.com A kaydı -> <sunucu-ip>  (yayılmasını bekle: dig +short bayios.example.com)
sudo certbot --nginx -d bayios.example.com --redirect -m ops@example.com --agree-tos -n
sudo certbot renew --dry-run          # otomatik yenileme (certbot.timer) çalışıyor mu
```
certbot, `/etc/nginx/sites-available/bayios` dosyasına `listen 443 ssl` + sertifika satırlarını ve http→https
yönlendirmesini kendisi ekler. HSTS'i uygulamalar zaten gönderir (Next.js ve API Production'da).

### 6. Migration + deploy (derleme makinesinde: Linux / WSL / CI)
```bash
# a) Önce şema (sıra değişmez: migration -> API -> web). Yalnızca bayios_migrator rolüyle:
MIGRATOR_URL=... SCRIPTS_DIR=... bash infra/deploy/apply-migrations.sh

# b) Web'in derleme zamanı değerleri (yalnızca NEXT_PUBLIC_*, sır değil; dosya git'e girmez):
cp infra/linux/env/web.env.example infra/linux/env/web.env && $EDITOR infra/linux/env/web.env

# c) Derle, yükle, etkinleştir:
DEPLOY_HOST=deploy@bayios.example.com SSH_OPTS="-i ~/.ssh/bayios_deploy" \
  WEB_ENV_FILE=infra/linux/env/web.env bash infra/linux/deploy.sh
```
`deploy.sh` yeni sürümü `releases/<id>` altına koyar, `current`'ı çevirir, iki servisi yeniden başlatır ve
`/health` (API) + `/` (web) sağlıklı olana kadar 60 sn bekler. Sağlıksızsa **önceki sürüme kendiliğinden döner** ve
hata koduyla çıkar. Son 5 sürüm saklanır (`KEEP_RELEASES`).

```bash
DEPLOY_HOST=deploy@bayios.example.com bash infra/linux/deploy.sh list       # sürümler (* = çalışan)
DEPLOY_HOST=deploy@bayios.example.com bash infra/linux/deploy.sh rollback   # bir önceki sürüme el ile dön
API_URL=https://bayios.example.com EXPECT_ENVIRONMENT=Production bash infra/deploy/smoke.sh   # dışarıdan smoke
```
Not: kod geri alınır, **şema geri alınmaz**; migration'lar geriye uyumlu yazılır
([deploy-and-rollback.md](../../docs/operations/deploy-and-rollback.md)).

### 7. Log izleme
```bash
journalctl -u bayios-api -f                       # API (JSON satırlar)
journalctl -u bayios-web -f                       # Next.js
journalctl -u bayios-api --since "10 min ago" -p warning
journalctl -u bayios-api -o cat -n 200 | grep '"level":"Error"'
systemctl status bayios-api bayios-web
sudo tail -f /var/log/nginx/access.log /var/log/nginx/error.log
sudo fail2ban-client status sshd
```
`deploy` kullanıcısı `systemd-journal` grubundadır; logları sudo olmadan okur.

## Sorun giderme

| Belirti | Bak | Olası neden / çözüm |
|---|---|---|
| Tarayıcıda **502 Bad Gateway** | `systemctl status bayios-api bayios-web` | Servis çalışmıyor ya da henüz deploy yok. `journalctl -u bayios-api -n 100` |
| API sürekli yeniden başlıyor, sonra `failed` | `journalctl -u bayios-api -n 100` | Eksik/yanlış env (ör. `ConnectionStrings__Default`). Düzelt → `sudo systemctl reset-failed bayios-api && sudo systemctl restart bayios-api` (`StartLimitBurst=10`/5 dk) |
| `/health` 200 ama `/health/ready` 503 | `curl -s localhost:5080/health/ready` | Veritabanına ya da Supabase Auth'a erişilemiyor: bağlantı dizesi, `sslmode=require`, Supabase IP izinleri |
| Deploy "unhealthy" deyip geri döndü | Deploy çıktısı + `journalctl -u bayios-api -u bayios-web --since "5 min ago"` | Yeni sürüm açılmadı; eski sürüm çalışmaya devam ediyor. Sorunu düzeltip yeniden deploy et |
| Web açılıyor ama API çağrıları **CORS / CSP** hatası | Tarayıcı konsolu | `NEXT_PUBLIC_API_BASE_URL` ile `Cors__AllowedOrigins__0` aynı `https://` origin olmalı; değişiklik yeniden **derleme** ister (`deploy.sh`) |
| `certbot` başarısız | `dig +short <domain>`, `sudo ufw status` | DNS henüz sunucuyu göstermiyor ya da 80 kapalı |
| `npm ci` deploy sırasında takılıyor | `remote-activate.sh` çıktısı | Sunucunun registry.npmjs.org'a çıkışı yok; giden trafik ufw'de açık olmalı |
| SSH'a giremiyorum | Sağlayıcının web konsolu | `fail2ban-client set sshd unbanip <ip>`; parola girişi bilerek kapalı |
| Upload **413** | nginx error.log | `client_max_body_size 25m` (import sınırı 20 MB) |

## Nasıl doğrulandı

Hepsi 2026-11-09'da Windows + Docker Desktop üzerinde çalıştırıldı; kullanılan konteyner ve imajlar sonra silindi.

**1. `bash infra/linux/test.sh`** — tek komut, `ubuntu:24.04` konteynerinde (`--rm`):

```text
shellcheck 0.9.0, nginx version: nginx/1.24.0 (Ubuntu), systemd 255
PASS  bash -n bootstrap.sh / deploy.sh / remote-activate.sh / test.sh
PASS  shellcheck (all scripts, default severity)
PASS  nginx -t
PASS  visudo -cf
PASS  systemd-analyze verify bayios-api.service      exposure: 1.5 OK :-)
PASS  systemd-analyze verify bayios-web.service      exposure: 1.5 OK :-)
PASS  first deploy / second deploy
PASS  broken release rolled back automatically
PASS  manual rollback
PASS  malformed release id rejected
PASS  pruned to the newest 3 releases
ALL CHECKS PASSED
```
Test kendi hatasını yakalıyor mu: ilk çalıştırmada shellcheck iki uyarı (SC2015, SC2029) ve unit dosyalarının
çalıştırılabilir izni yüzünden **FAIL** verdi; düzeltildikten sonra geçti.

**2. Gerçek bootstrap + gerçek deploy, systemd'li konteynerde** (elle, bir kerelik; `--privileged`, PID 1 = systemd):

- `bootstrap.sh` baştan sona çalıştı (`rc=0`), **ikinci kez** çalıştırıldığında da `rc=0` (idempotent).
  Sonuç: `nginx`, `fail2ban`, `ssh` aktif; `ufw` = deny incoming, yalnızca 22/80/443; `sshd -T` →
  `passwordauthentication no`, `permitrootlogin without-password`; `/etc/bayios` `drwx------ root`;
  `sudo -l -U deploy` yalnızca 4 `systemctl` komutu. ASP.NET Core 10.0.14 Ubuntu arşivinden (`noble-updates`),
  Node v24 NodeSource'tan kuruldu.
- API `dotnet publish` ve web `next build` (bu repodaki commit) ile bir sürüm hazırlanıp `releases/` altına kondu,
  `deploy` kullanıcısı olarak `remote-activate.sh activate <id>` çalıştırıldı:
  `npm ci --omit=dev` → `current -> <id>` → `healthy: api=200 web=200` → `deployed`.
  nginx üzerinden: `/health` 200, `/api/v1/meta` 200 (`"environment":"Production"`), `/giris` 200 (gzip, CSP,
  HSTS, `X-Frame-Options` uygulamadan geliyor), `/health/ready` **503** (beklenen: konteynerde veritabanı yoktu).
  `systemd-analyze security` çalışan servislerde de 1.5 "OK".
- Bozuk bir sürüm (`BayiOS.Api.dll` içeriği bozuldu) deploy edildi: `unhealthy after 10 tries: api=000` →
  `rolling back` → `healthy: api=200` → çıkış kodu 1; servis eski sürümle çalışmaya devam etti.

**Doğrulanamayanlar (dürüst liste):**
- Gerçek bir VPS ve gerçek alan adı yok → `certbot` / TLS ve gerçek SSH girişi (port, fail2ban banı) denenmedi.
- Konteynerde veritabanı yoktu → `/health/ready` ve gerçek iş akışları bu kurulumda denenmedi (onlar
  `e2e-real` / CI'da gerçek Postgres ile test ediliyor).
- `deploy.sh`'in yerel yarısı (ssh + rsync) uçtan uca değil, parça parça denendi: aynı `dotnet publish` /
  `next build` komutları elle çalıştırıldı, sunucu yarısı (`remote-activate.sh`) gerçekten çalıştı.

## Bilinen sınırlar
- **Tek sunucu:** yük dengeleme veya yüksek erişilebilirlik yok; restart sırasında birkaç saniyelik kesinti olur.
- **İstemci IP'si:** API şu an `X-Forwarded-For` başlığını okumuyor (ForwardedHeaders middleware yok), bu yüzden
  nginx arkasında audit IP hash'i `127.0.0.1` için hesaplanır. Rate limit kullanıcı bazlı olduğu için etkilenmez.
  Çözüm (ayrı iş): API'de yalnızca `127.0.0.1` proxy'sine güvenen `UseForwardedHeaders`.
- Veritabanı VPS'te değil, Supabase'de (yönetilen). Yedekleme: [backup-and-restore.md](../../docs/operations/backup-and-restore.md).
- Görev tanımında Node 22 geçiyordu; web uygulaması `node >=24` istediği için varsayılan **24** seçildi.
