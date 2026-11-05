# BayiOS — Tenant izolasyon paketi ve 10k katalog performans testleri

| Alan | Değer |
|---|---|
| Sahip | QA Lead + Security Engineer |
| Issue | #15 (API kısmı), #20 |
| Son güncelleme | 2026-11-05 |

Kod: `services/api/tests/BayiOS.IntegrationTests/Security/` ve `.../Performance/`. Hepsi her PR'da CI'da koşar
(`ci-api` → `dotnet test`). Mobil (Flutter) ayağı ve personel daveti BLOCKED (#15).

## 1. Veritabanı guard'ları (`DatabaseSecurityGuardTests`, `Category=Security`)
pg_catalog'dan okunur; migrator'ın sahip olduğu her şema (`extensions` hariç) = iş şeması. Yeni modül/tablo ek iş
gerektirmeden taranır.

| Kural | Not |
|---|---|
| Her iş tablosu RLS ENABLED + FORCED, `bayios_app` için ≥ 1 politika | İstisna: `platform.jobs`, `platform.outbox_messages` (RLS yok, tenant trigger'ı zorunlu — data-model §2.3) |
| Politikalar PUBLIC'e değil `bayios_app`'e; her ifade `current_setting('app.…')`'e bağlı; INSERT/UPDATE politikası WITH CHECK'li; `tenant_id`'li her tablo `app.tenant_id`'ye bakar | İstisna: `platform.idempotency_keys` migrator purge politikaları |
| `bayios_app` yetkileri tablo bazında **tam olarak** beyan edilen (`DeclaredGrants`) | Yeni tablo beyan edilmeden test kırılır. Ledger/audit/sipariş geçmişi/sevkiyat paketleri: yalnız SELECT+INSERT; katalog ve "arşivlenir, silinmez" tablolar: DELETE yok |
| Ledger ve audit için UPDATE/DELETE/TRUNCATE trigger'ları etkin | |
| SECURITY DEFINER fonksiyonlar `search_path` sabitli, PUBLIC çalıştıramaz, sahibi `bayios_app` değil | |
| `bayios_app`: süper kullanıcı/BYPASSRLS/CREATEROLE/CREATEDB değil, hiçbir rolün üyesi değil, hiçbir nesnenin sahibi değil, hiçbir şemada CREATE yok | |
| PUBLIC ve Supabase istemci rolleri (`anon`, `authenticated`, `service_role` — varsa) iş şemalarına erişemez; view/sequence/sütun yetkisi yok | |
| Davranış: `bayios_app` bağlamsız ya da boş tenant bağlamında hiçbir RLS tablosunda satır görmez | |

## 2. API sweep'leri (OpenAPI güdümlü, `Category=Security`)
Rotalar çalışan API'nin `/openapi/v1.json` belgesinden okunur. Yeni uç otomatik taranır; prob kurulamıyorsa test
**kırılır** (sınıflandırılmalı). A = kurban, B = saldırgan; ikisi de API üzerinden kurulmuş tam tenant'tır
(`TenantWorld`: birim, marka, kategori, özel alan, ürün/varyant, depo/raf, mal kabul hareketi, fiyat listesi/kalem,
bayi grubu, müşteri/adres, import işi/şablonu, taslak sipariş, açık toplama görevi, taslak sevkiyat).

| Test | Kapsam |
|---|---|
| IDOR | `{id}` içeren her tenant ucu × (B sahibi, B başlığıyla gelen A sahibi) × (A'nın id'si; B'nin üst kaynağı + A'nın alt kaynağı). Geçerli gövde ve güncel If-Match ile → yalnız 404/403, yanıtta A verisi yok; GET uçlarında A sahibiyle pozitif kontrol; A'nın tüm tenant satırlarının parmak izi önce/sonra eşit |
| Liste/arama sızıntısı | Parametresiz her tenant GET'i, A'nın token/SKU/barkod/id'leriyle filtrelenerek B olarak → A verisi dönmez |
| Gövdede çapraz referans | B'nin geçerli isteğinde tek bir A id'si (birim, kategori, depo, raf, varyant, liste, grup, müşteri…) → 4xx, A değişmez |
| X-Tenant-Id sahteciliği | Her tenant ucu × (başka tenant sahibi, tenant'sız kullanıcı, `invited`/`disabled` üyelik, bilinmeyen tenant) → 403 `tenant.access_denied` |
| Kimliksiz erişim | `GET /api/v1/meta` dışında her uç tokensız → 401 |
| Rol sweep'i (`RolePermissionSweepTests`) | 6 rol × izinli her uç; beklenti `RolePermissions.IsGranted` (belgeyle eşitliği unit test korur): izinli → 401/403/5xx değil, izinsiz → 403 `auth.forbidden`. Her `x-permission` matriste tanımlı |

Bilinçli dışlama: `POST /api/v1/sector-templates/{code}/apply` (`{code}` global şablon, tenant kaynağı değil).

## 3. 10k sentetik katalog (`CatalogPerformanceTests`, `Category=Performance`)
Üreteç: `Performance/SyntheticCatalog.cs` — deterministik (yalnız satır id'leri rastgele), 10.000 ürün (5.000 motor
yedek parça, 5.000 LED; her dört LED'den biri 3 renk varyantlı → 12.500 varyant), 2 kök + 10 alt kategori, 8 marka,
filtrelenebilir özel alanlar (`voltaj` enum, `guc_w` integer, `uyumlu_model` text, varyant `renk` enum). Toplu yükleme
tek INSERT…SELECT ile `bayios_app` olarak tenant RLS bağlamında yapılır (migrator FORCE RLS nedeniyle satır
göremez/yazamaz — beklenen davranış), ardından ANALYZE. Yükleme ≈ 1 sn.

Ölçüm: senaryo başına 5 ısınma + 40 ölçüm, süreç içi HTTP → API → PostgreSQL 17. Performans koleksiyonu paralel
testler bittikten sonra tek başına koşar. Bütçe (ROADMAP W4): **her senaryoda p95 < 500 ms** — aşılırsa test kırılır ve
tüm tablo hata mesajındadır. GitHub Actions'ta tablo `ci-api` iş özetine (job summary) yazılır.

Yerel ölçüm (2026-11-05, Windows 11 + Docker Desktop/WSL2, Release, yalnız performans koleksiyonu):

| Senaryo | p50 ms | p95 ms |
|---|---:|---:|
| Liste: varsayılan sayfa (-createdAt, 50) | 6.9 | 9.3 |
| Liste: ada göre (50) | 5.4 | 6.1 |
| Arama: ad içinde (q) | 10.4 | 11.4 |
| Arama: SKU parçası (q) | 13.8 | 14.7 |
| Arama: barkod (q) | 13.7 | 15.1 |
| Arama: tam SKU (`/variants?sku`) | 3.2 | 4.6 |
| Barkod okutma (`/variants/lookup`) | 3.1 | 3.8 |
| Filtre: kök kategori + alt kategoriler | 7.2 | 9.1 |
| Filtre: yaprak kategori + ad araması | 7.9 | 10.1 |
| Filtre: `attr.voltaj` (enum) | 8.9 | 10.2 |
| Filtre: `attr.guc_w.min` (sayı aralığı) | 6.6 | 7.6 |
| Filtre: varyant `attr.renk` + ad | 14.0 | 15.4 |
| Sayfalama: derin keyset sayfası (100–180) | 5.3 | 6.0 |

Tutarlılık: tüm sayfalar dolaşıldığında her ürün tam bir kez döner (`-createdAt` — zaman damgaları üçerli tekrarlanır,
id ile ayrışır —, `name`, arşiv dahil tüm durumlar, varyantlar); arama/kategori/attr filtrelerinin sonucu SQL
"doğrusu" ile birebir aynıdır. Büyük veride: başka tenant hiçbir sonuç bulamaz/404/403 alır; viewer okur ama yazamaz,
depo personeli fiyat listesine erişemez.

## 4. Çalıştırma
```bash
cd services/api
dotnet test --solution BayiOS.slnx                                            # hepsi (CI)
dotnet test --solution BayiOS.slnx --filter-trait "Category=Security"          # izolasyon paketi
dotnet test --solution BayiOS.slnx --filter-trait "Category=Performance" --output detailed   # perf + tablo
```
CI'a eklenen süre (yerel, Release): tam paket 46 sn → 58 sn (+≈12 sn; performans ≈ 6 sn yük + ölçüm).
