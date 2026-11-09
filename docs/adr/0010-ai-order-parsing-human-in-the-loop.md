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

## Uygulama notları (JOB 12 — #46–#50, 2026-11-05)
Karar geçerlidir; aşağıdakiler uygulamadaki somutlaştırmalar ve bilinçli sapmalardır.
- **Modül:** `BayiOS.Modules.Integrations` (şema `integrations`): `ai_parse_requests` (inceleme kaydı), `ai_usage`
  (tenant × saat sayaçları). Catalog ve Orders'a yalnızca host port'ları üzerinden erişir (`IAssistantCatalog`,
  `IAssistantOrderDrafts` — `BayiOS.Api/Platform/IntegrationsPorts.cs`).
- ~~Senkron işlem (sapma)~~ → **#121 ile §2.1'e dönüldü (202 + job):** `POST /ai/order-parses` metni doğrular, tenant
  opt-in'ini (data-policy §7) ve limitleri kontrol eder, maskelenmiş metinle `pending` bir kayıt ve
  `integrations.ai_order_parse` job'ını **aynı** transaction'da yazar, `202` + `Location: …/{id}/status` döner. Durum
  ucu `GET /ai/order-parses/{id}/status` → `pending | succeeded | failed` (+ `pollAfterMs`, `failureCode`); istemci
  geri çekilmeli yoklar. Job bir `IExternalCallJobHandler`'dır (ADR 0006): (1) kısa transaction'da kaydı okur
  (işlenmişse/son tarihi geçmişse/opt-in kapandıysa iş yok) → commit; (2) **hiçbir bağlantı tutmadan** kotayı ayırır
  (kendi kısa transaction'ı), AI'ı çağırır (≤ `Ai:TimeoutSeconds` × (1 + `Ai:MaxRetries`)), token'ları yazar; (3) yeni
  kısa transaction'da eşleştirir, incelemeyi yazar ve job'ı `succeeded` işaretler. Lease kaybedilmişse (3) geri alınır;
  iki worker aynı işi işleyemez (SKIP LOCKED + `pending` koşulu; testli). Son deneme de başarısızsa kayıt `failed`
  (`ai.parse_failed`); `Ai:ProcessingDeadlineSeconds` (900) içinde işlenmeyen kayıt `ai.parse_timed_out` olarak
  raporlanır. İdempotency: submit `Idempotency-Key` ile (aynı anahtar → aynı 202 gövdesi, tek kayıt, tek job; job
  `dedupe_key` = parse id).
- **Şema (§2.2):** `lines[] { rawText, productCode?, productText, quantity?, unit?, brandModel?, confidence, notes? }`,
  `unparsedFragments[]`, `warnings[]` (sabit kod listesi). Fiyat/stok/katalog kimliği/onay alanı yok; sunucu doğrulayıcısı
  (`ParsedOrderValidator`) tam özellik kümesini, tipleri, enum'ları ve API'nin ifade edemediği sınırları (uzunluk,
  miktar 0 < q ≤ 10⁶ ve ≤ 3 ondalık, güven 0–1) ayrıca zorlar. `customer_hints` / `delivery_notes` ayrı alan değil:
  `unparsedFragments` + `delivery_note` / `customer_info_present` uyarısı.
- **Grounding (uydurma koruması):** modelin `rawText`'i mesajda geçmiyorsa satır atılır (`line_not_in_message`);
  mesajda geçmeyen `productCode` silinir (`code_not_in_message`). Model kataloğu görmediğinden ürün de seçemez.
- **Claude çağrısı:** resmî C# SDK (`Anthropic` 13.5.0), model `Ai:Model` (varsayılan `claude-opus-5-5`), structured
  outputs (`output_config.format`), `output_config.effort` (varsayılan `low`; Opus 5.5'te thinking kapatılamaz); forced
  tool choice / prefill / sampling parametresi yok; sabit sistem talimatı `cache_control` ile önbellekli; mesaj
  `<untrusted_message>` bloğunda ve açılı parantezleri nötrlenmiş. `stop_reason` (`refusal`, `max_tokens`) içerikten önce
  kontrol edilir. `prompt_version` talimat + şemanın SHA-256 önekinden türetilir (`claude-v1-…`); kural ayrıştırıcı
  `rules-v1`.
- **Geri dönüş:** AI kapalı/anahtarsız, kota dolu veya sağlayıcı hatası/zaman aşımı/ret/kesik/geçersiz çıktı →
  deterministik kural ayrıştırıcı (`fallbackReason`). Ayrıntı ve maliyet modeli: `docs/operations/ai-cost-quality.md`.
- **Eşleştirme (§2.3):** (a) SKU/barkod tam eşleşme, (c) normalize ad tam eşleşme, (d) token benzerliği — Türkçe
  katlama, hafif kök bulma, yazım hatası için trigram, rakam içeren token'larda **kesin** karşılaştırma (H4 ≠ H7).
  `pg_trgm` sorgusu yerine bellek içi skorlama seçildi (aynı kod eval kümesinde birim test edilir); tenant'ın en çok
  `Ai:MatchCatalogLimit` (25.000) aktif varyantı yüklenir, tam kodlar her zaman tüm katalogda aranır.
  (b)/(c) **öğrenilmiş alias (WAI-07, #121):** Catalog'daki `variant_aliases` yerine Integrations'a ait
  `integrations.learned_aliases` (tenant, isteğe bağlı müşteri, normalize ifade → varyant; RLS FORCE). Onayda, Orders
  taslağı kabul ettikten sonra dahil edilen ve kod/barkod/tam adla zaten belirlenmemiş her satırın ifadesi tenant
  geneline ve siparişin müşterisine yazılır (son onay kazanır; satır başına `learnAlias: false` ile kapatılır). Eşleştirmede
  kod eşleşmesinden sonra, ad/benzerlikten önce müşteri alias'ı, sonra tenant alias'ı denenir (`method = alias`; yine
  yalnızca öneri, insan onaylar). Alias yalnızca tenant'ın **aktif** varyantına işaret ediyorsa kullanılır; ürün, fiyat
  veya stok getiremez. `GET /ai/aliases`, `DELETE /ai/aliases/{id}` (denetimli) ile yönetilir.
- **Kişi adı maskeleme (#121):** telefon/e-postaya ek olarak bağlamla tanınan adlar `[ad]` olur (en iyi çaba; sınırlar
  data-policy §3).
- **API ekleri (#121):** satırlarda `textStart`/`textLength` (`messageText` içinde UTF-16 ofset; bulunamazsa null),
  `GET /ai/settings` giriş limitlerini yayınlar, submit `warehouseId` kabul eder (onayda varsayılan), onay satırında
  `unit` (verilirse varyantın birimi olmalı, aksi 422 — dönüşüm yok) ve yanıt satırında `selectedUnit`.
- **Onay (§2.4–2.5):** `POST /ai/order-parses/{id}/approve` her inceleme satırının açıkça listelenmesini ister
  (dahil edilen satırda `variantId` zorunlu — eşleşen satırın önerisi de örtük kabul edilmez); ayrı `PUT …/lines` ucu
  yok, seçimler onay isteğinde gelir. Taslak Orders `CreateAsync` ile `source = whatsapp_ai`,
  `source_reference_id = parse id` olarak oluşur; fiyat Pricing'den. Sipariş tablosunda `ai_parse_request_id`
  kolonu yerine mevcut `source_reference_id` kullanılır.
- **Gizlilik (§3):** gönderilen yalnızca maskelenmiş mesaj metni + sabit talimat; saklanan metin de maskelenmiş
  hâlidir. Metin, ayrıştırma ve inceleme satırları 30 gün sonra `integrations.purge_expired_parse_texts` ile silinir
  (sayılar, token'lar ve sipariş bağı kalır). Kota, `ai_parses_per_month` entitlement'ı değil, yapılandırılabilir
  günlük çağrı/token limitidir (entitlement altyapısı henüz yok). *Güncelleme (#51/#121):* submit artık
  `ai_order_parsing` entitlement'ını (`RequireEntitlement`) ve aylık `ai_parses_per_month` limitini de uygular;
  günlük çağrı/token kotası ayrıca geçerlidir.
