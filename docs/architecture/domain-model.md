# BayiOS Domain Modeli

> Sahip: Principal Software Architect · Durum: Taslak v1 · Tarih: 2026-11-04
> Tablolar: [data-model.md](data-model.md) · Stok/sipariş algoritmaları: [inventory-and-orders.md](inventory-and-orders.md)

## 0. Ortak yapı taşları (SharedKernel)

| Tip | Açıklama |
|---|---|
| `Entity<TId>` / `AggregateRoot<TId>` | Kimlik eşitliği; aggregate root domain event listesi tutar |
| `TenantId`, `UserId` | Strongly-typed `Guid` sarmalayıcıları; tüm tenant'a ait aggregate'lerde `TenantId` zorunlu ve değişmez |
| `Money` (VO) | `Amount: decimal`, `Currency: CurrencyCode`; farklı para birimleri toplanamaz |
| `Quantity` (VO) | `decimal`, ≥ 0 (hareket yönü ayrı tutulur); birimin `AllowsFraction=false` ise tam sayı olmalı |
| `Sku`, `Barcode` (VO) | Normalize edilmiş (trim, büyük harf / GTIN check digit doğrulaması opsiyonel) |
| `RowVersion` | Optimistic concurrency (ETag) |
| `DomainEvent` | `OccurredAt`, `TenantId`, `CorrelationId` |
| `Result<T>` / `DomainError` | Beklenen hatalar istisna değil, kodlu hata (`order.invalid_transition`) |

Genel kurallar:
- Aggregate'ler arası referans **yalnızca ID** ile.
- Bir transaction normalde tek aggregate değiştirir; istisnalar (sipariş onayı + rezervasyon, sevkiyat + ledger) bilinçli olarak tek transaction'da yapılır ve [inventory-and-orders.md](inventory-and-orders.md)'de tanımlıdır.
- Silme yerine arşivleme (`archived_at`) — sipariş/ledger geçmişi referanslarını korumak için.

---

## 1. Identity & Tenancy

### Aggregate: `Tenant`
- Alanlar: `Id`, `Name`, `Slug`, `Status (active|suspended|closed)`, `DefaultCurrency`, `Locale`, `TimeZone`, `Settings` (VO: `NegativeStockPolicy` — yalnızca `block`, OD-09: stok eksiye düşmez; `RequireOrderApproval`, `OrderNumberPrefix` ve roles-permissions.md §4'teki ayarlanabilir kurallar).
- Invariant'lar: `Slug` global benzersiz; `suspended` tenant'ta yazma komutları reddedilir (okuma serbest); en az bir aktif `owner` üyeliği bulunur.
- Event'ler: `TenantCreated`, `TenantSuspended`, `TenantSettingsChanged`.

### Aggregate: `Membership`
- Alanlar: `TenantId`, `UserId` (Supabase `sub`), `Role`, `Status (invited|active|disabled)`, `WarehouseScope?` (ileride).
- Invariant'lar: (TenantId, UserId) benzersiz; son `owner` düşürülemez/silinemez; `owner` rolünü yalnızca `owner` atayabilir.
- Event'ler: `MemberInvited`, `MemberJoined`, `MemberRoleChanged`, `MemberDisabled`.

### Aggregate: `Invitation`
- Alanlar: `Email`, `Role`, `TokenHash`, `ExpiresAt`, `AcceptedAt`.
- Invariant'lar: tek kullanımlık; süresi dolmuş davet kabul edilemez; token düz metin saklanmaz.

### Roller ve izinler (kodda)
`Role` enum: `owner, admin, manager, sales, warehouse_staff, viewer`. `Permission` sabitleri (`catalog.read`, `catalog.write`, `orders.approve`, `inventory.adjust`, ...). Harita kodda statik; kanonik matris `docs/product/roles-permissions.md`, kod eşlemesi [data-model §3](data-model.md#3-roller-ve-izinler-karar).

---

## 2. Billing & Entitlements

### Aggregate: `SubscriptionPlan` (platform, tenant'sız)
- `Code`, `Name`, `Features` (ör. `ai_order_parsing`, `multi_warehouse`, `mobile_scanning`), `Limits` (`max_users`, `max_products`, `max_warehouses`, `ai_parses_per_month`; Temel 999/999/999 — OD-02).

### Aggregate: `TenantSubscription`
- `PlanCode`, `Status (trialing|active|past_due|cancelled)`, `CurrentPeriodStart/End`.
- Event'ler: `SubscriptionChanged` → entitlement'ler yeniden hesaplanır.

### Aggregate: `TenantEntitlement`
- (TenantId, FeatureCode) → `Enabled`, `Limit?`, `Source (plan|override)`.
- Invariant: override'ın bitiş tarihi olabilir; etkin değer = override ?? plan.

### `UsageCounter`
- (TenantId, Metric, PeriodStart) → `Value`. Atomik artış (`UPDATE ... SET value = value + n`). Limit aşımı komutu reddeder (`entitlement.limit_exceeded`).

---

## 3. Catalog

### Aggregate: `Product`
- Alanlar: `Name`, `Description`, `CategoryId?`, `BrandId?`, `DefaultUnitId`, `TaxRate` (VO, %), `Status (active|inactive|archived)` (PRD CAT-02), `Attributes` (jsonb, ürün seviyesi özel alanlar), `RowVersion`.
- Çocuk entity: **`ProductVariant`** (aggregate içinde; ancak varyant ID'si diğer modüller tarafından referans alınan temel kimliktir)
  - `Sku`, `Name`/`OptionValues` (ör. renk=kırmızı, beden=42), `UnitId`, `Barcodes[]`, `Aliases[]`, `Attributes` (jsonb), `Status`, `PurchasePrice?` (bilgi amaçlı), `Weight?`.
- Invariant'lar:
  - Bir ürünün en az bir varyantı vardır (tek varyantlı ürün = "varsayılan varyant").
  - `Sku` tenant içinde benzersiz (arşivliler dahil).
  - `Barcode` tenant içinde benzersiz (bir barkod tek varyanta işaret eder).
  - `Attributes` tenant'ın `CustomFieldDefinition`'larına göre geçerlidir (tip, zorunluluk, seçenek listesi).
  - Stokta/rezervde miktarı olan veya açık siparişte geçen varyant **silinemez**, yalnızca arşivlenir.
- Event'ler: `ProductCreated`, `ProductUpdated`, `VariantAdded`, `VariantArchived`, `BarcodeAssigned`.

### Aggregate: `Category`
- Ağaç (`ParentId`, materialized `Path`). Invariant: döngü yok; derinlik ≤ 4 (PRD CAT-11); aynı ebeveyn altında ad benzersiz.

### Aggregate'ler: `Brand`, `Unit`, `Supplier`
- `Unit`: `Code` (adet, kg, m, koli), `AllowsFraction`. Tenant içinde kod benzersiz.
- `Supplier`: ad, vergi no (opsiyonel), iletişim. Varyant ↔ tedarikçi ilişkisi MVP sonrası.

### Aggregate: `CustomFieldDefinition`
- `EntityType (product|variant|customer)`, `Key` (snake_case, değişmez), `Label`, `DataType (text|number|bool|date|select|multiselect)`, `Options[]`, `Required`, `Filterable`, `SortOrder`.
- Invariant'lar: (TenantId, EntityType, Key) benzersiz; tip değişikliği yalnızca veri yoksa; `select` seçeneği silinirken kullanımdaysa reddedilir.

---

## 4. Pricing

### Aggregate: `PriceList`
- `Name`, `Currency`, `Kind (sales|purchase)`, `IsDefault`, `ValidFrom?`, `ValidTo?`, `PricesIncludeTax`, `Status`.
- Çocuk: `PriceListItem` (`VariantId`, `MinQuantity` (kademe), `UnitPrice: Money`).
- Invariant'lar: tenant başına para birimi başına en fazla bir `IsDefault` satış listesi; (PriceListId, VariantId, MinQuantity) benzersiz; fiyat ≥ 0; item para birimi listeninkine eşit.
- Event'ler: `PriceListPublished`, `PriceListItemsChanged`.

### Domain servisi: `PriceResolver`
Çözümleme sırası: müşteriye atanmış liste → bayi grubunun listesi → tenant varsayılan listesi. Geçerlilik tarihi ve kademe (en büyük `MinQuantity ≤ miktar`) uygulanır. Bulunamazsa `pricing.price_not_found` (sipariş satırı manuel fiyat izni gerektirir: `orders.override_price`).

> **Uygulanan durum (#78):** `IPriceResolver.QuoteAsync(PriceQuoteRequest(customerId?, date, lines[variantId, quantity], currency?))` — çağıranın UnitOfWork'ünde yalnızca okur. Her listede kalem yoksa taban liste − yüzde kuralı denenir (`BasePriceListId`, `DiscountPercent`; tek seviye). Fiyat bulunamaması hata değil, satır sonucudur: `status = no_price`, `unitPrice = null`; bilinmeyen müşteri/varyant 422 `validation.failed`. Sonuç satırı kaynak (`customer_price_list | dealer_group_price_list | default_price_list`), kural (`item | base_discount`), liste/kalem kimlikleri, kademe, `pricesIncludeTax` ve ürünün KDV oranını taşır; bayi grubunun `DefaultDiscountPercent` değeri bilgi amaçlı döner, fiyata uygulanmaz. Event'ler henüz yayınlanmıyor (outbox #79 sonrası).

---

## 5. Customers

### Aggregate: `Customer` (cari / bayi)
- `Code`, `Name`, `TaxNumber?`, `TaxOffice?`, `Phone?`, `Email?`, `DealerGroupId?`, `PriceListId?`, `PaymentTermDays?`, `CreditLimit?: Money`, `Status`, `Attributes` (jsonb), `WhatsAppNumbers[]` (AI eşleştirme için).
- Çocuk: `CustomerAddress` (`Type (billing|shipping)`, `IsDefault`, adres alanları).
- Invariant'lar: `Code` tenant içinde benzersiz; tür başına en fazla bir varsayılan adres; arşivli müşteriye yeni sipariş açılamaz.
- Event'ler: `CustomerCreated`, `CustomerUpdated`, `CustomerArchived`.

### Aggregate: `DealerGroup`
- `Name`, `PriceListId?`, `DefaultDiscountPercent?`. Ad tenant içinde benzersiz.

---

## 6. Inventory (saf stok motoru)

### Aggregate: `StockLedger` (kavramsal) → kayıt: `InventoryMovement`
- Değişmez (append-only). Alanlar: `VariantId`, `WarehouseId`, `LocationId?`, `Type`, `Quantity` (işaretli: giriş +, çıkış −), `UnitCost?`, `SourceType`/`SourceId`/`SourceLineId` (ör. `shipment`, `stock_count`), `Reason?`, `CorrelationId`, `CreatedBy`, `CreatedAt`, `TransferGroupId?`.
- Tipler ve işaret:

| Tip | İşaret | Kaynak |
|---|---|---|
| `receipt` | + | Mal kabul |
| `adjustment_in` / `adjustment_out` | + / − | Manuel düzeltme (sebep zorunlu) |
| `count_correction` | ± | Sayım farkı |
| `transfer_out` / `transfer_in` | − / + | Depo/lokasyon transferi (aynı `TransferGroupId`) |
| `shipment_out` | − | Sevkiyat onayı |
| `return_in` | + | Müşteri iadesi |
| `damage_out` | − | Hasar/fire |

- Rezervasyon **hareket değildir**.
- Invariant'lar: kayıt güncellenmez/silinmez (düzeltme = ters hareket); `Quantity ≠ 0`; işaret tiple uyumlu.

### Aggregate: `InventoryBalance` (türetilmiş, ledger ile aynı transaction'da)
- Anahtar: (TenantId, VariantId, WarehouseId, LocationId?). Alanlar: `OnHand`, `Reserved`, `Version`. `Available = OnHand − Reserved`.
- Invariant'lar: `Reserved ≥ 0`; `OnHand ≥ 0` ve `Reserved ≤ OnHand` (OD-09: stok eksiye düşmez; tek istisna OD-09 öncesinden kalmış, yalnızca iyileşebilen negatif satır); `OnHand = Σ ledger.Quantity` (mutabakat job'ı ile doğrulanır).

### Aggregate: `StockReservation`
- `OrderId`, `OrderLineId`, `VariantId`, `WarehouseId`, `LocationId?`, `Quantity`, `Status (active|consumed|released)`, `ConsumedQuantity`.
- Invariant'lar: aktif rezervasyonların toplamı = ilgili bakiyelerin `Reserved`'ı; `ConsumedQuantity ≤ Quantity`.

### Public komutlar (Contracts)
`RecordReceipt`, `RecordAdjustment`, `RecordTransfer`, `RecordCountCorrection`, `ReserveForOrder` (çok satırlı, ya hep ya hiç), `ReleaseReservations`, `ConsumeReservationsForShipment`, `RecordReturn`, `RecordDamage`; sorgular: `GetAvailability`, `GetBalances`, `GetMovements`.

### Event'ler
`StockReceived`, `StockAdjusted`, `StockReserved`, `ReservationReleased`, `StockShipped`, `StockBelowThreshold` (eşik varsa → outbox bildirim).

---

## 7. Warehouse

### Aggregate: `Warehouse`
- `Code`, `Name`, `Address`, `UsesLocations: bool`, `IsDefault`, `Status`.
- Çocuk: `WarehouseLocation` (`Code` ör. `A-01-03`, `Barcode?`, `PickSequence`, `IsActive`).
- Invariant'lar: kod tenant içinde benzersiz; lokasyon kodu depo içinde benzersiz; stoğu olan lokasyon pasifleştirilemez; `UsesLocations` yalnızca depo boşken değiştirilebilir.

### Aggregate: `StockCount` (sayım)
- `WarehouseId`, `Scope (full|partial)`, `Status (open|submitted|applied|cancelled)`, satırlar: `VariantId`, `LocationId?`, `ExpectedQty` (snapshot), `CountedQty`.
- Invariant'lar: `applied` sonrası değişmez; uygulama anında fark **o anki** bakiyeye göre hesaplanır (bkz. algoritma).

### Aggregate: `PickingTask`
- `Type (pick|putaway)`, `OrderId`, `WarehouseId`, `Status (open|in_progress|completed|completed_with_shortage|cancelled)`, `AssignedTo?`, satırlar: `OrderLineId`, `VariantId`, `LocationId?`, `QtyToPick`, `QtyPicked`, `QtyShort`, `ShortageReason?`.
- Invariant'lar: `QtyPicked ≤ QtyToPick`; barkod okutulan varyant satırla eşleşmeli; tamamlanınca değişmez.
- Event'ler: `PickingStarted`, `PickingCompleted`.

### Aggregate: `Shipment`
- `OrderId`, `WarehouseId`, `Status (draft|confirmed|cancelled)`, `Carrier?`, `TrackingNumber?`, `ShippedAt?`, satırlar: `OrderLineId`, `VariantId`, `LocationId?`, `Quantity`.
- Invariant'lar: satır miktarı ≤ ilgili sipariş satırının kalan sevk edilebilir miktarı; onaylanan sevkiyat değişmez (iade ayrı akış).
- Event'ler: `ShipmentConfirmed` (aynı tx'te Orders tarafından işlenir), outbox: `ShipmentConfirmedNotification`.

> **Uygulanan durum (JOB 10, #36–#39):** domain event'leri yerine modül sözleşmeleri, aynı transaction'da: Orders → Warehouse `IOrderFulfillmentListener` (onayda görev oluştur; iptal/short-close'da açık görev ve onaylanmamış sevkiyatı iptal et, toplananlar için `putaway` görevi), Warehouse → Orders `IOrderFulfillment` (start_picking, pack, report_shortage, ship). `PickingTask` satırı rezervasyon (sipariş satırı + lokasyon) başınadır; `AssignedTo` null = havuz. `Shipment` durumları `draft → packed (isteğe bağlı paket kontrolü) → confirmed`, ayrıca `cancelled`; satır `PickedQuantity`, `Quantity` (sevk edilecek), `PackedQuantity`, `ShortQuantity` taşır; koliler `ShipmentPackage`. Görev ve sevkiyat geçişlerinin tek yeri `FulfillmentStateMachine`. Outbox: `warehouse.picking_task_created.v1`, `warehouse.picking_task_assigned.v1`, `warehouse.picking_completed.v1`, `warehouse.shortage_reported.v1`, `warehouse.shipment_confirmed.v1`, `warehouse.shipment_cancelled.v1` (#86), `warehouse.putaway_created.v1`. Ayrıntı: [inventory-and-orders §8](inventory-and-orders.md#8-uygulama-durumu--depo-hazırlama-ve-sevkiyat-job-10--36-37-38-39).

---

## 8. Orders

### Aggregate: `Order`
- Alanlar: `Number` (tenant içinde sıralı, ör. `SIP-2026-000123`, önek tenant ayarı — PRD ORD-14), `CustomerId`, `WarehouseId`, `Status`, `Source (manual|whatsapp_ai|import|mobile)`, `Currency`, `PriceListId?`, `ShippingAddress` (snapshot VO), `Notes`, `Totals` (VO: ara toplam, iskonto, vergi, genel toplam), `RequestedDeliveryDate?`, `FulfillmentStatus (none|partial|full)`, `RowVersion`.
- Çocuk: `OrderLine` (`VariantId`, `SkuSnapshot`, `NameSnapshot`, `Quantity`, `UnitPrice: Money` (snapshot), `DiscountPercent`, `TaxRate`, `QtyShipped`, `QtyCancelled`).
- Durum makinesi: [inventory-and-orders.md §3](inventory-and-orders.md#3-sipariş-durum-makinesi).
- Invariant'lar:
  - Satırlar yalnızca `Draft` (ve `PendingApproval`, onay talebi geri çekilerek) durumunda değiştirilebilir.
  - En az bir satır olmadan `PendingApproval`'a geçilemez.
  - `QtyShipped + QtyCancelled ≤ Quantity`.
  - Fiyatlar onay anında dondurulur (snapshot); sonraki fiyat listesi değişiklikleri etkilemez.
  - `Cancelled` ve `Completed` terminal durumdur.
  - AI kaynaklı sipariş, bir insan tarafından onaylanmadan `Approved` olamaz (`Source=whatsapp_ai` ise `ApprovedBy` zorunlu, sistem kullanıcısı olamaz).
- Event'ler: `OrderCreated`, `OrderSubmitted`, `OrderApproved` (→ Warehouse picking task), `OrderPickingStarted`, `OrderPacked`, `OrderShipped`, `OrderPartiallyShipped`, `OrderCompleted`, `OrderCancelled` (→ rezervasyon iadesi, picking task iptali).

### Entity: `OrderStatusHistory`
- Her geçiş: `FromStatus`, `ToStatus`, `ChangedBy`, `ChangedAt`, `Reason?`. Append-only.

> **Uygulanan durum (#32/#33/#35):** satırlar yalnızca `Draft`'ta değişir (`PendingApproval`'da önce `withdraw`). Fiyat snapshot'ı onayda değil taslağın her düzenlemesinde alınır; onay snapshot'ı yeniden çözmeden dondurur. Domain event'leri yerine outbox mesajları: `orders.approved.v1`, `orders.cancelled.v1`, `orders.shipped.v1`. `OrderStatusHistory` her eylemi (`create` dahil) `Action` ile kaydeder. `OrderShipment` = onaylanan Warehouse `Shipment`'ının sipariş tarafı kaydı (aynı kimlik, JOB 10). Depo eksik bildirimi siparişi `HasShortage` + `report_shortage` zaman çizelgesi satırıyla işaretler (durum değişmez). Ayrıntı: [inventory-and-orders §7](inventory-and-orders.md#7-uygulama-durumu--siparişler-job-11--32-33-35).

---

## 9. Imports

### Aggregate: `ImportJob`
- `Type (products|prices|customers|opening_stock)`, `FileObjectPath`, `FileName`, `Status (uploaded|validating|validated|failed|importing|completed|cancelled)`, `Mapping` (kolon eşlemesi, jsonb), `Stats` (toplam/geçerli/hatalı/işlenen), `Mode (create_only|update_only|upsert)`, `DryRun`.
- Çocuk: `ImportRow` (`RowNumber`, `Raw` jsonb, `Normalized` jsonb, `Status (valid|invalid|imported|skipped)`, `Errors[]`, `TargetId?`).
- Invariant'lar: içe aktarım yalnızca `validated` durumdan başlar ve kullanıcı onayı ister; satır işleme idempotent (`TargetId` dolu satır tekrar işlenmez); ilgili modül komutları üzerinden yazılır.

---

## 10. Integrations

### Aggregate: `Integration`
- `Kind (whatsapp|...)`, `Status`, `Config` (jsonb, gizli değerler değil — sırlar secret store'da, burada referans).

### Aggregate: `AiParseRequest`
- `Channel (whatsapp_paste|whatsapp_webhook)`, `RawTextRef` / `RawText` (şifreli veya kısıtlı saklama), `CustomerId?` (gönderen numaradan eşleşen), `Status (queued|parsing|parsed|needs_review|approved|rejected|failed)`, `Model`, `PromptVersion`, `ParsedPayload` (jsonb; modelin yapılandırılmış çıktısı), `MatchedLines` (jsonb: her satır için aday varyantlar, eşleşme yöntemi, güven skoru), `OrderId?`, `ReviewedBy?`, `TokenUsage`.
- Invariant'lar:
  - Model çıktısı yalnızca **metinden çıkarılan** ürün ifadesi, miktar, birim ve notları içerir; **fiyat, stok ve SKU uydurma yasaktır** — SKU/fiyat/stok değerleri yalnızca deterministik eşleştirme ve Pricing/Inventory'den gelir.
  - Eşleşmeyen veya düşük güvenli satır, kullanıcı seçimi olmadan siparişe dönüşmez.
  - Onay sonucu en fazla `Draft` sipariş oluşturur; siparişin `Approved` olması normal insan onayı ister.
- Event'ler: `AiParseCompleted`, `AiParseApproved` (→ Orders `CreateDraftOrder`).

---

## 11. Audit & Notifications

### `AuditEvent` (append-only)
- `ActorUserId`, `Action` (ör. `order.approved`), `EntityType`, `EntityId`, `Changes` (jsonb, hassas alanlar maskelenmiş), `Ip?`, `UserAgent?`, `CorrelationId`, `OccurredAt`.

### Aggregate: `DeviceToken`
- `UserId`, `Platform (android|ios|web)`, `Token`, `LastSeenAt`. Invariant: token global benzersiz (cihaz başka kullanıcıya geçerse yeniden atanır).

### `Notification`
- `UserId`, `Type`, `Payload`, `ReadAt?`. Outbox'tan gelen integration event'lerden üretilir; FCM gönderimi outbox dispatcher'ında.

---

## 12. Modüller arası event akışı (özet)

| Event | Üreten | Tüketen | Teslim |
|---|---|---|---|
| `OrderApproved` | Orders | Warehouse (picking task oluştur) | Aynı transaction |
| `OrderCancelled` | Orders | Warehouse (açık picking task iptal) | Aynı transaction |
| `ShipmentConfirmed` | Warehouse | Orders (QtyShipped, durum) | Aynı transaction |
| `PickingCompleted` | Warehouse | Orders (`Picking`→`Packed` opsiyonel otomatik) | Aynı transaction |
| `OrderStatusChanged` | Orders | Notifications (FCM, SignalR) | Outbox |
| `StockBelowThreshold` | Inventory | Notifications | Outbox |
| `AiParseCompleted` | Integrations | Notifications (inceleme bekliyor) | Outbox |
| `ImportJobCompleted` | Imports | Notifications | Outbox |
| `SubscriptionChanged` | Billing | Billing (entitlement yeniden hesap), cache invalidation | Aynı transaction + outbox |
