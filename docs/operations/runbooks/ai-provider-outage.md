# Runbook — AI sağlayıcısı kesintisi (Anthropic)

**Tetik:** `BayiOSAiFallbackRate` (`bayios_ai_fallbacks_total{reason!="quota_exceeded"}` 15 dk'da ≥ 5); kullanıcı "WhatsApp ayrıştırma kötü/yavaş"; https://status.anthropic.com olayı; maliyet alarmı.
**Önem:** SEV3 (ürün çalışmaya devam eder). Tasarım gereği (ADR 0010): AI başarısız olursa istek **kural tabanlı ayrıştırıcıya** düşer, kullanıcı uyarı görür; AI fiyat/stok/ürün kodu uyduramaz ve insan onayı olmadan sipariş kesinleşmez — kesinti veri bütünlüğünü etkilemez.

## 1. Teşhis
- `reason` etiketi (`../observability.md` §3): `timeout` (gecikme, `Ai:TimeoutSeconds`=20), `provider_error` (5xx/429/bağlantı), `refused`, `truncated` (`Ai:MaxOutputTokens`), `invalid_output` (şema dışı yanıt — model/prompt değişikliği?), `quota_exceeded` (tenant günlük kotası — kesinti değil).
- Loglar: `OrderParseService` fallback satırları (tenant id + reason; mesaj metni loglanmaz).
- Anthropic durum sayfası; API anahtarı geçerli mi / harcama limiti doldu mu (Anthropic Console, ürün sahibi).
- Son değişiklik: `Ai__Model`, `Ai__Effort`, prompt sürümü (`ai-cost-quality.md`).

## 2. Aksiyon
| Neden | Yapılacak |
|---|---|
| Sağlayıcı kesintisi | Bekle; kural tabanlı ayrıştırıcı çalışıyor. Uzun sürerse (≥ 1 saat) AI'yi geçici kapat: `Ai__Enabled=false` + yeniden başlat (gereksiz zaman aşımı beklemesini ve maliyeti keser). Tenant'lara kısa bilgi |
| Harcama limiti / anahtar iptal | Ürün sahibi Anthropic Console'da limiti/anahtarı düzeltir; yeni anahtar host secret'ına (`Anthropic__ApiKey`), asla repo/issue'ya |
| `invalid_output` artışı | Model/prompt sürümü değiştiyse geri al (`Ai__Model`); değerlendirme setini çalıştır (`ai-cost-quality.md`, `BAYIOS_AI_EVAL=1`) |
| `timeout` artışı | Sağlayıcı yavaş: `Ai__TimeoutSeconds` artırmak kullanıcı bekleme süresini uzatır — önce kesintiyi bekle |
| Kota | Tenant'ın günlük kotası (`AI_DAILY_TENANT_QUOTA` / `Ai:Daily*`) — ürün/paket kararı |

## 3. Doğrulama
Fallback oranı normale döndü; bir sentetik mesajla (`apps/web` WhatsApp ekranı, sentetik metin) ayrıştırıcı `parser=ai` dönüyor. Gerçek WhatsApp gönderimi yapılmaz (onay gerektirir).
