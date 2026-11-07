# Runbook — Stok tutarsızlığı incelemesi

**Tetik:** `ledger-reconciliation.sql` satır döndürdü; kullanıcı "ekranda X görünüyor, rafta Y var" diyor; `BayiOSNegativeStockConfirmed*` veya `BayiOSReservationShortageSpike` alarmı; sevkiyat sonrası beklenmeyen eksi bakiye.
**Önem:** defter/bakiye uyumsuzluğu **SEV1** (AUTOPILOT §5 durdurma koşulu). Fiziksel sayım farkı (sistem doğru, raf farklı) iş sürecidir — SEV3.

Değişmez kurallar (ADR 0004): tek doğruluk kaynağı `inventory.inventory_movements` (append-only; UPDATE/DELETE/TRUNCATE trigger'la reddedilir). `inventory.inventory_balances` türetilmiştir ve aynı transaction'da güncellenir. Rezervasyon sipariş onayında, fiziksel çıkış sevkiyatta. **Düzeltme her zaman yeni hareketle yapılır — defter veya bakiye elle güncellenmez.**

## 1. Mutabakat sorgusu
```bash
# Tüm tenant'lar (admin/BYPASSRLS, salt okunur transaction):
psql "<admin url>" -v ON_ERROR_STOP=1 -c 'BEGIN READ ONLY' -f infra/ops/sql/ledger-reconciliation.sql
# Tek tenant, bayios_app olarak (RLS o tenant'la sınırlar):
#   BEGIN READ ONLY; SELECT set_config('app.tenant_id', '<tenant uuid>', true); \i infra/ops/sql/ledger-reconciliation.sql
```
Her satır bir tutarsızlıktır: `kind=on_hand` → `balance.on_hand ≠ Σ movements.quantity`; `kind=reserved` → `balance.reserved ≠ Σ açık rezervasyon (quantity − consumed − released, status=active)`; anahtar (tenant, variant, warehouse, location). Boş sonuç = bakiyeler deftere uyuyor. Çıktıda yalnızca kimlik ve miktar vardır.

## 2. Sınıflandırma
| Durum | Muhtemel neden | Yapılacak |
|---|---|---|
| Mutabakat **boş**, kullanıcı farkı görüyor | Fiziksel fark (sayılmamış giriş, kayıp, yanlış raf) veya beklenen rezervasyon (`available = on_hand − reserved`) | Hareket geçmişini göster (`GET /api/v1/inventory/movements?variantId=…`); sayım yap; fark **sayım düzeltmesi/adjustment** ile girilir (audit'li) |
| `on_hand` uyumsuz | Bakiyeyi defter dışı değiştiren kod/SQL (hata), yarım kalmış elle müdahale, restore sonrası eksik tablo | **SEV1.** Yazmayı durdurmayı değerlendir; son deploy'u kontrol et (`rollback.md` §1); kök neden bulunmadan bakiyeye dokunma |
| `reserved` uyumsuz | Rezervasyon bırakma/tüketme hatası, iptal/kısmi sevk akışında hata | **SEV1/2.** Etkilenen siparişleri bul (aşağıdaki sorgu); kod düzeltmesi + testi |
| Mutabakat boş ama beklenmedik **eksi** bakiye | Tenant politikası `warn`/`allow` ve eksi stok onaylandı | Audit: `inventory.negative_stock_confirmed` olayları; politika doğru mu (`identity.tenant_settings.negative_stock_policy`)? Gerekirse tenant ile `block`'a geçiş |
| Rezervasyon reddi artışı | Gerçek stok yok (mal kabul gecikti), içe aktarma başarısız, politika değişti | `bayios_imports_failed_total`, son `inventory.receipt_posted` audit'leri; tenant ile konuş |

## 3. İnceleme sorguları (admin, `BEGIN READ ONLY`)
```sql
-- Bir anahtarın defter geçmişi, sırayla (balance_after her hareketten sonraki bakiye olmalı):
SELECT occurred_at, type, quantity, balance_after, source_type, source_id, correlation_id,
       sum(quantity) OVER (ORDER BY occurred_at, id) AS running_total
FROM inventory.inventory_movements
WHERE tenant_id = :'tenant' AND variant_id = :'variant' AND warehouse_id = :'warehouse'
ORDER BY occurred_at, id;

-- balance_after zinciri kırılan ilk hareket (running_total ≠ balance_after):
SELECT * FROM (
  SELECT id, occurred_at, type, quantity, balance_after,
         sum(quantity) OVER (PARTITION BY tenant_id, variant_id, warehouse_id, location_id ORDER BY occurred_at, id) AS running_total
  FROM inventory.inventory_movements WHERE tenant_id = :'tenant') m
WHERE running_total <> balance_after ORDER BY occurred_at LIMIT 20;

-- Açık rezervasyonu olan ama siparişi onaylı/toplamada olmayan satırlar (sızan rezervasyon):
SELECT r.id, r.order_id, r.order_line_id, r.quantity - r.consumed_quantity - r.released_quantity AS open, o.status
FROM inventory.stock_reservations r JOIN orders.orders o ON o.id = r.order_id AND o.tenant_id = r.tenant_id
WHERE r.tenant_id = :'tenant' AND r.status = 'active' AND o.status NOT IN ('approved', 'picking', 'packed');

-- Hareketleri üreten istekler / işler: correlation_id → log satırları ve audit olayları
SELECT action, entity_type, entity_id, actor_user_id, occurred_at FROM audit.audit_events
WHERE tenant_id = :'tenant' AND correlation_id = :'correlation' ORDER BY occurred_at;
```
`balance_after` zinciri sayım düzeltmeleri için (`count_correction`) de geçerlidir: `quantity` farktır, `balance_after` sayım sonucudur.

## 4. Düzeltme
1. Kök neden (kod hatası) önce düzeltilir: `fix/` dalı + **eşzamanlılık/regresyon testi** (CLAUDE.md: eşzamanlılık testleri zorunlu).
2. Veri düzeltmesi yalnızca **telafi hareketiyle**: tenant'ın yetkili kullanıcısı (veya onaylı operatör aracı) `POST /api/v1/inventory/adjustments` ile farkı girer; neden alanına olay issue numarası yazılır. Bakiye tablosuna doğrudan UPDATE **yapılmaz** (bakiye defterden türetilir; elle yazmak bir sonraki mutabakatta yeniden uyumsuzluk üretir).
3. Bakiye satırı defterle uyumsuz kaldıysa (kod hatası bakiyeyi defter dışı değiştirdiyse) yeniden hesaplama ayrı, gözden geçirilmiş bir migration/operatör betiğiyle, ürün sahibi onayıyla ve önce bir geri yükleme kopyasında (`../backup-and-restore.md`) denenerek yapılır.
4. Doğrulama: mutabakat sorgusu 0 satır; ilgili tenant'a açıklama.
