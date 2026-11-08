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
| `BAYIOS_FIREBASE_API_KEY`, `BAYIOS_FIREBASE_PROJECT_ID`, `BAYIOS_FIREBASE_MESSAGING_SENDER_ID`, `BAYIOS_FIREBASE_ANDROID_APP_ID`, `BAYIOS_FIREBASE_IOS_APP_ID`, `BAYIOS_FIREBASE_IOS_BUNDLE_ID` | Opsiyonel, push (#44). Boşsa Firebase hiç başlatılmaz (push yok, izin istenmez). Değerler ve kurulum: [push-notifications.md](../../docs/operations/push-notifications.md) |

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
Emülatörde gerçek yığına karşı web ↔ mobil akış: `.github/workflows/e2e-mobile.yml` (aşağıda "Web ↔ mobil gerçek akış").

| Test | Kapsam |
|---|---|
| `test/core/api/api_client_test.dart` | `X-Tenant-Id`, `Idempotency-Key` (yeniden denemede aynı anahtar), 401 → yenile → tekrar, Problem Details, Retry-After, ağ/zaman aşımı |
| `test/core/api/problem_messages_test.dart` | Problem `code`/durum → Türkçe mesaj (401, 403 `tenant.access_denied`, 429 + Retry-After, 409, 5xx) |
| `test/core/auth/*` | Oturum durumu; gerçek GoTrue istemcisiyle süresi dolmuş token'ın yenilenmesi, iptal edilmiş oturum = çıkış |
| `test/core/session/session_controller_test.dart` | `/me`, tenant çözümleme, askıya alınmış işletme, `tenant.access_denied`/`inactive` |
| `test/app/*` | Yönlendirme korumaları, izin bazlı gezinme, uçtan uca uygulama akışı (sahte Auth/API) |
| `test/features/*` | Giriş formu doğrulama/hata, işletme seçici, pano, dokunma hedefleri ≥ 48 dp (`androidTapTargetGuideline`) |
| `test/features/picking/*` | Liste durumları/sekmeler, tara eşleşme/yanlış ürün/yanlış raf, adet doğrulama, aynı anahtarla yeniden gönderim, If-Match/412, izin bazlı görünürlük (sahte API sunucusu, idempotent tekrar oynatma) |
| `test/contract/contract_test.dart` | Kullanılan uç/başlık/alanlar `packages/contracts/openapi.json` ile birebir |
| `test/features/stock/*` | Stok sorgulama durumları, mal kabul akışı (okutma eşleşme/eşleşmeme, miktar/raf doğrulama, aynı `Idempotency-Key` ile yeniden deneme, `inventory.busy`), izin bazlı gizleme + 403, düzeltme + negatif stok onayı |
| `test/core/push/*` | Push kaydı: Firebase yoksa hiçbir şey; izin bir kez; işletmede kayıt; token yenileme; işletme değişimi (eski işletmeden silme); çevrimdışı kayıt aynı anahtarla yeniden; çıkıştan önce silme + FCM token iptali |
| `test/app/push_coordinator_test.dart` | Bildirime dokunma → `/gorevler/:id` (oturum geri yüklendikten sonra; soğuk başlangıç; başka üyelik işletmesine geçiş; üye olunmayan işletme yok sayılır; ön planda "Aç" bandı) |
| `test/core/network/connection_errors_test.dart` | Ağ yok → bant; ağ gelince `/meta` yoklaması → bant kalkar, başarısız yükleme kendiliğinden yenilenir; çevrimdışıyken periyodik yoklama; sunucu hatası otomatik yenilenmez |

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
5. Stok smoke (`integration_test/stock_smoke_test.dart`, #43) **yazar**: demo tenant'ta bir mal kabul (ledger `receipt`
   hareketi) oluşturur — yalnızca yerel/demo yığında çalıştırın. Ürün/raf `--dart-define` ile verilir
   (`BAYIOS_SMOKE_SKU`, `BAYIOS_SMOKE_BARCODE`, `BAYIOS_SMOKE_BIN`, `BAYIOS_SMOKE_QTY`; dosyanın başındaki örneğe bakın).
   Emülatör kamerası gerçek etiket okuyamadığından kodlar elle giriş alanından girilir; kamera ekranı bir kez açılıp
   "Kodu elle gir" ile kapatılır (önce `adb shell pm grant com.bayios.bayios_mobile android.permission.CAMERA`).

6. **Toplama smoke** (`integration_test/picking_smoke_test.dart`, **yazar**: okutur ve görevi tamamlar — yalnızca demo
   işletmede, smoke için açılmış bir siparişle). Kodlar elle giriş alanından yazılır (emülatör kamerasının önünde
   gerçek barkod yoktur); doğru raf/barkod ekrandan okunur. Ekran görüntüleri `$BAYIOS_SCREENSHOT_DIR`'e yazılır:
   ```bash
   BAYIOS_SCREENSHOT_DIR=/tmp/shots flutter drive -d emulator-5554 \
     --driver=test_driver/integration_test.dart --target=integration_test/picking_smoke_test.dart \
     --dart-define-from-file=env/dev.json --dart-define-from-file=env/smoke.json \
     --dart-define=BAYIOS_SMOKE_ORDER=<sipariş no> --dart-define=BAYIOS_SMOKE_WRONG_CODE=<başka ürünün barkodu>
   ```

7. **Push kaydı smoke** (`integration_test/push_smoke_test.dart`, #44, **yazar**: demo işletmede bir cihaz kaydı oluşturur
   ve çıkışta siler). Firebase projesi olmadığından uygulama sentetik bir token alır (`app.run(pushOverride: …)`); geri
   kalan her şey gerçektir. API'nin `/api/v1/devices` ucunu içeren bir sürümü gerekir (gerekirse worktree'den ayrı bir
   portta çalıştırın). Her adımdan sonra `BAYIOS_SMOKE_HOLD_SECONDS` (varsayılan 20) bekler; bu sırada kayıt hosttan
   `GET /api/v1/devices` ile görülebilir:
   ```bash
   flutter test integration_test/push_smoke_test.dart -d emulator-5560 \
     --dart-define-from-file=env/dev.json --dart-define-from-file=env/smoke.json \
     --dart-define=BAYIOS_SMOKE_PUSH_TOKEN=smoke-emulator-<32 hex>
   ```

## Web ↔ mobil gerçek akış — tek komut (#45)
`tool/e2e.sh` web → mobil → web akışını gerçek yığın ve gerçek emülatörle koşar; hiçbir şey mock'lanmaz ve her
çalıştırma **kendi** işletmesini, kullanıcılarını (sahip + davetle katılan `warehouse_staff`) ve ürünlerini kurar —
elle hazırlanmış veri ya da `.demo/credentials.json` gerekmez, mevcut işletmelere dokunulmaz.

1. **web** (`apps/web/e2e-local/web-mobile-1-prepare.spec.ts`): API'den işletme, raflı depo, A/B/C ürünleri, stok,
   personel daveti + kabul; sahip siparişi **web arayüzünde** oluşturur ve onaylar (rezervasyon).
2. **telefon** (`integration_test/smoke_test.dart` + `web_flow_test.dart`, personel olarak): giriş/çıkış smoke;
   stokta web onayının rezervasyonu ve "Düzelt" düğmesinin **olmaması**; mal kabul (bilinmeyen kod reddi, C rafına 4);
   havuzdaki görevi üstlenme; raf → bilinmeyen kod → **yanlış ürün** → doğru ürün (iki okutma) → **yanlış raf** → doğru
   raf → tamamla. Kodlar elle giriş alanından yazılır. Ekran görüntüleri: `build/e2e/screenshots/`.
3. **web** (`web-mobile-2-verify.spec.ts`): görev "Tamamlandı" (atanan personel), sipariş "Paketlendi", stok hâlâ
   ayrılmış, C'nin bakiyesi rafında; ledger'da personelin `receipt` hareketi; personel token'ıyla stok düzeltme 403 ve
   fiyatsız sipariş; sahip paket kontrolü + sevk → stok raftan düşer, rezervasyon kapanır.

Yerelde (Git Bash; Docker, .NET SDK, Node 24, Flutter, Android SDK):
```bash
export PATH="/d/DevTools/flutter/bin:$PATH" ANDROID_HOME='D:\DevTools\android-sdk'
emulator -avd BayiOS_Smoke_API36 &            # emulator-5554 (başka port: E2E_DEVICE=emulator-5560)
npm --prefix apps/web ci && npx --prefix apps/web playwright install chromium   # ilk sefer

# A) Çalışan bir yığına karşı (API :5280, web :3200, Supabase :54321) — örn. sahibin kendi yığını:
apps/mobile/tool/e2e.sh
# Farklı portlar: E2E_API_PORT=5380 E2E_WEB_PORT=3300 apps/mobile/tool/e2e.sh

# B) Yığını da başlatarak (Windows'ta 55432 yasak → E2E_PG_PORT):
E2E_START_STACK=1 E2E_PG_PORT=55632 E2E_API_PORT=5380 E2E_WEB_PORT=3300 apps/mobile/tool/e2e.sh
```
- Supabase publishable anahtarı (herkese açık yerel geliştirme anahtarı): `SUPABASE_PUBLISHABLE_KEY`, yoksa
  `apps/web/.env.local`, yoksa `supabase status` (`SUPABASE_WORKDIR`, varsayılan bu checkout'un `infra/supabase/local`'ı;
  git worktree'de imza anahtarı dosyası olmadığından ana checkout'un yolunu verin). Anahtar ekrana yazılmaz.
- **`e2e-stack.sh` Supabase'i (54321) yerel tüm yığınlarla paylaşır.** `E2E_START_STACK=1` onu (zaten çalışıyorsa
  aynen) başlatır; betik asla `down` çalıştırmaz. Paylaşılan makinede `e2e-stack.sh down` çalıştırmayın.
- Çalıştırma durumu ve o çalıştırmanın sentetik giriş bilgileri `build/e2e/` (git-ignored) altındadır; yazdırmayın,
  commit etmeyin. Faz 3 tek başına yeniden koşturulabilir:
  `cd apps/web && npx playwright test -c playwright.local.config.ts web-mobile-2-verify` (faz 2 başarılıysa).
- Süre (yerel, sıcak Gradle): faz 1 ≈ 5 sn, smoke ≈ 35 sn, mobil akış ≈ 2 dk, faz 3 ≈ 10 sn.

CI: `.github/workflows/e2e-mobile.yml` (`e2e web-mobile flow (android emulator)`): `e2e-stack.sh up` + KVM'li
emülatör (`ReactiveCircus/android-emulator-runner`, SHA sabit, API 34 x86_64; AVD/snapshot, Gradle ve pub önbellekli) +
aynı `tool/e2e.sh`. Path filtreli (`apps/mobile/**`, `apps/web/e2e-local/**`, `packages/contracts/**`,
`services/api/**`, yığın betikleri); `ci gate` çalıştığında bekler. Ekran görüntüleri her koşuda artefakt olarak yüklenir.

## iOS — BLOCKED
iOS derlemesi macOS + Xcode ister; geliştirme makinesi Windows'tur. GitHub'ın `macos-latest` runner'ı public repoda
ücretsiz olsa da `docs/operations/ci-cd.md` §1 ("iOS derlemesi gerekirse macOS runner — ücretli dakika çarpanı, onay
gerekir") ve `docs/operations/connectors.md` gereği macOS runner **ürün sahibi onayı** ister; bu yüzden
`flutter build ios --no-codesign` işi eklenmedi. Durum: **BLOCKED — sahip kararı** (macOS runner onayı; TestFlight /
cihaz dağıtımı için ayrıca Apple Developer hesabı). Onay gelirse önerilen iş: `macos-latest`, Flutter 3.47.9,
`flutter pub get --enforce-lockfile` → `flutter build ios --debug --no-codesign` (yalnızca `apps/mobile/**` değişince).
Kod tarafı hazır: `ios/` projesi, `NSCameraUsageDescription`; Dart kodu platformdan bağımsızdır ve aynı testlerden geçer.

## Stok ve mal kabul (#43)
- `/stok` (`inventory.read`): barkod/SKU/ad ile arama; depo/raf bazında eldeki, rezerve, satılabilir ve minimum stok.
- `/stok/mal-kabul` (`inventory.receive`): API'de taslak mal kabul yoktur; satırlar cihazda toplanır, tek
  `POST /inventory/receipts` ile gönderilir. Bağlantı koparsa satırlar kilitlenir, "Tekrar dene" aynı anahtarı gönderir.
- `/stok/duzeltme` (`inventory.adjust`): bakiye satırından açılır; tür, miktar, zorunlu sebep.
- Barkod: `mobile_scanner` (kamera) + her ekranda elle giriş alanı (el terminali / klavye-kama okuyucu da buraya yazar).
  Stok miktarı yalnızca ledger uçlarıyla değişir; uygulama miktar "ayarlamaz".

## Görevler ve toplama (#41, #42)
- **Pano:** bana atanan / toplanıyor / havuz sayıları (`GET /picking-tasks/mine`), sıradaki 3 görev, hızlı erişim.
  Yalnızca `tasks.read.all` (ör. viewer) → aktif görev sayısı (`GET /picking-tasks`). Selamlama adı: `/me fullName`
  → Supabase `user_metadata.full_name` → e-postanın `@` öncesi (web ile aynı).
- **Görev listesi** (`/gorevler`): `tasks.read.own` → "Bana atanan" + "Havuz"; `tasks.read.all` → "Tümü" (keyset
  sayfalama). Aşağı çekerek yenileme, boş/hata durumları.
- **Toplama ekranı** (`/gorevler/:id`): raf sırasıyla satırlar, raf kodu/barkod/miktarlar, sipariş bilgisi (fiyatsız,
  `orders.read`). Raf okut → ürün okut (kamera `mobile_scanner` veya elle giriş/klavye-kama okuyucu), okutma başına adet
  (0'dan büyük, en çok 3 ondalık, satırda kalandan fazla değil), eksik bildirimi, tamamla (eksik varsa onay).
  Yanlış ürün/raf/bilinmeyen kod/fazla toplama: büyük kırmızı uyarı + titreşim/ses (`HapticFeedback`/`SystemSound`,
  ek izin yok); satır sayılmaz (API reddeder, hiçbir şey değişmez).
- **İdempotens:** her okutma/eksik/tamamla/üstlen bir `IdempotentAction`; cevap kaybolursa "Tekrar gönder" aynı anahtarı
  ve gövdeyi gönderir (API kayıtlı yanıtı tekrar oynatır, bir kez sayılır). `*.busy` 409 bir kez otomatik, aynı anahtarla.
- **Eşzamanlılık:** "Görevi tamamla" `If-Match: W/"<sürüm>"` gönderir; 412 → "Görev değişmiş" + yeniden yükleme.
  Okutma ve eksik bildirimi web ile aynı şekilde If-Match'siz gider: API her okutmayı kilit altında doğrular; araya
  giren başka bir okutma yüzünden geçerli okutmaların 412 almaması için. Üstlenme yarışını API satır kilidi çözer (409).
- **Kamera:** #43 ile ortak tarayıcı (`lib/features/stock/scanner.dart`, `barcodeScannerProvider`). İzin: Android
  `CAMERA` (zorunlu donanım değil), iOS tek `NSCameraUsageDescription`; reddedilirse, kamera yoksa veya tarayıcı
  kapatılırsa elle giriş alanına odaklanılır.

## Push bildirimleri (#44)
- Firebase **yalnızca bildirim** için; `BAYIOS_FIREBASE_*` tanımları boşsa `NoopPushService` (izin yok, kayıt yok).
  `google-services.json` / `GoogleService-Info.plist` kullanılmaz ve commit edilmez: Firebase Dart'tan
  (`FirebaseOptions`) başlatılır, Android derlemesi bu dosyalar olmadan çalışır (Google Services Gradle eklentisi yok).
- `lib/core/push/`: `PushService` (arayüz, `NoopPushService`), `firebase_push.dart` (tek Firebase dosyası),
  `device_api.dart` (`POST/DELETE /api/v1/devices`), `push_registration.dart` (yaşam döngüsü). `lib/app/push_coordinator.dart`:
  dokunma → `/gorevler/:id` (yalnızca `SessionReady` olunca; görev başka bir üyelik işletmesindeyse önce ona geçilir),
  ön planda gelen bildirim → "Aç" bandı.
- İzin (Android 13+ `POST_NOTIFICATIONS`, iOS) giriş + işletme seçiminden sonra bir kez istenir. Kayıt işletme bazlıdır;
  işletme değişince eskisinden silinir, çıkıştan **önce** silinir (`beforeSignOutProvider`, en çok ~5 sn).
- iOS'ta APNs anahtarı ve Xcode *Push Notifications* yeteneği ürün sahibi adımıdır (push-notifications.md §3).

## Bağlantı hataları (#44)
- `OfflineBanner` her ekranın üstünde (`WithOfflineBanner`). Bant: bir istek yanıt alamayınca **veya** işletim sistemi ağ
  olmadığını bildirince (`connectivity_plus`, `PlatformConnectivitySource`) çıkar; ağ geri gelince ve çevrimdışıyken
  15 sn'de bir `GET /api/v1/meta` yoklanır; herhangi bir yanıt bandı kaldırır.
- Başarısız **yüklemeler** (`ErrorView`, `StockMessagePanel(error:)`, pano kartı) bağlantı gelince kendiliğinden yeniden
  yüklenir (`RefetchOnReconnect`; yalnızca bağlantı hatalarında). **Yazmalar** asla kendiliğinden gönderilmez: sonucu
  bilinmeyen idempotent yazma "Tekrar gönder" gösterir (okutma, üstlen, görevi tamamla, mal kabul, stok düzeltme) ve aynı
  `Idempotency-Key` ile gönderir.
- Testlerde periyodik yoklama kapalıdır (`offlineProbeIntervalProvider` = null), bağlantı kaynağı sahtedir.

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
