# BayiOS — Roller ve Yetkiler

## 1. İlkeler
1. **Sabit rol seti (MVP):** `owner`, `admin`, `manager`, `sales`, `warehouse_staff`, `viewer`. `dealer_user` gelecekteki bayi portalı için **rezerve**dir; MVP'de atanamaz.
2. Yetki kontrolü **API'de** yapılır; istemci yalnızca görünürlüğü ayarlar. Her istek: (a) kimlik, (b) tenant üyeliği ve aktif durum, (c) rol → izin, (d) paket/modül entitlement.
3. **İzin ≠ entitlement.** İzin "bu rol yapabilir mi?", entitlement "bu tenant bu özelliğe sahip mi?" sorusudur. İkisi de gereklidir.
4. Kullanıcı bir tenant'ta tek role sahiptir; farklı tenant'larda farklı rolleri olabilir.
5. Güvenlik, temel stok doğruluğu, veri dışa aktarma ve işlem geçmişi hiçbir pakette kilitlenmez (yalnızca rol ile sınırlandırılır).
6. Özelleştirilebilir roller MVP dışıdır; tenant ayarları yalnızca belirtilen **ayarlanabilir kurallar** (bölüm 4) ile davranışı değiştirir.

## 2. Roller

| Rol | Tanım | Tipik persona |
|---|---|---|
| owner | Tenant'ın sahibi; tüm yetkiler + faturalama, sahiplik devri, tenant silme talebi, tam veri dışa aktarma | İşletme sahibi |
| admin | Owner'ın yetkilerinin çoğu; faturalama ve sahiplik hariç | Güvenilir yönetici, katalog sorumlusu |
| manager | Operasyon yöneticisi: sipariş onayı, depo, stok işlemleri, görev atama | Depo/operasyon müdürü |
| sales | Müşteri ve sipariş oluşturma, WhatsApp asistanı; stok düzeltemez | Ofis/satış personeli |
| warehouse_staff | Görev odaklı: toplama, paketleme, mal kabul, sayım; fiyat göremez | Depo personeli |
| viewer | Salt okunur; rapor ve dışa aktarma | Muhasebeci, ortak |
| dealer_user *(rezerve)* | Kendi müşteri kaydına ait sipariş ve fiyatları görür | Bayi müşteri (gelecek) |

## 3. İzin matrisi

Gösterim: **✓** izinli · **–** izin yok · **K** koşullu (dipnot) · **O** yalnızca kendi kayıtları

### 3.1 Tenant, üyeler, abonelik
| İzin | owner | admin | manager | sales | warehouse_staff | viewer |
|---|---|---|---|---|---|---|
| tenant.read | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| tenant.settings.manage (profil, KDV, birimler, kurallar) | ✓ | ✓ | – | – | – | – |
| tenant.delete.request | ✓ | – | – | – | – | – |
| members.read | ✓ | ✓ | ✓ | – | – | – |
| members.invite | ✓ | ✓ | – | – | – | – |
| members.manage (rol, askıya alma) | ✓ | K¹ | – | – | – | – |
| ownership.transfer | ✓ | – | – | – | – | – |
| billing.read (paket, modüller) | ✓ | ✓ | – | – | – | – |
| billing.manage (modül talebi, paket değişikliği) | ✓ | – | – | – | – | – |
| audit.read | ✓ | ✓ | – | – | – | – |
| security.manage (2FA zorunluluğu, oturumlar) | ✓ | ✓ | – | – | – | – |

### 3.2 Katalog ve fiyat
| İzin | owner | admin | manager | sales | warehouse_staff | viewer |
|---|---|---|---|---|---|---|
| catalog.read | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| catalog.write (oluştur/düzenle/arşivle) | ✓ | ✓ | ✓ | – | – | – |
| catalog.barcode.assign (mevcut ürüne barkod ekleme) | ✓ | ✓ | ✓ | – | K² | – |
| catalog.fields.manage (özel alan, kategori, birim) | ✓ | ✓ | – | – | – | – |
| prices.read.sell | ✓ | ✓ | ✓ | ✓ | – | ✓ |
| prices.read.cost (alış fiyatı) | ✓ | ✓ | K³ | – | – | K³ |
| prices.write (fiyat listeleri, ürün fiyatı) | ✓ | ✓ | – | – | – | – |
| pricelists.assign (müşteri grubuna) | ✓ | ✓ | ✓ | – | – | – |

### 3.3 İçe / dışa aktarma ve raporlar
| İzin | owner | admin | manager | sales | warehouse_staff | viewer |
|---|---|---|---|---|---|---|
| imports.run (ürün, fiyat, müşteri) | ✓ | ✓ | – | – | – | – |
| imports.run.stock | ✓ | ✓ | ✓ | – | – | – |
| imports.rollback | ✓ | ✓ | – | – | – | – |
| reports.read | ✓ | ✓ | ✓ | K⁴ | – | ✓ |
| reports.export (CSV/Excel) | ✓ | ✓ | ✓ | K⁴ | – | ✓ |
| data.export.full (tüm tenant verisi) | ✓ | – | – | – | – | – |

### 3.4 Stok ve depo
| İzin | owner | admin | manager | sales | warehouse_staff | viewer |
|---|---|---|---|---|---|---|
| inventory.read | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| inventory.movements.read | ✓ | ✓ | ✓ | – | O⁵ | ✓ |
| inventory.receive (mal kabul oluştur) | ✓ | ✓ | ✓ | – | ✓ | – |
| inventory.receive.approve | ✓ | ✓ | ✓ | – | K⁶ | – |
| inventory.adjust (manuel düzeltme) | ✓ | ✓ | ✓ | – | – | – |
| inventory.count.manage (başlat/onayla) | ✓ | ✓ | ✓ | – | – | – |
| inventory.count.perform (say) | ✓ | ✓ | ✓ | – | ✓ | – |
| inventory.transfer (oluştur/gönder) | ✓ | ✓ | ✓ | – | – | – |
| inventory.transfer.receive | ✓ | ✓ | ✓ | – | ✓ | – |
| inventory.returns (iade kaydı) | ✓ | ✓ | ✓ | K⁷ | ✓ | – |
| warehouses.manage (depo, raf) | ✓ | ✓ | ✓ | – | – | – |

### 3.5 Siparişler ve müşteriler
| İzin | owner | admin | manager | sales | warehouse_staff | viewer |
|---|---|---|---|---|---|---|
| orders.read | ✓ | ✓ | ✓ | ✓ | K⁸ | ✓ |
| orders.create (manuel) | ✓ | ✓ | ✓ | ✓ | – | – |
| orders.edit.draft | ✓ | ✓ | ✓ | O | – | – |
| orders.submit (onaya gönder) | ✓ | ✓ | ✓ | ✓ | – | – |
| orders.approve | ✓ | ✓ | ✓ | K⁹ | – | – |
| orders.edit.approved (onay sonrası satır) | ✓ | ✓ | ✓ | – | – | – |
| orders.price.override (liste dışı fiyat/iskonto) | ✓ | ✓ | ✓ | K¹⁰ | – | – |
| orders.cancel | ✓ | ✓ | ✓ | K¹¹ | – | – |
| orders.ship (sevk onayı) | ✓ | ✓ | ✓ | – | ✓ | – |
| whatsapp.parse (AI asistan) | ✓ | ✓ | ✓ | ✓ | – | – |
| customers.read | ✓ | ✓ | ✓ | ✓ | – | ✓ |
| customers.write | ✓ | ✓ | ✓ | ✓ | – | – |
| customers.groups.manage | ✓ | ✓ | ✓ | – | – | – |

### 3.6 Depo görevleri
| İzin | owner | admin | manager | sales | warehouse_staff | viewer |
|---|---|---|---|---|---|---|
| tasks.read.all | ✓ | ✓ | ✓ | K¹² | – | ✓ |
| tasks.read.own (atanan + havuz) | ✓ | ✓ | ✓ | – | ✓ | – |
| tasks.assign | ✓ | ✓ | ✓ | – | – | – |
| tasks.claim (havuzdan al) | – | – | ✓ | – | ✓ | – |
| tasks.pick / tasks.pack | ✓ | ✓ | ✓ | – | ✓ | – |
| tasks.report_shortage | ✓ | ✓ | ✓ | – | ✓ | – |

### Dipnotlar
1. admin, owner'ı ve diğer admin'leri değiştiremez/askıya alamaz; yalnızca manager ve altı rolleri yönetir.
2. Tenant ayarı "Depo personeli barkod atayabilir" açıksa (varsayılan kapalı).
3. Tenant ayarı "Alış fiyatını göster: manager / viewer" (varsayılan kapalı).
4. sales yalnızca sipariş ve müşteri raporlarını görür/dışa aktarır; maliyet içeren raporları göremez.
5. warehouse_staff yalnızca kendi oluşturduğu hareketleri ve görev bağlamındaki ürün hareketlerini görür.
6. Tenant ayarı "Depo personeli mal kabul onaylayabilir" (varsayılan kapalı).
7. sales iade talebi oluşturur (taslak); stok girişini manager veya warehouse_staff onaylar.
8. warehouse_staff yalnızca kendine atanan veya havuzdaki görevlerin bağlı olduğu siparişleri, fiyatsız görür.
9. Tenant ayarı "Satış personeli sipariş onaylayabilir" (varsayılan: açık; tutar eşiği P1). Bkz. PRD AQ-02.
10. Tenant ayarı "Satış personeli iskonto sınırı %X" (varsayılan %0 = override yok).
11. sales yalnızca kendi oluşturduğu Taslak / Onay Bekliyor siparişleri iptal edebilir.
12. sales, kendi siparişlerine bağlı görevlerin durumunu görür.

**Uygulama yorumu (#69, matris değişikliği değildir):**
- Dipnot 5 "görev bağlamı": kullanıcıya atanmış veya havuzdaki **aktif** (open / in_progress) toplama görevlerinin satırlarındaki ürünler.
- Dipnot 8: kullanıcıya atanmış (iptal hariç her durumda) veya havuzdaki aktif görevlerin siparişleri; liste ve detayda tüm fiyat alanları boş (`totals`, `grandTotal`, satır fiyat/iskonto/tutarları `null`).
- Dipnot 10: sınır > %0 iken sales yalnızca iskonto (yüzde veya tutar; satır iskontosu ≤ satır ara toplamının %X'i) uygulayabilir, manuel birim fiyat giremez.
- Dipnot 12: sales, oluşturduğu siparişlerin görevlerini ve sevkiyatlarını görür. `tasks.read.all` veya `tasks.read.own` sahibi görev detayı ve sevkiyat okuma uçlarına erişir (biri yeterli).
- Görünürlük koşulu dışındaki kayıt 404 (varlık ipucu yok); görünür ama koşul dışı işlem 403 — #111'den beri ayrı kodlarla: kendi kaydı değil `orders.not_owner`, dipnot 11 durum sınırı `orders.cancel_not_allowed_in_status`, dipnot 10 `orders.discount_limit_exceeded` / `orders.manual_price_not_allowed` (`limitPercent` ile), kuralı kapalı K (dipnot 2, 6, 9, 10 %0) `auth.disabled_by_tenant_rule`. İzin hiç yoksa ve fail-closed güvenlik ağında `auth.forbidden` kalır (api-strategy §6).

**Uygulama yorumu (#92 / #111, matris değişikliği değildir):**
- Onaysız tenant (`require_order_approval = false`, §3.1 #1a): submit ancak gönderen `orders.approve`'u aktif kurallarla koşulsuz taşıyorsa siparişi onaylar; sales için dipnot 9 kapalıysa sipariş `pending_approval`'da bekler (hata değil). AI kaynaklı taslak her zaman insan onayı bekler.
- `GET /me` dipnot 10 koşulunu `parameters.maxDiscountPercent` ile verir (istemci "en fazla %X" gösterebilir).
- `GET /members?permission=…` aynı matris + tenant kuralı değerlendirmesini kullanır (kayıt düzeyi K/O dahil, kuralı kapalı K hariç); ayrı bir izin değildir, `members.read` ister.

**Uygulama yorumu (#86, matris değişikliği değildir):**
- Üye listesi (`GET /members`, personel seçici dahil) `members.read` ister; ayrı bir "seçici" izni yoktur (owner/admin/manager — `tasks.assign` ile aynı küme).
- Sipariş yanıtlarındaki müşteri adı müşteri verisidir: yalnızca `customers.read` sahiplerine dolu; warehouse_staff (dipnot 8) için boş. Kayıtlardaki kişi görünen adları (oluşturan, onaylayan, zaman çizelgesi, görev atanan) kaydı okuyabilen herkese gösterilir; e-posta yalnızca üye listesinde (`members.read`).
- Onaylanmamış sevkiyat iptali `tasks.assign` ister (yönetici kararı); `orders.ship` sahibi warehouse_staff iptal edemez.

**Uygulama yorumu (#129, matris değişikliği değildir) — ekip yönetimi:**
- Davet (`members.invite`): owner `admin`, `manager`, `sales`, `warehouse_staff`, `viewer` davet eder; admin yalnızca manager
  ve altını (dipnot 1'in davete uygulanışı; "kimse kendi rolünden yükseğini veremez, admin'i yalnızca owner verir").
  `owner` davet edilmez ve rol değişikliğiyle verilmez; yalnızca **sahiplik devri** (`ownership.transfer`, yalnızca owner):
  hedef aktif üye owner olur, devreden admin olur (tek transaction). Davet yenileme/iptali de aynı hiyerarşiye tabidir
  (admin, admin davetine dokunamaz → `members.role_not_grantable`).
- Rol değişikliği ve askıya alma / yeniden etkinleştirme (`members.manage`): owner herkesi; admin (dipnot 1) yalnızca
  manager ve altındaki üyeleri yönetir (`members.not_manageable`) ve admin atayamaz. Son **aktif** owner düşürülemez /
  askıya alınamaz (`members.last_owner`); kimse kendini askıya alamaz. Askıya alma bir sonraki istekte etkilidir.
- Davet kabulü izin değildir: davetli henüz üye olmadığından tenant'sız uçtur; davet adresiyle aynı, doğrulanmış e-posta
  ile oturum gerekir (threat-model §4.1).

## 4. Ayarlanabilir kurallar (tenant düzeyi)

Okuma/güncelleme: `GET/PATCH /api/v1/tenant/settings` (`tenant.settings.manage`: owner, admin; ETag/If-Match; değerler
doğrulanır). Bu tablodaki kurallardan biri değişince `tenant.rules_changed`, diğer iş ayarları değişince
`tenant.settings_changed` audit olayı yazılır (#129). Kurallar her istekte okunur: değişiklik bir sonraki istekte
etkilidir (`GET /me` izinleri, uç kapısı `auth.disabled_by_tenant_rule`, `GET /members?permission=`). "2FA zorunlu" bayrağı
saklanır ve audit'lenir ancak **henüz zorlanmaz**: bu belge API tarafında zorlanabilir bir kural tanımlamıyor (öneri:
owner/admin için Supabase token'ında `aal = aal2` şartı; MFA kayıt ekranı ve kilitlenme kurtarma yolu olmadan açılırsa
owner'ı kilitler — ürün sahibi kararı).
| Kural | Varsayılan | Etkilediği izin |
|---|---|---|
| Satış personeli sipariş onaylayabilir | Açık (OD-09, 2026-11-09; yeni tenant varsayılanı, kapatılabilir) | orders.approve |
| Satış personeli iskonto sınırı | %0 | orders.price.override |
| Alış fiyatı görünürlüğü (manager, viewer) | Kapalı | prices.read.cost |
| Depo personeli barkod atayabilir | Kapalı | catalog.barcode.assign |
| Depo personeli mal kabul onaylayabilir | Kapalı | inventory.receive.approve |
| Stok eksiye düşmez (sabit kural, OD-09 — ayarlanamaz) | Her zaman | orders.approve, inventory.adjust |
| 2FA zorunlu (owner/admin) | Kapalı (öneri: açık) | Oturum |

## 5. Entitlement eşlemesi (özet)
| Özellik | Temel | Profesyonel | Ek modül | Not |
|---|---|---|---|---|
| Ürün, temel stok, sipariş, temel rapor | ✓ | ✓ | – | |
| Depo sayısı (`max_warehouses`, aktif depo) | 999 | Sınırsız | – | Temel: ürün sahibi kararı OD-02 (2026-11-09) |
| Depolar arası transfer, stok düzeltme | ✓ | ✓ | – | Paket düşürmede ve limit aşımında da açık (OD-03) |
| Barkod okutma, mobil toplama ve barkod doğrulaması (`barcode_operations`) | ✓ | ✓ | – | Ürün sahibi kararı OD-02 (2026-11-09): Temel pakette |
| Raf/lokasyon (`warehouse_locations`) | – | ✓ | – | |
| Kullanıcı sayısı (`max_users`) | 999 | 999 | – | Temel: OD-02. Profesyonel sayısı kararlaştırılmadı; Temel'in altına inmesin diye eşit |
| Ürün sayısı (`max_products`, arşivlenmemiş ürün) | Sınırsız | Sınırsız | – | OD-02 düzeltmesi (2026-11-09): ürün sınırı yok; kod yalnızca operatör override'ı için duruyor |
| AI WhatsApp asistanı | – | – | ✓ | Yokken manuel sipariş formu |
| Bayi portalı (`dealer_user`) | – | – | ✓ | MVP dışı |
| Seri/garanti | – | – | ✓ | MVP dışı |
| ERP entegrasyonu, resmî WhatsApp otomasyonu, gelişmiş analitik | – | – | ✓ | MVP dışı |
| Ledger, rezervasyon, audit, 2FA, CSV/Excel dışa aktarma, tam veri dışa aktarma, işlem geçmişi | ✓ | ✓ | – | **Asla kilitlenmez** |

Abonelik düşürüldüğünde/bittiğinde: veri silinmez; entitlement'ı kalkan özellik salt okunur olur. **Ürün sahibi kararı OD-03 (2026-11-09):** limit aşımında bile (ör. düşürme sonrası aktif depo sayısı > `max_warehouses`) depoyu boşaltmak için **transfer ve stok düzeltme her zaman yapılabilir**; bu uçlar hiçbir paket özelliği veya limitiyle kapılanmaz (`DowngradeOverLimitTests`: limit aşımında düzeltmeyle boşaltma + boşalan depoyu pasifleştirme; transfer/düzeltme uçlarında OpenAPI `x-entitlement` / `x-entitlement-limit` yok — iki aşamalı transfer #168 uçları da bu kurala tabidir).

**Uygulanan davranış (#51/#52 — ürün sahibi onayına sunulan kararlar):**
- Deneme: yeni tenant `Billing:TrialDays` (varsayılan 30) gün Profesyonel + tüm ek modüllerle başlar (SUB-05); bitişte Temel'e iner (`status = expired`). Pilot tenant'lara süresiz erişim (AQ-08) operatör override'ı ile verilir.
- Temel'e inince **çekirdek tam çalışır**: ürün, stok hareketleri (mal kabul/düzeltme/sevkiyat — lokasyonlu depoda da), sipariş, toplama, rapor, dışa aktarma, audit. Mevcut tüm depolar ve lokasyonlar okunur ve stok işlemlerinde kullanılır.
- **Salt okunur olanlar (yeni kullanım kapalı):** yeni aktif depo (aktif depo sayısı ≤ `max_warehouses`; pasif depoyu yeniden aktifleştirme de sayılır), lokasyon oluşturma/düzenleme ve depoda lokasyonu açma (`warehouse_locations`), yeni WhatsApp ayrıştırması (`ai_order_parsing`). Depo adı değiştirme, depoyu pasifleştirme, lokasyonu kapatma (`usesLocations = false`, stok yokken) her pakette açıktır (geri dönüş yolu).
- AI ek modülü kapalıyken mevcut ayrıştırmalar listelenir, okunur, **onaylanır/reddedilir** (sipariş akışında takılı kalmaz); yalnızca yeni ayrıştırma reddedilir.
- Ek modül "etkinleştirme" ödeme olmadan ücretsiz ve süreli (`Billing:ModuleTrialDays`, varsayılan 30) bir denemedir; modül başına bir kez; yalnızca owner (`billing.manage`), denetim kaydıyla. Paket değişikliği API'de yoktur.
- Limitler (katalog `2026-11-09.2`): **Temel `max_users` 999, `max_warehouses` 999, `max_products` sınırsız; `barcode_operations` (barkod okutma, mobil toplama) Temel'de de var (OD-02)**; Profesyonel depo ve ürün sınırsız, `max_users` 999; **AI ek modülü `ai_parses_per_month` 300 (OD-08)**; AI, tenant açıkça açana kadar kapalıdır (opt-in). `max_products` = arşivlenmemiş ürün: ürün oluşturma, arşivden geri alma ve ürün içe aktarma bu sınırla zorlanır (içe aktarmada sınırı aşan satırlar `entitlement.limit_exceeded` ile başarısız olur, iş devam eder); arşivleme yer açar; eşzamanlı oluşturmalar tenant başına kilitle sayılır. `max_users` = **aktif üye + açık (bekleyen, süresi dolmamış) davet**; davet oluşturma, davet yenileme, davet kabulü ve askıdaki üyeyi yeniden etkinleştirme bu sınırla zorlanır (#129); askıdaki üye ve iptal edilmiş/süresi dolmuş davet koltuk tutmaz. `barcode_operations` (Profesyonel) tanımlıdır, henüz bir uç kapılamaz.

## 6. Test gereksinimleri
- Her API ucu için rol × izin tablo-temelli yetki testi (bu dokümandaki matris kaynak alınır).
- Her uç için çapraz tenant erişim testi (başka tenant'ın kaydına ID ile erişim → 404).
- Entitlement kapalı özellik çağrısı → `403` + `code: entitlement.required` (api-strategy §6).
- Askıya alınan kullanıcının mevcut token'ı ile istek → 401/403.
