# BayiOS — Ortamlar

| Alan | Değer |
|---|---|
| Sahip | DevOps Engineer |
| Durum | Taslak v0.1 — hiçbir bulut kaynağı henüz oluşturulmadı |
| Son güncelleme | 2026-11-07 |

## 1. Ortam tanımları

| | Local / dev | Preview / staging | Production |
|---|---|---|---|
| Amaç | Geliştirme, test, ajan çalışması | PR doğrulama, entegrasyon (`preview` dalı), demo | Gerçek tenant'lar (pilot) |
| Veri | Sentetik seed | **Yalnızca sentetik** | Gerçek müşteri verisi |
| Web | `npm run dev` (localhost:3000) | Vercel Preview (PR başına URL) + `preview` dalı için sabit alias | Vercel Production |
| API | `dotnet run` (localhost:5280) veya `services/api/Dockerfile` imajı (port 8080) | Seçilen hosttaki preview uygulaması (bkz. hosting-options.md; Fly.io şablonu `infra/deploy/fly.preview.toml`, deploy edilmedi) | Seçilen hosttaki prod uygulaması (EU) |
| DB | Docker compose `postgres:17` | Supabase `bayios-preview` projesi (**onay bekliyor**) | Supabase `bayios-prod` (**onay + muhtemelen Pro plan**) |
| Auth | Local: Supabase CLI local stack (kurulunca) veya test JWT üretici | `bayios-preview` Supabase Auth | `bayios-prod` Supabase Auth |
| Storage | Local Supabase stack veya MinIO/disk adaptörü | `bayios-preview` Storage | `bayios-prod` Storage |
| Push | `bayios-dev` Firebase projesi | `bayios-dev` | `bayios-prod` |
| AI | Mock/kayıtlı yanıt (varsayılan) | Gerçek Claude API, düşük bütçe limitli anahtar | Gerçek Claude API, ayrı anahtar |
| Deploy | — | Otomatik (PR / `preview` merge) | Yalnızca ürün sahibi onayıyla (`main` + tag) |

## 2. Yapılandırma ve sırlar
Ayrıntılı sır tablosu: `docs/security/data-policy.md` §9. Özet:
- **Local:** `.env` (git-ignored) — `.env.example` yalnızca değişken adları.
- **Preview:** GitHub Environment `preview` — secrets `PREVIEW_MIGRATOR_CONNECTION`, `FLY_API_TOKEN`, opsiyonel `VERCEL_DEPLOY_HOOK_URL`; variables opsiyonel `FLY_APP`, `API_BASE_URL` — API host (Fly) secrets, Vercel "Preview" env.
- **Production:** GitHub Environment `production` (required reviewer: ürün sahibi) — secrets `PRODUCTION_MIGRATOR_CONNECTION`, `FLY_API_TOKEN` (ayrı token), opsiyonel `VERCEL_DEPLOY_HOOK_URL`; variables `FLY_APP`, `API_BASE_URL`; repo değişkeni `PRODUCTION_DEPLOY_ENABLED` — API host env, Vercel "Production" env. Tam liste ve sıra: `deploy-and-rollback.md` §8.
- `ConnectionStrings__Default` = `bayios_app` rolü (`NOBYPASSRLS`, tablo sahibi değil); `ConnectionStrings__Migrator` = `bayios_migrator` (yalnızca yerel/CI migration; preview/production'da aynı rolün URI'si `*_MIGRATOR_CONNECTION` GitHub Environment secret'ı, API'ye verilmez). `postgres`/service-role ile uygulama bağlantısı yasaktır; local compose'da `infra/docker/initdb/`, Supabase'de `infra/supabase/bootstrap/` aynı rolleri kurar.
- API yapılandırması ASP.NET Core adlandırmasını kullanır (`:` → `__`). Diziler indeksle verilir: CORS için `Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1`, … (tek değerli `Cors__AllowedOrigins=` diziye bağlanmaz).
- Eksik `ConnectionStrings__Default` / `Auth__*` ile API yine açılır (`/health` 200) ama kapalı-güvenli kalır: `/health/ready` 503, tüm token'lar 401.
- `Audit__IpHashKey`: audit kayıtlarındaki istemci IP'sinin HMAC-SHA-256 anahtarı (data-policy §8.5). **Fail-safe:** boşsa API çalışır ama IP hash'i hiç yazılmaz (`ip_hash = NULL`). Doluysa en az 32 bayt olmalı; daha kısası başlangıçta `OptionsValidationException` ile reddedilir (API açılmaz — yanlış sır bir deploy hatasıdır). Ortam başına ayrı rastgele değer (ör. `openssl rand -base64 48`); preview/production'da API host secret'ı, repo'ya asla. Değiştirmek eski hash'lerle karşılaştırmayı keser.
- `Audit__Retention` 730 günden kısa olamaz (başlangıçta reddedilir; veritabanı tabanı da 2 yıl). Saklama ve kuyruk temizliği saatlik bakım job'larıyla çalışır (`Jobs__Enabled=false` olan instance'ta çalışmaz; o durumda ayrı worker çalıştırılmalıdır).
- Değişken adları tüm ortamlarda aynı; değerler farklı. API `ASPNETCORE_ENVIRONMENT` = `Development | Preview | Production`.

Örnek değişken adları (değer yok):
```
API (uygulanmış): ConnectionStrings__Default, ConnectionStrings__Migrator (yalnızca migration adımı),
Auth__Authority (= JWT issuer, https://<ref>.supabase.co/auth/v1), Auth__Audience, Auth__JwksUrl (opsiyonel;
varsayılan {Authority}/.well-known/jwks.json), Cors__AllowedOrigins__0..n,
Audit__IpHashKey (sır; opsiyonel), Audit__Retention (opsiyonel, varsayılan 730.00:00:00),
Jobs__Enabled, Jobs__SucceededRetention / Jobs__DeadRetention / Jobs__OutboxRetention (opsiyonel; 7 / 30 / 7 gün),
Health__QueueLagDegradedAfter / Health__QueueLagUnhealthyAfter (opsiyonel; 5 dk / 30 dk, observability.md §4),
OTEL_EXPORTER_OTLP_ENDPOINT (boş = OpenTelemetry kapalı, hiçbir şey dışarı gitmez), OTEL_EXPORTER_OTLP_PROTOCOL,
OTEL_EXPORTER_OTLP_HEADERS (sır), OTEL_SERVICE_NAME, ErrorTracking__Provider (none|otel; varsayılan none),
Push__Enabled (varsayılan false), Push__ServiceAccountJson (sır; yalnızca Push__Enabled=true iken), Push__ProjectId,
Push__StaleAfterDays / Push__MaxDevicesPerUser / Push__TimeoutSeconds (opsiyonel; push-notifications.md §2)
API (henüz uygulanmadı, ad önerisi): SUPABASE_URL, SUPABASE_SECRET_KEY, STORAGE_BUCKET_IMPORTS, ANTHROPIC_API_KEY,
AI_MODE (mock|live), AI_DAILY_TENANT_QUOTA
Web: NEXT_PUBLIC_API_BASE_URL, NEXT_PUBLIC_SUPABASE_URL, NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY
Mobil (--dart-define): BAYIOS_ENV, BAYIOS_API_BASE_URL, BAYIOS_SUPABASE_URL, BAYIOS_SUPABASE_PUBLISHABLE_KEY, BAYIOS_CLIENT_VERSION,
BAYIOS_FIREBASE_API_KEY / _PROJECT_ID / _MESSAGING_SENDER_ID / _ANDROID_APP_ID / _IOS_APP_ID / _IOS_BUNDLE_ID (boş = push yok)
```

## 3. Veri kuralları
- Preview/dev'e production verisi kopyalanmaz; prod dump'ı alınıp başka ortama yüklenmez.
- Preview seed'i her sıfırlamada sentetik fixture'dan üretilir.
- Production'da gerçek veri silme/aktarma ürün sahibi onayı gerektirir (AUTOPILOT.md §4).

## 4. Supabase planı (dev/preview)

**Mevcut durum (Supabase MCP, 2026-11-04):** Org "HalilMertDeveli's Org"; `led-com-staging` ACTIVE (ilgisiz ürün),
`HalilMertDeveli's Project` ve `prosev-prod` INACTIVE. BayiOS projesi yok.

**Resmi limitler (https://supabase.com/pricing, 2026-11-04 kontrol edildi):** Free plan 2 aktif proje, 500 MB DB/proje,
1 GB dosya depolama, 50.000 MAU, 5 GB egress; free projeler 1 hafta etkisizlikte duraklatılır. Pro $25/ay'dan başlar,
$10 compute kredisi içerir. **Branching yalnızca Pro ve üstünde**; branch başına saatlik $0.01344.

**Önerilen plan:**
| Seçenek | Açıklama | Maliyet | Durum |
|---|---|---|---|
| A (varsayılan) | Dev: local Postgres (docker compose) + (Supabase CLI kurulunca) local Supabase stack | 0 | Hazır (Docker çalışıyor); Supabase CLI kurulu değil |
| B | Preview: tek free Supabase projesi `bayios-preview`, region `eu-central-1` | 0 (free) — ancak org'daki 2 aktif proje kotasının 2.'sini kullanır (`led-com-staging` aktif) | **Ürün sahibi onayı gerekli** |
| C | Supabase Branching (PR başına DB) | Pro plan + branch saat ücreti | Yalnızca ücretli plan onaylanırsa |
| Prod | `bayios-prod` (eu-central-1); duraklatma riskini önlemek ve yedek için Pro | ≥ $25/ay | Pilot öncesi **onay gerekli** |

Not: Free preview projesi 1 hafta etkisizlikte duraklar; CI'da haftalık "keep-alive" yerine gerektiğinde manuel resume tercih edilir (kural dolanımı yapılmaz).

## 5. Flutter flavor'ları ve `--dart-define`
- Flavor'lar: `dev`, `preview`, `prod` (Android productFlavors / iOS schemes), ayrı uygulama kimliği:
  `com.bayios.app.dev`, `com.bayios.app.preview`, `com.bayios.app` (kimlik domain/marka kararına bağlı — taslak).
- Derleme:
  ```
  flutter run --flavor dev --dart-define=API_BASE_URL=http://10.0.2.2:5080 --dart-define=SUPABASE_URL=... --dart-define=SUPABASE_PUBLISHABLE_KEY=...
  flutter build appbundle --flavor prod --dart-define-from-file=config/prod.json
  ```
- `config/*.json` yalnızca public değerler içerir; prod dosyası CI'da secret'tan üretilir, repoda yalnızca `config/example.json`.
- Sır gömülmez; uygulamaya giden her değer public kabul edilir.

## 6. Firebase projeleri
| Proje | Kullanım | Uygulamalar |
|---|---|---|
| `bayios-dev` | local + preview push | `com.bayios.app.dev`, `com.bayios.app.preview` |
| `bayios-prod` | production push | `com.bayios.app` |
FCM ücretsizdir (https://firebase.google.com/pricing, 2026-11-04). Spark planında kalınır; Blaze'e geçiş (kart gerektirir) yalnızca onayla. Proje adları Firebase'de küresel benzersiz ID gerektirir; `bayios-dev` alınmışsa sonek eklenir. **Firebase CLI kurulu değil; projeleri ürün sahibi oluşturur veya CLI kurulduktan sonra onayla oluşturulur.** Kurulum ve push'u açma adımları: [push-notifications.md](push-notifications.md) §3 (bugünkü Android paket adı `com.bayios.bayios_mobile`; ortam flavor'ları gelince tablodaki id'ler eklenir).

## 7. Migration terfi akışı
1. İş şeması migration'ı ilgili modülde EF Core migration'ı olarak yazılır (`services/api/src/Modules/<M>/…/Migrations`, ADR 0005); `infra/supabase/bootstrap/` yalnızca bootstrap içindir (DB rolleri `bayios_app`/`bayios_migrator`, `pg_trgm`, access-token hook; ileride Storage politikaları) ve proje başına bir kez ürün sahibince çalıştırılır. PR'da idempotent SQL script artefaktı + geri dönüş notu.
2. CI: boş Postgres 17 container'a tüm migration'lar uygulanır (`ci-migrations`) + bootstrap ve deploy migration adımının düz Postgres 17 ve Supabase Postgres imajında provası, RLS/GRANT kontrolü `verify.sql` (`ci-db-bootstrap`) + integration testler.
3. `preview` dalına merge → `deploy-preview.yml` migration'ı `bayios-preview`'a `bayios_migrator` ile uygular (GitHub Environment `preview`), sonra API, sonra web. Sırlar yoksa adım "skipped: secrets missing" ile atlanır.
4. Release adayı: `main`'e PR → ürün sahibi onayı → `vX.Y.Z` etiketi → `deploy-production.yml` (`PRODUCTION_DEPLOY_ENABLED` + `production` environment reviewer onayı) → prod migration → API deploy.
5. Kural: **expand → migrate → contract.** Yıkıcı değişiklik (sütun silme/yeniden adlandırma) iki ayrı sürümde yapılır; API önce eski+yeni şemayla uyumlu çalışır.
6. Production migration'ları asla ajan tarafından elle/MCP ile uygulanmaz.

## 8. Rollback
Adım adım deploy/rollback akışı: `deploy-and-rollback.md`.

| Bileşen | Yöntem |
|---|---|
| Web (Vercel) | Önceki production deployment'ı "promote/rollback" (anlık) |
| API | Önceki imaj etiketine yeniden deploy (host'un release geçmişi) |
| DB | İleri yönlü düzeltme migration'ı tercih edilir (expand/contract sayesinde API rollback'i DB rollback gerektirmez); felakette Supabase yedeğinden/PITR'den geri yükleme (PITR ücretli eklenti — plan kararı) |
| Mobil | Mağaza sürümü geri alınamaz → sunucu tarafı feature flag + minimum desteklenen sürüm kontrolü; API geriye uyumlu |
