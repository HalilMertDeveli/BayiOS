# ADR 0004: Ledger tabanlı stok, aynı transaction'da türetilmiş bakiyeler ve sıralı satır kilitleri

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-04

## Bağlam
Stok doğruluğu ürünün temel değeridir. Gereksinimler: denetlenebilir geçmiş (kim, ne zaman, neden), çok depo/lokasyon, sipariş onayında rezervasyon, sevkiyatta fiziksel çıkış, kısmi sevkiyat, transfer, sayım, eşzamanlı onaylarda fazla satış (oversell) olmaması. Doğrudan `UPDATE stock SET qty = …` modeli geçmişi kaybeder ve eşzamanlılık hatalarını gizler.

## Karar
1. `inventory.inventory_movements`: **append-only** ledger (UPDATE/DELETE trigger ve yetkiyle engelli). Tipler: `receipt, adjustment_in, adjustment_out, count_correction, transfer_out, transfer_in, shipment_out, return_in, damage_out`. Miktar işaretlidir. **Rezervasyon hareket değildir.**
2. `inventory.inventory_balances (tenant_id, variant_id, warehouse_id, location_id NULL, on_hand, reserved, version)`: hareketle **aynı transaction'da** güncellenir. `available = on_hand − reserved`. Benzersizlik `NULLS NOT DISTINCT`.
3. `inventory.stock_reservations`: sipariş satırı (ve lokasyonlu depoda lokasyon) başına; onayda oluşturulur, sevkiyatta tüketilir, iptalde serbest bırakılır.
4. Eşzamanlılık: READ COMMITTED + bakiye satırlarının `SELECT … FOR UPDATE` ile **deterministik sırada** (`variant_id, warehouse_id, location_id`) kilitlenmesi; kilit hiyerarşisi (sipariş başlığı → warehouse dokümanı → bakiyeler → rezervasyonlar). Deadlock/lock-timeout'ta tüm transaction idempotent şekilde yeniden denenir.
5. DB son savunması: `CHECK (reserved >= 0)` ve `CHECK (allow_negative OR (on_hand >= 0 AND reserved <= on_hand))`; `allow_negative` CHECK muafiyetidir; ürün sahibi kararı OD-09 (2026-11-09) ile stok eksiye düşmez — yeni satırlarda ve kötüleştiren her değişiklikte `false` (ayrıntı: inventory-and-orders.md §2.4).
6. Sipariş onayında çok satırlı rezervasyon **ya hep ya hiç**; sevkiyatta `shipment_out` + rezervasyon tüketimi; iptalde serbest bırakma. Algoritmalar: [inventory-and-orders.md](../architecture/inventory-and-orders.md).
7. Günlük mutabakat job'ı `on_hand = Σ ledger` ve `reserved = Σ açık rezervasyon` kontrol eder; fark kritik alarmdır.
8. Inventory modülü dışa bağımlılığı olmayan saf bir stok motorudur; Warehouse ve Orders onun Contracts komutlarını çağırır.

## Değerlendirilen alternatifler
| Alternatif | Neden seçilmedi |
|---|---|
| Yalnızca bakiye tablosu (geçmişsiz) | Denetim yok, hata ayıklanamaz, mutabakat imkânsız |
| Yalnızca ledger, bakiye sorgu anında `SUM` | Okuma maliyeti büyür; eşzamanlı rezervasyon kontrolü için yine kilitlenecek bir satır gerekir |
| Bakiyeyi trigger ile güncellemek | İş kuralları (rezervasyon, negatif stok) SQL'e dağılır, test/gözlem zor; EF ile çift yazma riski |
| Event sourcing (tam) | MVP için aşırı karmaşık; ledger zaten ihtiyaç duyulan denetim değerini veriyor |
| Optimistic concurrency (version kontrolü + retry) bakiyelerde | Yoğun SKU'larda yüksek çakışma ve retry fırtınası; pessimistic kilit tahmin edilebilir |
| SERIALIZABLE izolasyon | Serileştirme hatalarında geniş retry; sıralı kilitle aynı garanti daha ucuza |

## Sonuçlar
- (+) Tam denetim izi, kanıtlanabilir tutarlılık (invariant testleri), fazla satış yok.
- (+) Stok kartı (hareket listesi) doğal olarak mevcut.
- (−) Her stok yazımı iki tabloya yazar; kilit çekişmesi popüler SKU'larda darboğaz olabilir (izlenir; gerekirse rezervasyon kuyruğu).
- (−) Lokasyonlu depolarda rezervasyon lokasyona tahsis edilir; rezerveli stoğun lokasyonlar arası taşınması rezervasyonu taşımayı gerektirir.
- (−) Ledger büyür; ileride `occurred_at` ile partitioning ve arşiv (ayrı ADR).
- Eşzamanlılık testleri (Testcontainers, gerçek paralel bağlantılar) **zorunludur** ve CI'da çalışır.
