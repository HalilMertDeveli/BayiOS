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
- Görünürlük koşulu dışındaki kayıt 404 (varlık ipucu yok); görünür ama koşul dışı işlem 403 `auth.forbidden`.

## 4. Ayarlanabilir kurallar (tenant düzeyi)
| Kural | Varsayılan | Etkilediği izin |
|---|---|---|
| Satış personeli sipariş onaylayabilir | Açık | orders.approve |
| Satış personeli iskonto sınırı | %0 | orders.price.override |
| Alış fiyatı görünürlüğü (manager, viewer) | Kapalı | prices.read.cost |
| Depo personeli barkod atayabilir | Kapalı | catalog.barcode.assign |
| Depo personeli mal kabul onaylayabilir | Kapalı | inventory.receive.approve |
| Negatif stok politikası | İzin verme | orders.approve, inventory.adjust |
| 2FA zorunlu (owner/admin) | Kapalı (öneri: açık) | Oturum |

## 5. Entitlement eşlemesi (özet)
| Özellik | Temel | Profesyonel | Ek modül | Not |
|---|---|---|---|---|
| Ürün, temel stok, sipariş, temel rapor | ✓ | ✓ | – | |
| Tek depo | ✓ | ✓ | – | |
| Çoklu depo + transfer | – | ✓ | – | |
| Raf/lokasyon + barkod operasyonları (mobil toplama barkod doğrulaması) | – | ✓ | – | Öneri: Temel'de mobil toplama listesi var, barkod doğrulaması yok (ürün sahibi kararı) |
| Çoklu personel (kullanıcı sınırı) | Sınırlı | Geniş | – | Sayılar AQ-01 |
| AI WhatsApp asistanı | – | – | ✓ | Yokken manuel sipariş formu |
| Bayi portalı (`dealer_user`) | – | – | ✓ | MVP dışı |
| Seri/garanti | – | – | ✓ | MVP dışı |
| ERP entegrasyonu, resmî WhatsApp otomasyonu, gelişmiş analitik | – | – | ✓ | MVP dışı |
| Ledger, rezervasyon, audit, 2FA, CSV/Excel dışa aktarma, tam veri dışa aktarma, işlem geçmişi | ✓ | ✓ | – | **Asla kilitlenmez** |

Abonelik düşürüldüğünde/bittiğinde: veri silinmez; entitlement'ı kalkan özellik salt okunur olur (ör. çoklu depo: mevcut depolar görüntülenir, yeni transfer yapılamaz; ancak stoğu sıfırlamak için transfer/düzeltme izinli bırakılır — öneri, ürün sahibi kararı).

## 6. Test gereksinimleri
- Her API ucu için rol × izin tablo-temelli yetki testi (bu dokümandaki matris kaynak alınır).
- Her uç için çapraz tenant erişim testi (başka tenant'ın kaydına ID ile erişim → 404).
- Entitlement kapalı özellik çağrısı → `403` + `code: entitlement.required` (api-strategy §6).
- Askıya alınan kullanıcının mevcut token'ı ile istek → 401/403.
