# ADR 0009: Mobil — Flutter, Riverpod, dio, go_router, mobile_scanner, firebase_messaging

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-04

## Bağlam
Mobil uygulama (`apps/mobile`) öncelikle depo personeli (barkodla mal kabul, toplama, sayım, sevkiyat) ve sahadaki satış temsilcileri (sipariş girişi, stok sorgu) için. Android ağırlıklı (Türkiye pazarı, depo cihazları), iOS da desteklenmeli. Kamera ile hızlı barkod okuma ve push bildirimleri gereklidir. Tek kod tabanı istenir.

## Karar
1. **Flutter (stable kanal)**, tek kod tabanı Android + iOS.
2. **Durum yönetimi:** Riverpod (kod üretimli sağlayıcılar opsiyonel; ekip kararı). **HTTP:** dio (interceptor'lar: Bearer token yenileme, `X-Tenant-Id`, `Idempotency-Key`, `X-Correlation-Id`, `X-Client-Version`, Problem Details eşleme). **Yönlendirme:** go_router (oturum/tenant guard'ları).
3. **Kimlik:** Supabase Flutter istemcisi yalnızca oturum için; veri yalnızca API'den.
4. **Barkod:** `mobile_scanner` (kamera); donanım tarayıcılı cihazlar (klavye wedge) için metin alanı odak modu.
5. **Push:** `firebase_messaging`; token `POST /api/v1/devices` ile kaydedilir; FCM yalnızca bildirim/uyandırma içindir, veri taşıyıcı değildir (bildirim gelince uygulama API'den günceli çeker).
6. **API istemcisi:** başlangıçta sözleşmedeki tiplere sadık ince dio katmanı; sözleşme stabilleşince `openapi-generator` (`dart-dio`) ile üretilen paket.
7. **Çevrimdışı:** MVP'de tam çevrimdışı yok. Toplama/sayım okutmaları kısa süreli bağlantı kopmaları için yerel kuyrukta tutulur ve **aynı `Idempotency-Key`** ile yeniden gönderilir (çift kayıt yok). Tam çevrimdışı senkron ayrı ADR.

## Değerlendirilen alternatifler
| Alternatif | Neden seçilmedi |
|---|---|
| React Native (Expo) | Web ile TS paylaşımı avantajı; ancak tarama performansı ve tutarlı UI için Flutter tercih edildi (ekip kararı) |
| Native (Kotlin + Swift) | İki kod tabanı, maliyet |
| PWA | Kamera/barkod ve arka plan push'u iOS'ta kısıtlı; depo cihazlarında güvenilirlik |
| Bloc (durum yönetimi) | Geçerli seçenek; Riverpod daha az boilerplate ve test kolaylığı |

## Sonuçlar
- (+) Tek kod tabanı, iyi kamera/tarama performansı.
- (−) Dart için OpenAPI istemci üretimi TypeScript kadar olgun değil; üretilen kodun review'u gerekir.
- (−) Mağaza dağıtımı nedeniyle eski sürümler uzun yaşar → API geriye uyumluluğu ve `X-Client-Version` ile zorunlu güncelleme mekanizması.
- (−) Firebase projesi yalnızca FCM için; Firebase Auth/Firestore **kullanılmaz**.
