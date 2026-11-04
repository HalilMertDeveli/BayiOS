# ADR 0010: WhatsApp sipariş ayrıştırma — Claude API ile yapılandırılmış çıktı, deterministik katalog eşleştirme, zorunlu insan onayı

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-04

## Bağlam
Bayiler siparişlerinin önemli kısmını WhatsApp mesajlarıyla serbest metin olarak alır ("3 koli X deterjan 5 lt, 10 adet ABC-12, geçen seferki şampuandan 2"). Bunları elle siparişe çevirmek zaman alır ve hatalıdır. LLM'ler serbest metinden ürün ifadesi/miktar çıkarmada başarılıdır ancak SKU, fiyat veya stok **uydurabilir**. Kural: AI fiyat/stok/ürün kodu uyduramaz; insan onayı olmadan sipariş kesinleşmez.

## Karar
1. **Konum:** Integrations modülü, backend'den Anthropic Claude API çağrısı. Anahtar yalnızca sunucuda (secret store). Model adı ve prompt sürümü **yapılandırmadır** (`ai_parse_requests.model`, `prompt_version` kaydedilir).
2. **Akış:**
   1. Kullanıcı mesajı yapıştırır (MVP) → `POST /api/v1/ai/order-parses` → `202`; iş `platform.jobs`'a.
   2. Job, Claude API'yi **JSON şemasıyla kısıtlanmış yapılandırılmış çıktı** ile çağırır. Şema yalnızca metinden çıkarılanları içerir: `lines[] { raw_text, product_phrase, quantity, unit_phrase, sku_mentioned?, barcode_mentioned?, notes? }`, `customer_hints`, `delivery_notes`, `uncertainties[]`. Şemada **fiyat, stok veya katalog ID'si alanı yoktur**. Çıktı sunucuda şemaya karşı yeniden doğrulanır; geçersizse tek tekrar, sonra `failed`.
   3. **Deterministik eşleştirme (kodda, AI'sız):** her satır için sırayla (a) metinde geçen SKU / barkod ile tam eşleşme, (b) müşteriye özgü alias, (c) genel alias / normalize ad tam eşleşme, (d) `pg_trgm` benzerliği (normalize ad ve alias üzerinde) ile en iyi N aday. Her aday için yöntem ve **güven skoru** (yöntem ağırlığı × benzerlik × birim uyumu) hesaplanır. Fiyatlar Pricing'den, stok Inventory'den alınır — modelden asla.
   4. Durum `needs_review`; kullanıcı ekranda her satır için adayı onaylar/değiştirir, miktarı düzeltir. Güven eşiğinin altındaki veya eşleşmeyen satırlar seçim yapılmadan onaylanamaz.
   5. `approve` → Orders `CreateDraftOrder` (`source = whatsapp_ai`, `ai_parse_request_id`). Sipariş normal akışla gönderilir ve **insan tarafından onaylanır**; AI kaynaklı siparişlerde otomatik onay yoktur.
   6. Kullanıcı düzeltmeleri opsiyonel olarak `variant_aliases`'a "öğrenilmiş alias" olarak eklenir (kullanıcı onayıyla) → sonraki eşleşmeler deterministik iyileşir.
3. **Gizlilik ve maliyet:** Gönderilen içerik yalnızca mesaj metni ve gerekirse birim listesi; müşteri katalog/fiyat verisi modele gönderilmez. Ham metin `docs/security/data-policy.md` §4'teki süre (öneri 30 gün; Phase 0 kapısında hizalandı) sonunda maskelenir. Token kullanımı kaydedilir; `ai_parses_per_month` entitlement'ı ve rate limit uygulanır. Prompt injection'a karşı: model çıktısı yalnızca veri olarak işlenir, hiçbir araç/eylem yetkisi yoktur; şema dışı alan reddedilir.
4. **Değerlendirme:** anonimleştirilmiş/sentetik örnek mesaj seti ile prompt/model değişikliklerinde regresyon testi (eşleşme doğruluğu, satır yakalama oranı).
5. Gerçek WhatsApp Business API entegrasyonu (webhook) ayrı karar ve ürün sahibi onayı gerektirir.

## Değerlendirilen alternatifler
| Alternatif | Neden seçilmedi |
|---|---|
| LLM'e katalog verip doğrudan SKU seçtirmek | Uydurma/yanlış SKU riski, büyük kataloglarda bağlam maliyeti, tenant verisinin modele gönderilmesi |
| Embedding/vektör arama ile eşleştirme | Ek altyapı (pgvector, embedding maliyeti); MVP için alias + trigram yeterli; ileride hibrit olarak eklenebilir |
| Yalnızca kural tabanlı ayrıştırma (regex) | Serbest metin çeşitliliğinde düşük isabet |
| Tam otomatik sipariş oluşturma (eşikli) | Ürün kuralıyla çelişir; hatalı sevkiyatın maliyeti yüksek |
| İstemci tarafında AI çağrısı | API anahtarı sızıntısı, maliyet kontrolü yok |

## Sonuçlar
- (+) AI yalnızca metin anlama yapar; katalog, fiyat, stok doğruluğu deterministik kodda ve test edilebilir.
- (+) İnsan onayı iki noktada: ayrıştırma incelemesi ve sipariş onayı.
- (−) Harici API bağımlılığı (gecikme, kesinti, maliyet); işler asenkron ve yeniden denenebilir.
- (−) Türkçe normalize etme (ı/i, ş/s, birim kısaltmaları) ve alias kalitesi eşleşme başarısını belirler; ayrı test seti gerekir.
- (−) Model/sağlayıcı sürüm değişiklikleri davranışı değiştirebilir; `prompt_version` + değerlendirme seti ile kontrol.
