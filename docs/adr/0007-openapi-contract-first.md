# ADR 0007: OpenAPI ile contract-first API, commit'li sözleşme ve CI drift kontrolü

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-04

## Bağlam
Üç istemci-sunucu tarafı (ASP.NET Core API, Next.js web, Flutter mobil) paralel ve farklı ajanlarca geliştirilecek. Elle senkron tutulan DTO'lar ve dokümantasyon kayar. Mobil uygulamalar mağaza üzerinden dağıtıldığı için eski sürümler uzun süre canlı kalır; kırıcı API değişiklikleri erken yakalanmalıdır.

## Karar
1. API, REST `/api/v1`, JSON, RFC 9457 Problem Details; konvansiyonlar [api-strategy.md](../architecture/api-strategy.md).
2. OpenAPI belgesi .NET'in yerleşik `Microsoft.AspNetCore.OpenApi` paketiyle üretilir; build-time üretim ile `packages/contracts/openapi.json` dosyasına yazılır ve **commit edilir**.
3. **CI drift kontrolü:** CI belgeyi yeniden üretir; commit'li dosyadan farklıysa başarısız olur. Ayrıca `preview` dalındaki sözleşmeye karşı OpenAPI diff ile **kırıcı değişiklik** kontrolü; `v1` altında kırıcı değişiklik bloklanır.
4. **Contract-first akış:** Yeni uç önce sözleşme (DTO + route + yanıtlar, handler iskeleti) olarak PR'lanır ve Principal Architect review eder; web/mobil bu sözleşmeye karşı (gerekirse mock ile) paralel ilerler.
5. İstemci tipleri: Web `openapi-typescript` ile tip üretir (ince fetch sarmalayıcısı veya `openapi-fetch` + TanStack Query). Mobil ileride `openapi-generator` `dart-dio` jeneratörü.
6. Sözleşme zenginleştirme: `operationId`, modül `tags`, tüm hata yanıtları, gerekli başlıklar (`X-Tenant-Id`, `Idempotency-Key`, `If-Match`), `x-permission`, `x-entitlement` uzantıları. Ondalıklar string.

## Değerlendirilen alternatifler
| Alternatif | Neden seçilmedi |
|---|---|
| Elle yazılan YAML (design-first, koddan bağımsız) | Kod ile sözleşme arasında ikinci kaynak; drift; .NET tarafında jeneratör kalitesi |
| Swashbuckle | .NET 9+ şablonlarında yerleşik `Microsoft.AspNetCore.OpenApi` öne çıktı; ek bağımlılık gereksiz |
| GraphQL | Ekip deneyimi, önbellekleme, yetkilendirme karmaşıklığı; ihtiyaç REST ile karşılanıyor |
| gRPC / gRPC-Web | Tarayıcı ve Flutter web uyumu, debug zorluğu |
| tRPC | Yalnızca TypeScript; .NET ve Dart ile uyumsuz |

## Sonuçlar
- (+) Tek sözleşme, üç tarafta tip güvenliği; kırıcı değişiklik CI'da yakalanır.
- (+) Sözleşme PR'ı, paralel ajan çalışması için net bir senkron noktası.
- (−) Üretilen belgenin deterministik olması gerekir (sıralama, örnekler); CI'da kararsızlık çıkarsa normalize adımı.
- (−) Yerleşik OpenAPI üreticisinin bazı senaryolarda (polimorfizm, ondalık-string eşleme) özel dönüştürücüler (transformer) gerektirmesi.
