# BayiOS — Deploy ve Rollback

| Alan | Değer |
|---|---|
| Sahip | DevOps Engineer |
| Durum | v0.2 — deploy hattı **hazır** (`deploy-preview.yml`, `deploy-production.yml` iskeleti, #70) ve Supabase bootstrap'ı hazır (#76); **hiçbir ortam deploy edilmedi**, hesaplar ürün sahibini bekliyor (#138) |
| İlgili | `environments.md` (ortamlar, sırlar, migration terfisi), `ci-cd.md` (workflow'lar), `hosting-options.md`, `infra/deploy/`, `infra/supabase/bootstrap/` |
| Son güncelleme | 2026-11-07 |

## 1. Preview ve production ayrımı

| | Preview | Production |
|---|---|---|
| Tetik | `preview` dalına merge (API), her PR (web) | `main`'den `vX.Y.Z` etiketi + GitHub Environment `production` reviewer onayı (ürün sahibi) |
| API | Fly.io `bayios-api-preview` (`fra`, auto-stop, 0–1 makine) — şablon: `infra/deploy/fly.preview.toml` | Ayrı Fly uygulaması `bayios-api-prod` (`fra`, **min 1 makine always-on**) — şablon onaydan sonra yazılır |
| Web | Vercel Preview deployment'ları (PR başına URL) + `preview` dalı alias'ı | Vercel Production |
| DB | Supabase `bayios-preview` (free, yalnızca sentetik veri) | Supabase `bayios-prod` (Pro önerilir) |
| Sırlar | GitHub Environment `preview`, Fly preview app secrets, Vercel "Preview" env | GitHub Environment `production`, Fly prod app secrets, Vercel "Production" env |
| Paylaşım | Hiçbir sır, token, DB veya Fly uygulaması iki ortam arasında paylaşılmaz | |

Kurallar: preview'a production verisi gelmez; fork PR'larında deploy ve sır yok; production adımları yalnızca ürün sahibi onayıyla.

## 2. İmaj ve doğrulama (bugün çalışan kısım)
- `services/api/Dockerfile`: SDK ile derleme → `aspnet:10.0-noble-chiseled-extra` runtime (kabuk yok, non-root UID 1654,
  ICU + tzdata dahil — `tr-TR` kültürü ve `Europe/Istanbul` için). Port 8080. Taban imajlar digest ile sabit.
- İmajda yapılandırma/sır yok; `appsettings.Development.json` imaja girmez. `ASPNETCORE_ENVIRONMENT` varsayılanı `Production`;
  host `Preview`/`Production` olarak ayarlar.
- **HEALTHCHECK yok** (chiseled imajda kabuk/curl yok). Liveness dışarıdan `GET /health` ile ölçülür: Fly `[[http_service.checks]]`,
  CI smoke adımı. DB readiness kontrolü API ekibinin işidir (#10); eklendiğinde host kontrolü ona bağlanır.
- `ci-container.yml` (job: **`api container build, smoke & scan`**): imajı derler, `--read-only --cap-drop ALL` ile çalıştırır,
  `/health` = 200 bekler, root olmadığını doğrular, Trivy ile tarar (HIGH+CRITICAL raporlanır; düzeltmesi olan CRITICAL'da kırmızı).
  Registry'ye push ve deploy **yapmaz**.

## 3. API deploy ve rollback (Fly.io)

**Deploy (`.github/workflows/deploy-preview.yml`; push `preview` veya elle `workflow_dispatch`):**

| Job | Ortam | Ne yapar |
|---|---|---|
| `deploy prepare (image, migration scripts, local smoke)` | — (sır yok) | `infra/ci/ef-migrations.sh` (idempotent script'ler + `manifest.txt`, boş Postgres 17'de iki kez prova) → `docker build` → imaj yerelde `--read-only --cap-drop ALL` ile çalıştırılıp `infra/deploy/smoke.sh` (`/health` 200, yapılandırmasız `/health/ready` **503** = kapalı-güvenli, ortam `Production`) |
| `migrate preview database (bayios_migrator)` | `preview` | `infra/deploy/preflight.sh` → `infra/deploy/apply-migrations.sh` (`PREVIEW_MIGRATOR_CONNECTION`) → `verify.sql` duruş kontrolü |
| `deploy preview API (Fly.io)` | `preview` | imaj `registry.fly.io/<FLY_APP>:<git-sha>` (değişmez, `latest` yok) → `flyctl deploy --config infra/deploy/fly.preview.toml --image …` (rolling) → `smoke.sh` (`/health`, `/health/ready` 200, `/api/v1/meta` = `Preview`) |
| `deploy preview web (Vercel)` | `preview` | `VERCEL_DEPLOY_HOOK_URL` varsa hook'u tetikler; yoksa not düşer (Vercel Git entegrasyonu kendisi deploy eder) |

Davranış:
- **Sır yok** (bugünkü durum): `migrate` job'ı `skipped: secrets missing (PREVIEW_MIGRATOR_CONNECTION FLY_API_TOKEN)` notu
  ve özet tablosuyla **yeşil** biter; push, migration, deploy **olmaz**; sonraki job'lar atlanır.
- **Yarım yapılandırma** (ör. `FLY_API_TOKEN` var, migrator yok): **kırmızı**, hiçbir şey deploy edilmez (migration'sız API yok).
- **PR'lar** (deploy dosyaları değişince): yalnızca `prepare` (dry run) — ortam ve deploy sırrı **okunmaz**; aynı preflight
  kodu sırsız çalıştırılıp "skipped: secrets missing" raporu gösterilir.
- Migration adımı reddeder: `bayios_migrator` dışı kullanıcı (`postgres`, `service_role` …), `sslmode=require` olmayan URL,
  eksik `manifest.txt`; herhangi bir SQL hatası veya `verify.sql` ihlali (ör. RLS'siz tablo, `anon` yetkisi) → API'ye dokunulmaz.
- Eşzamanlılık: gerçek deploy'lar `deploy-preview` grubunda sıraya girer, **iptal edilmez** (yarım migration olmaz).
- Fly yeni makineyi `/health` geçene kadar trafiğe almaz; geçmezse deploy başarısız olur, eski sürüm çalışmaya devam eder.

**Production (`deploy-production.yml`) — iskelet, kapalı:** `vX.Y.Z` etiketi push'u veya `workflow_dispatch` (girdi: etiket).
`production guard` job'ı repo değişkeni `PRODUCTION_DEPLOY_ENABLED` `true` değilse "skipped: production deploy disabled" ile
yeşil biter. `true` ise kapalı-güvenli kontroller: etiket biçimi, etiket commit'i `main` üzerinde, GitHub Environment
`production` **var ve required reviewer kuralı içeriyor**, `infra/deploy/fly.production.toml` var. Sonra tek
`production release (migrate -> API -> web)` job'ı (`production` ortamı → **tek onay**): katı preflight (her sır/değişken
zorunlu) → migration → imaj `registry.fly.io/<FLY_APP>:<vX.Y.Z>-<sha12>` → `flyctl deploy` → smoke (`Production`) → web hook.

**Rollback:**
1. `fly releases --app <app> --image` ile önceki sağlıklı release'in imaj referansını bul.
2. `fly deploy --config infra/deploy/fly.preview.toml --image registry.fly.io/<app>:<önceki-git-sha>`
   (production'da: `deploy-production.yml`'i önceki etiketle `workflow_dispatch`, `production` environment onayıyla).
3. DB geri alınmaz: expand → migrate → contract kuralı sayesinde önceki API sürümü güncel şemayla çalışır (§5).
4. Rollback sonrası incident notu + kök neden issue'su.

Production farkları: `min_machines_running >= 1` (worker + SignalR), 2 makine önerilir, deploy yalnızca etiketten.

## 4. Web deploy ve rollback (Vercel — onaydan sonra)

**Preview kurulumu (ürün sahibi yapar; repoya dosya eklenmez):**
| Ayar | Değer |
|---|---|
| Git entegrasyonu | `HalilMertDeveli/BayiOS` |
| Root Directory | `apps/web` |
| Framework | Next.js (otomatik) — build/install komutları varsayılan (`npm ci`, `next build`) |
| Production Branch | `main` |
| Env (Preview) | `NEXT_PUBLIC_API_BASE_URL` = preview API URL'si (ör. `https://bayios-api-preview.fly.dev`) |
| Env (Production) | `NEXT_PUBLIC_API_BASE_URL` = prod API URL'si (prod onayından sonra) |
| Deployment Protection | Vercel Authentication açık (preview URL'leri herkese açık olmaz) |
| Fork PR'ları | Otomatik deploy kapalı / onay gerekli |

`NEXT_PUBLIC_*` değerleri build zamanında pakete gömülür ve **herkese açıktır** — buraya sır konmaz. Değer değişince yeniden
deploy gerekir. API tarafında CORS'a preview web origin'i eklenmelidir (API ekibi). PR'da doğrulama: Vercel bot yorumundaki
preview URL'si açılır, sayfanın API'ye `/api/v1/meta` çağrısının başarılı olduğu kontrol edilir (#10 kabul kriteri).

**Rollback:** Vercel Dashboard → Deployments → önceki production deployment → "Instant Rollback" (yeniden derleme yok, anlık).
Instant Rollback sonrası otomatik production atamaları durur; düzeltme merge edilince "Promote" ile tekrar açılır.

**Web sıralaması — bilinen sınır:** Vercel Git entegrasyonu `preview` dalını API deploy'unu beklemeden kendisi derler.
Sözleşme kuralı (önce API PR'ı, sonra web; API geriye uyumlu) bu riski karşılar. Sırayı kesinleştirmek için ürün sahibi
Vercel'de `preview` (ve production için `main`) dalının otomatik deploy'unu kapatıp aynı dal için bir Deploy Hook
oluşturur ve `VERCEL_DEPLOY_HOOK_URL` sırrını ilgili GitHub Environment'a ekler; o zaman web yalnızca API smoke'u geçtikten
sonra deploy olur. (Dal bazlı kapatma `apps/web/vercel.json` `git.deploymentEnabled` ile de yapılabilir — web ekibi işi.)

## 5. Migration sırası (değişmez kural)
- Migration'ları **yalnızca** CI/deploy pipeline'ındaki ayrı migration adımı (`infra/deploy/apply-migrations.sh`), `bayios_migrator` rolüyle çalıştırır (deploy'da `PREVIEW_MIGRATOR_CONNECTION` / `PRODUCTION_MIGRATOR_CONNECTION`; yerelde `ConnectionStrings__Migrator`).
- API **asla** başlangıçta migration çalıştırmaz (`Database.Migrate()` yok), Fly `release_command` kullanılmaz;
  API uygulaması `ConnectionStrings__Migrator`'ı hiç görmez — yalnızca `bayios_app` (`NOBYPASSRLS`) `ConnectionStrings__Default`'u vardır.
- Sıra: **migration → API deploy → web deploy.** Migration başarısızsa deploy durur.
- Migration'ın ön koşulları (roller, `pg_trgm`, Auth hook'u) Supabase projesinde bir kez `infra/supabase/bootstrap/`
  ile kurulur; her deploy sonunda `verify.sql` duruşu yeniden denetler.
- Şema değişiklikleri expand → migrate → contract: yeni sürüm eski şemayla, eski sürüm yeni şemayla çalışabilmeli; böylece API/web
  rollback DB rollback gerektirmez. Yıkıcı değişiklik iki ayrı sürümde.
- Production migration'ı ajan tarafından elle/MCP ile uygulanmaz (`environments.md` §7).

## 6. Supabase preview projesi — maliyet notu (onay gerekli)
- Plan: tek **free** proje `bayios-preview`, `eu-central-1`. Free limitler (https://supabase.com/pricing, 2026-11-04): 500 MB DB,
  1 GB dosya, 50.000 MAU, 5 GB egress, org başına 2 aktif free proje, **1 hafta etkisizlikte duraklatma**.
- Org'da `led-com-staging` zaten aktif → `bayios-preview` 2. ve son free slotu kullanır.
- Branching (PR başına DB) yalnızca Pro ($25/ay'dan) + branch saat ücreti — önerilmiyor.
- **Ürün sahibi onayı olmadan proje oluşturulmaz.** Ayrıntı: `environments.md` §4.

## 7. Ürün sahibi aksiyonları (özet)
| Aksiyon | Maliyet | Durum |
|---|---|---|
| Hosting sağlayıcısını onayla (öneri Fly.io `fra`), hesap aç, harcama limiti kur | Free tier yok; preview auto-stop ile ~$0–4/ay, prod ~$10–20/ay (tahmini) | Bekliyor |
| `bayios-preview` Supabase projesi | $0 (free) — son free slot | Bekliyor |
| Vercel projesi (root `apps/web`) | Hobby $0 ama ticari olmayan kullanım; ticari pilotta Pro $20/kullanıcı/ay | Bekliyor |
| GitHub Environments `preview` / `production` (+ `production` reviewer) ve deploy token'ları — adlar §8 | $0 | Bekliyor |
| `ci-container` job'ını zorunlu kontrol yapma | $0 | Karşılandı: `ci gate` tüm workflow'ları kapsıyor (#71) |

## 8. Ürün sahibi kurulum listesi (yalnızca adlar — değerler sağlayıcılarda, repoda asla)
Hat bu adımlar yapılana kadar güvenli no-op'tur. Sıra önemlidir: önce DB, sonra API, en son GitHub sırları.

**A. Supabase (ortam başına ayrı proje)** — `infra/supabase/bootstrap/README.md`
1. Projeyi oluşturun (`bayios-preview`, `eu-central-1`; maliyet §6).
2. `SUPABASE_DB_URL=… bash infra/supabase/bootstrap/bootstrap.sh --set-passwords` (yalnızca kendi makinenizde; `postgres`
   URL'si hiçbir yere kaydedilmez). `bayios_migrator` ve `bayios_app` parolalarını parola yöneticinizde saklayın.
3. Auth kontrol listesi (README §3): Confirm email, Secure email change, min. parola ≥ 8, redirect URL'leri, ES256 JWT
   imzalama, access-token hook'u etkinleştirme + doğrulama.

**B. Fly.io (ortam başına ayrı uygulama)**
1. Uygulama: `fly apps create <ad>` (öneri `bayios-api-preview`; prod `bayios-api-prod`), harcama limiti/alarm.
2. Uygulama secret'ları (`fly secrets set --app <ad> …`, adlar):
   `ConnectionStrings__Default` (`bayios_app`), `Auth__Authority`, `Auth__Audience`, `Cors__AllowedOrigins__0`
   (`__1` …), `Audit__IpHashKey`; hook doğrulandıktan sonra `Invitations__TrustUserMetadataEmailVerified` = `false`.
   **`bayios_migrator` bilgisi Fly'a konmaz.**
3. Yalnızca o uygulamaya kısıtlı deploy token'ı: `fly tokens create deploy --app <ad>` → GitHub'a (C).

**C. GitHub → Settings → Environments**
| Environment | Secrets | Variables | Koruma |
|---|---|---|---|
| `preview` | `PREVIEW_MIGRATOR_CONNECTION`, `FLY_API_TOKEN`, (opsiyonel) `VERCEL_DEPLOY_HOOK_URL` | (opsiyonel) `FLY_APP` (varsayılan `bayios-api-preview`), `API_BASE_URL` (varsayılan `https://<FLY_APP>.fly.dev`) | Deployment branches: yalnızca `preview` |
| `production` | `PRODUCTION_MIGRATOR_CONNECTION`, `FLY_API_TOKEN` (ayrı token), (opsiyonel) `VERCEL_DEPLOY_HOOK_URL` | `FLY_APP`, `API_BASE_URL` (zorunlu) | **Required reviewers: ürün sahibi**; deployment tags: `v*` |

Repository variable (Settings → Secrets and variables → Actions → Variables): `PRODUCTION_DEPLOY_ENABLED` = `true` —
**yalnızca** `production` ortamı ve prod kaynakları hazır ve production onayı verildiğinde. Sırlar repo düzeyine değil
**environment** düzeyine konur (iki ortam arasında paylaşım yok, PR'lar okuyamaz).

**D. Vercel** — §4 tablosu; sıralama için opsiyonel Deploy Hook (§4 "Web sıralaması").

**E. Doğrulama** — `preview`'a bir merge (veya Actions → deploy-preview → Run workflow): `migrate` → `verify: OK`,
`deploy-api` → smoke yeşil, ortam URL'si GitHub deployment'ında görünür. Ardından #10 uçtan uca smoke.
