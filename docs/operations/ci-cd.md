# BayiOS — CI/CD Planı

| Alan | Değer |
|---|---|
| Sahip | DevOps Engineer |
| Durum | Plan — workflow dosyaları henüz yazılmadı; GitHub reposu (`HalilMertDeveli/BayiOS`) henüz yok |
| Son güncelleme | 2026-11-04 |

## 1. Genel kurallar
- Tüm workflow'larda en üstte `permissions: contents: read`; iş başına gerekenler ayrıca açılır.
- Üçüncü taraf Action'lar **commit SHA** ile sabitlenir (yorumda sürüm); `actions/*` için de SHA tercih edilir.
- `pull_request_target` **kullanılmaz**. Fork PR'larında sır ve deploy yok.
- `concurrency` ile aynı dal için eski koşular iptal edilir.
- Path filtreleri: her uygulamanın workflow'u yalnızca kendi dizini veya `packages/contracts/**` değişince koşar; ancak
  zorunlu kontrol olarak işaretlenen işler path filtresi yüzünden "pending" kalmamalı → tek bir `ci-gate` toplayıcı iş kullanılır.
- Runner: `ubuntu-latest` (iOS derlemesi gerekirse macOS runner — ücretli dakika çarpanı, onay gerekir).

## 2. Planlanan workflow'lar

| Dosya | Tetikleyici | Adımlar |
|---|---|---|
| `ci-api.yml` | PR, push (`preview`, `main`) — `services/api/**` | `setup-dotnet` (10.x) → `dotnet restore` → `dotnet build -warnaserror` → `dotnet format --verify-no-changes` → `dotnet test` (Testcontainers; Docker runner'da mevcut) → `dotnet list package --vulnerable --include-transitive` (bulunursa hata) → test sonuç artefaktı |
| `ci-web.yml` | PR, push — `apps/web/**` | `setup-node` (LTS) → `npm ci` → `lint` → `typecheck` → `test` (Vitest) → `build` → Playwright smoke + axe-core (local API'ye karşı veya MSW) |
| `ci-mobile.yml` | PR, push — `apps/mobile/**` (Flutter eklendiğinde) | `subosito/flutter-action` (SHA pin) → `flutter pub get` → `flutter analyze` → `flutter test` → (opsiyonel) `flutter build apk --flavor dev` |
| `contracts.yml` | PR — `services/api/**`, `packages/contracts/**` | API'den OpenAPI üret → `packages/contracts/openapi.json` ile `git diff --exit-code` (ADR 0007) → `preview` sözleşmesine karşı kırıcı değişiklik diff'i (ör. oasdiff); TS/Dart istemci üretimi derlenir |
| `db.yml` | PR — `infra/supabase/**`, `services/api/**/Migrations/**` | Postgres 17 servis container → bootstrap SQL (roller, uzantılar) + tüm modüllerin EF migration'ları (idempotent script) → RLS/GRANT kontrol SQL'i (`anon`/`authenticated` iş şemasında yetkisiz, tüm tenant tablolarında RLS FORCE) |
| `codeql.yml` | PR, push `main`/`preview`, haftalık cron | CodeQL `csharp`, `javascript-typescript`, `actions` |
| `dependency-review.yml` | PR | `actions/dependency-review-action` — `fail-on-severity: high`; lisans deny listesi (ör. ticari/kısıtlı lisanslar) |
| `secrets.yml` | PR, push | gitleaks (SHA pin) tam geçmiş taraması (`fetch-depth: 0`) |
| `ci-gate.yml` (veya her workflow sonunda) | — | Yukarıdakilerin sonucunu toplayan tek zorunlu kontrol |
| `deploy-preview.yml` | push `preview` | Migration `bayios-preview` → API preview deploy → smoke test (GitHub Environment `preview`) |
| `deploy-production.yml` | tag `v*` (yalnızca `main`'den) | GitHub Environment `production` (**required reviewer: ürün sahibi**) → prod migration → API deploy → smoke → Vercel prod promote |

## 3. Dependabot planı (`.github/dependabot.yml`)
| Ekosistem | Dizin | Sıklık | Gruplama |
|---|---|---|---|
| `nuget` | `/services/api` | haftalık | minor+patch tek PR; major ayrı |
| `npm` | `/apps/web` | haftalık | aynı |
| `pub` | `/apps/mobile` | haftalık (Flutter eklenince) | aynı |
| `github-actions` | `/` | haftalık | tek PR |
| `docker` | `/infra/docker`, `/services/api` | aylık | — |
PR hedef dalı: `preview`. Dependabot security updates açık. FluentAssertions ≥ 8 için `ignore` kuralı (lisans).

## 4. GitHub güvenlik ayarları (repo oluşturulunca)
- Secret scanning + **push protection** açık (public repoda ücretsiz).
- Private vulnerability reporting açık (SECURITY.md).
- Dependabot alerts + security updates açık.
- CodeQL default veya advanced setup (yukarıdaki workflow).
- Actions: "Allow actions: selected" (GitHub + doğrulanmış yaratıcılar + SHA ile listelenenler); fork PR workflow'ları için onay gerekli ("Require approval for all outside collaborators").
- Workflow'ların varsayılan `GITHUB_TOKEN` izni: read-only.

## 5. Preview deploy akışı
- **Web:** Vercel Git entegrasyonu → her PR için otomatik Preview URL; `preview` dalı için sabit alias. Preview env değişkenleri
  `bayios-preview` API URL'sine işaret eder. Vercel Deployment Protection (Vercel Authentication) açık.
  ⚠️ Vercel Hobby planı **yalnızca kişisel/ticari olmayan** kullanım içindir (https://vercel.com/docs/plans/hobby, 2026-11-04);
  ticari pilot öncesi Pro ($20/kullanıcı/ay) **onayı gerekir**.
- **API:** Varsayılan strateji: tek paylaşımlı preview API (`preview` dalından deploy) + tek `bayios-preview` DB. PR başına API
  oluşturmak maliyet ve DB izolasyonu gerektirdiğinden başlangıçta yapılmaz; PR'lar CI'da Testcontainers ile tam test edilir,
  web PR preview'ları paylaşımlı preview API'yi kullanır. Sözleşme kıran API değişikliklerinde: önce API PR'ı `preview`'a merge, sonra web.
- Fork PR'larında preview deploy yok.

## 6. Branch rulesets
**`main`:**
- PR zorunlu; doğrudan push yok
- Onay: en az 1, **CODEOWNERS = ürün sahibi** (`@HalilMertDeveli` — CODEOWNERS dosyasında), son push sonrası onay yenilenir (dismiss stale)
- Zorunlu kontroller: `ci-gate` (api, web, mobile*, contracts, db, codeql, dependency-review, gitleaks)
- Dal güncel olmalı (strict); konuşmalar çözülmeli
- Force push yok, silme yok; linear history (squash merge)
- Bypass listesi boş (admin dahil)

**`preview`:**
- PR zorunlu; zorunlu kontroller aynı (`ci-gate`)
- Onay zorunlu değil (ajan PR'ları; Orkestra Şefi review'u), force push ve silme yok

Not: Tek kişilik projede "approvals by owner" kuralı, PR'ı açanın kendisini onaylayamaması nedeniyle ürün sahibinin açtığı PR'larda sorun yaratabilir; ajanlar PR'ı bot/ayrı hesapla değil ürün sahibinin token'ıyla açarsa onay mekanizması çalışmaz. **Karar gerekir:** (a) ajanlar için ayrı bir GitHub makine kullanıcısı/GitHub App, veya (b) `main`'de onay yerine `production` environment reviewer + ruleset bypass yok kombinasyonu.

## 7. Sürüm / etiket stratejisi
- SemVer: `v0.x.y` pilot öncesi; `v1.0.0` ilk ticari sürüm.
- Akış: `feature/*` → `preview` (PR) → `main` (release PR, ürün sahibi onayı) → `vX.Y.Z` etiketi → production deploy.
- Release notes: GitHub Release, otomatik oluşturulan notlar + Technical Writer düzenlemesi.
- Mobil: `versionName = X.Y.Z`, `versionCode = CI run numarası`; mağaza yayını ayrı onay.
- Hotfix: `fix/<issue>` → `main`'e PR (onaylı) → etiket → `preview`'a geri merge.

## 8. Rollback
1. **Web:** Vercel'de önceki production deployment'ı yeniden promote (anlık).
2. **API:** `deploy-production.yml`'i önceki etiketle `workflow_dispatch` ile çalıştır (veya host CLI'sı ile önceki release).
3. **DB:** Geri alma migration'ı yerine ileri düzeltme; expand/contract sayesinde API rollback DB değişikliği gerektirmez. Veri bozulmasında Supabase yedeğinden geri yükleme (ürün sahibi onayı).
4. Rollback sonrası: incident notu `docs/operations/` altında, kök neden issue'su.
