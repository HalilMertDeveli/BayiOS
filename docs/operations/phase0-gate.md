# BayiOS — Phase 0 Kalite Kapısı

| Alan | Değer |
|---|---|
| Tarih | 2026-11-04 |
| Kapı | Phase 0 (Hafta 1 — Organizasyon, PRD, Mimari) çıkış kapısı |
| İnceleyenler (rol) | Principal Architect · Security Engineer · QA Lead (bağımsız inceleme, belgeleri yazan ajanlardan ayrı) |
| Karar sahibi | Chief Orchestrator; kapsam/maliyet kararları ürün sahibi |
| İncelenen belgeler | `CLAUDE.md`, `AUTOPILOT.md`, `ROADMAP.md`, `BACKLOG.md`, `docs/product/*`, `docs/architecture/*`, `docs/adr/*`, `docs/security/*`, `docs/testing/*`, `docs/operations/*`, `docs/design/*` |
| Yöntem | Üç ayrı yazarın belgeleri arasında çelişki ve boşluk taraması (rol/izin, durum makinesi, veri modeli ↔ PRD, tenancy/RLS, test ↔ invariant, paket/entitlement, ROADMAP ↔ BACKLOG bağımlılıkları, ortam/CI, doğrulanamayan olgular) |

> Not: ROADMAP kapıyı Gün 05'e koyar; inceleme Gün 01'de (2026-11-04) belgeler tamamlanınca yapıldı. Kod veya bulut kaynağı incelenmedi.

## 1. Chief Orchestrator tarafından kabul edilmiş mimari kararlar (inceleme bunlara göre yapıldı)
1. Ayrı DB rolleri (`bayios_migrator` sahip, `bayios_app` `NOBYPASSRLS`, sahip değil) + tüm tenant tablolarında `FORCE ROW LEVEL SECURITY`.
2. Tenant bağlamı her transaction'da `set_config('app.tenant_id', …, true)`.
3. `platform.jobs` / `platform.outbox_messages` RLS'siz (yalnızca app rolü); worker iş başına tenant bağlamını kurar.
4. Modül DbContext'leri arasında paylaşılan bağlantı/transaction (UnitOfWork).
5. Lokasyon düzeyinde rezervasyon tahsisi.
6. `inventory_balances.allow_negative` denormalize kolonu.
7. Kısmi sevkiyat için `fulfillment_status` + short-close (ana durum makinesinde "Kısmi Sevk" durumu yok).
8. EF migration'ları modüllerde; `infra/supabase` yalnızca bootstrap.

## 2. Kapı kriterleri

| # | Kriter | Durum | Kanıt / not |
|---|---|---|---|
| 1 | Ürün kapsamı net | ✅ Koşullu | PRD (P0/P1/P2, hedef dışı listesi), BACKLOG 102 hikâye (70 P0; sayılar tablolarla doğrulandı). Açık ürün kararları §5'te. |
| 2 | Mimari review | ✅ Koşullu | Modüler monolith, tenancy/RLS, ledger, durum makinesi, API stratejisi tutarlı hâle getirildi; eksik P0 tabloları `data-model.md` §5.13'te zamanlandı. |
| 3 | Güvenlik yaklaşımı | ✅ Koşullu | STRIDE tehdit modeli, tenant kaçış vektörleri, JWT kontrolleri, veri politikası; platform yöneticisi aktörü modellenmedi (F-09). |
| 4 | Modül sorumlulukları | ✅ Koşullu | Modül haritası ve bağımlılık kuralları net; Raporlama/dışa aktarma ve platform yöneticisi için sahip modül kararı açık (F-08, F-09). |
| 5 | GitHub iş akışı hazır | ⚠️ Kısmi (kabul) | Git akışı, ruleset planı, CI/CD planı yazılı; `.github/` altında issue/PR şablonları, `dependabot.yml`, `ci-api`, `ci-web`, `codeql`, `dependency-review` workflow'ları mevcut. **GitHub reposu henüz oluşturulmadı** (ürün sahibi onayı, ROADMAP W2 Gün 06) — bu kapı için kabul edilebilir. `contracts`, `db`, `secrets`, `ci-gate`, deploy workflow'ları ve `main` onay modeli (risk R-20) W2'ye kaldı. |
| 6 | Bağımlılıklar | ✅ | Dış bağımlılık tablosu (ROADMAP), blokerler (STATUS), ROADMAP ↔ BACKLOG sıralama çelişkileri düzeltildi (F-10). |
| 7 | Test stratejisi | ✅ | 10 kritik senaryo mimari invariant'larla (I1–I13, C1–C8) eşleşiyor; terim uyumsuzlukları düzeltildi (F-19). Mobil ayaklar Flutter SDK'ya kadar BLOCKED (bilinen). |

### 10 kritik senaryo ↔ mimari invariant eşlemesi
| # | Senaryo | Karşılayan invariant / algoritma |
|---|---|---|
| 1 | Web ürün → mobilde görünür | api-strategy §12.3 (`/variants/lookup`), I13 |
| 2 | Mobil mal kabul → stok artar | §4.1 receipt, I1 |
| 3 | Onay → rezervasyon | §4.3, I2, I5, I6 |
| 4 | Mobil toplama → web ilerleme | §4.5 (ledger hareketi yok), outbox/SignalR |
| 5 | Sevkiyat → `on_hand` azalır | §4.6, I5, I7, C5 |
| 6 | İptal → rezervasyon serbest | §4.4, I5, C6 |
| 7 | Son 1 adete iki eşzamanlı onay | C1, C4, I3 |
| 8 | Tenant A ↛ tenant B | I13, ADR 0002, threat-model T1–T11 |
| 9 | Aynı Excel iki kez | `import_rows.target_id` idempotency, SKU upsert, hash uyarısı |
| 10 | Belirsiz WhatsApp satırı | I12, ADR 0010 |

## 3. Bulgular

Ciddiyet: **BLOCKER** = düzeltilmeden kapı geçilemez · **MAJOR** = ilgili haftadan önce kapanmalı · **MINOR** = iz sürülür.
Çözüm: **fixed** (bu incelemede belgelerde düzeltildi) · **accepted** (koşul/tarihle kabul) · **owner-decision** (ürün sahibi kararı, §5).

| ID | Ciddiyet | Belgeler | Açıklama | Çözüm |
|---|---|---|---|---|
| F-01 | BLOCKER | roles-permissions.md, data-model §3, api-strategy §12, domain-model | Rol × izin matrisi iki yerde ve çelişkili: izin adları farklı (`orders.write`/`fulfillment.pick`/`ai.parse` vs `orders.create`/`tasks.pick`/`whatsapp.parse`), atamalar farklı (sales `orders.approve` yok vs koşullu açık; manager `imports.run` ve fiyat yazma var vs yok; admin `billing.manage` var vs yok; warehouse_staff transfer oluşturma var vs yok). Yetki testleri hangi matrise göre yazılacağı belirsizdi. | **fixed** — roles-permissions.md kanonik ilan edildi; data-model §3'teki çelişkili tablo kaldırılıp taslak grup → kanonik izin eşlemesi kondu; api-strategy §12 ve system-architecture'a not. Koşul C-1. |
| F-02 | MAJOR | PRD ORD-05, user-flows, inventory-and-orders §3 | PRD/user-flows "Paketlendi → İptal"e izin verirken mimari vermiyordu; mimari "Toplanıyor → Sevk Edildi"ye (paketleme atlanırsa) izin verirken user-flows vermiyordu. | **fixed** — mimariye Packed → Cancelled (sevkiyat yoksa, `putaway` görevi), user-flows'a Toplanıyor → Sevk Edildi eklendi. |
| F-03 | MAJOR | user-flows AF-03/AF-04, test-strategy §4–§5, ADR 0004 | Rezervasyon "hareket" olarak anlatılıyordu (rezervasyon hareketi, iptalde ledger'da ters kayıt); ADR 0004'e göre rezervasyon hareket değildir. | **fixed** |
| F-04 | MAJOR | PRD INV-08, user-flows AF-07, BACKLOG US-INV-007, roles (`inventory.transfer.receive`), inventory-and-orders §4.7 | Ürün belgeleri iki aşamalı transfer ("Yolda", kısmi teslim, fark raporu) isterken mimari MVP'de anlık transfer tanımlıyor. | **owner-decision** OD-01 (W6 Gün 26 öncesi) |
| F-05 | MAJOR | data-model, PRD | P0 gereksinimlerin tablosu yok: birim dönüşümü (TEN-06/INV-16), uyumluluk (CAT-06), fiyat listesi yüzde kuralı (CAT-08), depo bazlı kritik seviye (CAT-12), varsayılan raf (INV-04), mal kabul belgesi taslak→onay (INV-05), transfer belgesi (INV-08), müşteri iadesi (INV-09), KVKK onay kaydı (SEC-04). | **accepted** — `data-model.md` §5.13'te sahip modül, asgari kolon ve son tarih ile listelendi. Koşul C-2. |
| F-06 | MAJOR | PRD TEN-04/05/12, roles-permissions §4, data-model `tenant_settings`/`tenants`, domain-model | Negatif stok politikası 3 durumlu (izin verme/uyar/izin ver) iken modelde `bool`; roles §4'teki ayarlanabilir kurallar (sales onayı, iskonto sınırı, maliyet görünürlüğü, depo personeli barkod/mal kabul onayı, 2FA), otomatik görev ayarı ve tenant profil/sektör alanları yoktu; "uyar" politikasının DB `allow_negative` ile ilişkisi tanımsızdı. | **fixed** — `negative_stock_policy` (block/warn/allow), `allow_negative = policy ≠ block`, teyit kuralı (inventory-and-orders §2.4) ve eksik ayar/profil kolonları eklendi. |
| F-07 | MAJOR | PRD INV-09/INV-10, BACKLOG US-INV-009, data-model, inventory-and-orders | "Hasarlı iade hasarlı stoğa girer, satılabilir değişmez" kuralının veri karşılığı yok (`available` tüm lokasyonları sayıyor). | **accepted** — §5.13'te lokasyon `is_sellable` yaklaşımı not edildi; Principal Architect W6 Gün 29 öncesi tasarlar. |
| F-08 | MAJOR | PRD REP-01…09, system-architecture §5 | Raporlama ve tam veri dışa aktarma (P0) için modül/okuma modeli yok; modüller arası FK/Contracts kuralıyla çapraz şema raporlar nasıl yazılacak belirsiz. | **accepted** — system-architecture §10'a açık nokta eklendi; W11 Gün 53 öncesi ADR (C-3). |
| F-09 | MAJOR | PRD SUB-08, BACKLOG US-SUB-001 (P0), threat-model, data-model | "Platform yöneticisi" tenant'lar arası yetkili aktör; kimliği, rolü, RLS dışı erişim yolu ve audit'i hiçbir belgede tanımlı değil. | **accepted** — threat-model §9 ve system-architecture §10'a eklendi; W11 Gün 51 öncesi ADR + tehdit modeli güncellemesi (C-3). |
| F-10 | MAJOR | ROADMAP, BACKLOG bağımlılıkları, api-strategy §7 | Idempotency (her POST'ta zorunlu) W7 Gün 35'te, entitlement W11'de planlıydı ama W3 (deneme planı), W4 (oluşturma uçları), W6 (Pro kapıları) bunlara bağlı; stok importu (W5) ledger'dan (W6), müşteri importu Customers'tan (W7) önceydi; onboarding'deki ilk depo (W3) Warehouse modülünden (W6) önceydi. | **fixed** — ROADMAP W3 ve W5'e bağımlılık notları eklendi. |
| F-11 | MAJOR | PRD IMP-01, BACKLOG US-IMP-001, threat-model §3.5/§6.2, api-strategy §10 | Yükleme limiti PRD'de 20 MB + `.xls`, güvenlik/API'de 10 MB + yalnızca `.xlsx/.csv`. | **owner-decision** OD-04 (W5 Gün 21 öncesi) |
| F-12 | MAJOR | BACKLOG US-SEC-003, threat-model §6.5, ADR 0003 | "5 başarısız giriş → 15 dk hesap kilidi + e-posta" kabul kriteri; giriş doğrudan Supabase Auth'a gider, API başarısız denemeleri göremez. Supabase Auth'un hesap bazlı kilit + e-posta sağladığı doğrulanmadı. | **owner-decision** OD-05 (W3 Gün 11 öncesi) |
| F-13 | MAJOR | PRD WAI-09, BACKLOG US-WAI-005, threat-model TB2→TB4/§3.7, data-policy §3/§6, ADR 0010, data-model | AI'ya "ilgili katalog adayları" gönderileceği yazıyordu; kabul edilmiş ADR 0010'a göre modele katalog/fiyat verisi gönderilmez. Ham metin saklama 90 gün (ADR/data-model) vs 30 gün (data-policy). | **fixed** — tüm belgeler ADR 0010'a hizalandı; saklama data-policy §4'e bağlandı (nihai süre OD-08). |
| F-14 | MINOR | PRD SUB-03, BACKLOG, roles §6, system-architecture, api-strategy | Entitlement hata kodu üç farklı adla (`feature_not_entitled`, `entitlement-required`, `entitlement.required`); idempotency kodu `idempotency_key_reused` vs `idempotency.key_reused`. | **fixed** — `entitlement.required`, `idempotency.key_reused` |
| F-15 | MINOR | api-strategy §9, threat-model §6.5 | Başlangıç rate limit değerleri farklı (600/120 vs 300/60; AI 20/dk vs 30/saat). | **fixed** — threat-model §6.5 tek kaynak |
| F-16 | MINOR | environments §2/§7, ci-cd §2, ADR 0005/0007 | Migration yolu `infra/supabase/migrations` olarak anlatılıyordu (kabul edilen karara aykırı); `DATABASE_URL`/`DATABASE_MIGRATION_URL` DB rolleriyle eşlenmemişti; `db.yml` modül migration'larında tetiklenmiyordu; `contracts.yml` `openapi.yaml` diyordu (sözleşme `openapi.json`); kırıcı değişiklik diff adımı eksikti. | **fixed** |
| F-17 | MINOR | threat-model §3.3/§3.4/§3.6/§4 | `stock_balances` tablo adı ve "fonksiyonla güncelleme" (mimaride yok); kilit sırası `product_id` (mimaride `variant_id, warehouse_id, location_id`); `jobs.tenant_id NOT NULL` (data-model'de tenant'sız iş tipleri için NULL); "5 hata → failed" (ADR 0006: `max_attempts` → `dead`). | **fixed** |
| F-18 | MINOR | data-model, domain-model, PRD, api-strategy, data-policy | Küçük model uyumsuzlukları: ürün "Pasif" durumu yok; kategori derinliği 5 vs PRD 4; sipariş öneki `SO` vs PRD `SIP`; import modu "yalnızca güncelle" yok; aynı dosya hash'i UQ ile engelleniyordu (PRD: uyarı); geri alma için önceki değer yok; satır tutar iskontosu, koli sayısı, kör sayım, eksik bildirimi ve `putaway` görevi, WhatsApp 24 saat mükerrer hash'i alanları yok; audit `ip inet` vs data-policy "IP hash", `actor_role` yok; cihaz tablosu adı farklı. | **fixed** |
| F-19 | MINOR | test-strategy §4–§5, inventory-and-orders §5 | E2E #5 `shipment` (tip `shipment_out`), #10 `product_id` (model `variant_id`); I1–I13/C1–C8 test stratejisine bağlı değildi. | **fixed** |
| F-20 | MINOR | user-flows AF-05/AF-06, BACKLOG US-INV-006, inventory-and-orders §2.4/§4.2/§4.8 | Barkod ekleme izni `catalog.write` vs `catalog.barcode.assign`; sayım/düzeltmede "rezerve > fiziksel → uyarı" iken DB invariant'ı (politika "izin verme") bunu reddeder. | **fixed** — ret (409) + etkilenen siparişler listesi |
| F-21 | MINOR | BACKLOG US-REP-005, threat-model T7, data-policy §4 | "24 saatlik indirme bağlantısı" vs imzalı URL ≤ 5 dk. | **fixed** — dosya 24 saat indirilebilir, her indirmede yeni kısa ömürlü URL |
| F-22 | MINOR | PRD IMP-03/IMP-14, BACKLOG US-IMP-008 | Eşleme şablonu kaydı ve sektör şablon dosyası PRD'de P0, BACKLOG'da P1. | **accepted** — Product Manager W5 Gün 21 öncesi hizalar |
| F-23 | MINOR | roles-permissions §3.6 | owner/admin `tasks.pick` ✓ ama `tasks.claim` –. | **accepted** — PM teyidi (W8 Gün 36 öncesi) |
| F-24 | MINOR | roles §5, domain-model §2, PRD SUB-01/02 | Feature/entitlement kodlarının tek listesi yok (`ai_order_parsing`, `multi_warehouse`, `mobile_scanning` yalnızca örnek). | **accepted** — W3 Gün 13'te kod listesi roles §5'e eklenir; OD-02'ye bağlı |
| F-25 | MINOR | data-model §2.3, api-strategy §12.1 | Davet kabulü ve tenant oluşturma tenant bağlamı olmadan başlar; `identity.invitations` standart RLS'li olduğundan token ile bulunamaz. | **accepted** — W3 Gün 12'de RLS yolu netleşir (system-architecture §10, C-5) |
| F-26 | MINOR | data-model `notifications.device_tokens` | Global `UQ (token)` + standart RLS: cihaz başka tenant/kullanıcıya geçtiğinde upsert RLS'e takılır. | **accepted** — W9 Gün 44 |
| F-27 | MINOR | BACKLOG US-SEC-002, api-strategy | `warehouse_staff` için fiyat alanlarının yanıttan çıkarılması (alan düzeyi yetki) mimaride tanımlı değildi. | **fixed** — api-strategy §1 |
| F-28 | MINOR | sector-scenarios §2.3/L-05, PRD AQ-03, data-model | LED senaryosu MVP'de sevk satırına seri no kaydını varsayıyor; veri modelinde alan yok. | **owner-decision** OD-10 |
| F-29 | MINOR | PRD SEC-05/NFR-REL-02, data-policy §4, environments §4/§8 | Yedek "günlük, 30 gün saklama" ve RPO hedefi; seçilecek Supabase planının yedek saklama süresi/PITR maliyeti doğrulanmadı. | **owner-decision** OD-06 |
| F-30 | MINOR | PRD AQ-05, threat-model, data-policy, environments, hosting-options | Bölge kararı açıkken tüm altyapı belgeleri `eu-central-1`/Frankfurt varsayıyor. | **owner-decision** OD-07 (threat-model §9'a not) |
| F-31 | MINOR | PRD AQ-06, ADR 0010 (Kabul edildi), CLAUDE.md | AI sağlayıcısı PRD'de açık soru, ADR'de Claude olarak kabul edilmiş. | **owner-decision** OD-08 |
| F-32 | MINOR | hosting-options, risk-register, environments, connectors, test-strategy, data-policy | Doğrulanamayan/kaynağı zayıf olgular: Fly.io "$7,73/ay", Hetzner "€6,49" ve "2026 zamları" (ikincil kaynak), Cloud Run Tier 2 birim fiyatı, Supabase limit/fiyatları ($25, $0,01344/branch-saat), Vercel Pro $20, RDAP sonuçları (`bayios.com` kayıt 2026-03-15), araç sürümleri (Docker 29.9.2, Node 26.13, gh 2.103.3, Vercel CLI 59.5.0, Claude Code 2.1.316), FluentAssertions lisans ve Agent Teams URL'leri. | **accepted** — yalnızca işaretlendi; ücretli/kalıcı karar öncesi ürün sahibi resmi panelden teyit eder (belgeler zaten "doğrulanacak" diyor) |
| F-33 | MINOR | ADR 0005 | "CLAUDE.md güncellenmeli" notu açık kalmıştı; CLAUDE.md zaten güncel. | **fixed** |

**Kalan BLOCKER: yok.** Kalan MAJOR: F-04, F-11, F-12 (ürün sahibi kararı); F-05, F-07, F-08, F-09 (koşullu kabul).

## 4. Bu incelemede yapılan düzeltmeler (dosya bazında)
- `docs/architecture/data-model.md` — §3 kanonik izin eşlemesi; `tenants` profil/sektör alanları; `tenant_settings` (negatif stok politikası + roles §4 kuralları, `SIP` öneki); ürün `inactive` durumu; `allow_negative` güncelleme notu; `order_lines.discount_amount`; `shipments.package_count`; `picking_tasks.type`/eksik durumu ve satır eksik alanları; `stock_counts.is_blind`; import modu/hash/önceki değer; AI `text_hash` ve saklama; audit `actor_role`/`ip_hash`; yeni §5.13 eksik tablolar.
- `docs/architecture/domain-model.md` — izin kaynağı, Tenant settings VO, ürün durumu, kategori derinliği, sipariş öneki, import modu, PickingTask alanları.
- `docs/architecture/inventory-and-orders.md` — Packed → Cancelled; §4.4 durum kümesi; `warn` politikası teyit kuralı.
- `docs/architecture/api-strategy.md` — `SIP` örneği; alan düzeyi yetki; rate limit tek kaynak; yükleme limiti notu; izin sütunu notu.
- `docs/architecture/system-architecture.md` — entitlement kodu; kanonik izin matrisi; eksik tablolar notu; açık noktalar (raporlama, platform yöneticisi, tenant'sız başlayan uçlar).
- `docs/adr/0005-ef-core-migrations.md` — CLAUDE.md notu kapatıldı. `docs/adr/0010-…` — saklama süresi data-policy'ye bağlandı.
- `docs/product/PRD.md` — SUB-03 hata kodu; WAI-09 veri minimizasyonu.
- `docs/product/roles-permissions.md` — entitlement hata kodu.
- `docs/product/user-flows.md` — rezervasyon ≠ hareket; barkod ekleme izni; sayımda rezerve > sayılan kuralı; Toplanıyor → Sevk Edildi.
- `docs/security/threat-model.md` — AI'ya giden veri; bakiye tablosu/güncelleme yolu; kilit sırası; iş `tenant_id` kuralı; `dead` durumu; açık sorular (bölge, platform yöneticisi).
- `docs/security/data-policy.md` — AI'ya giden veri; cihaz ve personel tablo adları.
- `docs/testing/test-strategy.md` — E2E #5/#6/#10 ve §5 terimleri; invariant bağlantısı.
- `docs/operations/environments.md` — migration yolu; DB rolü ↔ bağlantı değişkeni eşlemesi.
- `docs/operations/ci-cd.md` — `openapi.json`, kırıcı değişiklik diff'i; `db.yml` tetikleyicisi ve adımları.
- `BACKLOG.md` — entitlement ve idempotency kodları; US-WAI-005; US-INV-006; US-REP-005.
- `ROADMAP.md` — W3 platform/entitlement altyapısı notu; W5 stok/müşteri importu zamanlaması notu.

## 5. Ürün sahibi kararı bekleyen konular

| ID | Karar | Bağlı bulgu | Gerektiği an |
|---|---|---|---|
| OD-01 | Depolar arası transfer MVP'de iki aşamalı mı ("Yolda", kısmi teslim, fark raporu — PRD/BACKLOG) yoksa anlık mı (mimari)? İki aşamalı seçilirse sanal "in-transit" lokasyonu + ek iş W6'ya eklenir. | F-04 | W6 Gün 26 |
| OD-02 | Temel pakette barkod okutma/mobil toplama doğrulaması olacak mı (roles §5 önerisi: yok) ve paket limitleri (AQ-01: kullanıcı, depo, ürün)? | F-24 | W3 Gün 13 (asgari), W11 Gün 51 |
| OD-03 | Paket düşürmede mevcut stoğu boşaltmak için transfer/düzeltmeye izin verilsin mi (roles §5 öneri) yoksa "yeni transfer başlatılamaz" mı (US-SUB-004)? | — | W11 Gün 52 |
| OD-04 | İçe aktarma dosya limiti: 20 MB + `.xls` (PRD) mi, 10 MB + yalnızca `.xlsx/.csv` (güvenlik) mi? | F-11 | W5 Gün 21 |
| OD-05 | Hesap kilitleme: kabul kriteri Supabase Auth hız limiti + CAPTCHA olarak mı güncellensin, yoksa özel kilit/e-posta (Auth hook) mı geliştirilsin? | F-12 | W3 Gün 11 |
| OD-06 | Yedek saklama (30 gün) ve RPO hedefi için Supabase planı/PITR maliyeti. | F-29 | Pilot öncesi (W11 Gün 55) |
| OD-07 | Veri barındırma bölgesi (AB-Frankfurt vs Türkiye) ve KVKK yurt dışı aktarım dayanağı (AQ-05). | F-30 | Supabase preview projesi onayından önce (W2 Gün 10) |
| OD-08 | AI sağlayıcısı (ADR 0010: Claude) onayı, aylık bütçe/tenant kotası (AQ-06) ve WhatsApp ham metin saklama süresi (öneri 30 gün). | F-13, F-31 | W10 Gün 46 |
| OD-09 | Sipariş varsayılanları: sales kendi siparişini onaylayabilsin mi (AQ-02; belgelerde varsayılan "açık"), negatif stok varsayılanı (AQ-04), kısmi onay MVP'de mi (AQ-10)? | F-06 | W7 Gün 32 |
| OD-10 | LED pilotunda seri numarası: MVP'de sevk satırına serbest kayıt mı, tamamen P2 mi (AQ-03)? | F-28 | W8 Gün 36 |
| OD-11 | GitHub: public repo oluşturma, lisans seçimi, `main` onay modeli (ajanlar için bot hesabı/GitHub App vs environment reviewer — risk R-20). | Kriter 5 | W2 Gün 06 |
| OD-12 | Ücretli kaynaklar: Supabase `bayios-preview` (free kotası) ve prod (Pro), API host (öneri Fly.io), ticari pilot için Vercel Pro — fiyatlar resmi panelden teyit edilerek. | F-32 | W2 Gün 10 / W3 sonu |

### 5.1 Ürün sahibinin cevapları (2026-11-09)

| ID | Karar | Uygulama |
|---|---|---|
| OD-01 | **İki aşamalı transfer:** gönderilen mal "Yolda" görünür, alan depo varışı onaylar (kısmi teslim + fark raporu). | #168 |
| OD-02 | Temel paket limitleri: **999 kullanıcı, 999 depo, 999 ürün**. Barkod/mobil toplama doğrulaması sorusu cevaplanmadı; mevcut durum korunur. | Billing plan limitleri |
| OD-03 | **Evet:** paket düşürmede depoyu boşaltmak için transfer ve düzeltme yapılabilir. | Billing/Inventory |
| OD-04 | İçe aktarma dosya limiti **20 MB**; `.xls` onaylanmadı → yalnızca `.xlsx/.csv`. | Imports |
| OD-05 | **Supabase Auth hazır hız limiti + CAPTCHA** (Cloudflare Turnstile); özel kilit/e-posta yok. | Web giriş/kayıt/şifre sıfırlama |
| OD-06 | Ücretsiz plan (Supabase Free: günlük yedek, PITR yok). Pilot sonrası RPO/plan yeniden değerlendirilir. | — |
| OD-07 | Şimdilik **AB-Frankfurt** (`eu-central-1`); ileride Türkiye'de yerel barındırmaya geçilecek (taşıma planı o zaman yazılır). | Ortam kurulumu |
| OD-08 | AI sağlayıcısı **Claude**; WhatsApp ham metni **30 gün** saklanır. Tenant başı bütçe/kota belirtilmedi (AI ayrıştırma tenant için varsayılan kapalı kalır). | #121 |
| OD-09 | **Stok hiçbir zaman eksiye düşmez** (`negative_stock_policy` yalnızca `block`). Satışçının kendi siparişini onaylaması ve kısmi onay cevaplanmadı; mevcut davranış korunur. | Identity/Inventory |
| OD-10 | Seri numarası **ayrı modül** (P2); MVP'de sevk satırına serbest kayıt yok. | #147 |
| OD-11 | **Evet:** repo public kalır, `main` merge yalnızca ürün sahibi onayıyla. Açık kaynak lisansı eklenmez (tüm hakları saklı). | — |
| OD-12 | **Yalnızca ücretsiz kaynaklar:** Supabase Free, Vercel Hobby, ücretsiz API barındırma (Fly.io ücretsiz değil → alternatif seçilir), Firebase Spark, Cloudflare Turnstile. Ücretli plan açılmaz. | #138 |
| Marka rengi | Ürün sahibi seçimi bize bıraktı: güven veren, sıradan maviden farklı bir renk. | Tasarım token'ları |

### 5.2 Ürün sahibinin ikinci tur cevapları (2026-11-09)

| Konu | Karar |
|---|---|
| OD-02 (düzeltme) | Temel paket: **999 kullanıcı, 999 depo, ürün sınırsız**. Barkod okutma / mobil toplama **temel pakette**. |
| OD-09 | Satışçı kendi siparişini **onaylayabilir** (yeni tenant varsayılanı açık); işletme sahibi ayarlardan kapatabilir. **Kısmi onay MVP'de yok** (pilot sonrası); stok yoksa sipariş bekler. |
| OD-08 | AI ek paketi kotası **ayda 300 ayrıştırma** / tenant; tenant açmadıkça AI kapalı. |
| Alarm kanalı | **E-posta** (ücretsiz). |
| #98 depo görevleri | Sipariş onayında toplama görevi **otomatik** açılır; görevde **öncelik ve müşteri adı** görünür; raf değişiminde görev **yeniden dağıtılır**. |
| OD-12 | Pilot için ücretsiz barındırma yeterli (API: Render Free, web: Vercel Hobby); satış başlayınca ücretli plana geçilir. |
| OD-04 | `.xls` **yok**; `.xlsx` ve `.csv` yeterli. |
| Mobil CAPTCHA | Cevaplanmadı → öneri uygulanır: pilot sonrası. Supabase'te CAPTCHA o zamana kadar kapalı kalır (`environments.md` §2.1). |
| Ürün yönü | **Sektörden bağımsız self-servis:** müşteri kendi alanını (işletme/tenant) kendisi oluşturur; hiç depo sistemi olmayan biri girip kurulumu kendi başına, otomatik yönlendirmelerle tamamlayabilmeli. LED ve motosiklet yalnızca örnek senaryolardır; ürün, metinler ve demo bunlara bağlı kalmaz. |

## 6. Karar

**KOŞULLU GEÇTİ.**

Kalan BLOCKER yoktur; tek BLOCKER (F-01, rol × izin matrisi çelişkisi) bu incelemede kanonik kaynak belirlenerek kapatıldı. Hafta 2'ye geçilebilir; aşağıdaki koşullar ilgili tarihlerde kapanmazsa ilgili iş `BLOCKED` olur (AUTOPILOT §5):

| Koşul | İçerik | Sahip | Son tarih |
|---|---|---|---|
| C-1 | API uç tablosundaki izin sütunu roles-permissions.md kimliklerine geçirilir; `RolePermissions` haritası bu matristen tablo-temelli unit testle doğrulanır. | Backend Lead + Principal Architect | W3 Gün 12 |
| C-2 | `data-model.md` §5.13'teki tablolar belirtilen günlerde tasarlanıp migration PR'ına girer; hasarlı stok/`is_sellable` tasarımı (F-07) dahil. | Database Engineer + Principal Architect | §5.13'teki tarihler (en geç W6 Gün 29) |
| C-3 | Raporlama/dışa aktarma modül sınırı (F-08) ve platform yöneticisi (F-09) için ADR + tehdit modeli güncellemesi. | Principal Architect + Security Engineer | W11 Gün 51 |
| C-4 | OD-01, OD-04, OD-05 kararları ilgili günden önce alınır ve PRD/BACKLOG/mimari tek yönde güncellenir. | Chief Orchestrator → ürün sahibi | §5'teki tarihler |
| C-5 | Davet kabulü ve tenant oluşturma için RLS yolu (F-25) netleşir ve izolasyon testine eklenir. | Backend Lead + Security Engineer | W3 Gün 12 |
| C-6 | GitHub reposu + ruleset'ler (ürün sahibi onayıyla) ve eksik workflow'lar (`contracts`, `db`, `secrets`, `ci-gate`). | DevOps Engineer | W2 Gün 06–08 |
