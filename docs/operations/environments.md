# BayiOS — Ortamlar

| Alan | Değer |
|---|---|
| Sahip | DevOps Engineer |
| Durum | Taslak v0.1 — hiçbir bulut kaynağı henüz oluşturulmadı |
| Son güncelleme | 2026-11-04 |

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
- **Preview:** GitHub Environment `preview` (deploy token'ları), API host env, Vercel "Preview" env.
- **Production:** GitHub Environment `production` (required reviewer: ürün sahibi), API host env, Vercel "Production" env.
- `ConnectionStrings__Default` = `bayios_app` rolü (`NOBYPASSRLS`, tablo sahibi değil); `ConnectionStrings__Migrator` = `bayios_migrator` (yalnızca CI/deploy migration adımı, API'ye verilmez). `postgres`/service-role ile uygulama bağlantısı yasaktır; local compose'da da aynı bootstrap SQL'i (`infra/docker/initdb/`) çalıştırılır.
- API yapılandırması ASP.NET Core adlandırmasını kullanır (`:` → `__`). Diziler indeksle verilir: CORS için `Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1`, … (tek değerli `Cors__AllowedOrigins=` diziye bağlanmaz).
- Eksik `ConnectionStrings__Default` / `Auth__*` ile API yine açılır (`/health` 200) ama kapalı-güvenli kalır: `/health/ready` 503, tüm token'lar 401.
- Değişken adları tüm ortamlarda aynı; değerler farklı. API `ASPNETCORE_ENVIRONMENT` = `Development | Preview | Production`.

Örnek değişken adları (değer yok):
```
API (uygulanmış): ConnectionStrings__Default, ConnectionStrings__Migrator (yalnızca migration adımı),
Auth__Authority (= JWT issuer, https://<ref>.supabase.co/auth/v1), Auth__Audience, Auth__JwksUrl (opsiyonel;
varsayılan {Authority}/.well-known/jwks.json), Cors__AllowedOrigins__0..n
API (henüz uygulanmadı, ad önerisi): SUPABASE_URL, SUPABASE_SECRET_KEY, STORAGE_BUCKET_IMPORTS, ANTHROPIC_API_KEY,
AI_MODE (mock|live), AI_DAILY_TENANT_QUOTA, FCM_SERVICE_ACCOUNT_JSON
Web: NEXT_PUBLIC_API_BASE_URL, NEXT_PUBLIC_SUPABASE_URL, NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY
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
FCM ücretsizdir (https://firebase.google.com/pricing, 2026-11-04). Spark planında kalınır; Blaze'e geçiş (kart gerektirir) yalnızca onayla. Proje adları Firebase'de küresel benzersiz ID gerektirir; `bayios-dev` alınmışsa sonek eklenir. **Firebase CLI kurulu değil; projeleri ürün sahibi oluşturur veya CLI kurulduktan sonra onayla oluşturulur.**

## 7. Migration terfi akışı
1. İş şeması migration'ı ilgili modülde EF Core migration'ı olarak yazılır (`services/api/src/Modules/<M>/…/Migrations`, ADR 0005); `infra/supabase/migrations` yalnızca bootstrap içindir (DB rolleri `bayios_app`/`bayios_migrator`, uzantılar, Storage politikaları). PR'da idempotent SQL script artefaktı + geri dönüş notu.
2. CI: boş Postgres 17 container'a tüm migration'lar uygulanır + RLS/GRANT kontrol SQL'i + integration testler.
3. `preview` dalına merge → migration `bayios-preview`'a otomatik uygulanır (GitHub Environment `preview`).
4. Release adayı: `main`'e PR → ürün sahibi onayı → `production` environment reviewer onayı → prod migration → API deploy.
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
