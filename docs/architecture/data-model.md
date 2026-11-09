# BayiOS Veri Modeli (Taslak)

> Sahip: Principal Software Architect · Durum: Taslak v1 · Tarih: 2026-11-04
> Kaynak: EF Core 10 migration'ları ([ADR 0005](../adr/0005-ef-core-migrations.md)). Bu belge tasarım taslağıdır; nihai şema migration'lardır.

## 1. Genel kurallar

| Kural | Karar |
|---|---|
| Veritabanı | Supabase PostgreSQL 17 |
| Şemalar | **Modül başına bir PostgreSQL şeması**; `public` şeması iş verisi için **kullanılmaz** |
| Adlandırma | `snake_case`, tablo adları çoğul; EF Core'da snake_case adlandırma konvansiyonu |
| PK | `id uuid` (UUIDv7, uygulamada üretilir). Platform referans tablolarında doğal anahtar olabilir |
| Tenant | Tenant'a ait her tabloda `tenant_id uuid NOT NULL` |
| Tenant-güvenli FK | Modül içi FK'ler **bileşik**: `(tenant_id, x_id) → parent(tenant_id, id)`; bunun için parent'ta `UNIQUE (tenant_id, id)`. Böylece farklı tenant'ın satırına referans DB seviyesinde imkânsızdır |
| Modüller arası referans | **FK yok**, yalnızca ID (çıkarılabilirlik + modül sınırı). Bütünlük uygulamada (Contracts üzerinden doğrulama) ve arşivleme (hard delete yok) ile sağlanır |
| Zaman damgaları | `created_at timestamptz NOT NULL DEFAULT now()`, `updated_at timestamptz`, `created_by uuid`, `updated_by uuid` (tenant tablolarında) |
| Silme | `archived_at timestamptz NULL` (soft); iş tablolarında hard delete yok |
| Concurrency | Düzenlenebilir aggregate'lerde `row_version bigint NOT NULL DEFAULT 1` (uygulama artırır; ETag kaynağı) |
| Para | `numeric(18,4)` + `currency char(3)` |
| Miktar | `numeric(18,3)` |
| Esnek alanlar | `attributes jsonb NOT NULL DEFAULT '{}'` (bkz. §4) |
| Uzantılar | `pg_trgm` (bulanık arama), `citext` gerekirse; `extensions` şemasında |
| Benzersizlik + NULL | Nullable kolon içeren benzersizliklerde `UNIQUE NULLS NOT DISTINCT` (PG15+) |

Şemalar: `identity`, `billing`, `catalog`, `pricing`, `customers`, `inventory`, `warehouse`, `orders`, `imports`, `integrations`, `audit`, `notifications`, `platform`.

## 2. Veritabanı rolleri, yetkiler ve RLS

### 2.1 Roller

| Rol | Amaç | Özellikler |
|---|---|---|
| `bayios_migrator` | Migration'ları çalıştırır; tüm iş tablolarının **sahibi** | Yalnızca CI/deploy adımı kullanır |
| `bayios_app` | API ve worker'ın çalışma zamanı rolü | `NOSUPERUSER NOBYPASSRLS`, tablo sahibi **değil**; iş şemalarında `SELECT/INSERT/UPDATE/DELETE` (ledger ve audit tablolarında yalnızca `SELECT/INSERT`) |
| `anon`, `authenticated` (Supabase) | Supabase istemci rolleri | İş şemalarında **USAGE dahil hiçbir yetki yok**; migration'da açıkça `REVOKE ALL` |

### 2.2 Standart RLS kalıbı (her tenant tablosu)

```sql
ALTER TABLE catalog.products ENABLE ROW LEVEL SECURITY;
ALTER TABLE catalog.products FORCE ROW LEVEL SECURITY;   -- sahip rol bile politikaya tabi

CREATE POLICY tenant_isolation ON catalog.products
  FOR ALL TO bayios_app
  USING      (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
```

- API her transaction başında: `SELECT set_config('app.tenant_id', @tenantId, true), set_config('app.user_id', @userId, true);` (`true` = transaction-local, `SET LOCAL` eşdeğeri ve parametre alabilir).
- `app.tenant_id` ayarlanmamışsa ifade `NULL` döner → hiçbir satır görünmez/yazılamaz (fail-closed).
- RLS'li her tablo için integration testi: tenant A bağlamında tenant B satırı okunamaz/yazılamaz.

### 2.3 RLS istisnaları

| Tablo | Politika |
|---|---|
| `identity.tenants` | `id = app.tenant_id` **veya** `id IN (SELECT tenant_id FROM identity.memberships WHERE user_id = app.user_id)` (kullanıcının tenant listesi) — yalnızca okuma; yazma yalnızca `id = app.tenant_id` |
| `identity.memberships` | `tenant_id = app.tenant_id` **veya** `user_id = app.user_id` (tenant çözümleme, tenant bilinmeden yapılır) — yalnızca okuma; yazma yalnızca `tenant_id = app.tenant_id` |
| `identity.user_profiles` | `user_id = app.user_id` veya aynı tenant üyeleri (okuma); yazma yalnızca `user_id = app.user_id` |
| `billing.subscription_plans` | Tenant'sız referans veri; RLS yok, `bayios_app` yalnızca `SELECT` |
| `platform.outbox_messages`, `platform.jobs` | Çok tenant'lı dispatcher tarafından okunur. RLS **yok**; yalnızca `bayios_app` erişir; worker bir işi işlerken iş öncesi `app.tenant_id`'yi o kaydın tenant'ına ayarlar, böylece iş verisine erişim yine RLS'li olur. Tenant bağlamı içindeyken başka tenant'a kayıt yazmayı tetikleyici engeller (§5.12) |
| `platform.idempotency_keys` | `tenant_id = app.tenant_id` **veya** (`tenant_id IS NULL` ve `user_id = app.user_id`) — tenant'sız uçların (ör. `POST /tenants`) anahtarları kullanıcıya kapsanır; yazma her zaman `user_id = app.user_id` (§5.12) |

### 2.4 EF Core tarafı (birincil zorlama)
- Tüm `ITenantOwned` entity'lerde global query filter: `e => e.TenantId == _tenantContext.TenantId`.
- `SaveChanges` interceptor: eklenen entity'ye `tenant_id` damgalar; değiştirilen/silinen entity'nin `tenant_id`'si bağlamdakiyle farklıysa istisna (ve güvenlik logu).
- `IgnoreQueryFilters()` kullanımı ArchitectureTests ile izin listesine bağlanır.

## 3. Roller ve izinler (karar)

**Karar: Roller ve izinler kodda tanımlanır; tabloda yalnızca üyeliğin rol kodu tutulur.**

- `identity.memberships.role text NOT NULL CHECK (role IN ('owner','admin','manager','sales','warehouse_staff','viewer'))`.
- `Permissions` sabitleri ve `RolePermissions` haritası `BayiOS.SharedKernel` / Identity modülünde statiktir; `GET /api/v1/me/permissions` istemcilere etkin izinleri döner.

Gerekçe: MVP'de sabit 6 rol var; harita kodla birlikte sürümlenir, code review'dan geçer, unit test ile doğrulanır (ör. "viewer hiçbir write iznine sahip değil"), DB round-trip'i ve cache invalidation sorunu yoktur; tenant'ın yanlış yapılandırmayla kendini kilitlemesi engellenir. Özel rol ihtiyacı doğarsa `identity.tenant_roles (tenant_id, code, permissions text[])` eklenir ve kodda tanımlı roller "sistem rolleri" olarak kalır (geriye uyumlu genişleme).

**Kanonik kaynak (Phase 0 kapısı kararı):** Rol × izin matrisi ve izin kimlikleri `docs/product/roles-permissions.md` §3–§4'tür. `Permissions` sabitleri o belgedeki kimliklerle birebir adlandırılır (ör. `orders.approve`, `inventory.count.perform`, `tasks.pick`, `prices.read.cost`). Koşullu izinler (K) `identity.tenant_settings` kurallarıyla (roles-permissions §4), "yalnızca kendi kaydı" (O) kuralları handler'da değerlendirilir. Bu belgenin ilk taslağındaki kaba izin tablosu roles-permissions.md ile çeliştiği için kaldırıldı (ör. `sales` için `orders.approve` koşullu açıktır; `manager` `prices.write` ve ürün `imports.run` iznine sahip değildir).

Taslak grup adları → kanonik izinler (API uç tablosu [api-strategy §12](api-strategy.md#12-mvp-uç-listesi-ilk-sürüm) W3 Gün 12 sözleşme PR'ında bu eşlemeyle güncellenir):

| Taslak grup | Kanonik izin(ler) — roles-permissions.md |
|---|---|
| `tenant.manage` | `tenant.settings.manage`, `catalog.fields.manage` (özel alan/kategori/birim), `warehouses.manage` (depo/raf) |
| `members.manage` | `members.read`, `members.invite`, `members.manage`, `ownership.transfer` |
| `billing.manage` | `billing.read`, `billing.manage` |
| `pricing.read` / `pricing.write` | `prices.read.sell`, `prices.read.cost` / `prices.write`, `pricelists.assign` |
| `inventory.receive` | `inventory.receive`, `inventory.receive.approve` |
| `inventory.count` | `inventory.count.manage`, `inventory.count.perform` |
| `inventory.transfer` | `inventory.transfer`, `inventory.transfer.receive` |
| `orders.write` | `orders.create`, `orders.edit.draft`, `orders.edit.approved` |
| `orders.override_price` | `orders.price.override` |
| `fulfillment.pick` / `fulfillment.ship` | `tasks.read.own`, `tasks.read.all`, `tasks.claim`, `tasks.assign`, `tasks.pick`, `tasks.pack`, `tasks.report_shortage` / `orders.ship` |
| `imports.run` | `imports.run`, `imports.run.stock`, `imports.rollback` |
| `ai.parse` / `ai.review` | `whatsapp.parse` (taslak sipariş oluşturma ayrıca `orders.create`) |
| `reports.read` | `reports.read`, `reports.export`, `data.export.full` |

## 4. Özel alanlar (karar)

**Karar: EAV yerine `attributes jsonb` + tenant'ın alan tanımlarına göre uygulama doğrulaması + GIN index.**

- `catalog.custom_field_definitions` hangi anahtarların hangi tipte olduğunu tanımlar.
- `catalog.product_variants.attributes`, `catalog.products.attributes`, `customers.customers.attributes` alanları kaydetmeden önce Catalog modülündeki `CustomFieldValidator` ile doğrulanır (bilinmeyen anahtar reddedilir, tip/zorunluluk/seçenek kontrolü).
- Index: `CREATE INDEX ... USING gin (attributes jsonb_path_ops);` → `attributes @> '{"renk":"kirmizi"}'` sorguları. Sık sıralanan/aralık sorgulanan alanlar için gerektiğinde ifade index'i (`((attributes->>'genislik')::numeric)`).
- Gerekçe: EAV; join patlaması, tipsiz değer kolonları ve yavaş filtreleme getirir. JSONB tek satırda okunur, GIN ile içerme sorguları hızlıdır, tenant başına farklı şema ihtiyacını karşılar. Bedel: DB seviyesinde tip zorlaması yok → uygulama doğrulaması + import doğrulaması zorunlu; tanım değişikliklerinde veri göçü job'ı.

## 5. Tablolar

Gösterim: **PK**, FK (modül içi, bileşik tenant FK), UQ = unique, IX = index. Tüm tenant tablolarında ayrıca `created_at/updated_at/created_by/updated_by` vardır, tekrar edilmez. "RLS: std" = §2.2 kalıbı.

### 5.1 `identity`

**identity.tenants** — RLS: istisna (§2.3)
| Kolon | Tip | Not |
|---|---|---|
| id | uuid **PK** | |
| name | text NOT NULL | |
| slug | text NOT NULL | UQ (global) |
| status | text NOT NULL | CHECK in (active, suspended, closed) |
| default_currency | char(3) NOT NULL | 'TRY' |
| locale | text NOT NULL | 'tr-TR' |
| time_zone | text NOT NULL | 'Europe/Istanbul' |
| sector | text NOT NULL | motorcycle_parts, led_electronics, general (TEN-03) |
| logo_object_path, tax_number, tax_office, phone | text NULL | TEN-04 |
| address | jsonb NULL | TEN-04 |
| row_version | bigint | |

> **Uygulanan durum (migration `BusinessSettings`, #14):** `tenants` tablosuna `logo_object_path`, `tax_number`, `tax_office`, `phone` eklendi; `default_currency` CHECK `^[A-Z]{3}$`. `PATCH /tenant` (If-Match = `tenants.row_version`) ad, sektör, para birimi ve bu alanları günceller; logo yolu tenant önekli olmalıdır (`{tenantId}/….png|jpg|jpeg|svg`), dosya yükleme henüz yok. `address` henüz yok. `tenant_settings` tablosuna `default_tax_rate numeric(5,2) NOT NULL DEFAULT 20` (CHECK 0–100) ve `prices_include_tax_display bool NOT NULL DEFAULT false` eklendi. Migration `OrderSettings` (#92): `require_order_approval bool NOT NULL DEFAULT true`, `order_number_prefix text NOT NULL DEFAULT 'SIP'` + CHECK `ck_tenant_settings_order_number_prefix` (`^[A-Z][A-Z0-9]{0,9}$`); `GET/PATCH /tenant/settings` ile.

**identity.tenant_settings** — RLS: std · PK `tenant_id`
| negative_stock_policy text NOT NULL DEFAULT 'block' CHECK in (block, warn, allow) — PRD TEN-12; `inventory_balances.allow_negative` = (policy ≠ 'block'); `warn`'da kullanıcı teyidi uygulamada istenir · require_order_approval bool DEFAULT true · sales_can_approve_orders bool DEFAULT true · sales_max_discount_percent numeric(5,2) DEFAULT 0 · show_cost_to_manager bool DEFAULT false · show_cost_to_viewer bool DEFAULT false · staff_can_assign_barcode bool DEFAULT false · staff_can_approve_receipt bool DEFAULT false · require_2fa bool DEFAULT false (roles-permissions §4 kuralları) · auto_create_picking_task bool DEFAULT true (WHT-01) · prices_include_tax_display bool DEFAULT false · default_tax_rate numeric(5,2) DEFAULT 20 (TEN-05) · order_number_prefix text DEFAULT 'SIP' (ORD-14) · low_stock_notifications bool · settings jsonb (diğer) · row_version |

**identity.user_profiles** — RLS: istisna · PK `user_id uuid` (= Supabase `auth.users.id` / JWT `sub`; **FK yok**, auth şeması Supabase'e ait)
| email text (yalnızca görüntüleme, Supabase'den senkron) · full_name text · phone text · locale text · last_seen_at |

**identity.memberships** — RLS: istisna
| Kolon | Tip | Not |
|---|---|---|
| id | uuid **PK** | |
| tenant_id | uuid NOT NULL | FK → tenants(id) |
| user_id | uuid NOT NULL | |
| role | text NOT NULL | CHECK (6 rol) |
| status | text NOT NULL | invited, active, disabled |
| UQ | (tenant_id, user_id) | |
| IX | (user_id, status) | tenant çözümleme sorgusu |

**identity.invitations** — RLS: std
| id **PK** · tenant_id · email citext · role · token_hash bytea UQ · expires_at · accepted_at · invited_by |
| UQ (tenant_id, email) WHERE accepted_at IS NULL (kısmi) |

> **Uygulanan durum (migration `MemberInvitations`, #129):** `identity.invitations` — `email text` (citext yerine kırpılmış
> küçük harf + CHECK `email = lower(btrim(email))`), `role` (CHECK: owner hariç 5 rol), `token_hash bytea` (CHECK 32 bayt,
> UQ `ux_invitations_token_hash`; token = 32 rastgele bayt base64url, düz metin saklanmaz), `status` (pending, accepted,
> revoked, expired — `expired` yalnızca süresi geçmiş bekleyen davet yenisiyle değiştirilirken yazılır; aksi hâlde
> `expires_at`'ten türetilir), `expires_at` (varsayılan +7 gün), `token_count`, `accepted_at/by`, `revoked_at/by`,
> `created_by` (= davet eden), `row_version`. Kısmi UQ `ux_invitations_pending_email (tenant_id, email) WHERE status = 'pending'`;
> IX `(tenant_id, status, expires_at)`. ENABLE + FORCE RLS (std), `bayios_app` SELECT/INSERT/UPDATE (**DELETE yok**).
> Kabul yolu (F-25 / C-5): davetli üye olmadığından satırı RLS altında göremez; `identity.find_invitation_tenant(bytea)`
> (SECURITY DEFINER, sahibi `bayios_migrator`, `search_path = pg_catalog`, PUBLIC'e kapalı, `bayios_app`'e EXECUTE; sahibin
> yalnızca `pending` satırları okuyan `invitations_token_lookup` politikası) token hash'ini **yalnızca tenant id'ye**
> çevirir; API sonra o tenant bağlamına geçer ve okuma/yazma/audit normal RLS ile yapılır. **Üyelik kabulde `active`
> oluşturulur:** `memberships.user_id` NOT NULL ve Supabase kullanıcı id'si davet anında bilinmez, bu yüzden bekleyen davet
> `memberships`'te `invited` satırı olarak tutulmaz (durum değeri geriye uyumluluk için CHECK'te kalır). `max_users`
> kullanımı = aktif üyelik + açık davet. Saklama: kapanmış davetlerin e-postası şimdilik silinmez (açık iş: 30 gün sonra
> temizleme işi, data-policy §4).

### 5.2 `billing`

**billing.subscription_plans** — tenant'sız, RLS yok
| code text **PK** · name · features text[] · limits jsonb (`{"max_users":5,...}`) · is_public bool · sort_order |

**billing.tenant_subscriptions** — RLS: std · PK id · UQ (tenant_id)
| plan_code text · status (trialing, active, past_due, cancelled) · current_period_start/end timestamptz · provider text NULL · provider_ref text NULL |

**billing.tenant_entitlements** — RLS: std
| id **PK** · tenant_id · feature_code text · enabled bool · limit_value bigint NULL · source (plan, override) · expires_at NULL |
| UQ (tenant_id, feature_code) |

**billing.usage_counters** — RLS: std
| tenant_id · metric text · period_start date · value bigint NOT NULL DEFAULT 0 |
| **PK** (tenant_id, metric, period_start) |

> **Uygulanan durum (migration `InitialBilling`, #51/#52):** `tenant_subscriptions`, `tenant_modules`, `tenant_entitlements`;
> hepsinde ENABLE + FORCE RLS (std), EF global query filter. Taslaktan farklar:
> - **`subscription_plans` tablosu yok.** Paket/ek modül kataloğu sürümlü koddur (`BillingCatalog`, `Version` örn.
>   `2026-11-05.1`): kodla birlikte gözden geçirilir, tüm tenant'lar aynı sürüme göre çözülür; kod listeleri CHECK
>   kısıtlarındadır (yeni plan/modül = migration). Entitlement'lar **saklanmaz, her istekte türetilir**: etkin plan
>   (deneme / aktif / süresi dolmuş → Temel) + açık ek modüller + override'lar; zaman girdi olduğundan bitiş anı bir
>   job'ı beklemez.
> - `tenant_subscriptions`: `plan_code` (basic, professional), `status` (trialing, active, expired, cancelled — `past_due`
>   yok, ödeme yok), `trial_started_at`, `trial_ends_at`, `current_period_start`, `current_period_end NULL`,
>   `catalog_version`, `provider`/`provider_ref` (boş; ödeme sağlayıcısı ürün sahibi onayı ister), `row_version`, audit
>   kolonları. `UQ (tenant_id)`; CHECK: trialing ⇒ deneme tarihleri dolu ve bitiş > başlangıç. Kısmi index
>   `ix_tenant_subscriptions_trialing_ends_at`.
> - Yeni tablo `tenant_modules` (ek modül etkinleştirmeleri): `module_code`, `status` (trialing, active, expired,
>   cancelled), `started_at`, `expires_at` (yalnızca ücretli `active` için NULL olabilir), `deactivated_at/by`.
>   `UQ (tenant_id, module_code)` — ücretsiz süre modül başına bir kez.
> - `tenant_entitlements` yalnızca **operatör override'ı** (`source = 'override'`; SUB-08): `feature_code`, `enabled`
>   (özellik kodları), `limit_value` (limit kodları; NULL = sınırsız), `reason`, `expires_at`. `UQ (tenant_id, feature_code)`.
> - `usage_counters` yok: sayılar sahip modüllerden okunur (aktif depo, aktif üye, `integrations.ai_usage` aylık toplamı).
>
> `bayios_app`: `tenant_subscriptions`, `tenant_modules` SELECT/INSERT/UPDATE (**DELETE yok** — veri silinmez, SUB-06);
> `tenant_entitlements` yalnızca SELECT (operatör migrator rolüyle yazar). Deneme bitişi kaydı: tenant'sız bakım işi
> `billing.subscription_expiry` → `billing.due_subscription_tenants(limit)` (SECURITY DEFINER, sabit search_path; sahibin
> politikaları `tenant_subscriptions_expiry_select` / `tenant_modules_expiry_select` yalnızca bitişi geçmiş `trialing`
> satırları gösterir, fonksiyon yalnızca tenant id döner); güncelleme ve denetim her tenant'ın kendi bağlamında RLS ile
> yapılır. Yeni tenant denemesi `POST /tenants` transaction'ında başlar; aboneliği olmayan eski tenant'a ilk yazma
> isteğinde (ON CONFLICT DO NOTHING) deneme kaydı açılır, salt-okunur istekler aynı denemeyi kaydetmeden çözer.

### 5.3 `catalog`

> **Uygulanan durum (migration `InitialCatalog`, #16/#17):** `units`, `brands`, `categories`, `products`, `product_variants` oluşturuldu. Tüm tablolarda `tenant_id`, `UQ (tenant_id, id)`, bileşik tenant FK'leri, `row_version`, `created_at/by`, `updated_at/by`, ENABLE + FORCE RLS (std) ve EF global query filter var. `bayios_app` katalogda yalnızca `SELECT/INSERT/UPDATE` alır (**DELETE yok**: katalog satırları arşivlenir, fiziksel silinmez). `units`, `brands`, `categories` ayrıca `archived_at` taşır; `brands` ve kardeş kategori adı benzersizliği arşivlenmiş satırları dışlar (kısmi index), SKU benzersizliği arşivlenmişleri **içerir**. Kategori ebeveyni oluşturulduktan sonra değişmez (taşıma ileride). Henüz yapılmayanlar: `variant_barcodes` (çoklu barkod — şimdilik varyantta tek `barcode` kolonu, `UQ (tenant_id, barcode)`, NULL çakışmaz), `variant_aliases`, `suppliers`, `variant_unit_conversions`, `product_compatibilities`, `purchase_price`/`weight_kg`/`low_stock_threshold`/`normalized_name` kolonları. Arama: `products.name` ve `product_variants.sku` üzerinde `pg_trgm` GIN (`ILIKE '%q%'`), barkod tam eşleşme (unique btree). `pg_trgm` yoksa migration `extensions` şemasına kurar; varsa bulunduğu şemadaki operatör sınıfını kullanır.
>
> **Uygulanan durum (migration `CustomFieldDefinitions`, #14/#19):** `custom_field_definitions` oluşturuldu (std RLS, ENABLE + FORCE, `bayios_app` yalnızca `SELECT/INSERT/UPDATE`; tanımlar `archived_at` ile arşivlenir). Taslaktan farklar: `entity_type` şimdilik yalnızca `product`, `variant` (CHECK; `customer` Customers modülüyle eklenecek); benzersizlik `UQ (tenant_id, key)` — anahtar ürün ve varyant arasında da tekildir ve arşivlense bile yeniden kullanılmaz (`attr.<key>` filtresi tek anlamlı kalır); `key` CHECK `^[a-z][a-z0-9_]{0,49}$`; `data_type` ∈ text, number, integer, boolean, enum, multi_enum, range (`{"min","max"}`), date; ek `unit text NULL` (number/integer/range) ve `archived_at`; `options` jsonb dizi (CHECK). `key`, `entity_type`, `data_type` değişmez. `products.attributes` / `product_variants.attributes` artık API'den yazılır ve `CustomFieldValidator` ile doğrulanır (bilinmeyen/arşivli anahtar, tip, seçenek, zorunluluk → 422, alan bazlı hata). Ürün listesi `attr.<key>` filtresi: eşitlikler tek `attributes @> {...}` içermesinde birleşir (GIN `jsonb_path_ops`); sayı `.min/.max` sınırları ve range "değer aralıkta" testi `jsonb_path_exists` ile, index'li koşulların bıraktığı satırlarda değerlendirilir (ifade index'i gerekirse §4). Sektör şablonları tablo değil kod yapılandırmasıdır (`SectorTemplates`); uygulama idempotenttir (mevcut birim kodu / kök kategori adı / alan anahtarı atlanır).
>
> **Kimlik sözleşmesi:** Stok tutulan ve satılan kimlik **`product_variants.id` (variant_id)**'dir. Inventory, Orders, Pricing ve Warehouse yalnızca `variant_id`'ye referans verir (modüller arası FK yok); ürün id'si veya SKU referans olarak kullanılmaz (SKU değiştirilebilir). Basit ürün tek varyantlıdır.

**catalog.units** — RLS: std · UQ (tenant_id, code), UQ (tenant_id, id)
| id · code text · name · allows_fraction bool |

**catalog.brands** — RLS: std · UQ (tenant_id, lower(name)), UQ (tenant_id, id)
| id · name · archived_at |

**catalog.categories** — RLS: std · UQ (tenant_id, id)
| id · parent_id uuid NULL FK (tenant_id, parent_id) → categories · name · path text (materialized, ör. `/root-id/child-id/`) · sort_order · archived_at |
| UQ NULLS NOT DISTINCT (tenant_id, parent_id, lower(name)) · IX (tenant_id, path text_pattern_ops) |

**catalog.suppliers** — RLS: std
| id · name · tax_number · phone · email · notes · archived_at · UQ (tenant_id, id) |

**catalog.custom_field_definitions** — RLS: std · UQ (tenant_id, id)
| id · entity_type text (product, variant; customer ileride) · key text · label · data_type text · unit text NULL · options jsonb (dizi) · required bool · filterable bool · sort_order · archived_at · row_version |
| UQ (tenant_id, key) `ux_custom_field_definitions_tenant_id_key` · IX (tenant_id, entity_type, sort_order) |

**catalog.products** — RLS: std
| Kolon | Tip | Not |
|---|---|---|
| id | uuid **PK** | UQ (tenant_id, id) |
| tenant_id | uuid NOT NULL | |
| name | text NOT NULL | |
| description | text | |
| category_id | uuid NULL | FK (tenant_id, category_id) → categories |
| brand_id | uuid NULL | FK (tenant_id, brand_id) → brands |
| default_unit_id | uuid NOT NULL | FK → units |
| tax_rate | numeric(5,2) NOT NULL | ör. 20.00 |
| status | text | active, inactive (pasif: satışa kapalı, stokta görünür), archived — PRD CAT-02 |
| attributes | jsonb NOT NULL DEFAULT '{}' | IX GIN jsonb_path_ops |
| row_version | bigint | |
| IX | (tenant_id, status, name) | liste |
| IX | GIN (name gin_trgm_ops) | arama |

**catalog.product_variants** — RLS: std
| Kolon | Tip | Not |
|---|---|---|
| id | uuid **PK** | UQ (tenant_id, id) — diğer modüllerin referans verdiği kimlik |
| tenant_id | uuid NOT NULL | |
| product_id | uuid NOT NULL | FK (tenant_id, product_id) → products |
| sku | text NOT NULL | UQ (tenant_id, sku) |
| name | text | varyant adı / seçenek özeti |
| option_values | jsonb | ör. `{"renk":"kırmızı","beden":"42"}` |
| unit_id | uuid NOT NULL | FK → units |
| purchase_price | numeric(18,4) NULL | bilgi |
| weight_kg | numeric(12,3) NULL | |
| attributes | jsonb NOT NULL DEFAULT '{}' | IX GIN jsonb_path_ops |
| normalized_name | text NOT NULL | AI/arama için (küçük harf, Türkçe karakter katlama) — IX GIN gin_trgm_ops |
| low_stock_threshold | numeric(18,3) NULL | |
| status | text | |
| row_version | bigint | |
| IX | (tenant_id, product_id) | |

**catalog.variant_barcodes** — RLS: std
| id · variant_id FK (tenant_id, variant_id) · barcode text · kind (ean13, upc, internal) · UQ (tenant_id, barcode) |

**catalog.variant_aliases** — RLS: std (AI eşleştirme ve arama için müşteri/serbest adlar)
| id · variant_id FK · alias text · normalized_alias text · customer_id uuid NULL (müşteriye özgü ad; modüller arası → FK yok) · source (manual, learned_from_ai) |
| UQ NULLS NOT DISTINCT (tenant_id, normalized_alias, customer_id) · IX GIN (normalized_alias gin_trgm_ops) |

### 5.4 `pricing`

> **Uygulanan durum (migration `InitialPricing`, #78):** `price_lists` ve `price_list_items` oluşturuldu; ENABLE + FORCE RLS (std), EF global query filter, `bayios_app`: listelerde `SELECT/INSERT/UPDATE` (**DELETE yok**, liste `status = archived` ile arşivlenir), kalemlerde ayrıca `DELETE`. Ek kolonlar (§5.13): `base_price_list_id` (bileşik tenant FK, aynı tablo) + `discount_percent numeric(5,2)` — ikisi birlikte set edilir (CHECK), taban liste aynı para birimi ve KDV modunda, kendisi türetilmemiş bir satış listesi olmalıdır (tek seviye; zincir/döngü yok). `status` (active, inactive, archived) + `archived_at`; varsayılan liste yalnızca aktif satış listesi olabilir (CHECK) ve tenant + para birimi başına tektir (kısmi UQ); bir para biriminin ilk satış listesi varsayılan olur. Ad UQ `(tenant_id, name)` arşivlenmemişlerde. Kalem UQ adı 63 bayt sınırı nedeniyle `ux_price_list_items_tenant_id_list_id_variant_id_min_qty`. `purchase` listeleri yalnızca koşulsuz `prices.read.cost` iznine görünür.
>
> **Çözümleme (`IPriceResolver`, Pricing.Contracts):** satır başına sıra: müşteri listesi (`customers.price_list_id`) → bayi grubu listesi (`dealer_groups.price_list_id`, grup arşivli değilse) → tenant varsayılan satış listesi (istenen para biriminde). Her listede önce en büyük `min_quantity ≤ miktar` kalemi (`item`), yoksa liste türetilmişse taban listenin kalemi × (100 − yüzde) / 100, 4 haneye yarım-sıfırdan-uzağa yuvarlanır (`base_discount`). Aktif olmayan, tarih penceresi dışındaki, satış olmayan ya da başka para birimindeki listeler atlanır. Hiçbiri fiyat vermezse satır `no_price` ve `unitPrice = null` (asla 0). KDV oranı Catalog ürününün `tax_rate`'idir; `pricesIncludeTax` listeden gelir. `POST /api/v1/pricing/quote` (`prices.read.sell`) aynı servisi çağırır.

**pricing.price_lists** — RLS: std · UQ (tenant_id, id)
| id · name · kind (sales, purchase) · currency char(3) · is_default bool · prices_include_tax bool · valid_from date NULL · valid_to date NULL · status · row_version |
| UQ (tenant_id, currency) WHERE is_default AND kind='sales' (kısmi) · CHECK valid_to ≥ valid_from |

**pricing.price_list_items** — RLS: std
| id · price_list_id FK (tenant_id, price_list_id) · variant_id uuid (→ catalog, FK yok) · min_quantity numeric(18,3) NOT NULL DEFAULT 1 · unit_price numeric(18,4) NOT NULL CHECK ≥ 0 |
| UQ (tenant_id, price_list_id, variant_id, min_quantity) · IX (tenant_id, variant_id) |

### 5.5 `customers`

> **Uygulanan durum (migration `InitialCustomers`, #31):** `dealer_groups`, `customers`, `customer_addresses` oluşturuldu; ENABLE + FORCE RLS (std), EF global query filter, `bayios_app`: grup ve müşterilerde `SELECT/INSERT/UPDATE` (**DELETE yok** — arşiv veya anonimleştirme), adreslerde ayrıca `DELETE`. `dealer_groups.status` (active, archived) + `archived_at`; ad benzersizliği `UQ (tenant_id, lower(name)) WHERE status <> 'archived'` (ifade index'i, SQL ile). `customers` ek kolonlar: `contact_name`, `notes`, `anonymized_at`; `tax_number` CHECK `^[0-9]{10,11}$`; `payment_term_days` 0–3650; `currency` NULL = tenant para birimi; `whatsapp_numbers` E.164 (en çok 5). `attributes` (özel alanlar) **henüz yok** (Catalog `CustomFieldValidator` sözleşmesi gerekiyor). Arama: `name` pg_trgm GIN (ILIKE) + kod ILIKE + vergi no / telefon / WhatsApp tam eşleşme; liste keyset `(name, id)`. Adres: tür başına ilk adres varsayılan olur; varsayılan silinirse en eski adres varsayılan olur. Bayi grubu / müşteri fiyat listesi ataması ayrıca `pricelists.assign` ister. Kişisel veri (data-policy §2, §5): listelerde 11 haneli vergi no maskelenir; `POST /customers/{id}/anonymize` (owner/admin) kişisel alanları siler, adresleri kaldırır, müşteriyi arşivler ve müşterinin tüm sipariş adres snapshot'larındaki kişisel alanları maskeler (#115, §5.8); tekrar çağrı idempotenttir (200, müşteri değişmez; yalnızca kalan snapshot'lar maskelenir). Audit `customer.anonymized` yalnızca sayılar taşır (`addressesRemoved`, `ordersMasked`, `repeat`).

**customers.dealer_groups** — RLS: std · UQ (tenant_id, lower(name)), UQ (tenant_id, id)
| id · name · price_list_id uuid NULL (→ pricing) · default_discount_percent numeric(5,2) NULL |

**customers.customers** — RLS: std · UQ (tenant_id, id)
| Kolon | Tip | Not |
|---|---|---|
| id | uuid **PK** | |
| code | text NOT NULL | UQ (tenant_id, code) |
| name | text NOT NULL | IX GIN trgm |
| tax_number / tax_office | text NULL | IX (tenant_id, tax_number) |
| phone, email | text NULL | |
| whatsapp_numbers | text[] NOT NULL DEFAULT '{}' | E.164; IX GIN — AI'da göndericiden cari bulma |
| dealer_group_id | uuid NULL | FK (tenant_id, dealer_group_id) |
| price_list_id | uuid NULL | → pricing (FK yok) |
| payment_term_days | int NULL | |
| credit_limit | numeric(18,4) NULL | |
| currency | char(3) | |
| attributes | jsonb | |
| status, archived_at, row_version | | |

**customers.customer_addresses** — RLS: std
| id · customer_id FK (tenant_id, customer_id) · type (billing, shipping) · is_default bool · title · line1 · line2 · district · city · postal_code · country char(2) · contact_name · contact_phone |
| UQ (tenant_id, customer_id, type) WHERE is_default (kısmi) |

### 5.6 `warehouse` (ana veri ve operasyonlar)

**warehouse.warehouses** — RLS: std · UQ (tenant_id, code), UQ (tenant_id, id)
| id · code · name · address jsonb · uses_locations bool NOT NULL DEFAULT false · is_default bool · status |
| UQ (tenant_id) WHERE is_default (kısmi) |

**warehouse.warehouse_locations** — RLS: std · UQ (tenant_id, id)
| id · warehouse_id FK (tenant_id, warehouse_id) · code text · barcode text NULL · pick_sequence int · is_active bool |
| UQ (tenant_id, warehouse_id, code) · UQ (tenant_id, barcode) WHERE barcode IS NOT NULL |

> Uygulandı (JOB 09, migration `InitialWarehouse`): `warehouses` ve `warehouse_locations` std kolonlarla (`row_version`, `created_*`, `updated_*`); `address` kolonu henüz yok. Kod büyük harfe normalize edilir; varsayılan depo kısmi benzersiz index `ux_warehouses_tenant_id_default`. Lokasyon FK'si bileşik `(tenant_id, warehouse_id)`. `bayios_app`: SELECT/INSERT/UPDATE (DELETE yok).

**warehouse.stock_counts** / **warehouse.stock_count_lines** — RLS: std
| counts: id · warehouse_id · scope · is_blind bool NOT NULL DEFAULT false (INV-06) · status (open, submitted, applied, cancelled) · snapshot_at timestamptz · applied_at · applied_by · row_version |
| lines: id · stock_count_id FK · variant_id · location_id NULL · expected_qty numeric(18,3) · counted_qty numeric(18,3) NULL · line_status (pending, counted, needs_review, applied) · applied_delta numeric(18,3) NULL · UQ NULLS NOT DISTINCT (tenant_id, stock_count_id, variant_id, location_id) |

> Uygulandı (#168, migration `StockTransfersAndCounts`): **stock_counts** — `number` (`SAY-…`, UQ (tenant_id, number)) · `warehouse_id` (bileşik tenant FK) · `scope` (full, locations, variants) + `scope_location_ids uuid[]` / `scope_variant_ids uuid[]` · `is_blind` · `status` (open, submitted, applied, cancelled) · `note` · `snapshot_at` · `submitted_at/by` · `applied_at/by` · `cancelled_at/by` · `cancel_reason` · std kolonlar; depo başına tek açık sayım: kısmi UQ `ux_stock_counts_tenant_id_warehouse_id_active` (`status IN ('open','submitted')`). **stock_count_lines** — taslaktan farklar: kolon adları `expected_quantity` / `counted_quantity`, ek `sku` (snapshot), satır başına `snapshot_at` (sayım sırasında bulunan satırın kendi anlık görüntüsü), `counted_at/counted_by`; `line_status` (pending, counted, applied, uncounted) — `needs_review` durumu yok, "sonradan hareket gördü" bilgisi okumada hesaplanır (`movedSinceSnapshot`) ve uygulamada `acceptMovedLines` ister. Kör sayımda `expected_quantity` yalnızca `inventory.count.manage` ile döner. Davranış: [inventory-and-orders §4.8](inventory-and-orders.md).

**warehouse.stock_transfers** / **warehouse.stock_transfer_lines** — RLS: std (#168, OD-01; §5.13'te planlanmıştı)
| transfers: id · number (`TRF-…`, UQ (tenant_id, number)) · source_warehouse_id / destination_warehouse_id (bileşik tenant FK) · status (draft, in_transit, partially_received, received, closed, cancelled) · note · dispatched_at/by · last_received_at · completed_at · close_resolution (lost, return_to_source) · close_reason · closed_by · cancelled_at/by · cancel_reason · std kolonlar · IX (tenant_id, source_warehouse_id, status), (tenant_id, destination_warehouse_id, status), (tenant_id, status, created_at), (tenant_id, created_at, id) |
| lines: id · transfer_id FK (tenant_id, transfer_id) · line_no · variant_id · sku (snapshot) · source_location_id NULL / destination_location_id NULL (planlanan; bileşik tenant FK) · quantity (gönderilen) · received_quantity · returned_quantity · lost_quantity — yolda = quantity − received − returned − lost · (variant, kaynak raf) belge başına bir kez |

> Her iki çiftte ENABLE + FORCE RLS, EF global query filter; `bayios_app`: SELECT/INSERT/UPDATE (DELETE yok — iptal edilir). Yolda stok `inventory.inventory_balances.is_transit` satırlarındadır (§5.7). Davranış: [inventory-and-orders §4.7](inventory-and-orders.md).

**warehouse.picking_tasks** — RLS: std · UQ (tenant_id, id)
| id · type (pick, putaway) — putaway: iptal edilen siparişte toplananları rafa geri koyma (ORD-07) · order_id uuid (→ orders) · warehouse_id FK · status (open, in_progress, completed, completed_with_shortage, cancelled) — WHT-06 · assigned_to uuid NULL · started_at · completed_at · row_version |
| IX (tenant_id, warehouse_id, status) · IX (tenant_id, order_id) |

**warehouse.picking_task_lines** — RLS: std
| id · picking_task_id FK (tenant_id, picking_task_id) · order_line_id uuid · variant_id uuid · location_id uuid NULL · qty_to_pick numeric(18,3) · qty_picked numeric(18,3) DEFAULT 0 · qty_short numeric(18,3) DEFAULT 0 · shortage_reason text NULL (not_found, damaged, insufficient — WHT-06) · CHECK (qty_picked BETWEEN 0 AND qty_to_pick) |

**warehouse.shipments** — RLS: std · UQ (tenant_id, id)
| id · shipment_number text UQ (tenant_id, shipment_number) · order_id uuid · warehouse_id FK · status (draft, confirmed, cancelled) · carrier · tracking_number · package_count int NULL (ORD-15) · shipped_at · confirmed_by · row_version |
| IX (tenant_id, order_id) |

**warehouse.shipment_lines** — RLS: std
| id · shipment_id FK (tenant_id, shipment_id) · order_line_id uuid · variant_id uuid · location_id uuid NULL · quantity numeric(18,3) CHECK > 0 |
| IX (tenant_id, order_line_id) |

> Uygulandı (JOB 10, migration `Fulfillment`): `picking_tasks`, `picking_task_lines`, `shipments`, `shipment_lines`, `shipment_packages`, `shipment_package_items`; hepsinde ENABLE + FORCE RLS (std), EF global query filter, bileşik tenant FK'leri (depo, lokasyon, görev, sevkiyat, koli). `bayios_app`: görev/sevkiyat tabloları SELECT/INSERT/UPDATE (**DELETE yok**, iptal edilir), koliler SELECT/INSERT (bir kez yazılır). Taslaktan farklar — `picking_tasks`: ek `order_number` (snapshot), `assigned_at`, `completed_by`, `cancelled_at`, `cancel_reason`, std kolonlar; kısmi benzersiz `ux_picking_tasks_tenant_id_order_id_active` (`type = 'pick' AND status IN ('open','in_progress')`), IX (tenant_id, assigned_to, status), (tenant_id, created_at, id). `picking_task_lines`: ek `sequence` (UQ (tenant_id, picking_task_id, sequence)), `sku`/`name`/`unit` snapshot; CHECK `qty_to_pick > 0 AND qty_picked + qty_short ≤ qty_to_pick`, `shortage_reason` listesi. `shipments`: `shipment_number` yerine `number` (`{sipariş no}-S{n}`, UQ (tenant_id, number)), ek `order_number`, `picking_task_id` (FK), `packed_at`/`packed_by`, `cancelled_at`, durum listesine `packed`; CHECK `confirmed ⇒ shipped_at, confirmed_by`; kısmi benzersiz `ux_shipments_tenant_id_order_id_open` (`status IN ('draft','packed')`). `shipment_lines`: ek `sku`/`name`/`unit`, `picked_quantity`, `packed_quantity`, `short_quantity`, `shortage_reason`; CHECK `quantity + short_quantity = picked_quantity`, `quantity ≥ 0` (paketleme eksiğiyle 0 olabilir; onayda 0'lı satır sevk edilmez). Yeni: `shipment_packages` (package_no, UQ (tenant_id, shipment_id, package_no)), `shipment_package_items` (shipment_line_id FK, quantity > 0). Davranış: [inventory-and-orders §8](inventory-and-orders.md#8-uygulama-durumu--depo-hazırlama-ve-sevkiyat-job-10--36-37-38-39).

### 5.7 `inventory`

**inventory.inventory_movements** — RLS: std · **append-only** (`bayios_app`: yalnızca SELECT, INSERT; ayrıca `BEFORE UPDATE OR DELETE` trigger'ı exception fırlatır)
| Kolon | Tip | Not |
|---|---|---|
| id | uuid **PK** | UUIDv7 → zaman sıralı |
| tenant_id | uuid NOT NULL | |
| variant_id | uuid NOT NULL | → catalog (FK yok) |
| warehouse_id | uuid NOT NULL | → warehouse (FK yok) |
| location_id | uuid NULL | |
| type | text NOT NULL | CHECK in (receipt, adjustment_in, adjustment_out, count_correction, transfer_out, transfer_in, shipment_out, return_in, damage_out) |
| quantity | numeric(18,3) NOT NULL | işaretli; CHECK quantity <> 0; işaret–tip tutarlılığı CHECK |
| unit_cost | numeric(18,4) NULL | |
| source_type | text NOT NULL | receipt, adjustment, stock_count, transfer, shipment, return, import |
| source_id | uuid NULL | |
| source_line_id | uuid NULL | |
| transfer_group_id | uuid NULL | transfer çiftini bağlar |
| reason | text NULL | adjustment/damage için zorunlu (app) |
| balance_after | numeric(18,3) NOT NULL | ilgili bakiye satırının hareket sonrası on_hand'i (denetim/mutabakat kolaylığı) |
| correlation_id | text | |
| occurred_at | timestamptz NOT NULL | |
| IX | (tenant_id, variant_id, warehouse_id, occurred_at DESC) | stok kartı |
| IX | (tenant_id, source_type, source_id) | |
| UQ | (tenant_id, source_type, source_line_id, type, location_id) NULLS NOT DISTINCT WHERE source_line_id IS NOT NULL | aynı kaynak satırı iki kez işlenmesin (idempotency, ek güvence) |

Not: hacim büyüdüğünde `occurred_at` üzerinden aylık partitioning değerlendirilir (ADR gerektirir).

**inventory.inventory_balances** — RLS: std
| Kolon | Tip | Not |
|---|---|---|
| id | uuid **PK** | |
| tenant_id, variant_id, warehouse_id | uuid NOT NULL | |
| location_id | uuid NULL | depo lokasyon kullanmıyorsa NULL |
| on_hand | numeric(18,3) NOT NULL DEFAULT 0 | |
| reserved | numeric(18,3) NOT NULL DEFAULT 0 | |
| allow_negative | bool NOT NULL DEFAULT false | tenant ayarının denormalize kopyası (CHECK için) |
| version | bigint NOT NULL DEFAULT 0 | her güncellemede +1 |
| updated_at | timestamptz | |
| UQ | **NULLS NOT DISTINCT** (tenant_id, variant_id, warehouse_id, location_id) | |
| CHECK | `reserved >= 0` | |
| CHECK | `allow_negative OR (on_hand >= 0 AND reserved <= on_hand)` | |
| IX | (tenant_id, warehouse_id, variant_id) | |

`available` saklanmaz; sorgularda `on_hand - reserved`. Tenant `negative_stock_policy` ayarını değiştirdiğinde bir job tüm bakiyelerin `allow_negative` kolonunu günceller; `block`'a geçiş, negatif bakiye (veya `reserved > on_hand`) varsa reddedilir.

> **Yolda stok (#168, migration `TransitStock`):** `is_transit bool NOT NULL DEFAULT false`. İki aşamalı transferin yolda noktası bir bakiye satırıdır: `warehouse_id` = **transfer id** (gerçek depo değil), `location_id NULL`; CHECK `ck_inventory_balances_transit`: `NOT is_transit OR (on_hand >= 0 AND reserved = 0 AND location_id IS NULL AND NOT allow_negative)`. Bu satırlar hiçbir depoda satılabilir sayılmaz; `GET /inventory/balances` ve sayım anlık görüntüsü `is_transit` satırlarını dışlar. Yolda bacaklarının hareketlerinde de `warehouse_id = source_id = transfer id`.

**inventory.stock_reservations** — RLS: std
| Kolon | Tip | Not |
|---|---|---|
| id | uuid **PK** | |
| order_id, order_line_id | uuid NOT NULL | → orders (FK yok) |
| variant_id, warehouse_id | uuid NOT NULL | |
| location_id | uuid NULL | lokasyonlu depoda tahsis edilen lokasyon |
| quantity | numeric(18,3) NOT NULL CHECK > 0 | |
| consumed_quantity | numeric(18,3) NOT NULL DEFAULT 0 | CHECK 0 ≤ consumed ≤ quantity |
| released_quantity | numeric(18,3) NOT NULL DEFAULT 0 | CHECK consumed + released ≤ quantity |
| status | text | active, consumed, released |
| IX | (tenant_id, order_id) WHERE status = 'active' | |
| UQ | NULLS NOT DISTINCT (tenant_id, order_line_id, location_id) WHERE status='active' | |

> Uygulandı (JOB 09, migration `InitialInventory`): üç tablo yukarıdaki gibi; ek kolonlar: `inventory_movements.created_by` (aktör), `inventory_balances.created_at`, `stock_reservations.correlation_id/created_at/created_by/updated_at/updated_by`. Ek CHECK'ler: hareket `source_type` listesi, `unit_cost >= 0`, rezervasyon `status <> 'active'` ise `consumed + released = quantity`. Append-only: `bayios_app` yalnızca SELECT/INSERT + `trg_inventory_movements_append_only` (BEFORE UPDATE/DELETE) ve `trg_inventory_movements_no_truncate` trigger'ları (`restrict_violation`). Bakiye ve rezervasyonlarda `bayios_app` için DELETE yok. Index adları: `ux_inventory_balances_key`, `ux_inventory_movements_source_line`, `ux_stock_reservations_active_line`.

**inventory.stock_thresholds** — RLS: std (#137, §5.13) · minimum stok (kritik stok eşiği); stok değil, yapılandırma — hareket/bakiye yazmaz
| Kolon | Tip | Not |
|---|---|---|
| id | uuid **PK** | UUIDv7 |
| tenant_id | uuid NOT NULL | |
| variant_id | uuid NOT NULL | → catalog (FK yok) |
| warehouse_id | uuid NULL | → warehouse (FK yok). **NULL = varyantın genel (tenant geneli) eşiği**; dolu = o depoda genel eşiğin yerine geçen istisna |
| min_quantity | numeric(18,3) NOT NULL | CHECK ≥ 0 (uygulama: ≤ 1.000.000.000, 3 ondalık, kesirsiz birimde tam sayı). Taslaktaki `min_qty` |
| version | bigint NOT NULL DEFAULT 1 | her değişiklikte +1 (ETag / If-Match) |
| created_at/by, updated_at/by | | |
| UQ | **NULLS NOT DISTINCT** (tenant_id, variant_id, warehouse_id) `ux_stock_thresholds_key` | varyant başına bir genel + depo başına bir istisna |
| IX | (tenant_id, warehouse_id) | |

> Uygulandı (migration `StockThresholds`, #137): ENABLE + FORCE RLS (std), EF global query filter, `bayios_app`:
> `SELECT/INSERT/UPDATE/DELETE` (eşik kaldırılabilir; kaldırma `inventory.threshold_removed` ile audit'lenir).
> Yeniden sipariş hedefi (reorder target) §5.13'te tanımlı olmadığı için **yok**. Yazımlar SQL ile (`ThresholdService`):
> savepoint + `lock_timeout` 5 s, mevcut satırlar `(variant_id, warehouse_id NULLS FIRST)` sırasında `FOR UPDATE`,
> eksikler `INSERT … ON CONFLICT DO NOTHING` (yarışta kilitlenip güncellenir). **Etkin minimum** (Inventory sözleşmesi
> `IStockThresholdQueries`): tek depo kapsamında depo istisnası, yoksa genel eşik; tüm depolar kapsamında genel eşik,
> yoksa varyantın depo istisnalarının toplamı. Satılabilir = kapsamdaki bakiyelerin Σ on_hand − Σ reserved (bakiye
> satırı yoksa 0); **kritik** = etkin minimumu var ve satılabilir ≤ minimum.

### 5.8 `orders`

> **Uygulanan durum (migration `InitialOrders`, #32/#33/#35):** `orders`, `order_lines`, `order_status_history`, `order_shipments`, `order_shipment_lines`, `order_number_sequences`; hepsinde ENABLE + FORCE RLS (std), EF global query filter, bileşik tenant FK'leri. `bayios_app`: `orders` ve `order_number_sequences` SELECT/INSERT/UPDATE (**DELETE yok**), `order_lines` ayrıca DELETE (yalnızca taslak siparişin satırı — `trg_order_lines_delete_draft_only`, aksi `restrict_violation`), `order_status_history` / `order_shipments` / `order_shipment_lines` yalnızca SELECT/INSERT (append-only). Taslaktan farklar: `warehouse_id`, `price_list_id` (başlıkta) ve `shipping_address` / `billing_address` / `ai_parse_request_id` **yok**; yerine `source_reference_id uuid NULL`, `prices_include_tax bool`, `price_date date`, `submitted_by`, `shipped_at`, `completed_at`. Satırda ek: `unit` (birim kodu snapshot), `price_source`, `price_list_id`, `line_subtotal`, `line_discount`, `qty_reserved` (açık rezervasyon aynası); `unit_price`/`line_*` NULL = fiyat yok (CHECK: hepsi NULL ya da tutarlı, `line_total = line_subtotal − line_discount`); `discount_percent` / `discount_amount` en fazla biri (CHECK `num_nonnulls ≤ 1`); `qty_reserved + qty_shipped + qty_cancelled ≤ quantity` (I7). Satır no benzersizliği `ux_order_lines_tenant_id_order_id_line_no` **DEFERRABLE INITIALLY DEFERRED** (taslak düzenlemede yeniden numaralandırma). `orders` CHECK: durum/fulfillment/kaynak listeleri, `cancelled` ise `cancel_reason` zorunlu. `order_status_history` ek kolonlar: `action`, `fulfillment_status`, `correlation_id`. `order_number_sequences.next_value` = verilen son numara. Index: `ux_orders_tenant_id_number`, (tenant_id, created_at DESC, id DESC), (tenant_id, status, created_at DESC, id DESC), (tenant_id, customer_id, created_at DESC). Davranış: [inventory-and-orders §7](inventory-and-orders.md#7-uygulama-durumu--siparişler-job-11--32-33-35). Migration `OrderShortageFlag` (JOB 10): `orders.has_shortage bool NOT NULL DEFAULT false`, `order_status_history.action` listesine `report_shortage`. `order_shipments` / `order_shipment_lines` kimlikleri Warehouse `shipments` / `shipment_lines` kimlikleriyle aynıdır (onaylanan sevkiyatın sipariş tarafı kaydı). Migration `OrderSettingsAndAddresses` (#92): `orders` ek kolonlar `shipping_address_id uuid NULL`, `billing_address_id uuid NULL` (taslak seçimi; → customers.customer_addresses, modüller arası FK yok), `shipping_address jsonb NULL`, `billing_address jsonb NULL` (snapshot: addressId, title, line1, line2, district, city, postalCode, country, contactName, contactPhone); trigger `trg_orders_address_snapshot_immutable` (durum `draft`/`pending_approval` dışındayken bu dört kolon değişemez, `restrict_violation`). `order_number_sequences`: ek `prefix text NOT NULL DEFAULT 'SIP'` (CHECK `^[A-Z][A-Z0-9]{0,9}$`), PK `(tenant_id, prefix, year)`. Yeni tablo yok; yetki ve RLS değişmedi. Not: snapshot müşteri kişisel verisi (iletişim adı/telefonu) içerir. Migration `AnonymizeOrderAddressSnapshots` (#115): `orders.anonymize_address_snapshot(jsonb)` (IMMUTABLE, STRICT, idempotent) tek maskeleme kuralıdır — `title`, `line2`, `postalCode`, `contactName`, `contactPhone` → `null`, `line1` → `"Silinmiş adres"`; `addressId`, `district`, `city`, `country` kalır. `trg_orders_address_snapshot_immutable` onay sonrasında tek bir istisnaya izin verir: transaction-yerel `app.anonymize_customer_id` = siparişin `customer_id`'si **ve** `app.tenant_id` = siparişin tenant'ı, adres kimlikleri ve `customer_id` değişmiyor, iki snapshot da tam olarak `anonymize_address_snapshot(OLD.…)` değerine eşit; başka her değişiklik (her rol, tablo sahibi dahil) `restrict_violation`. `POST /customers/{id}/anonymize` bunu aynı UnitOfWork transaction'ında Orders sözleşmesi `IOrderPersonalData` üzerinden çağırır (müşterinin tüm sipariş başlıkları önce `FOR UPDATE` kilitlenir, işaret güncellemeden hemen sonra temizlenir; maskelenen siparişte `row_version` artar).

**orders.order_number_sequences** — RLS: std · **PK** (tenant_id, year)
| next_value bigint — `UPDATE ... SET next_value = next_value + 1 RETURNING` (satır kilidi; boşluksuz numara) |

**orders.orders** — RLS: std · UQ (tenant_id, id)
| Kolon | Tip | Not |
|---|---|---|
| id | uuid **PK** | |
| number | text NOT NULL | UQ (tenant_id, number) |
| customer_id | uuid NOT NULL | → customers |
| warehouse_id | uuid NOT NULL | → warehouse (MVP: sipariş tek depodan karşılanır) |
| status | text NOT NULL | CHECK (Draft…Cancelled; snake_case: draft, pending_approval, approved, picking, packed, shipped, completed, cancelled) |
| fulfillment_status | text NOT NULL DEFAULT 'none' | none, partial, full |
| source | text NOT NULL | manual, whatsapp_ai, import, mobile |
| currency | char(3) NOT NULL | |
| price_list_id | uuid NULL | |
| shipping_address | jsonb | snapshot |
| billing_address | jsonb | snapshot |
| subtotal, discount_total, tax_total, grand_total | numeric(18,4) | |
| notes | text | |
| requested_delivery_date | date NULL | |
| submitted_at, approved_at, approved_by, cancelled_at, cancelled_by, cancel_reason | | |
| ai_parse_request_id | uuid NULL | → integrations |
| row_version | bigint | |
| IX | (tenant_id, status, created_at DESC, id) | liste (keyset) |
| IX | (tenant_id, customer_id, created_at DESC) | |

**orders.order_lines** — RLS: std
| id · order_id FK (tenant_id, order_id) · line_no int · variant_id uuid · sku_snapshot text · name_snapshot text · unit_snapshot text · quantity numeric(18,3) CHECK > 0 · unit_price numeric(18,4) · discount_percent numeric(5,2) DEFAULT 0 · discount_amount numeric(18,4) DEFAULT 0 (satır başına tek iskonto tipi, CHECK — ORD-03) · tax_rate numeric(5,2) · line_total numeric(18,4) · qty_shipped numeric(18,3) DEFAULT 0 · qty_cancelled numeric(18,3) DEFAULT 0 · price_overridden bool |
| CHECK qty_shipped + qty_cancelled ≤ quantity · UQ (tenant_id, order_id, line_no) |

**orders.order_status_history** — RLS: std · append-only
| id · order_id FK · from_status · to_status · changed_by uuid · changed_at · reason text · IX (tenant_id, order_id, changed_at) |

### 5.9 `imports`

**imports.import_jobs** — RLS: std
| id · type (products, prices, customers, opening_stock) · status · file_object_path text (Storage yolu) · file_name · file_size_bytes · file_sha256 · mapping jsonb · mode (create_only, update_only, upsert) — IMP-06 · dry_run bool · total_rows · valid_rows · invalid_rows · processed_rows · error_summary jsonb · started_at · finished_at · row_version |
| IX (tenant_id, created_at DESC) · IX (tenant_id, file_sha256, type) — aynı hash daha önce `completed` ise kullanıcıya uyarı (PRD IMP-09: engelleme değil uyarı); çoğalmayı SKU/barkod upsert anahtarı önler |

**imports.import_rows** — RLS: std
| id · import_job_id FK · row_number int · raw jsonb · normalized jsonb · status (valid, invalid, imported, skipped) · errors jsonb · target_id uuid NULL · previous_values jsonb NULL (geri alma için güncellenen alanların önceki değeri — IMP-12) |
| UQ (tenant_id, import_job_id, row_number) · IX (tenant_id, import_job_id, status) |

> **Uygulanan durum (migration `InitialImports`, #21–#24):** `import_jobs`, `import_files`, `import_rows`, `mapping_templates` oluşturuldu; ENABLE + FORCE RLS (std), EF global query filter, bileşik tenant FK'leri (`import_files`/`import_rows` → `import_jobs`). `bayios_app`: `import_jobs` `SELECT/INSERT/UPDATE` (**DELETE yok**, geçmiş), `import_files` `SELECT/INSERT/DELETE`, `import_rows` ve `mapping_templates` ayrıca `UPDATE/DELETE`. Taslaktan farklar: `type` şimdilik yalnızca `products`, `prices` (stok ve müşteri importu sonra); `status` ∈ uploaded, mapped, validating, validated, running, completed, failed, cancelled (`dry_run` kolonu yok — önizleme ayrı `validating` adımı); `file_object_path` yerine ham dosya **`imports.import_files.content bytea`** (≤ 10 MB, PK `import_job_id`) — barındırılan nesne deposu olmadığı için; `IImportFileStore` arayüzü Supabase Storage ile değiştirilebilir. Ek kolonlar: `file_format` (xlsx, csv), `file_encoding`, `delimiter`, `sheet_name`, `headers` jsonb, `sample_rows` jsonb (ilk 5 satır), `match_key` (sku, barcode), `price_list_id`, önizleme sayaçları `preview_create/update/skip/error`, sonuç sayaçları `created_rows/updated_rows/skipped_rows/failed_rows`, `processed_rows`, satır kontrol noktası `checkpoint_row`, `run_id` (eski çalıştırmanın job'ları no-op), `started_by` (işlem bu kullanıcının yetkisiyle yürür, her parçada yeniden doğrulanır), `failure_code/message`, `duplicate_of_import_id` (aynı hash + tür daha önce `completed` → yalnızca uyarı). `import_rows`: `action` (create, update, skip, error — önizleme kararı) + `status` (valid, invalid, skipped, imported, failed) + `outcome`; `errors` jsonb `{field, column, code, message}` dizisi; `matched_variant_id`, `target_id`, `previous_values` (güncellenen alanların eski değerleri, fiyatta `unitPrice`), `processed_at`; IX `(tenant_id, import_job_id, status, row_number)`. `mapping_templates`: `name`, `type`, `mapping` jsonb (alan → kaynak başlık), `match_key`, `mode`; UQ `(tenant_id, type, name)`. Yazımlar Catalog/Pricing komutlarıyla yapılır (bu şema dışına doğrudan yazım yok); stok tablolarına hiçbir import yazmaz.

> **Uygulanan durum (migration `ImportsHardening`, #94):** `import_jobs.status` ek değerleri `rolling_back`, `rolled_back`, `partially_rolled_back`, `rollback_failed`; geri alma kolonları `rollback_requested_at/by`, `rollback_finished_at`, sayaçlar `rolled_back_rows`, `rollback_conflict_rows`, `rollback_failed_rows`, `rollback_skipped_rows`. `import_rows`: `applied_values` jsonb (yürütme sonrası kayıtta saklanan değerler — geri almada çakışma kontrolü), `rollback_status` (rolled_back, conflict, failed, skipped; null = henüz işlenmedi = geri alma kontrol noktası), `rolled_back_at`. `previous_values`/`applied_values` anahtarları: `name, description, taxRate, brandId, categoryId, status, sku, barcode, unitId, attr.<key>`, fiyatta `unitPrice` (`null` = import kalemi oluşturdu). Geri alma (IMP-12, 7 gün, bir kez): oluşturulan ürün arşivlenir, güncellenen alan/fiyat yalnızca kayıt hâlâ `applied_values` ile aynıysa geri yazılır, aksi hâlde satır `conflict` olur ve dokunulmaz; yazımlar yine Catalog/Pricing komutlarıyla. Ham dosya saklama (data-policy §4): `imports.purge_expired_files(interval, integer)` SECURITY DEFINER (search_path sabit, PUBLIC'e kapalı, `bayios_app` EXECUTE) aktif olmayan ve bitişinden/yüklenmesinden 30 gün geçmiş import'ların `import_files` satırını siler; `bayios_migrator` için yalnızca 1 günlük tabanı olan SELECT/DELETE politikaları (`import_files_purge_select/_delete`, `import_jobs_purge_select`). Yükleme hız limiti tablo eklemez: tenant'ın son 1 saatteki `import_jobs` satırları `pg_advisory_xact_lock` altında sayılır (tüm API instance'ları ortak). Yeni grant yok.

### 5.10 `integrations`

**integrations.integrations** — RLS: std
| id · kind text · status · config jsonb (sır değil) · secret_ref text NULL · UQ (tenant_id, kind) |

**integrations.ai_parse_requests** — RLS: std
| Kolon | Tip | Not |
|---|---|---|
| id | uuid **PK** | |
| channel | text | whatsapp_paste, whatsapp_webhook |
| sender_phone | text NULL | E.164 |
| customer_id | uuid NULL | eşleşen/seçilen cari |
| raw_text | text NOT NULL | saklama: `docs/security/data-policy.md` §4 (öneri 30 gün) sonunda maskelenir (job) |
| text_hash | bytea NOT NULL | normalize metnin SHA-256'sı; (tenant_id, customer_id, text_hash) son 24 saat mükerrer uyarısı (US-WAI-004) — IX |
| status | text | queued, parsing, parsed, needs_review, approved, rejected, failed |
| model | text | yapılandırmadan |
| prompt_version | text | |
| parsed_payload | jsonb | modelin şemaya uygun çıktısı |
| match_result | jsonb | satır başına adaylar, yöntem (sku, barcode, alias, trigram), skor |
| overall_confidence | numeric(4,3) | |
| order_id | uuid NULL | onay sonrası taslak sipariş |
| reviewed_by, reviewed_at | | |
| input_tokens, output_tokens | int | maliyet/kullanım |
| error | text NULL | |
| IX | (tenant_id, status, created_at DESC) | inceleme kuyruğu |

> **Uygulanan durum (migration `InitialIntegrations`, #46–#50):** modül `BayiOS.Modules.Integrations`, iki tablo; ikisinde
> de ENABLE + FORCE RLS (std), EF global query filter. **`integrations.ai_parse_requests`** — taslaktan farklar:
> `sender_phone` yok (telefon maskelenir, ayrı saklanmaz); `raw_text` **maskelenmiş** metindir; `text_hash bytea` yerine
> `text_sha256 char(64)` (boşluk normalize, hex); `status` yalnızca `needs_review`, `approved`, `rejected` (ayrıştırma
> senkron); ek kolonlar `parser` (ai, rules), `fallback_reason` (disabled, quota_exceeded, provider_error, timeout,
> refused, truncated, invalid_output), `review jsonb` (satırlar + adaylar + onaydaki seçim; `match_result` yerine),
> `warnings jsonb` (yalnızca kodlar), `line_count` / `matched_count` / `ambiguous_count` / `unmatched_count`,
> `cache_read_tokens`, `cache_write_tokens`, `duration_ms`, `text_length`, `text_purged_at`, `row_version`, `created_by`,
> `updated_*`; `error` yok. CHECK: durum/kanal/ayrıştırıcı/geri dönüş listeleri, sayaçlar ≥ 0 ve
> matched + ambiguous + unmatched = line_count, `(status = 'approved') = (order_id IS NOT NULL)`, temizlenmiş satırda
> metin kolonları NULL. Index: (tenant_id, created_at DESC), (tenant_id, status, created_at DESC), (tenant_id, text_sha256),
> kısmi (created_at) WHERE text_purged_at IS NULL. `bayios_app`: SELECT/INSERT/UPDATE (**DELETE yok**).
> **`integrations.ai_usage`** (yeni): PK (tenant_id, usage_hour) — saatlik `parse_requests`, `ai_calls`, `ai_failures`,
> `fallbacks`, `input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_write_tokens` (varsayılan 0, CHECK ≥ 0);
> yalnızca atomik upsert ile, istekten ayrı kısa transaction'larda yazılır (çoklu instance güvenli; günlük kota tenant
> başına `pg_advisory_xact_lock` altında). `bayios_app`: SELECT/INSERT/UPDATE. Saklama (data-policy §4):
> `integrations.purge_expired_parse_texts(interval, integer)` SECURITY DEFINER (search_path sabit, PUBLIC'e kapalı,
> `bayios_app` EXECUTE) `p_retention ≥ 1 gün` olan satırlarda `raw_text`, `parsed_payload`, `review` → NULL,
> `text_purged_at` = now(); sahip için yalnızca `ai_parse_requests_purge_select` / `_purge_update` politikaları
> (created_at < now() − 1 gün; UPDATE'in WITH CHECK'i yalnızca metin kolonlarının NULL olmasına izin verir). Çalıştıran:
> tenant'sız `integrations.ai_text_retention` job'ı (`PlatformWorker.MaintenanceJobTypes`, saatlik). `integrations.integrations`
> tablosu henüz yok.

### 5.10a `reports` (#53)

**reports.export_requests** — RLS: std · `bayios_app`: SELECT, INSERT, DELETE (UPDATE yok)
| id uuid PK · tenant_id · report text CHECK (critical_stock, orders, top_products, stock_movements, picking_tasks, ai_usage) · format text CHECK (csv, xlsx) · row_count int ≥ 0 · created_at timestamptz · IX (tenant_id, created_at DESC) |

Yalnızca saatlik dışa aktarma sınırının sayacıdır (threat-model §6.5): rapor verisi ve kişi (aktör) tutmaz — aktör audit
olayındadır. Bir tenant'ın her dışa aktarması, aynı transaction'da o tenant'ın 1 günden eski satırlarını siler (RLS
altında). Reports modülünün başka tablosu yoktur; raporlar sahibi modüllerin tablolarını port adaptörleriyle okur
(api-strategy §12.13). Modüller arası FK yok.

### 5.11 `audit` ve `notifications`

**audit.audit_events** — RLS: std · append-only (yalnızca INSERT/SELECT) — #79
| id · actor_user_id NULL (sistem/job aktörü) · actor_role · action text · entity_type · entity_id uuid NULL · changes jsonb NULL · ip_hash bytea (HMAC-SHA-256, anahtar `Audit:IpHashKey`; anahtar yoksa NULL; data-policy §8) · user_agent (≤ 200, yazdırılabilir) · correlation_id · occurred_at |
| IX (tenant_id, occurred_at DESC, id DESC) — keyset · IX (tenant_id, entity_type, entity_id, occurred_at DESC) · IX (occurred_at) — saklama temizliği (#89) |
| CHECK: action `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$` (≤ 100) · `pg_column_size(changes) ≤ 32 KB` |

- Sahibi `PlatformDbContext` (migration `AuditJobsOutbox`); çalışma zamanı: `IAuditLog.RecordAsync` (SharedKernel arayüzü, `BayiOS.Api/Platform/Audit` ham SQL) — **isteğin UnitOfWork transaction'ında** yazar; tenant/aktör/rol/correlation id istek bağlamından gelir.
- `changes` yalnızca değişen alanlar: `{"alan": {"from": …, "to": …}}`; kişisel veri (vergi no/TCKN, telefon) yalnızca `{"changed": true}`; serbest metin (stok düzeltme sebebi) audit'e kopyalanmaz, kayda id ile bağlanır.
- `bayios_app`: yalnızca `SELECT, INSERT`; `trg_audit_events_append_only` / `_no_truncate` UPDATE/DELETE/TRUNCATE'i sahip ve süper kullanıcı için de reddeder.
- **Saklama istisnası (#89, migration `AuditRetentionQueueCleanup`; data-policy §4: 2 yıl):** append-only kuralının **tek** istisnası. `audit.reject_audit_change()` yalnızca şu üç koşul birlikte sağlanınca bir satır **DELETE**'ine izin verir: (1) transaction-yerel `bayios.audit_retention_purge = 'on'` işareti, (2) `current_user` tablo sahibi (`bayios_migrator`), (3) `occurred_at < now() - interval '2 years'` (veritabanındaki sabit taban). UPDATE ve TRUNCATE koşulsuz reddedilir. İşareti sahip olarak koyan tek yol `audit.purge_expired_audit_events(p_retention interval, p_limit int)` — SECURITY DEFINER, sahibi `bayios_migrator`, `search_path = pg_catalog`, PUBLIC'e kapalı, `bayios_app`'e yalnızca EXECUTE; `p_retention < 730 gün` ve `p_limit ∉ 1..10000` reddedilir, kesim `least(now() - p_retention, now() - 2 yıl)`, en eski önce `p_limit` satır siler. Sahip FORCE RLS altında: yalnızca `bayios_migrator`'a özel `audit_retention_select/_delete` politikaları (`occurred_at < now() - 2 yıl`) eklidir. `bayios_app`'in UPDATE/DELETE/TRUNCATE yetkisi yoktur — işareti kendisi koysa da bir şey değişmez; süper kullanıcı sahip olmadığından tetikleyiciye takılır. Tabanı kısaltmak gözden geçirilmiş migration ister; uzatmak yapılandırmadır (`Audit:Retention`, varsayılan ve en az 730 gün). Çalıştıran: tenant'sız `platform.audit_retention` job'ı (saatlik bakım, 1000'lik partiler).
- Okuma: `GET /api/v1/audit-events` (`audit.read` — owner/admin), filtre: entityType, entityId, action, actorUserId, from, to; keyset sayfalama.

**notifications.device_tokens** — RLS: `tenant_id = app.tenant_id` **ve** (`app.user_id` boş — job — **veya** `user_id = app.user_id`) — #44, migration `InitialNotifications` (`NotificationsDbContext`, Notifications modülü)
| id · tenant_id · user_id · platform CHECK (android, ios) · token text (32–4096, `[A-Za-z0-9_:.-]`) · token_sha256 bytea (32) · app_version NULL (≤ 32) · locale NULL (≤ 35) · created_at · last_seen_at · UQ (token_sha256) — global: bir token aynı anda tek (tenant, user) · IX (tenant_id, user_id, last_seen_at) · IX (last_seen_at) |

- `bayios_app`: SELECT, INSERT, UPDATE, DELETE (çıkış, FCM "unregistered", tenant başına kullanıcı başına en çok `Push:MaxDevicesPerUser`). Token API yanıtında ve logda yoktur.
- Devir: `notifications.release_device_token(p_token_sha256 bytea)` — SECURITY DEFINER (sahip `bayios_migrator`, `search_path = pg_catalog`, PUBLIC'e kapalı); istek bağlamı (`app.tenant_id` + `app.user_id`) şart; token başına `pg_advisory_xact_lock`; aynı hash'i **başka** (tenant, user) için tutan satırları siler. Sahibin FORCE RLS altında yalnızca `bayios.device_token_handover` (fonksiyonun koyduğu transaction-yerel değer) hash'ine eşit satırları gören `device_tokens_handover_select/_delete` politikaları vardır.
- Temizlik: `notifications.purge_stale_device_tokens(p_max_age interval, p_limit int)` — SECURITY DEFINER, `p_max_age < 30 gün` reddedilir; `device_tokens_purge_select/_delete` (`last_seen_at < now() - 30 gün`). Çalıştıran: tenant'sız `notifications.device_cleanup` bakım job'ı (`Push:StaleAfterDays`, varsayılan 90).
- Modül seçimi: Identity değil ayrı Notifications modülü — FCM/Google OAuth bağımlılığı, dış çağrı yapan job ve outbox aboneliği kimlik modülünü genişletmez; Warehouse yalnızca outbox olayı yazar (modüller arası referans yok). Ayrıntı: docs/operations/push-notifications.md.

**notifications.notifications** — RLS: std
| id · user_id · type · payload jsonb · read_at · IX (tenant_id, user_id, read_at, created_at DESC) |

### 5.12 `platform`

**platform.outbox_messages** — RLS yok (§2.3)
| Kolon | Tip | Not |
|---|---|---|
| id | uuid **PK** | message id (tüketici idempotency anahtarı) |
| tenant_id | uuid NULL | |
| type | text NOT NULL | ör. `orders.order_status_changed.v1` |
| payload | jsonb NOT NULL | |
| correlation_id | text | |
| occurred_at | timestamptz NOT NULL | |
| processed_at | timestamptz NULL | |
| attempts | int DEFAULT 0 | |
| next_attempt_at | timestamptz NOT NULL DEFAULT now() | |
| last_error | text | yalnızca temizlenmiş hata (`JobErrorSanitizer`, ADR 0006) |
| IX | (next_attempt_at) WHERE processed_at IS NULL | dispatcher (`ix_outbox_messages_pending`) |
| IX | (processed_at) WHERE processed_at IS NOT NULL | `platform.queue_cleanup` (`Jobs:OutboxRetention`, 7 gün) |

**platform.jobs** — RLS yok (§2.3)
| id **PK** · tenant_id NULL · type text · payload jsonb · status (queued, running, succeeded, dead) · priority smallint · run_after timestamptz · attempts · max_attempts (1–100) · locked_by text · locked_until timestamptz · last_error (≤ 1000) · dedupe_key text NULL · correlation_id · created_at · finished_at |
| IX (priority DESC, run_after) WHERE status = 'queued' · IX (locked_until) WHERE status = 'running' (lease süpürücü) · IX (finished_at) WHERE status IN ('succeeded','dead') · UQ (type, dedupe_key) WHERE dedupe_key IS NOT NULL AND status IN ('queued','running') |

- Başarısız deneme `failed` durumu tutmaz: `max_attempts` dolmadıysa üstel geri çekilmeyle `queued`'a (`run_after`), dolduysa `dead`'e döner.
- **Tenant koruması (RLS yerine):** `platform.enforce_queue_tenant()` tetikleyicisi (`trg_jobs_tenant`, `trg_outbox_messages_tenant`) — `app.tenant_id` ayarlıyken (istek veya job handler'ı) yalnızca **aynı** tenant'a satır yazılabilir; tenant'sız bağlam (worker'ın kendi alma/dağıtma transaction'ları) her tenant'ı yazabilir.
- `bayios_app`: `SELECT, INSERT, UPDATE, DELETE` (jobs, outbox_messages).
- `last_error` (≤ 1000) yalnızca `JobErrorSanitizer` çıktısıdır: istisna tipi + temizlenmiş mesaj; SQL parametresi, `Detail`, kişisel veri yok (#89).
- Temizlik (#89): tenant'sız `platform.queue_cleanup` job'ı `succeeded` job'ları `Jobs:SucceededRetention` (7 gün), `dead` job'ları `Jobs:DeadRetention` (30 gün) sonra siler; `queued`/`running` satırlara dokunmaz.
- Kayıtsız tip: `IBackgroundJobs.EnqueueAsync` handler'ı kayıtlı olmayan tipi transaction içinde reddeder (`UnknownJobTypeException`).

**Süresi dolmuş idempotency anahtarı temizliği:** tenant'sız `platform.idempotency_cleanup` job'ı `platform.purge_expired_idempotency_keys(p_limit)` fonksiyonunu çağırır (SECURITY DEFINER, sahibi `bayios_migrator`, `search_path = pg_catalog`; `bayios_app` yalnızca EXECUTE). Sahip FORCE RLS altında olduğundan yalnızca `bayios_migrator`'a özel `idempotency_purge_select/_delete` politikaları (`expires_at < now()`) eklidir: fonksiyon yalnızca süresi dolmuş satırları görebilir ve silebilir; `idempotency_scope` gevşetilmedi.

**platform.idempotency_keys** — RLS: tenant + kullanıcı kapsamı (#68). Sahibi `PlatformDbContext` (`src/Modules/Platform/BayiOS.Modules.Platform` — yalnızca şema + migration; çalışma zamanı kodu `BayiOS.SharedKernel/Platform/Idempotency` ham SQL kullanır; migration geçmişi `platform.__ef_migrations_history`).
Politika `idempotency_scope`: okuma `tenant_id = app.tenant_id` **veya** (`tenant_id IS NULL` ve `user_id = app.user_id`); yazma ayrıca `user_id = app.user_id` ister.
| Kolon | Tip | Not |
|---|---|---|
| id | uuid **PK** | |
| tenant_id | uuid NULL | NULL = tenant'sız uç (ör. `POST /tenants`); anahtar kullanıcıya kapsanır |
| key | text NOT NULL | istemci üretir (UUID önerilir), 1–100 karakter (CHECK) |
| user_id | uuid NOT NULL | |
| method, path | text NOT NULL | path = gerçek istek yolu (route değerleri dahil) |
| request_hash | bytea NOT NULL | SHA-256 (method + path + bağlanmış JSON gövde) |
| status | text NOT NULL | in_progress, completed (CHECK) |
| response_status | int NULL | yalnızca 2xx saklanır |
| response_body | bytea NULL | yanıt baytları birebir (jsonb anahtar sırasını değiştirirdi) |
| response_headers | jsonb NULL | Content-Type, Location, ETag |
| created_at | timestamptz | |
| expires_at | timestamptz | created_at + 24 saat; süresi dolan anahtar yeniden kullanılabilir; saatlik `platform.idempotency_cleanup` job'ı siler |
| UQ | (tenant_id, key) WHERE tenant_id IS NOT NULL | `ux_idempotency_keys_tenant_key` |
| UQ | (user_id, key) WHERE tenant_id IS NULL | `ux_idempotency_keys_user_key` |
| IX | (expires_at) | temizlik |

### 5.13 Phase 0 kapısında tespit edilen eksik tablolar (P0 gereksinimleri)

Aşağıdaki P0 gereksinimlerinin tablosu ilk taslakta yoktu. Sahip modül ve asgari içerik burada sabitlenir; ayrıntı ilgili haftanın ilk günündeki migration PR'ında tasarlanır ve bu belgeye işlenir (Phase 0 kapısı koşulu).

| Tablo (modül) | Gereksinim | Asgari içerik | En geç |
|---|---|---|---|
| `catalog.variant_unit_conversions` | TEN-06, INV-16, sektör L-03 | variant_id · unit_id · factor numeric(18,6) (temel birime) · UQ (tenant_id, variant_id, unit_id). Ledger/rezervasyon daima temel birimde; sipariş/mal kabul satırı girilen birim + katsayı snapshot'ı tutar | W4 Gün 16 |
| `catalog.product_compatibilities` | CAT-06, US-CAT-005 | product_id · make · model · year_from · year_to · IX (tenant_id, make, model) | W4 Gün 16 |
| `pricing.price_lists` ek kolonlar | CAT-08, US-CAT-006 | base_price_list_id NULL · discount_percent NULL (yüzde kuralı; `price_list_items` ürün özel fiyatı kuralı ezer) | W4 Gün 19 |
| `inventory.stock_thresholds` | CAT-12, US-REP-002 | variant_id · warehouse_id · min_qty · UQ (tenant_id, variant_id, warehouse_id) — **uygulandı (#137), §5.7** | W6 Gün 26 |
| `warehouse.variant_default_locations` | INV-04 | variant_id · warehouse_id · location_id · UQ (tenant_id, variant_id, warehouse_id) | W6 Gün 28 |
| `warehouse.goods_receipts` / `goods_receipt_lines` | INV-05, US-INV-004 | supplier_id · document_no · warehouse_id · status (draft, approved, cancelled) · mükerrer uyarısı için IX (tenant_id, supplier_id, document_no) · satır: variant_id · unit_id · quantity · factor · location_id · unit_cost | W6 Gün 28 |
| `warehouse.stock_transfers` / `stock_transfer_lines` | INV-08, US-INV-007 | **Uygulandı (#168)** — iki aşamalı "yolda" modeli (OD-01); bkz. §5.6 ve `inventory_balances.is_transit` (§5.7) | W6 Gün 29 |
| `warehouse.customer_returns` / `customer_return_lines` | INV-09, US-INV-009 | order_id NULL · status (draft, approved) · satır: order_line_id NULL · variant_id · quantity · condition (sellable, damaged) · hedef depo/lokasyon. Hasarlı stoğun satılabilirden ayrılması için lokasyon düzeyinde `is_sellable` bayrağı (available hesabı satılamaz lokasyonları dışlar) tasarlanır | W6 Gün 29 |
| `identity.consents` | SEC-04, US-SEC-004 | user_id · document (ör. kvkk_aydinlatma) · version · accepted_at · ip_hash — tenant'sız; RLS: `user_id = app.user_id` | W3 Gün 13 |

## 6. ER diyagramı (çekirdek: katalog / stok / sipariş / sevkiyat)

Kesikli ilişkiler modüller arası **mantıksal** referanslardır (DB FK yok).

```mermaid
erDiagram
    PRODUCTS ||--|{ PRODUCT_VARIANTS : "has"
    CATEGORIES ||--o{ PRODUCTS : "classifies"
    BRANDS ||--o{ PRODUCTS : "brands"
    UNITS ||--o{ PRODUCT_VARIANTS : "measured in"
    PRODUCT_VARIANTS ||--o{ VARIANT_BARCODES : "has"
    PRODUCT_VARIANTS ||--o{ VARIANT_ALIASES : "known as"

    PRICE_LISTS ||--o{ PRICE_LIST_ITEMS : "contains"
    PRODUCT_VARIANTS ||..o{ PRICE_LIST_ITEMS : "priced in"

    WAREHOUSES ||--o{ WAREHOUSE_LOCATIONS : "has"
    PRODUCT_VARIANTS ||..o{ INVENTORY_BALANCES : "stocked as"
    WAREHOUSES ||..o{ INVENTORY_BALANCES : "holds"
    WAREHOUSE_LOCATIONS ||..o{ INVENTORY_BALANCES : "bin"
    PRODUCT_VARIANTS ||..o{ INVENTORY_MOVEMENTS : "moved"
    WAREHOUSES ||..o{ INVENTORY_MOVEMENTS : "in"

    CUSTOMERS ||..o{ ORDERS : "places"
    ORDERS ||--|{ ORDER_LINES : "contains"
    ORDERS ||--o{ ORDER_STATUS_HISTORY : "tracks"
    PRODUCT_VARIANTS ||..o{ ORDER_LINES : "ordered"
    ORDER_LINES ||..o{ STOCK_RESERVATIONS : "reserves"

    ORDERS ||..o{ PICKING_TASKS : "fulfilled by"
    PICKING_TASKS ||--|{ PICKING_TASK_LINES : "contains"
    ORDERS ||..o{ SHIPMENTS : "shipped by"
    SHIPMENTS ||--|{ SHIPMENT_LINES : "contains"
    ORDER_LINES ||..o{ SHIPMENT_LINES : "fulfils"
    SHIPMENT_LINES ||..o{ INVENTORY_MOVEMENTS : "shipment_out"

    PRODUCT_VARIANTS {
        uuid id PK
        uuid tenant_id
        uuid product_id FK
        text sku "UQ tenant"
        jsonb attributes
    }
    INVENTORY_BALANCES {
        uuid id PK
        uuid tenant_id
        uuid variant_id
        uuid warehouse_id
        uuid location_id "nullable"
        numeric on_hand
        numeric reserved
        bigint version
    }
    INVENTORY_MOVEMENTS {
        uuid id PK
        uuid tenant_id
        text type
        numeric quantity "signed"
        text source_type
        uuid source_id
    }
    STOCK_RESERVATIONS {
        uuid id PK
        uuid order_line_id
        numeric quantity
        numeric consumed_quantity
        text status
    }
    ORDERS {
        uuid id PK
        text number "UQ tenant"
        text status
        text fulfillment_status
    }
    ORDER_LINES {
        uuid id PK
        uuid order_id FK
        numeric quantity
        numeric unit_price
        numeric qty_shipped
    }
```

## 7. Açık noktalar
- Varyant–tedarikçi ilişkisi ve satın alma siparişi (MVP sonrası).
- Lot/seri numarası ve SKT takibi (MVP dışı; ledger'a `lot_id` kolonu olarak eklenebilir).
- Ledger partitioning eşiği.
