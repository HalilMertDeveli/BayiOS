# BayiOS — CI/CD Planı

| Alan | Değer |
|---|---|
| Sahip | DevOps Engineer |
| Durum | Kısmen uygulandı — mevcut: `ci-api.yml`, `ci-web.yml`, `ci-container.yml`, `codeql.yml`, `dependency-review.yml`, `ci-migrations.yml`, `ci-gate.yml`; diğerleri plan |
| Son güncelleme | 2026-11-04 |

## 1. Genel kurallar
- Tüm workflow'larda en üstte `permissions: contents: read`; iş başına gerekenler ayrıca açılır.
- Üçüncü taraf Action'lar **commit SHA** ile sabitlenir (yorumda sürüm); `actions/*` için de SHA tercih edilir.
- `pull_request_target` **kullanılmaz**. Fork PR'larında sır ve deploy yok.
- `concurrency` ile aynı dal için eski koşular iptal edilir.
- Path filtreleri: her uygulamanın workflow'u yalnızca kendi dizini veya `packages/contracts/**` değişince koşar; ancak
  zorunlu kontrol olarak işaretlenen işler path filtresi yüzünden "pending" kalmamalı → tek bir `ci-gate` toplayıcı iş kullanılır
  (§2.1). Path filtresi olan workflow'ların işleri **kendileri** zorunlu kontrol yapılmaz; yalnızca `ci gate`.
- Runner: `ubuntu-latest` (iOS derlemesi gerekirse macOS runner — ücretli dakika çarpanı, onay gerekir).

## 2. Planlanan workflow'lar

| Dosya | Tetikleyici | Adımlar |
|---|---|---|
| `ci-api.yml` | PR, push (`preview`, `main`) — `services/api/**` | `setup-dotnet` (10.x) → `dotnet restore` → `dotnet build -warnaserror` → `dotnet format --verify-no-changes` → `dotnet test` (Testcontainers; Docker runner'da mevcut) → `dotnet list package --vulnerable --include-transitive` (bulunursa hata) → test sonuç artefaktı |
| `ci-container.yml` | PR, push (`preview`, `main`) — `services/api/**` veya workflow'un kendisi | `docker build` (`services/api/Dockerfile`, push yok) → container'ı `--read-only --cap-drop ALL` ile çalıştır → `GET /health` = 200 → non-root kontrolü → Trivy (SHA pin) HIGH+CRITICAL rapor, düzeltmesi olan CRITICAL'da hata. Job adı: `api container build, smoke & scan` |
| `ci-web.yml` | PR, push — `apps/web/**` | `setup-node` (LTS) → `npm ci` → `lint` → `typecheck` → `test` (Vitest) → `build` → Playwright smoke + axe-core (local API'ye karşı veya MSW) |
| `ci-mobile.yml` | PR, push — `apps/mobile/**` (Flutter eklendiğinde) | `subosito/flutter-action` (SHA pin) → `flutter pub get` → `flutter analyze` → `flutter test` → (opsiyonel) `flutter build apk --flavor dev` |
| `contracts.yml` | PR — `services/api/**`, `packages/contracts/**` | API'den OpenAPI üret → `packages/contracts/openapi.json` ile `git diff --exit-code` (ADR 0007) → `preview` sözleşmesine karşı kırıcı değişiklik diff'i (ör. oasdiff); TS/Dart istemci üretimi derlenir |
| `ci-migrations.yml` ✅ | PR, push (`preview`, `main`) — `services/api/**`, `infra/docker/initdb/**`, `infra/ci/ef-migrations.sh`, workflow'un kendisi | `infra/ci/ef-migrations.sh` (yerelde aynı komut: `bash infra/ci/ef-migrations.sh`; Docker + .NET SDK gerekir): `dotnet-ef` `Directory.Packages.props`'taki **EF Core sürümüne sabitlenir** → `Microsoft.EntityFrameworkCore.Design` referanslayan her modül projesindeki her DbContext otomatik bulunur (`dotnet ef dbcontext list`; yeni modül için workflow düzenlenmez) → her biri için `migrations has-pending-model-changes` (değişiklik varsa hata) ve `migrations script --idempotent` → boş `postgres:17` (`infra/docker/initdb` ile roller) üzerinde tüm script'ler `bayios_migrator` olarak **iki kez** uygulanır (idempotentlik) → `<şema>.__ef_migrations_history` kayıt sayısı doğrulanır → script'ler `migrations-sql-<sha>` artefaktı (30 gün; deploy adımı bu script'i kullanır, ADR 0005 §6). Job adı: `api migrations script, apply & drift`. Parolalar runner içindeki geçici container için sabit `ci-local-only`; sır yok |
| `db.yml` | PR — `infra/supabase/**`, `services/api/**/Migrations/**` | (Kalan kısım; migration uygulaması `ci-migrations.yml`'de) Postgres 17 servis container → bootstrap SQL (roller, uzantılar) + tüm modüllerin EF migration'ları (idempotent script) → RLS/GRANT kontrol SQL'i (`anon`/`authenticated` iş şemasında yetkisiz, tüm tenant tablolarında RLS FORCE) |
| `codeql.yml` | PR, push `main`/`preview`, haftalık cron | CodeQL `csharp`, `javascript-typescript`, `actions` |
| `dependency-review.yml` | PR | `actions/dependency-review-action` — `fail-on-severity: high`; lisans deny listesi (ör. ticari/kısıtlı lisanslar) |
| `secrets.yml` | PR, push | gitleaks (SHA pin) tam geçmiş taraması (`fetch-depth: 0`) |
| `ci-gate.yml` ✅ | PR → `preview`, `main` (path filtresi yok, her zaman koşar) | Job adı **`ci gate`** — tek zorunlu kontrol. Ayrıntı §2.1 |
| `deploy-preview.yml` | push `preview` | Migration `bayios-preview` → API imajı değişmez etiketle (git SHA) push → `fly deploy --image` (`infra/deploy/fly.preview.toml`) → smoke test (GitHub Environment `preview`). Hosting onayı bekliyor; ayrıntı `deploy-and-rollback.md` |
| `deploy-production.yml` | tag `v*` (yalnızca `main`'den) | GitHub Environment `production` (**required reviewer: ürün sahibi**) → prod migration → API deploy → smoke → Vercel prod promote |

### 2.1 `ci-gate` tasarımı (issue #71)
- `ci gate` işi, PR head SHA'sı için `pull_request` olayıyla tetiklenen **diğer tüm workflow run'larını** (workflow başına en son
  run), GitHub Actions dışı check run'ları (ör. `CodeQL` uyarı kontrolü) ve commit status'larını GitHub API ile izler
  (`infra/ci/ci-gate.sh`; yalnızca `gh` + salt-okunur `GITHUB_TOKEN`: `actions`/`checks`/`statuses: read`; üçüncü taraf Action yok).
- Hepsi `success`/`neutral`/`skipped` ile bitince (art arda iki yoklamada) geçer; biri başarısız olursa hemen düşer; 55 dk'da zaman aşımı.
- Path filtresi yüzünden **hiç başlamayan** workflow'un run'ı olmaz → meşru olarak atlanmış sayılır. Mevcut workflow'lara
  dokunulmaz; yeni workflow'lar gate'te liste güncellemeden otomatik kapsanır.
- Neden `dorny/paths-filter` + `needs` değil: işlerin tek workflow'da toplanmasını (veya her workflow'a "her zaman koşan" iş
  eklenmesini) ve her yeni workflow'da gate listesinin elle güncellenmesini gerektirir; unutulan bir liste sessizce kapsam dışı kalır.
- Bilinen sınırlar: (1) başarısız bir iş tek başına yeniden çalıştırılırsa `ci-gate` run'ı da yeniden çalıştırılmalıdır;
  (2) YAML'ı geçersiz olduğu için run oluşturamayan bir workflow'u gate göremez (GitHub PR'da hata gösterir; review'da yakalanır);
  (3) gate CI süresince bir runner'ı meşgul eder (public repo — dakika ücretsiz).

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
- Zorunlu kontroller: yalnızca **`ci gate`** (kaynak: GitHub Actions; `ci-gate.yml` workflow'undaki job adı). Gate; api, web,
  container, migrations, codeql, dependency-review ve ileride eklenecek mobile*, contracts, db, gitleaks workflow'larını kapsar
- Dal güncel olmalı (strict); konuşmalar çözülmeli
- Force push yok, silme yok; linear history (squash merge)
- Bypass listesi boş (admin dahil)

**`preview`:**
- PR zorunlu; zorunlu kontroller aynı (yalnızca `ci gate`)
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
