# BayiOS — Deploy ve Rollback

| Alan | Değer |
|---|---|
| Sahip | DevOps Engineer |
| Durum | Taslak v0.1 — **hiçbir ortam deploy edilmedi**; hosting sağlayıcısı ürün sahibi onayı bekliyor |
| İlgili | `environments.md` (ortamlar, sırlar, migration terfisi), `ci-cd.md` (workflow'lar), `hosting-options.md`, `infra/deploy/` |
| Son güncelleme | 2026-11-04 |

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

## 3. API deploy ve rollback (Fly.io — onaydan sonra)

**Deploy (planlanan `deploy-preview.yml`, push `preview`):**
1. `ci-api` + `ci-container` yeşil.
2. İmaj derlenir, **değişmez etiketle** (`registry.fly.io/<app>:<git-sha>`) push edilir. `latest` kullanılmaz.
3. **Migration adımı** (§5) — `bayios_migrator` ile, API deploy'undan **önce**.
4. `fly deploy --config infra/deploy/fly.preview.toml --image registry.fly.io/<app>:<git-sha>` (rolling).
   Fly yeni makineyi `/health` geçene kadar trafiğe almaz; geçmezse deploy başarısız olur, eski sürüm çalışmaya devam eder.
5. Smoke: `GET https://<app>.fly.dev/health` = 200 ve `GET /api/v1/meta` ortam = `Preview`.

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

## 5. Migration sırası (değişmez kural)
- Migration'ları **yalnızca** CI/deploy pipeline'ındaki ayrı migration adımı, `bayios_migrator` rolüyle (`ConnectionStrings__Migrator`) çalıştırır.
- API **asla** başlangıçta migration çalıştırmaz (`Database.Migrate()` yok), Fly `release_command` kullanılmaz;
  API uygulaması `ConnectionStrings__Migrator`'ı hiç görmez — yalnızca `bayios_app` (`NOBYPASSRLS`) `ConnectionStrings__Default`'u vardır.
- Sıra: **migration → API deploy → web deploy.** Migration başarısızsa deploy durur.
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
| GitHub Environments `preview` / `production` (+ `production` reviewer) ve deploy token'ları | $0 | Bekliyor |
| `ci-container` job'ını zorunlu kontrol yapma (path filtresi/`ci-gate` kararıyla birlikte) | $0 | Bekliyor |
