# ADR 0011: Mobil temel — Riverpod 3 (kod üretimsiz), elle yazılmış API modelleri + sözleşme testi, sabit sürümler

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-08
- **İlgili:** [ADR 0009](0009-mobile-stack-flutter.md) (mobil yığın), [ADR 0007](0007-openapi-contract-first.md) (sözleşme), issue #7, #15

## Bağlam
ADR 0009 Flutter + Riverpod + dio + go_router'ı seçti ama durum yönetiminde kod üretimini, API istemcisinin nasıl
üretileceğini ve bağımlılık sürüm politikasını açık bıraktı. `apps/mobile` iskeleti (#7) ve oturum açma / token yenileme
(#15) bu kararları gerektiriyor. Temelin üzerine iki ajan daha özellik ekranı ekleyecek; yapı kolay genişlemeli.

## Karar
1. **Durum yönetimi: Riverpod 3 (`flutter_riverpod` 3.5.0), `riverpod_generator`/`build_runner` olmadan.**
   - Bloc yerine Riverpod: oturum → tenant → API istemcisi zinciri sağlayıcı bağımlılıkları olarak doğal ifade ediliyor
     (`authControllerProvider` → `sessionControllerProvider` → `apiClientProvider`); testlerde `ProviderContainer.test`
     ve `overrideWithValue` ile sahte depo/API takmak tek satır. Bloc'ta aynı zincir için olay/durum sınıfları ve
     `RepositoryProvider` katmanı gerekir (daha çok boilerplate, ADR 0009'daki gerekçe).
   - Kod üretimi yok: `build_runner` CI süresini ve üretilen dosya drift'ini getirir; elle yazılmış `Notifier`
     sınıfları bu ölçekte kısa ve okunur. İleride gerekirse ayrı karar.
   - Riverpod 3'ün otomatik yeniden deneme özelliği kapalı (`ProviderScope(retry: …null)`): yeniden deneme kullanıcının
     "Tekrar dene"siyle ve aynı `Idempotency-Key` ile yapılır.
2. **Yönlendirme: go_router 18.1.3**, oturum durumuna göre saf bir `redirectFor` fonksiyonuyla korunur (birim testli).
   Her özellik `lib/features/<ad>/` altında bir `AppFeature` değeri sunar; `lib/app/features.dart`'a tek satır eklenince
   rota, alt gezinme öğesi, izin bazlı görünürlük/koruma ve sözleşme kontrolü birlikte gelir.
3. **HTTP: dio 5.11.1** üzerinde ince `ApiClient`: Bearer (401'de bir kez yenile + aynı isteği tekrarla), `X-Tenant-Id`,
   `Idempotency-Key`, `X-Correlation-Id`, `X-Client-Version`/`X-Client-Platform`, Problem Details → `ApiException`.
   Başlıklar her uç için bir kez tanımlanan `ApiEndpoint`'ten türetilir.
4. **API modelleri elle yazılır + sözleşme testi.** `packages/contracts/openapi.json` OpenAPI **3.1** (`type: [null, …]`,
   `oneOf` ile null); `openapi-generator` `dart-dio` 3.1 null'lanabilirliğini güvenilir üretemiyor ve büyük bir üretilmiş
   paket getiriyor. Bunun yerine her model okuduğu alanları `ContractSchema` ile beyan eder; her uç `ApiEndpoint` ile.
   `test/contract/contract_test.dart` sözleşmeyi okur ve şunlarda kırmızı olur: yol/metot yok; `X-Tenant-Id` /
   `Idempotency-Key` zorunluluğu uçla uyuşmuyor; Bearer güvenliği uyuşmuyor; alan yok, JSON tipi farklı, ya da sözleşmede
   null/opsiyonel olabilen alan modelde zorunlu. CI (`ci-mobile`) `packages/contracts/**` değişince de koşar.
5. **Kimlik:** `supabase_flutter` 2.18.3 yalnızca oturum için (tablo/storage/realtime erişimi yok). Oturum
   `flutter_secure_storage` 11.2.0 ile Keystore/Keychain'de (varsayılan SharedPreferences yerine); Android
   `allowBackup=false`. Yenilenemeyen (iptal/süresi dolmuş) oturum = çıkış yapılmış.
6. **Sürümler sabit** (`^` yok) ve `pubspec.lock` commit'li; CI `flutter pub get --enforce-lockfile` kullanır. Güncelleme
   ayrı PR'dır. Lint: `very_good_analysis` 11.0.0 + `strict-casts/inference/raw-types`; `flutter analyze
   --fatal-infos`. Kapatılan iki kural: `public_member_api_docs` (uygulama kodu, yayınlanan paket değil) ve
   `unnecessary_type_name_in_constructor` (Dart 3.13 `new(...)` kısaltması; Flutter belgeleriyle aynı biçim korunur).
7. **Ortam:** API ve Supabase URL'si + publishable key `--dart-define-from-file=env/<ortam>.json` (git-ignored; yalnızca
   `env/*.example.json` commit'li). `dev` dışında `https` zorunlu; eksik yapılandırmada uygulama çökmez, yapılandırma
   ekranı gösterir. Düz HTTP yalnızca debug derlemede `10.0.2.2`/`localhost`'a (Android `src/debug` ağ güvenlik ayarı).

## Değerlendirilen alternatifler
| Alternatif | Neden seçilmedi |
|---|---|
| Bloc / Cubit | Geçerli; oturum-tenant-istemci zinciri için daha çok tür ve sağlayıcı katmanı (ADR 0009) |
| `riverpod_generator` | `build_runner` + üretilen dosyalar; bu ölçekte kazanç küçük |
| `openapi-generator` `dart-dio` | OpenAPI 3.1 null tipleri sorunlu, büyük üretilmiş kod; drift yine de test ister |
| `http` paketi | Interceptor/timeout/iptal için dio daha uygun (ADR 0009) |
| Caret (`^`) sürümler | Lock dosyası yine sabitler ama niyet belirsizleşir; güncellemeler görünür PR olsun |

## Sonuçlar
- (+) Yeni özellik: klasör + `AppFeature` + tek satır kayıt; uçları ve şemaları otomatik sözleşme kontrolünde.
- (+) Yazmalar tasarım gereği idempotent: `ApiClient` `Idempotency-Key` gerektiren uca anahtarsız istek göndermez.
- (−) Modeller elle yazılır; yalnızca okunan alanlar sözleşme testine girer (okunmayan yeni alanlar drift sayılmaz —
  bilinçli: geriye uyumlu ekleme istemciyi bozmaz).
- (−) Sözleşme stabilleşip 3.1 desteği olgunlaşınca üretilmiş istemciye geçiş yeniden değerlendirilir.
