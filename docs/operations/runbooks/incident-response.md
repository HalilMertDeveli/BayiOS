# Runbook — Olay müdahalesi (genel)

**Tetik:** kritik alarm (`../alerts.md`), kullanıcı/tenant bildirimi, CI/deploy hatası, güvenlik bulgusu.

## 1. Önem derecesi
| Derece | Tanım | Örnek | Hedef ilk yanıt |
|---|---|---|---|
| SEV1 | Veri bütünlüğü/gizliliği riski veya tüm tenant'lar için kesinti | Çapraz tenant veri görünürlüğü, defter tutarsızlığı, API tamamen erişilemez | 15 dk |
| SEV2 | Önemli işlev bozuk, geçici çözüm var | Sipariş onayı 5xx, iş kuyruğu 30+ dk takılı, giriş yapılamıyor | 1 saat |
| SEV3 | Kısmi/düşük etki | AI düşüşü (kural tabanlı ayrıştırıcı çalışıyor), tek içe aktarma hatası | Aynı iş günü |

Şüphe varsa bir üst dereceden başla; düşürmek kolaydır.

## 2. Roller (pilotta tek kişi birden çok rol üstlenebilir)
- **Olay sorumlusu:** kararları verir, zaman çizelgesini tutar (ürün sahibi veya devredilen kişi).
- **Teknik müdahale:** teşhis + düzeltme (Orkestra Şefi'nin görevlendirdiği ajan yalnızca preview/local'de ve okuma amaçlı çalışır; production değişikliği ürün sahibi onayıyla).
- **İletişim:** etkilenen tenant'lara bilgi (şablon §5).

## 3. Akış
1. **Tespit ve kayıt (0–15 dk):** GitHub'da `incident` etiketli issue aç (kişisel veri yazma). Başlangıç zamanı, belirti, alarm, örnek `correlationId` (yanıttaki `X-Correlation-Id` / problem details `correlationId`).
2. **Teşhis:**
   - `GET /health` (canlı mı?), `GET /health/ready` (`ready` / `degraded` / `unavailable`).
   - Loglarda `level=Error` ve ilgili `CorrelationId` / `Route` (`observability.md` §1). 5xx route dağılımı: `bayios_http_server_errors_total` etiketleri.
   - Son deploy/migration ne zamandı? (`fly releases`, Vercel deployments, GitHub Actions)
   - Supabase durum sayfası, Fly durum sayfası.
3. **Etkiyi durdur:** son deploy'la ilişkiliyse → `rollback.md` §1–§2 (önce geri al, sonra incele). Veri riski varsa yazmayı durdur: API'yi bakım moduna almak için Fly'da makineleri ölçeklemek (`fly scale count 0`) tam kesintidir — yalnızca SEV1 veri riskinde, ürün sahibi kararıyla.
4. **Özel runbook:** tenant sızıntısı → `tenant-data-leak.md`; stok → `stock-inconsistency.md`; kuyruk → `job-queue-stuck.md`; AI → `ai-provider-outage.md`; auth → `auth-provider-outage.md`.
5. **Düzelt ve doğrula:** düzeltme `fix/<issue>-…` dalında + test; preview'da doğrulama; production'a onaylı sürüm. Doğrulama: alarm temizlendi, `/health/ready` = `ready`, etkilenen akış elle denendi.
6. **Kapat:** issue'ya zaman çizelgesi, kök neden, etki (kaç tenant, ne kadar süre), alınan önlemler.

## 4. Olay sonrası inceleme (SEV1/SEV2: 5 iş günü içinde)
Suçlamasız format: ne oldu · nasıl fark edildi · neden oldu (5 neden) · ne iyi gitti · ne kötü gitti · aksiyonlar (her biri issue + sahip + tarih). Eksik alarm/test varsa aksiyon listesine girer. `docs/operations/risk-register.md` güncellenir.

## 5. Tenant iletişim şablonu (Türkçe, kişisel veri yok)
> Konu: BayiOS hizmetinde [kesinti/aksaklık] — [tarih saat, İstanbul]
> [Saat] itibarıyla [etkilenen işlev] [çalışmıyor/yavaş]. Ekibimiz sorunu inceliyor. Verileriniz [etkilenmedi / etkilenip etkilenmediği inceleniyor]. Bir sonraki bilgilendirme [saat]'te. — BayiOS

Veri ihlali şüphesinde iletişim `tenant-data-leak.md` §4'e göre yapılır (hukuki metin, zaman sınırları).
