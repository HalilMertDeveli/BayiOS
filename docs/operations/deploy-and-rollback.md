# BayiOS — Deploy ve Rollback

| Alan | Değer |
|---|---|
| Sahip | DevOps Engineer |
| Durum | v0.3 — preview: API Render free (Frankfurt, `render.yaml`), web Render free (Vercel bloklu, 2026-12-05'e kadar), DB Supabase `bayios-preview` (#218, OD-12). Production deploy edilmedi (`deploy-production.yml` iskeleti kapalı, Fly.io şablonu) |
| İlgili | `environments.md` (ortamlar, sırlar, migration terfisi), `ci-cd.md` (workflow'lar), `hosting-options.md`, `infra/deploy/`, `infra/linux/` (self-hosted Ubuntu VPS alternatifi — API + web + Nginx; sunucu açılmadı, #194), `infra/supabase/bootstrap/`, olay anı adımları: `runbooks/rollback.md`, yedek: `backup-and-restore.md`, izleme: `observability.md` |
| Son güncelleme | 2026-11-07 |

## 1. Preview ve production ayrımı

| | Preview | Production |
|---|---|---|
| Tetik | `preview` dalına merge: Render API + web (tüm check'ler yeşil olunca) | `main`'den `vX.Y.Z` etiketi + GitHub Environment `production` reviewer onayı (ürün sahibi) |
| API | Render free web service `bayios-api-preview` (`frankfurt`, Docker, uyur/uyanır) — tanım: `render.yaml` | Ayrı uygulama (öneri Fly.io `bayios-api-prod`, `fra`, **min 1 makine always-on**) — onaydan sonra |
| Web | Render free web service `bayios-web-preview` (`frankfurt`, Next.js) — Vercel bloklu (§4) | Vercel Production |
| DB | Supabase `bayios-preview` (free, yalnızca sentetik veri) | Supabase `bayios-prod` (Pro önerilir) |
| Sırlar | GitHub Environment `preview`, Render servis env'leri (Dashboard) | GitHub Environment `production`, prod host secrets, Vercel "Production" env |
| Paylaşım | Hiçbir sır, token, DB veya API servisi iki ortam arasında paylaşılmaz | |

Kurallar: preview'a production verisi gelmez; fork PR'larında deploy ve sır yok; production adımları yalnızca ürün sahibi onayıyla.

## 2. İmaj ve doğrulama (bugün çalışan kısım)
- `services/api/Dockerfile`: SDK ile derleme → `aspnet:10.0-noble-chiseled-extra` runtime (kabuk yok, non-root UID 1654,
  ICU + tzdata dahil — `tr-TR` kültürü ve `Europe/Istanbul` için). Port 8080. Taban imajlar digest ile sabit.
- İmajda yapılandırma/sır yok; `appsettings.Development.json` imaja girmez. `ASPNETCORE_ENVIRONMENT` varsayılanı `Production`;
  host `Preview`/`Production` olarak ayarlar.
- **HEALTHCHECK yok** (chiseled imajda kabuk/curl yok). Liveness dışarıdan `GET /health` ile ölçülür: Render
  `healthCheckPath` (`render.yaml`), CI smoke adımı. Readiness `GET /health/ready` (DB + yapılandırma + iş kuyruğu gecikmesi; `ready`/`degraded` 200, `unavailable` 503 — eşikler `observability.md` §4) dış izleme ve trafik kararı içindir.
- `ci-container.yml` (job: **`api container build, smoke & scan`**): imajı derler, `--read-only --cap-drop ALL` ile çalıştırır,
  `/health` = 200 bekler, root olmadığını doğrular, Trivy ile tarar (HIGH+CRITICAL raporlanır; düzeltmesi olan CRITICAL'da kırmızı).
  Registry'ye push ve deploy **yapmaz**.

## 3. API deploy ve rollback

### 3.1 Preview — Render (free, Frankfurt)
Tanım `render.yaml` (Blueprint): `bayios-api-preview`, `runtime: docker` (`services/api/Dockerfile`, bağlam `services/api`),
`plan: free`, `region: frankfurt`, `branch: preview`, `healthCheckPath: /health`, `autoDeployTrigger: checksPass`,
build filtresi `services/api/**` + `render.yaml`. İmajı Render derler; CI registry'ye push etmez, deploy token'ı yoktur.

**Sıra (migration → API) nasıl korunur:** `checksPass` ile Render bir `preview` commit'ini **ancak o commit'teki tüm GitHub
check'leri yeşil olduğunda** deploy eder. `deploy-preview.yml`'in `migrate` job'ı da bu check'lerden biridir: migration
kırmızıysa API deploy edilmez, eski sürüm çalışmaya devam eder.

| Job (`.github/workflows/deploy-preview.yml`) | Ortam | Ne yapar |
|---|---|---|
| `deploy prepare (image, migration scripts, local smoke)` | — (sır yok) | `infra/ci/ef-migrations.sh` (idempotent script'ler + `manifest.txt`, boş Postgres 17'de iki kez prova) → `docker build` → imaj yerelde `--read-only --cap-drop ALL` ile çalıştırılıp `infra/deploy/smoke.sh` (`/health` 200, yapılandırmasız `/health/ready` **503** = kapalı-güvenli, ortam `Production`) |
| `migrate preview database (bayios_migrator)` | `preview` | `infra/deploy/preflight.sh` → `infra/deploy/apply-migrations.sh` (`PREVIEW_MIGRATOR_CONNECTION`) → `verify.sql` duruş kontrolü |
| `preview release hand-over (Render checksPass)` | — | Yalnızca özet: Render'ın API ve web'i check'ler yeşil olunca deploy edeceğini yazar (Render bu job'ı beklediği için Render'ı bekleyemez) |
| `smoke preview API (Render)` | `preview` | Yalnızca `workflow_dispatch`: `smoke.sh` → `vars.API_BASE_URL` üzerinde `/health`, `/health/ready` (`vars.SMOKE_EXPECT_READY_STATUS`, varsayılan 200), `/api/v1/meta` = `Preview`; cold start için 240 sn |

Davranış:
- **Migrator sırrı yok:** `migrate` "skipped: secrets missing (PREVIEW_MIGRATOR_CONNECTION)" ile **yeşil** biter; migration
  olmaz. Render yine deploy eder; `ConnectionStrings__Default` da yoksa API kapalı-güvenlidir (`/health` 200,
  `/health/ready` 503). Bu yüzden ürün sahibi **önce** `PREVIEW_MIGRATOR_CONNECTION`'ı, **sonra** Render'daki
  `ConnectionStrings__Default`'u girer (§8).
- **PR'lar** (deploy dosyaları veya `render.yaml` değişince): yalnızca `prepare` (dry run) — ortam ve deploy sırrı **okunmaz**.
- Migration adımı reddeder: `bayios_migrator` dışı kullanıcı (`postgres`, `service_role` …), `sslmode=require` olmayan URL,
  eksik `manifest.txt`; herhangi bir SQL hatası veya `verify.sql` ihlali (ör. RLS'siz tablo, `anon` yetkisi) → kırmızı check →
  Render deploy etmez.
- Eşzamanlılık: gerçek çalıştırmalar `deploy-preview` grubunda sıraya girer, **iptal edilmez** (yarım migration olmaz).
- Render yeni örneği `/health` geçene kadar trafiğe almaz; geçmezse deploy başarısız olur, eski sürüm çalışmaya devam eder.
- **Free katman:** ~15 dk istek gelmezse örnek uyur; ilk istek **~30–60 sn** sürer. Worker (outbox, işler) yalnızca uyanıkken
  çalışır. Yalnızca preview için kabul edilmiştir (`hosting-options.md` §4).
- Dikkat: `checksPass` commit'teki **tüm** check'leri bekler; ilgisiz bir kırmızı check de (ör. mobil) API deploy'unu durdurur.

**Rollback (preview):** Render Dashboard → `bayios-api-preview` → Events → önceki sağlıklı deploy → **Rollback** (önceki
imaja anında döner). Rollback sonrası Render auto-deploy'u durdurur; düzeltme merge edilince Settings → Build & Deploy'dan
yeniden açılır. DB geri alınmaz (§5). Olay anı listesi: `runbooks/rollback.md`.

### 3.2 Production (`deploy-production.yml`) — iskelet, kapalı
`vX.Y.Z` etiketi push'u veya `workflow_dispatch` (girdi: etiket). `production guard` job'ı repo değişkeni
`PRODUCTION_DEPLOY_ENABLED` `true` değilse "skipped: production deploy disabled" ile yeşil biter. `true` ise kapalı-güvenli
kontroller: etiket biçimi, etiket commit'i `main` üzerinde, GitHub Environment `production` **var ve required reviewer kuralı
içeriyor**, `infra/deploy/fly.production.toml` var. Sonra tek `production release (migrate -> API -> web)` job'ı (`production`
ortamı → **tek onay**): katı preflight → migration → imaj `registry.fly.io/<FLY_APP>:<vX.Y.Z>-<sha12>` → `flyctl deploy` →
smoke (`Production`) → web hook. Production hostu ürün sahibi kararındadır (`hosting-options.md` §4.1); bu iş (#218) production'a
dokunmaz.

**Rollback (production):** `fly releases --app <app> --image` ile önceki sağlıklı imajı bulun; `deploy-production.yml`'i önceki
etiketle `workflow_dispatch` (`production` onayıyla). DB geri alınmaz (§5). Rollback sonrası incident notu + kök neden issue'su.

## 4. Web deploy ve rollback

### 4.1 Preview — Render free (`bayios-web-preview`, Frankfurt)
Vercel Hobby ekibi (`hmd-team`) diğer projelerin CPU kullanımı yüzünden **"fair use" blokunda: 2026-11-05 → 2026-12-05**;
proje açılamıyor ve blok kaldırmak ücretli Pro ister (OD-12'ye aykırı). Ürün sahibi kararı (2026-11-11): preview web de
Render'da, ikinci free web service olarak (`render.yaml`).

| Ayar | Değer |
|---|---|
| Servis | `bayios-web-preview`, `runtime: node`, `plan: free`, `region: frankfurt`, `branch: preview` |
| Build / start | `cd apps/web && npm ci && npm run build` / `cd apps/web && npm run start` (`next start`, Render'ın `PORT`'unu dinler) |
| Tetik | `autoDeployTrigger: checksPass`, build filtresi `apps/web/**`, `packages/contracts/**` |
| Env | `NODE_VERSION=24`, `NEXT_PUBLIC_API_BASE_URL` = API Render URL'si, `NEXT_PUBLIC_SUPABASE_URL`, `NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY` (herkese açık publishable key), opsiyonel `NEXT_PUBLIC_TURNSTILE_SITE_KEY` |
| Adres | `https://bayios-web-preview.onrender.com` — API CORS'unda (`Cors__AllowedOrigins__0`) izinli tek origin |
| Erişim | Herkese açık URL (Render free'de kimlik doğrulama koruması yok) → yalnızca sentetik veri |

`NEXT_PUBLIC_*` değerleri build zamanında pakete gömülür ve **herkese açıktır** — buraya sır konmaz. Değer değişince yeniden
deploy gerekir. **Ücretsiz instance saatleri (ayda 750) workspace'teki tüm servisler (API + web) arasında paylaşılır**; iki
servis de uyuduğunda saat harcamaz. Web de ~15 dk etkisizlikte uyur, ilk açılış ~30–60 sn sürer.

**Rollback:** Render Dashboard → `bayios-web-preview` → Events → önceki sağlıklı deploy → **Rollback** (sonra auto-deploy'u
yeniden açın, §3.1 ile aynı).

### 4.2 Vercel (2026-12-05 sonrası veya production) — repo hazır
`apps/web/vercel.json`: framework `nextjs`, `npm ci`, `npm run build`, `git.deploymentEnabled.main = false` (main'den
otomatik deploy yok). Proje: root `apps/web`, Node 24.x, aynı `NEXT_PUBLIC_*` değişkenleri (Preview env). Geçişte API'nin
`Cors__AllowedOrigins__*` listesine ve Supabase Auth redirect listesine Vercel adresi eklenir. Rollback: Deployments →
önceki deployment → Redeploy / Instant Rollback (production).

**Web sıralaması — bilinen sınır:** web ve API ayrı servislerdir; ikisi de `checksPass` ile aynı commit'i bekler ama
birbirini beklemez. Sözleşme kuralı (önce API PR'ı, sonra web; API geriye uyumlu) bu riski karşılar.

## 5. Migration sırası (değişmez kural)
- Migration'ları **yalnızca** CI/deploy pipeline'ındaki ayrı migration adımı (`infra/deploy/apply-migrations.sh`), `bayios_migrator` rolüyle çalıştırır (deploy'da `PREVIEW_MIGRATOR_CONNECTION` / `PRODUCTION_MIGRATOR_CONNECTION`; yerelde `ConnectionStrings__Migrator`).
- API **asla** başlangıçta migration çalıştırmaz (`Database.Migrate()` yok), hostta release/pre-deploy komutu kullanılmaz;
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
| Preview hostu: Render free (API + web; Vercel 2026-12-05'e kadar bloklu) — OD-12 | $0 | Karşılandı (#218) |
| `bayios-preview` Supabase projesi + bootstrap (roller, uzantı, hook, `bayios_migrator` parolası) | $0 (free) | Karşılandı; `bayios_app` parolası bekliyor |
| Production hostu (öneri Fly.io `fra`) | Free tier yok, prod ~$10–20/ay (tahmini) | Bekliyor (onay) |
| GitHub Environment `production` (+ reviewer) ve prod token'ları | $0 | Bekliyor |
| `ci-container` job'ını zorunlu kontrol yapma | $0 | Karşılandı: `ci gate` tüm workflow'ları kapsıyor (#71) |

## 8. Ürün sahibi kurulum listesi (yalnızca adlar — değerler sağlayıcılarda, repoda ve sohbette asla)
Sıra önemlidir: önce DB rolleri, sonra GitHub migrator sırrı, sonra Render'daki API bağlantısı, en son Auth URL'leri.

**A. Supabase `bayios-preview`** — `infra/supabase/bootstrap/README.md`
1. `bayios_app` parolası: kendi makinenizde `bash .demo/supabase-kurulum.sh` (veya
   `SUPABASE_DB_URL=… bash infra/supabase/bootstrap/bootstrap.sh --set-passwords`). Parolaları parola yöneticinizde saklayın.
2. Auth kontrol listesi (README §3): Confirm email, Secure email change, min. parola ≥ 8, ES256 JWT imzalama,
   access-token hook'u etkin. Hook doğrulandıktan sonra Render'da `Invitations__TrustUserMetadataEmailVerified` = `false`.
3. Authentication → URL Configuration: **Site URL** ve **Redirect URLs** = `https://bayios-web-preview.onrender.com` (Redirect URLs: `https://bayios-web-preview.onrender.com/**`).

**B. GitHub → Settings → Environments → `preview`**
| Tür | Ad | Değer |
|---|---|---|
| Secret | `PREVIEW_MIGRATOR_CONNECTION` | `bayios_migrator` URI'si, session pooler `aws-1-eu-central-1.pooler.supabase.com:5432`, `sslmode=require` |
| Variable | `API_BASE_URL` | Render URL'si (smoke job'ı için) |
| Variable (geçici) | `SMOKE_EXPECT_READY_STATUS` | `503` — yalnızca `bayios_app` bağlantısı girilene kadar |
Deployment branches: yalnızca `preview`. Eski `FLY_API_TOKEN` / `VERCEL_DEPLOY_HOOK_URL` / `FLY_APP` preview'da artık kullanılmaz.

**C. Render → `bayios-api-preview` → Environment** (`render.yaml`'da `sync: false` olanlar)
| Ad | Değer |
|---|---|
| `ConnectionStrings__Default` | **Sır.** Yalnızca `bayios_app`: `Host=aws-1-eu-central-1.pooler.supabase.com;Port=5432;Database=postgres;Username=bayios_app.<proje-ref>;Password=…;SSL Mode=Require` |
| `Auth__Authority` | `https://<proje-ref>.supabase.co/auth/v1` (ajan ayarlar) |
| `Cors__AllowedOrigins__0` | `https://bayios-web-preview.onrender.com` (ajan ayarlar) |
`Audit__IpHashKey` Render tarafından üretilir (`generateValue`). **`bayios_migrator` bilgisi Render'a konmaz.**

**D. Render → `bayios-web-preview` → Environment** — yalnızca herkese açık `NEXT_PUBLIC_*` (§4.1; ajan ayarlar).
Opsiyonel `NEXT_PUBLIC_TURNSTILE_SITE_KEY` (CAPTCHA işi, ayrı).

**E. Production** (onaydan sonra): GitHub Environment `production` — `PRODUCTION_MIGRATOR_CONNECTION`, `FLY_API_TOKEN`,
opsiyonel `VERCEL_DEPLOY_HOOK_URL`; variables `FLY_APP`, `API_BASE_URL`; required reviewers: ürün sahibi; deployment tags
`v*`. Repo değişkeni `PRODUCTION_DEPLOY_ENABLED` = `true` **yalnızca** production onayı verildiğinde.

**F. Doğrulama** — `preview`'a bir merge: `migrate` → `verify: OK`; Render Events → "Deploy live"; Actions → deploy-preview →
Run workflow → `smoke` yeşil. Ardından #10 uçtan uca smoke (`bayios-web-preview.onrender.com` üzerinde giriş → pano).
