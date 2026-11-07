# BayiOS Depo (Flutter)

Depo personeli için Android/iOS uygulaması. Veri **yalnızca** BayiOS API'sinden okunur ve yazılır; Supabase yalnızca
oturum (giriş, token yenileme) içindir. Hesap oluşturma, davet kabulü, işletme kurulumu ve şifre sıfırlama web'dedir.
Kararlar: [ADR 0009](../../docs/adr/0009-mobile-stack-flutter.md), [ADR 0011](../../docs/adr/0011-mobile-foundation-riverpod.md).

## Gereksinimler
- Flutter **3.47.9** stable (Dart 3.13), Android SDK, JDK 21. iOS derlemesi için macOS + Xcode.
- Windows/Git Bash: `export PATH="/d/DevTools/flutter/bin:$PATH" ANDROID_HOME='D:\DevTools\android-sdk'`

## Kurulum ve çalıştırma
```bash
cd apps/mobile
flutter pub get                # bağımlılıklar (sabit sürümler, pubspec.lock) + gen-l10n
cp env/dev.example.json env/dev.json   # git-ignored; değerleri doldurun
flutter run --dart-define-from-file=env/dev.json
```

`env/<ortam>.json` değişkenleri (yalnızca adlar commit'lidir; **gerçek anahtar commit edilmez**):

| Değişken | Açıklama |
|---|---|
| `BAYIOS_ENV` | `dev`, `preview`, `prod`. `dev` dışında URL'ler `https` olmak zorunda |
| `BAYIOS_API_BASE_URL` | API kökü (`/api/v1` olmadan). Android emülatörü: `http://10.0.2.2:<port>`; iOS simülatörü: `http://localhost:<port>` |
| `BAYIOS_SUPABASE_URL` | Supabase Auth kökü. Emülatör: `http://10.0.2.2:54321` |
| `BAYIOS_SUPABASE_PUBLISHABLE_KEY` | Supabase publishable (anon) anahtarı — public'tir; service-role anahtarı **asla** |
| `BAYIOS_CLIENT_VERSION` | Opsiyonel, `X-Client-Version` (varsayılan `0.1.0`) |

Yapılandırma eksikse uygulama çökmez, "Uygulama yapılandırılmamış" ekranını gösterir. Düz HTTP yalnızca **debug**
derlemede `10.0.2.2` / `localhost` / `127.0.0.1`'e açıktır (`android/app/src/debug/res/xml/network_security_config.xml`).

## Test ve kalite
```bash
flutter analyze --fatal-infos --fatal-warnings   # very_good_analysis + strict-casts/inference/raw-types
dart format lib test integration_test            # CI: --set-exit-if-changed
flutter test                                     # birim, widget, 48 dp/erişilebilirlik, sözleşme testi
flutter build apk --debug
```
CI: `.github/workflows/ci-mobile.yml` (`apps/mobile/**` veya `packages/contracts/**` değişince; `ci gate` bekler).

| Test | Kapsam |
|---|---|
| `test/core/api/api_client_test.dart` | `X-Tenant-Id`, `Idempotency-Key` (yeniden denemede aynı anahtar), 401 → yenile → tekrar, Problem Details, Retry-After, ağ/zaman aşımı |
| `test/core/api/problem_messages_test.dart` | Problem `code`/durum → Türkçe mesaj (401, 403 `tenant.access_denied`, 429 + Retry-After, 409, 5xx) |
| `test/core/auth/*` | Oturum durumu; gerçek GoTrue istemcisiyle süresi dolmuş token'ın yenilenmesi, iptal edilmiş oturum = çıkış |
| `test/core/session/session_controller_test.dart` | `/me`, tenant çözümleme, askıya alınmış işletme, `tenant.access_denied`/`inactive` |
| `test/app/*` | Yönlendirme korumaları, izin bazlı gezinme, uçtan uca uygulama akışı (sahte Auth/API) |
| `test/features/*` | Giriş formu doğrulama/hata, işletme seçici, dokunma hedefleri ≥ 48 dp (`androidTapTargetGuideline`) |
| `test/contract/contract_test.dart` | Kullanılan uç/başlık/alanlar `packages/contracts/openapi.json` ile birebir |

## Emülatör smoke (gerçek yerel yığın)
Gerçek Supabase Auth + API'ye karşı: giriş → işletme → pano → çıkış (`integration_test/smoke_test.dart`).

1. Yığını başlatın (repo kökünden; sahibin 3200/5280/55532'deki yığınıyla çakışmamak için farklı portlar):
   ```bash
   E2E_PG_PORT=55632 E2E_API_PORT=5380 E2E_WEB_PORT=3300 E2E_SKIP_WEB=1 bash infra/ci/e2e-stack.sh up
   node apps/web/e2e-local/demo/seed.mjs   # demo kullanıcıları; parolalar git-ignored .demo/credentials.json'a
   ```
   Zaten çalışan bir yerel yığın varsa (ör. API `5280`) yalnızca okuma yapan bu smoke ona karşı da koşabilir.
   **`e2e-stack.sh down` Supabase'i de durdurur — paylaşılan yığında çalıştırmayın.**
2. `env/dev.json`: `BAYIOS_API_BASE_URL=http://10.0.2.2:<API portu>`, `BAYIOS_SUPABASE_URL=http://10.0.2.2:54321`,
   anahtar: `npx supabase@2.122.1 status -o json --workdir infra/supabase/local` → `PUBLISHABLE_KEY`
   (yerel geliştirme anahtarı; yine de commit edilmez). API `127.0.0.1`'e bağlıdır; emülatör hostu `10.0.2.2` olarak görür.
   Mobil uygulama tarayıcı olmadığından CORS/Supabase yönlendirme ayarı gerekmez.
3. `env/smoke.json` (git-ignored): `{"BAYIOS_SMOKE_EMAIL": "...", "BAYIOS_SMOKE_PASSWORD": "..."}` —
   `.demo/credentials.json`'dan kopyalayın, ekrana yazdırmayın. Birden çok işletme varsa `BAYIOS_SMOKE_TENANT` (ad).
4. Emülatörü açın (`emulator -avd <ad>`), sonra:
   ```bash
   flutter test integration_test/smoke_test.dart -d emulator-5554 \
     --dart-define-from-file=env/dev.json --dart-define-from-file=env/smoke.json
   ```
   Elle smoke: `flutter run --dart-define-from-file=env/dev.json`, demo hesabıyla giriş → pano işletme adını ve rolü gösterir.

## Yapı ve yeni özellik ekleme
```
lib/
  main.dart                 Supabase.initialize (yalnızca auth) + ProviderScope
  app/                      app.dart, env.dart, router.dart (korumalar), shell.dart (alt gezinme),
                            feature.dart (AppFeature), features.dart (KAYIT LİSTESİ)
  core/
    api/                    ApiClient, ApiEndpoint, IdempotencyKey/IdempotentAction, ApiException,
                            ProblemDetails, problem_messages.dart (Türkçe), contract.dart (ContractSchema)
    auth/                   AuthRepository (+ Supabase), AuthController, güvenli oturum deposu
    session/                SessionController (/me + tenant), Access (izinler), Me modelleri
    ui/                     tokens.dart (web tasarım token'ları), app_theme.dart, widgets.dart
                            (ErrorView = tekrar dene kalıbı, OfflineBanner, LoadingView, roleLabel)
    providers.dart          appEnv, dio, apiClient, activeTenantId, networkStatus, identityApi
  features/<ad>/            <ad>_feature.dart + ekranlar + o özelliğin API/model dosyaları
  l10n/app_tr.arb           Tüm Türkçe metinler (gen-l10n)
```

Yeni özellik (ör. toplama):
1. `lib/features/picking/picking_api.dart`: uçları `ApiEndpoint` olarak tanımlayın
   (`ApiEndpoint(HttpMethod.post, '/api/v1/picking-tasks/{id}/start', idempotent: true)`), modellerde `ContractSchema`.
2. `lib/features/picking/picking_feature.dart`:
   ```dart
   final pickingFeature = AppFeature(
     id: 'picking', path: '/gorevler', label: (l) => l.navTasks, icon: Icons.checklist,
     isVisible: (access) => access.canAny(['tasks.read.own', 'tasks.read.all']),
     builder: (context, state) => const PickingListScreen(),
     routes: [GoRoute(path: ':id', builder: ...)],
     endpoints: PickingEndpoints.all, schemas: PickingModels.schemas,
   );
   ```
3. `lib/app/features.dart` listesine **tek satır**: `pickingFeature,`. Metinleri `app_tr.arb`'ye ekleyin.

Kurallar:
- API çağrısı: `ref.read(apiClientProvider).send(endpoint, decode: ..., pathParams: ..., body: ..., idempotencyKey: ...)`.
  `X-Tenant-Id` uçtan türetilir (aktif tenant); yazmalarda `Idempotency-Key` zorunludur.
- Yazma = bir kullanıcı eylemi: dokunuşta `IdempotentAction((key) => api.send(..., idempotencyKey: key))` oluşturun;
  "Tekrar dene" aynı nesnenin `run()`'ını çağırır (aynı anahtar). Yeni eylem = yeni nesne veya `restart()`.
- Hatalar: `ApiException` (`kind`, `code`, `retryAfter`, `fieldErrors`, `isTransient`); mesaj `errorMessage(l10n, e)`;
  yükleme hatasında `ErrorView(error: e, onRetry: ...)`. 401/403 `tenant.*` oturum katmanında otomatik ele alınır.
- Görünürlük yalnızca `GET /me` izinlerinden (`SessionReady.access.can/canSome`); rol adı yalnızca etiket içindir.
  Yetki her istekte API'de kontrol edilir.
- Dokunma hedefleri ≥ 48 dp (tema bunu sağlar); ekran testlerinde `expectAccessibleTargets(tester)` kullanın.
