# Supabase bootstrap (barındırılan projeler: `bayios-preview`, `bayios-prod`)

> **Durum: hiçbir Supabase projesine UYGULANMADI.** Proje henüz yok (#138). Bu klasör, proje açıldığında ürün sahibinin
> bir kez çalıştıracağı hazır script'leri içerir. CI bunları her PR'da geçici container'larda prova eder
> (`.github/workflows/ci-db-bootstrap.yml`).

Şemanın tek kaynağı EF Core migration'larıdır (ADR 0005). Burada yalnızca migration'ların **ön koşulları** vardır:
DB rolleri, uzantılar, Auth hook'u. İş tablosu, RLS politikası veya GRANT burada **yazılmaz** (onlar migration'larda).

| Dosya | Ne yapar | Kim çalıştırır |
|---|---|---|
| `01-roles.sql` | `bayios_migrator` (iş şemalarının sahibi, yalnızca deploy migration adımı) ve `bayios_app` (API çalışma rolü; `NOBYPASSRLS`, `NOINHERIT`, hiçbir şeyin sahibi değil) rollerini oluşturur; DB `CONNECT`/`CREATE` yetkileri. Var olan rol tehlikeli özellik taşıyorsa **hata verir**, sessizce düzeltmez | `postgres` |
| `02-extensions.sql` | `pg_trgm`'i `extensions` şemasına kurar; `bayios_migrator`'a `extensions` üzerinde `USAGE` (index'te `gin_trgm_ops` için). `bayios_app`'e yetki verilmez | `postgres` |
| `03-access-token-hook.sql` | `bayios_auth_hooks.custom_access_token` — token'a `auth.users.email_confirmed_at`'ten üretilen **üst düzey `email_verified`** claim'i ekler. Supabase Auth yoksa (düz Postgres) atlanır | `postgres` |
| `verify.sql` | Salt-okunur duruş kontrolü: rol özellikleri, `bayios_app` hiçbir şeyin sahibi/üyesi değil, `pg_trgm` var, `anon`/`authenticated`/`PUBLIC` iş şemalarında yetkisiz, tüm iş tablolarında `ENABLE + FORCE RLS` (belgeli istisna: `platform.jobs`, `platform.outbox_messages`), hook yalnızca `supabase_auth_admin` tarafından çağrılabilir | herhangi bir rol (deploy'da `bayios_migrator`) |
| `bootstrap.sh` | Yukarıdakileri sırayla çalıştırır; `--set-passwords` ile parolaları `\password` ile ayarlar | ürün sahibi |
| `test.sh` | Geçici container'larda prova (CI + yerel) | CI |

Hepsi **idempotent**: tekrar çalıştırmak güvenlidir (CI her koşuda iki kez uygular).

## Supabase'e özgü varsayımlar (doğrulandı: `supabase/postgres:17.11.0.006` imajı, 2026-11-07)
- `postgres` rolü **superuser değildir** (`supabase_admin` superuser'dır); `CREATEROLE`, `BYPASSRLS` ve DB sahipliği vardır.
  Bu yüzden `CREATE ROLE ... NOBYPASSRLS`, DB `GRANT`'leri ve `CREATE EXTENSION pg_trgm` (supautils "privileged
  extension") `postgres` ile çalışır. Superuser gerektiren hiçbir adım yoktur.
- `extensions` şemasının sahibi `postgres`'tir; `PUBLIC`'in orada yetkisi yoktur. Uzantı fonksiyonları yalnızca
  `postgres`'e açıktır. API yalnızca `ILIKE` kullanır; planlayıcı trigram GIN index'ini şema/fonksiyon yetkisi olmadan
  kullanır (`test.sh` bunu `bayios_app` ile kanıtlar). `similarity()`/`%` gerekirse ayrı, gözden geçirilmiş GRANT gerekir.
- Supabase **DDL'i loglar** (`log_statement = ddl`): `CREATE/ALTER ROLE ... PASSWORD 'düz-metin'` log'a düşer. Bu yüzden
  SQL dosyaları parola içermez; `bootstrap.sh --set-passwords` psql `\password` kullanır (yalnızca SCRAM doğrulayıcısı gider).
- Supabase'in varsayılan yetkileri (`ALTER DEFAULT PRIVILEGES FOR ROLE postgres IN SCHEMA public ... TO anon, authenticated`)
  yalnızca `postgres`'in `public`/`storage`'da oluşturduklarına uygulanır. İş şemalarını `bayios_migrator` oluşturur; yine de
  her migration `anon`/`authenticated` yetkilerini açıkça geri alır ve `verify.sql` bunu her deploy'da denetler.
- Bağlantı havuzu (Supavisor) özel rolleri kabul eder: kullanıcı adı `<rol>.<project-ref>` (ör. `bayios_migrator.<ref>`),
  rol `LOGIN` ve parolalı olmalı (Supavisor `pgbouncer.get_auth` ile tüm LOGIN rollerini doğrular — imajda kontrol edildi).
- GitHub Actions runner'larında IPv6 yoktur; doğrudan bağlantı (`db.<ref>.supabase.co`) IPv6'dır (IPv4 eklentisi ücretli).
  Bu yüzden CI/deploy migration bağlantısı **Session pooler** (port 5432) kullanır.

## 1. Bootstrap'ı çalıştırma (proje açıldıktan sonra, ortam başına bir kez)
Gerekenler: Docker (yerel `psql` yoksa `postgres:17` imajı kullanılır), proje `postgres` parolası.

1. Dashboard → **Connect** → *Session pooler* URI'sini kopyalayın (kullanıcı `postgres.<ref>`, port 5432), sonuna
   `?sslmode=require` ekleyin. Bu değer **hiçbir yere kaydedilmez** (repo, GitHub, Fly — hiçbiri).
2. İki güçlü parola üretin (URL-güvenli): `openssl rand -base64 32 | tr -d '/+='` — biri `bayios_migrator`, biri `bayios_app` için.
3. Repo kökünden:
   ```bash
   SUPABASE_DB_URL='postgresql://postgres.<ref>:<postgres-parolası>@<pooler-host>:5432/postgres?sslmode=require' \
     bash infra/supabase/bootstrap/bootstrap.sh --set-passwords
   ```
   Çıktıda `verify: OK` görünmeli; ardından iki rol için parola sorulur.
4. Komut geçmişinden URL'yi silin (ör. PowerShell/Git Bash history).

## 2. Bağlantı dizeleri (değerler yalnızca sağlayıcı sırlarında)
| Ad | Nerede | Biçim |
|---|---|---|
| `PREVIEW_MIGRATOR_CONNECTION` / `PRODUCTION_MIGRATOR_CONNECTION` | GitHub Environment `preview` / `production` secret | `postgresql://bayios_migrator.<ref>:<parola>@<pooler-host>:5432/postgres?sslmode=require` (Session pooler; `sslmode=require` yoksa deploy adımı reddeder) |
| `ConnectionStrings__Default` | Fly uygulama secret'ı (API) | Npgsql: `Host=db.<ref>.supabase.co;Port=5432;Database=postgres;Username=bayios_app;Password=<parola>;SSL Mode=Require` (Fly IPv6 çıkışı destekler). Alternatif: Session pooler, `Username=bayios_app.<ref>` |

- API **asla** `postgres`, `service_role` veya `bayios_migrator` ile bağlanmaz; `bayios_migrator` parolası API'ye verilmez.
- Transaction pooler (6543) API için kullanılmaz (oturum/prepared statement varsayımları API ekibince doğrulanmadan).
- Parola değişimi: `bootstrap.sh --set-passwords` (yalnızca `\password`), sonra ilgili secret'ı güncelleyin.

## 3. Auth ayarları kontrol listesi (Dashboard → Authentication) — ortam başına
- [ ] **Confirm email: AÇIK** (AF-01: doğrulanmamış e-posta ile tenant oluşturulmaz).
- [ ] **Secure email change: AÇIK** (davet kabulü buna dayanır: `auth.users.email` yalnızca yeni adres doğrulanınca değişir).
- [ ] **Secure password change: AÇIK** (güvenlik denetimi 2026-11 L-3: çalınan bir oturum, yakın zamanda giriş/yeniden
      kimlik doğrulama olmadan parolayı değiştiremez). Yerel yığında da açık (`infra/supabase/local/supabase/config.toml`).
- [ ] **Minimum password length ≥ 8**; **Password requirements: `letters_digits`** (harf + rakam; web 8+ harf+rakam ister;
      RC-1 R-13, denetim L-3). Yerel yığında da aynı.
- [ ] Anonymous sign-ins: KAPALI · Manual linking: KAPALI · Signups: AÇIK (self-servis kayıt). API anonim oturumları
      (`is_anonymous: true`) bu ayardan bağımsız olarak her uçta 401 ile reddeder (denetim L-2).
- [ ] **CAPTCHA (Cloudflare Turnstile)** — kayıt/giriş/parola sıfırlama kötüye kullanımına karşı önerilir (denetim L-3,
      tehdit modeli §6.5; OD-05 kararı: hız limitleri + CAPTCHA, özel kilit yok). **Web entegrasyonu hazır (#61):**
      `NEXT_PUBLIC_TURNSTILE_SITE_KEY` dolu olan web derlemesi `captchaToken` gönderir ve CSP'ye yalnızca
      `https://challenges.cloudflare.com` eklenir. Sıra: önce Vercel'e site anahtarı + yeniden deploy, sonra burada gizli
      anahtar — tersi **kimsenin giriş yapamaması** demektir. **Mobil uygulama henüz token göndermez:** burada açılınca mobil
      giriş reddedilir. Adımlar ve mobil notu: `docs/operations/environments.md` §2.1. Gizli anahtar yalnızca Supabase
      Dashboard'da durur.
- [ ] Rate Limits: e-posta gönderimi (`Email sent`) ve doğrulama/OTP sınırları Supabase varsayılanlarında veya daha sıkı;
      onay/sıfırlama e-postaları arası en az 60 sn (yerel yığındaki `max_frequency = "1s"` yalnızca E2E hızı içindir).
- [ ] URL Configuration: **Site URL** = ortamın web adresi; **Redirect URLs** yalnızca o ortamın
      `https://<web-host>/auth/callback` adresleri (preview için Vercel preview alias'ı; geniş joker kullanmayın).
- [ ] **JWT signing keys: asimetrik (ES256)**. Settings → JWT Keys → legacy secret'ı geçirin, ES256 standby anahtar
      oluşturup "rotate" ile güncel yapın. API yalnızca JWKS ile ES256/RS256 kabul eder (HS256 reddedilir, ADR 0003):
      `Auth__Authority=https://<ref>.supabase.co/auth/v1`, `Auth__Audience=authenticated`
      (JWKS varsayılanı `{Authority}/.well-known/jwks.json`). Legacy HS256 secret'ı iptal etmeden önce istemcilerin
      publishable key kullandığını doğrulayın.
- [ ] Data API → Exposed schemas: yalnızca varsayılanlar (`public`, `graphql_public`). **İş şemalarını asla eklemeyin.**
- [ ] E-posta: Supabase'in yerleşik SMTP'si yalnızca düşük hacimli/ekip içi gönderim içindir; gerçek kullanıcılar için
      özel SMTP gerekir (sağlayıcı/maliyet kararı — ürün sahibi).
- [ ] **Access-token hook** (bootstrap 3. adımı kurduktan sonra):
  1. Authentication → Hooks → *Customize Access Token (JWT) Claims* → Postgres function →
     şema `bayios_auth_hooks`, fonksiyon `custom_access_token` → Enable.
  2. Doğrulama: e-postası doğrulanmış bir test kullanıcısıyla giriş yapın, access token'ı çözün
     (token'ı üçüncü taraf sitelere yapıştırmadan, tarayıcı konsolunda
     `JSON.parse(atob(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')))`): **üst düzeyde**
     `"email_verified": true` olmalı.
  3. Ancak bundan sonra API'de `Invitations__TrustUserMetadataEmailVerified=false` (Fly secret/env). Hook açık değilken
     `false` yapılırsa davet kabulü kapalı-güvenli olarak reddedilir; hook açıkken `true` bırakmak kullanıcı tarafından
     yazılabilir `user_metadata.email_verified`'a güvenmek demektir.

## 4. Yerel prova
```bash
bash infra/ci/ef-migrations.sh                       # OUT_DIR'e script'ler + manifest.txt
SCRIPTS_DIR=<OUT_DIR> bash infra/supabase/bootstrap/test.sh postgres
SCRIPTS_DIR=<OUT_DIR> bash infra/supabase/bootstrap/test.sh supabase
```
Container'lar özel bir Docker ağında çalışır; sabit host portu açılmaz (çalışan yerel yığınlara dokunmaz).
