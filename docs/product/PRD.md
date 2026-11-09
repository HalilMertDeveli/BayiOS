# BayiOS — Ürün Gereksinimleri Dokümanı (PRD)

| Alan | Değer |
|---|---|
| Sürüm | 0.1 (MVP taslağı) |
| Tarih | 2026-11-04 |
| Sahip | Product Manager (Orkestra Şefi'ne raporlar) |
| Durum | Ürün sahibi onayı bekliyor |
| İlgili | `personas.md`, `sector-scenarios.md`, `user-flows.md`, `roles-permissions.md`, `../design/design-system-plan.md`, `../../BACKLOG.md` |

> Ürün vaadi: **"WhatsApp'tan gelen siparişten depodan çıkan son ürüne kadar işletmenin bütün stok ve sipariş operasyonlarını basitçe yönet."**

Bu dokümandaki tüm işletme adları, ürünler, fiyatlar ve kişiler sentetiktir.

---

## 1. Problem

Türkiye'deki KOBİ ölçekli B2B toptancı ve bayiler (yedek parça, elektronik, hırdavat vb.) siparişlerin büyük kısmını WhatsApp, telefon ve sözlü yollarla alır. Tipik durum:

| Problem | Sonuç |
|---|---|
| Siparişler WhatsApp mesajlarında dağınık; ürün adları serbest metin ("CG 125 debriyaj balatası 3 takım") | Yanlış ürün/adet, kaybolan sipariş, ofiste elle yeniden yazma |
| Stok Excel'de ya da kafada; fiziksel / rezerve / satılabilir ayrımı yok | Aynı ürünün iki müşteriye satılması (çift satış), "var" denilen ürünün rafta olmaması |
| Raf/lokasyon bilgisi yok | Toplama süresi uzun, deneyimli personele bağımlılık |
| Ürün kataloğu binlerce satır, sektöre özel alanlar (uyumlu model, pixel pitch) yapılandırılmamış | Doğru parçayı bulmak zor, yanlış parça gönderimi ve iade |
| Genel ERP'ler ağır, pahalı, kurulumu aylar sürer; basit stok uygulamaları sektör ihtiyacını karşılamaz | Dijitalleşme ertelenir |
| Hareket geçmişi yok | Sayım farkı, kayıp, hata kaynağı bulunamaz |

## 2. Hedefler ve hedef dışı olanlar

### 2.1 Hedefler (MVP)
1. **G1 — Stok doğruluğu:** Her stok değişikliği hareket defterinden (ledger) gelir; çift satış sistemsel olarak engellenir.
2. **G2 — Sipariş hızı:** WhatsApp mesajından onaylanabilir taslak siparişe 2 dakikanın altında geçiş.
3. **G3 — Hızlı başlangıç:** Yeni bir işletme kayıttan ilk siparişe aynı gün (≤ 1 iş günü) ulaşır; 10.000 satırlık katalog toplu içe aktarılabilir.
4. **G4 — Depo verimliliği:** Mobil toplama listesi, raf yönlendirmesi ve barkod doğrulaması ile hatalı toplama oranını düşürmek.
5. **G5 — Tek kod tabanı, çok sektör:** Motosiklet yedek parça ve LED/elektronik pilotları; sektör farkları yalnızca şablon, özel alan, rol, kural ve modülle karşılanır.
6. **G6 — Modüler gelir altyapısı:** Paket ve ek modül yetkilendirmesi (entitlement) backend'de zorlanır; ödeme altyapısı sonraya bırakılır.

### 2.2 Hedef dışı (MVP'de yapılmayacak)
- Muhasebe, e-Fatura/e-Arşiv, e-İrsaliye üretimi (yalnızca ileride entegrasyon).
- Canlı ödeme tahsilatı, abonelik faturalama (yalnızca entitlement/feature-flag altyapısı).
- Resmî WhatsApp Business API ile otomatik mesaj alma/gönderme.
- Bayi portalı (dealer self-servis sipariş).
- Seri numarası / garanti takibi modülü (yalnızca veri modeli hazırlığı; bkz. Açık Sorular).
- ERP entegrasyonları (Logo, Mikro, Netsis vb.).
- Gelişmiş analitik, tahminleme, otomatik satın alma önerisi.
- Kargo firması entegrasyonu (kargo takip no elle girilir).
- Lot/parti, son kullanma tarihi takibi.
- Çoklu dil arayüz (Türkçe önce; İngilizce altyapı hazır ama çevrilmez).
- Tenant'a özel kod/özelleştirme.

## 3. Hedef kullanıcılar

Ayrıntı: `personas.md`.

| Persona | Rol (sistem) | Ana istemci |
|---|---|---|
| İşletme sahibi | owner | Web (masaüstü + mobil tarayıcı) |
| Ofis / satış personeli (WhatsApp siparişi alan) | sales | Web (masaüstü) |
| Operasyon / depo yöneticisi | manager | Web + Flutter |
| Depo toplayıcı / sevkiyatçı | warehouse_staff | Flutter (Android ağırlıklı) |
| Sistem yöneticisi / güvenilir çalışan | admin | Web |
| Muhasebeci / dış danışman | viewer | Web |
| Bayi müşteri (gelecek) | dealer_user (rezerve) | Bayi portalı (MVP dışı) |

## 4. Kapsam

### 4.1 MVP kapsamında
| Modül | MVP kapsamı |
|---|---|
| 01 Tenant & Onboarding | Kayıt, sektör seçimi (şablon), logo, para birimi, KDV, birimler, personel daveti, sabit roller, demo veri |
| 02 Katalog | SKU, barkod, marka, kategori, varyant, alış/satış fiyatı, birimler, tedarikçi, görsel, sektör özel alanları, durum, çoklu fiyat listesi, bayi fiyatı |
| 03 Toplu içe aktarma | Excel/CSV, eşleme sihirbazı, AI kolon önerisi, doğrulama, önizleme, satır hataları, yeniden yüklemede tekrar önleme, stok/fiyat ayrı içe aktarma, arka plan işi, geri alma, 10k satır |
| 04 Stok & Depo | Fiziksel/rezerve/satılabilir, çoklu depo, raf, mal kabul, sayım, düzeltme, transfer, iade, hasar, hareket geçmişi; stok eksiye düşmez (OD-09) |
| 05 Siparişler | Manuel / WhatsApp metninden / web; durum makinesi; iptal; kısmi sevkiyat; idempotent işlemler |
| 06 WhatsApp asistanı v1 | Yapıştırılan mesajı AI ile ayrıştırma, yalnızca tenant kataloğuyla eşleşme, belirsizlik işaretleme, zorunlu insan onayı |
| 07 Depo görevleri | Toplama görevi atama, mobil toplama listesi, raf görünümü, barkod okuma, eksik bildirimi, paket kontrolü, sevkiyat onayı |
| 08 Müşteriler & bayiler | Müşteri/adres, bayi grupları, özel fiyat, sipariş geçmişi |
| 09 Raporlar | Günlük siparişler, stok seviyeleri, kritik stok, depo görevleri, en çok satanlar, hareketler, CSV/Excel dışa aktarma |
| 10 SaaS & modüller | Paket/ek modül entitlement altyapısı, modül mağazası görünümü (ödeme yok) |
| Platform | Çok kiracılılık, kimlik, denetim kaydı (audit), bildirim (FCM), arka plan işleri |

### 4.2 Kapsam dışı (açık liste)
1. Canlı ödeme / kart tahsilatı / otomatik fatura kesimi.
2. Resmî WhatsApp Business API (mesaj alma, otomatik yanıt, şablon mesaj).
3. Bayi portalı ve `dealer_user` rolünün aktif kullanımı.
4. Seri numarası ve garanti takibi iş akışları (LED pilotunda seri no serbest alan olarak tutulabilir; bkz. AQ-03).
5. ERP / muhasebe / e-Fatura entegrasyonları.
6. Kargo API entegrasyonları ve etiket basımı.
7. Satın alma siparişi (PO) yönetimi — mal kabul PO'suz yapılır.
8. Lot/SKT, seri bazlı FIFO maliyetleme, stok değerleme raporları (maliyet = son alış fiyatı bilgisi).
9. Özelleştirilebilir roller (MVP'de sabit 6 rol).
10. Gelişmiş analitik, BI panoları, tahminleme.
11. Çevrimdışı tam çalışan mobil uygulama (MVP: kısa bağlantı kesintilerine dayanıklı kuyruk; bkz. NFR-MOB-02).
12. Pazar yeri (Trendyol, Hepsiburada vb.) entegrasyonları.
13. Çoklu şirket/şube konsolidasyonu (bir kullanıcı birden çok tenant'a üye olabilir ama konsolide rapor yok).

---

## 5. Fonksiyonel gereksinimler

Öncelik: **P0** = MVP için zorunlu, **P1** = MVP'de hedeflenir / pilot sırasında eklenebilir, **P2** = MVP sonrası.

### 5.1 Modül 01 — Tenant & Onboarding (TEN)
| ID | Gereksinim | Öncelik |
|---|---|---|
| TEN-01 | Kullanıcı e-posta + şifre ile kaydolur, e-posta doğrulaması yapılır (Supabase Auth). | P0 |
| TEN-02 | Kayıt sonrası işletme (tenant) oluşturulur; oluşturan kullanıcı `owner` olur. | P0 |
| TEN-03 | Sektör seçimi: Motosiklet yedek parça, LED/Elektronik, Genel. Seçim; varsayılan kategorileri, özel alanları, birimleri ve demo veri setini yükler. | P0 |
| TEN-04 | İşletme profili: ad, logo (PNG/JPG/SVG ≤ 2 MB), vergi no (opsiyonel), adres, telefon. | P0 |
| TEN-05 | Para birimi (varsayılan TRY), varsayılan KDV oranı (%20), fiyatların KDV dahil/hariç gösterimi ayarı. | P0 |
| TEN-06 | Birim yönetimi: adet, kutu, koli, takım, metre, litre, kg; dönüşüm katsayısı (ör. 1 kutu = 10 adet). | P0 |
| TEN-07 | Personel daveti e-posta ile; davet rolü seçilir; davet 7 gün geçerli, iptal edilebilir. | P0 |
| TEN-08 | Üye rolü değiştirme, üyeliği askıya alma; son `owner` kaldırılamaz. | P0 |
| TEN-09 | Demo veri yükleme ve "demo veriyi temizle" (yalnızca demo etiketli kayıtlar, tek işlem). | P1 |
| TEN-10 | Onboarding kontrol listesi (logo, ilk depo, ürün içe aktarma, personel daveti, ilk sipariş) ilerleme göstergesi. | P1 |
| TEN-11 | Bir kullanıcı birden çok tenant'a üye olabilir; tenant seçici. | P1 |
| TEN-12 | Stok hiçbir zaman eksiye düşmez (ürün sahibi kararı OD-09, 2026-11-09): onay, düzeltme ve sevkiyat satılabilir stoğun altına inemez. Tenant ayarı değildir; ayarlarda sabit kural olarak gösterilir (eski `uyar` / `izin ver` seçenekleri kaldırıldı). | P0 |

### 5.2 Modül 02 — Katalog (CAT)
| ID | Gereksinim | Öncelik |
|---|---|---|
| CAT-01 | Ürün oluşturma/düzenleme: ad, SKU (tenant içinde benzersiz), barkod(lar), marka, kategori, açıklama, birim, alış fiyatı, satış fiyatı, KDV oranı, tedarikçi, durum. | P0 |
| CAT-02 | Durumlar: Aktif / Pasif (satışa kapalı, stokta görünür) / Arşiv (listelerde gizli, geçmiş korunur). Ürün fiziksel silinmez. | P0 |
| CAT-03 | Birden fazla barkod/alternatif kod (OEM no, tedarikçi kodu) tanımlama; tümü aramada ve eşleşmede kullanılır. | P0 |
| CAT-04 | Varyantlar (ör. renk, ölçü): her varyant kendi SKU, barkod, fiyat ve stoğuna sahiptir. | P1 |
| CAT-05 | Sektör özel alanları: tip (metin, sayı, seçim, çoklu seçim, evet/hayır, tarih), zorunluluk, filtrelenebilirlik; şablondan gelir, tenant ekleyebilir. | P0 |
| CAT-06 | Uyumluluk listesi (motosiklet): ürün ↔ marka/model/yıl aralığı çoklu eşleşme; uyumluluğa göre filtre. | P0 |
| CAT-07 | Ürün görselleri (≤ 5 adet, her biri ≤ 5 MB, otomatik küçük boyut üretimi). | P1 |
| CAT-08 | Çoklu fiyat listesi (ör. Perakende, Bayi A, Bayi B); liste bazlı ürün fiyatı veya yüzde iskonto kuralı. | P0 |
| CAT-09 | Müşteri/bayi grubuna fiyat listesi atama; siparişte fiyat otomatik gelir. | P0 |
| CAT-10 | Ürün listesi: tam metin arama (ad, SKU, barkod, alternatif kod, marka, özel alan), filtre (kategori, marka, durum, stok durumu, özel alan), sıralama, sayfalama. | P0 |
| CAT-11 | Kategori ağacı (≤ 4 seviye), marka ve tedarikçi listeleri. | P0 |
| CAT-12 | Kritik stok seviyesi (min) ürün/depo bazında. | P0 |
| CAT-13 | Ürün değişiklik geçmişi (kim, ne zaman, hangi alan; fiyat geçmişi dahil). | P0 |
| CAT-14 | Toplu işlem: seçili ürünlerde durum, kategori, fiyat listesi güncelleme. | P1 |

### 5.3 Modül 03 — Toplu içe aktarma (IMP)
| ID | Gereksinim | Öncelik |
|---|---|---|
| IMP-01 | .xlsx, .csv (UTF-8 / Windows-1254, `;` ve `,` ayırıcı otomatik tespit) yükleme; ≤ 20 MB (ürün sahibi kararı OD-04, 2026-11-09; `.xls` onaylanmadı), ≤ 50.000 satır (uygulanan MVP iş sınırı: ≤ 10.000 satır/dosya). | P0 |
| IMP-02 | İçe aktarma türleri ayrı: **Ürün**, **Stok (açılış/sayım)**, **Fiyat listesi**, **Müşteri**. | P0 |
| IMP-03 | Kolon eşleme sihirbazı: kaynak kolon → hedef alan; eşleme şablonu kaydedilir ve sonraki yüklemelerde önerilir. | P0 |
| IMP-04 | AI kolon önerisi: başlık ve örnek değerlere göre eşleme önerisi + güven skoru; kullanıcı onaylamadan uygulanmaz. Hiçbir satır verisi AI tarafından üretilmez/değiştirilmez. | P1 |
| IMP-05 | Tip doğrulama: sayı, ondalık (virgül/nokta), para, birim, barkod formatı (EAN-8/13, UPC), zorunlu alan. | P0 |
| IMP-06 | Eşleşme anahtarı: SKU (varsayılan) veya barkod; mevcut ürün → güncelle, yok → oluştur; mod seçilebilir (yalnızca oluştur / yalnızca güncelle / ikisi). | P0 |
| IMP-07 | Önizleme: ilk 100 satır + özet (oluşturulacak / güncellenecek / hatalı / atlanacak sayıları). | P0 |
| IMP-08 | Satır bazlı hata listesi (satır no, kolon, hata, öneri); hatalı satırlar Excel olarak indirilebilir; hatasız satırlar devam ettirilebilir. | P0 |
| IMP-09 | Tekrar önleme: aynı dosya (içerik hash) yeniden yüklenirse uyarı; dosya içi yinelenen SKU/barkod tespiti. | P0 |
| IMP-10 | Büyük dosyalar arka plan işi olarak çalışır; ilerleme yüzdesi, tamamlandığında uygulama içi + e-posta bildirimi. | P0 |
| IMP-11 | Stok içe aktarma, doğrudan bakiye yazmaz; her satır için `import` tipli stok hareketi üretir. | P0 |
| IMP-12 | Geri alma: tamamlanan içe aktarma 7 gün içinde geri alınabilir (oluşturulan ürünler arşivlenir, güncellenen alanlar önceki değere döner, stok hareketleri ters kayıtla iptal edilir). Sonrasında işlem görmüş ürünler için uyarı. | P0 |
| IMP-13 | İçe aktarma geçmişi: dosya adı, kullanıcı, tarih, sonuç sayıları, rapor indirme. | P0 |
| IMP-14 | Şablon dosya indirme (sektör şablonuna göre kolonlar). | P0 |

### 5.4 Modül 04 — Stok & Depo (INV)
| ID | Gereksinim | Öncelik |
|---|---|---|
| INV-01 | Stok bakiyesi ürün × depo (× raf) için: **Fiziksel**, **Rezerve**, **Satılabilir = Fiziksel − Rezerve**. | P0 |
| INV-02 | Tek doğruluk kaynağı hareket defteri; her hareket: tip, miktar (+/−), ürün, depo, raf, referans belge, kullanıcı, zaman, açıklama. Hareketler değiştirilemez; düzeltme ters kayıtla yapılır. | P0 |
| INV-03 | Çoklu depo: oluşturma, varsayılan depo, pasifleştirme (stoğu olan depo pasifleştirilemez). | P0 |
| INV-04 | Raf/lokasyon: depo içinde hiyerarşik kod (Koridor-Raf-Göz, ör. A-03-2); ürün için varsayılan raf; raf barkodu. | P0 (Pro paket) |
| INV-05 | Mal kabul: tedarikçi, belge no, satırlar (ürün, miktar, birim, raf, alış fiyatı opsiyonel); taslak → onay; onayda giriş hareketi. | P0 |
| INV-06 | Stok sayımı: tam veya kısmi (kategori/raf); sayım sırasında sistem miktarı gizlenebilir (kör sayım); fark raporu; onayda düzeltme hareketi. | P0 |
| INV-07 | Manuel stok düzeltme: zorunlu sebep kodu (sayım farkı, hasar, kayıp, bulundu, diğer) + açıklama. | P0 |
| INV-08 | Depolar arası transfer: çıkış (yolda) → varış kabulü; kabul edilen miktar farkı raporlanır. | P0 (çoklu depo Pro) |
| INV-09 | Müşteri iadesi: siparişe bağlı veya bağımsız; durum: satılabilir / hasarlı; satılabilir ise stoğa girer. | P0 |
| INV-10 | Hasar/fire kaydı: hasarlı stoğa taşıma veya imha çıkışı. | P1 |
| INV-11 | Hareket geçmişi: ürün, depo, tarih, tip, kullanıcı filtreleri; CSV/Excel dışa aktarma. | P0 |
| INV-12 | Rezervasyon sipariş **onayında** oluşur; satılabilir yetersizse politika uygulanır (izin verme → onay engellenir, eksik satırlar gösterilir). | P0 |
| INV-13 | Fiziksel düşüm **sevkiyat onayında** yapılır; rezervasyon aynı transaction'da kapanır. | P0 |
| INV-14 | Eşzamanlılık: aynı ürün için paralel onaylarda satılabilir miktar asla aşılamaz (iyimser/kötümser kilit; testle kanıtlanır). | P0 |
| INV-15 | Ürün detayında depo bazlı stok ve son 50 hareket. | P0 |
| INV-16 | Birim dönüşümü: hareket girilen birimle kaydedilir, temel birime çevrilerek bakiye hesaplanır. | P0 |

### 5.5 Modül 05 — Siparişler (ORD)
| ID | Gereksinim | Öncelik |
|---|---|---|
| ORD-01 | Sipariş oluşturma kaynakları: Manuel (web), WhatsApp metninden (modül 06), Mobil (P2). Kaynak siparişte saklanır. | P0 |
| ORD-02 | Sipariş başlığı: müşteri, teslimat adresi, depo, teslim tarihi (opsiyonel), not, fiyat listesi. | P0 |
| ORD-03 | Satır: ürün, birim, miktar, birim fiyat (fiyat listesinden; yetkiye göre değiştirilebilir), satır iskontosu (% / tutar), KDV oranı, satır toplamı. | P0 |
| ORD-04 | Belge altı: ara toplam, iskonto, KDV dökümü (oran bazında), genel toplam; TRY, 2 ondalık, bankacı yuvarlaması değil — standart yuvarlama (yarım yukarı). | P0 |
| ORD-05 | Durum makinesi: Taslak → Onay Bekliyor → Onaylandı → Toplanıyor → Paketlendi → Sevk Edildi → Tamamlandı; İptal (Sevk Edildi öncesi her durumdan). Geçersiz geçişler API'de reddedilir. | P0 |
| ORD-06 | Onaylandı'ya geçişte rezervasyon (INV-12); satılabilir yetersizse onay engellenir veya kullanıcı kısmi onay seçer (P1). | P0 |
| ORD-07 | İptal: sebep zorunlu; rezervasyon serbest bırakılır; toplama görevi varsa iptal edilir, toplanmış ürünler için "rafa geri koy" görevi oluşur. | P0 |
| ORD-08 | Kısmi sevkiyat: sevk edilen miktar < sipariş miktarı ise sipariş "Kısmi Sevk" alt durumunda kalır; kalan rezervasyon korunur veya kullanıcı kalanı iptal eder. | P1 |
| ORD-09 | İdempotentlik: oluşturma ve durum geçişi istekleri `Idempotency-Key` başlığı ile; aynı anahtar tekrarında aynı sonuç döner, çift sipariş/çift rezervasyon oluşmaz. | P0 |
| ORD-10 | Onay kuralı: tenant ayarı — `sales` rolü siparişi kendisi onaylayabilir mi, yoksa `manager+` onayı mı gerekir; tutar eşiği (P1). | P0 |
| ORD-11 | Sipariş listesi: durum sekmeleri, arama (no, müşteri, ürün), tarih filtresi, kaynak filtresi. | P0 |
| ORD-12 | Sipariş detayında zaman çizelgesi (durum değişiklikleri, kim/ne zaman), bağlı stok hareketleri, orijinal WhatsApp metni. | P0 |
| ORD-13 | Sipariş PDF/yazdırma (proforma / sevk listesi). | P1 |
| ORD-14 | Sipariş numarası tenant bazında sıralı, önek ayarlanabilir (ör. SIP-2026-000123). | P0 |
| ORD-15 | Sevkiyat bilgisi: kargo firması (serbest liste), takip no, koli sayısı. | P0 |
| ORD-16 | Onay sonrası satır düzenleme: yalnızca `manager+`; rezervasyon farkı otomatik güncellenir. | P1 |

### 5.6 Modül 06 — WhatsApp asistanı v1 (WAI) — ek modül
| ID | Gereksinim | Öncelik |
|---|---|---|
| WAI-01 | Kullanıcı WhatsApp mesajını (veya herhangi bir serbest metni) metin alanına yapıştırır; opsiyonel müşteri seçimi. | P0 |
| WAI-02 | AI ayrıştırma: ürün adı/kodu, miktar, birim, marka/model/yıl, varyant ipuçları; çok satırlı ve tek satırlı mesajlar. | P0 |
| WAI-03 | Eşleme **yalnızca tenant kataloğu** üzerinde (SKU, barkod, alternatif kod, ad, uyumluluk, özel alan). Katalogda olmayan ürün "eşleşmedi" olarak işaretlenir; AI yeni SKU, fiyat veya stok bilgisi üretmez. | P0 |
| WAI-04 | Her satır için güven seviyesi: Kesin / Belirsiz (birden çok aday — en fazla 5 aday gösterilir) / Eşleşmedi. Belirsiz ve eşleşmeyen satırlar kullanıcı seçimi olmadan taslağa geçmez. | P0 |
| WAI-05 | Fiyat her zaman müşterinin fiyat listesinden, stok her zaman stok modülünden gelir; mesajdaki fiyat ifadeleri ("geçen seferki fiyattan") yalnızca not olarak gösterilir. | P0 |
| WAI-06 | Sonuç **Taslak** sipariş olarak kaydedilir; onay her zaman insan tarafından yapılır. | P0 |
| WAI-07 | Orijinal metin siparişe eklenir; kullanıcının düzeltmeleri (hangi aday seçildi) tenant içi eşleşme öğrenmesi için saklanır (eş anlamlı sözlük). | P1 |
| WAI-08 | Müşteri adı/telefonu mesajda geçiyorsa mevcut müşteriyle eşleşme önerisi; yeni müşteri otomatik oluşturulmaz. | P1 |
| WAI-09 | Kişisel veri en aza indirme: AI sağlayıcıya yalnızca mesaj metni gönderilir (katalog, fiyat ve müşteri verisi gönderilmez; eşleştirme sunucuda deterministik — ADR 0010); telefon numaraları maskelenir; sağlayıcıda eğitim için veri saklanmaz (sözleşme/ayar). | P0 |
| WAI-10 | Ayrıştırma süresi p95 ≤ 8 sn (≤ 30 satırlık mesaj); hata durumunda manuel sipariş formuna metinle birlikte geçiş. | P0 |
| WAI-11 | Kullanım kotası (tenant/ay) ve sayaç; kota aşımında açık mesaj. | P1 |
| WAI-12 | Resmî WhatsApp Business API ile otomatik alım. | P2 |

### 5.7 Modül 07 — Depo görevleri (WHT)
| ID | Gereksinim | Öncelik |
|---|---|---|
| WHT-01 | Onaylanan sipariş için toplama görevi oluşturulur (otomatik veya manuel; tenant ayarı). | P0 |
| WHT-02 | Görev atama: belirli personele veya ortak havuza; personel havuzdan görev alabilir. | P0 |
| WHT-03 | Mobil görev listesi: atanan/havuz, öncelik, müşteri, satır sayısı, teslim tarihi. | P0 |
| WHT-04 | Toplama ekranı: satırlar raf sırasına göre (rota), ürün görseli, raf kodu, istenen/toplanan miktar. | P0 |
| WHT-05 | Barkod okuma (kamera ve harici/el terminali klavye-wedge): yanlış ürün okutulursa sesli + titreşimli uyarı; miktar barkod başına +1 veya elle giriş. | P0 |
| WHT-06 | Eksik bildirimi: rafta bulunamadı / hasarlı / eksik miktar; görev "eksikli" işaretlenir, yönetici bildirimi alır. | P0 |
| WHT-07 | Paket kontrolü: toplanan ürünlerin paketleme aşamasında (opsiyonel ikinci okutma) doğrulanması, koli sayısı. | P1 |
| WHT-08 | Sevkiyat onayı (mobil veya web): fiziksel stok düşümü, kargo bilgisi. | P0 |
| WHT-09 | Raf görünümü: bir raf kodu okutulduğunda raftaki ürünler ve miktarlar. | P1 |
| WHT-10 | Stok sorgulama (mobil): barkod/SKU/ad ile ürün, depo-raf bazlı stok. | P0 |
| WHT-11 | Mobil mal kabul: barkod okutarak satır ekleme, raf seçimi. | P1 |
| WHT-12 | Görev push bildirimi (FCM) yeni atamada. | P1 |
| WHT-13 | Görev süre ölçümü (başlangıç/bitiş) raporlara beslenir. | P1 |

### 5.8 Modül 08 — Müşteriler & bayiler (CUS)
| ID | Gereksinim | Öncelik |
|---|---|---|
| CUS-01 | Müşteri kartı: ünvan, yetkili, telefon (WhatsApp), e-posta, vergi dairesi/no, birden çok adres, not, durum. | P0 |
| CUS-02 | Müşteri grubu / bayi grubu ve atanmış fiyat listesi. | P0 |
| CUS-03 | Müşteriye özel ürün fiyatı (fiyat listesini ezer). | P1 |
| CUS-04 | Müşteri sipariş geçmişi ve en çok aldığı ürünler. | P0 |
| CUS-05 | Telefonla hızlı arama (WhatsApp mesajındaki numara ile eşleşme). | P0 |
| CUS-06 | Cari bakiye / vade takibi. | P2 (kapsam dışı) |

### 5.9 Modül 09 — Raporlar (REP)
| ID | Gereksinim | Öncelik |
|---|---|---|
| REP-01 | Gösterge paneli: bugünkü siparişler (durum bazlı), bekleyen onaylar, açık depo görevleri, kritik stok sayısı. | P0 |
| REP-02 | Stok seviyesi raporu (depo, kategori, marka filtreleri). | P0 |
| REP-03 | Kritik stok raporu (satılabilir ≤ min). | P0 |
| REP-04 | Günlük/aralık sipariş raporu (adet, tutar, kaynak, kullanıcı). | P0 |
| REP-05 | Depo görev raporu (personel bazlı tamamlanan görev, ortalama süre, eksik bildirimleri). | P1 |
| REP-06 | En çok satan ürünler (adet/tutar). | P1 |
| REP-07 | Stok hareket raporu. | P0 |
| REP-08 | Tüm raporlar ve listeler CSV/Excel dışa aktarılabilir; dışa aktarma hiçbir pakette kilitlenmez. | P0 |
| REP-09 | Tam veri dışa aktarma (tüm tenant verisi, ZIP içinde CSV) — `owner`. | P0 |

### 5.10 Modül 10 — SaaS & modüller (SUB)
| ID | Gereksinim | Öncelik |
|---|---|---|
| SUB-01 | Paketler: **Temel** (ürün, temel stok, sipariş, temel depo, temel rapor), **Profesyonel** (gelişmiş depo, raf+barkod, çoklu personel, çoklu depo). | P0 |
| SUB-02 | Ek modüller: AI WhatsApp asistanı, Bayi portalı, Seri/Garanti, ERP entegrasyonları, Resmî WhatsApp otomasyonu, Gelişmiş analitik. | P0 (altyapı) |
| SUB-03 | Entitlement kontrolü backend'de (API) yapılır; istemci yalnızca görünürlüğü ayarlar. Yetkisiz özellik çağrısı `403` + `code: entitlement.required` döner (api-strategy §6). | P0 |
| SUB-04 | Modül mağazası sayfası: aktif paket, modüller, açıklama, "talep et" (ödeme yok; talep kaydı + iletişim). | P1 |
| SUB-05 | Pilot/deneme süresi: tenant oluşturulduğunda tüm modüller 30 gün açık (ayarlanabilir); süre bitiminde Temel'e iner. | P0 |
| SUB-06 | Abonelik bittiğinde/düşürüldüğünde veri silinmez; ilgili özellik salt okunur olur; dışa aktarma her zaman açık. | P0 |
| SUB-07 | Ücret duvarı dışı (her pakette): güvenlik (2FA, audit), temel stok doğruluğu (ledger, rezervasyon), veri dışa aktarma, işlem geçmişi. | P0 |
| SUB-08 | Platform yöneticisi (iç kullanım) tenant entitlement'ını değiştirebilir; değişiklik denetim kaydına yazılır. | P0 |
| SUB-09 | Paket limitleri: Temel 999 kullanıcı, 999 aktif depo, sınırsız ürün; barkod okutma ve mobil toplama Temel pakette (ürün sahibi kararı OD-02, 2026-11-09); AI ek modülü ayda 300 ayrıştırma (OD-08); limit aşımında depoyu boşaltmak için transfer/düzeltme açık kalır (OD-03). | P1 |

### 5.11 Platform & Güvenlik (PLT / SEC)
| ID | Gereksinim | Öncelik |
|---|---|---|
| PLT-01 | Her kayıt `tenant_id` taşır; API her istekte üyelik + rol + entitlement doğrular. | P0 |
| PLT-02 | Web ve mobil yalnızca API üzerinden yazar. | P0 |
| PLT-03 | Denetim kaydı (audit log): kim, ne zaman, hangi kayıt, önce/sonra; tenant içinden görüntülenebilir (`admin+`). | P0 |
| PLT-04 | Arka plan işleri (import, export, bildirim) kuyruklu, yeniden denenebilir, durum izlenebilir. | P0 |
| PLT-05 | Bildirim merkezi (uygulama içi) + FCM push (mobil) + e-posta (davet, import sonucu). | P1 |
| SEC-01 | Oturum: JWT, kısa ömürlü erişim + yenileme; mobilde güvenli depolama. | P0 |
| SEC-02 | 2FA (TOTP) — `owner`/`admin` için önerilir, tenant zorunlu kılabilir. | P1 |
| SEC-03 | Hız sınırlama (rate limit) tenant ve kullanıcı bazında; kimlik denemelerinde kilitleme. | P0 |
| SEC-04 | KVKK: aydınlatma metni, veri işleme kaydı, veri silme talebi süreci (owner talebiyle, onaylı). | P0 |
| SEC-05 | Yedekleme: günlük, 30 gün saklama; geri yükleme tatbikatı pilot öncesi. | P0 |

---

## 6. Fonksiyonel olmayan gereksinimler (NFR)

### 6.1 Performans
| ID | Hedef | Koşul |
|---|---|---|
| NFR-PERF-01 | Ürün listesi arama/filtre yanıtı **p95 < 500 ms** (API) | Tenant başına 10.000 ürün, 3 depo |
| NFR-PERF-02 | Ürün listesi ilk görüntü (web) **LCP < 2,5 sn** | 4G, orta seviye dizüstü |
| NFR-PERF-03 | 10.000 satırlık ürün içe aktarma **< 2 dk** uçtan uca (doğrulama + yazma) | Arka plan işi |
| NFR-PERF-04 | Sipariş onayı (rezervasyon dahil) **p95 < 800 ms** | 50 satırlık sipariş |
| NFR-PERF-05 | Barkod okutma → ekranda sonuç **< 300 ms** (yerel eşleşme) / < 700 ms (API) | Mobil, Wi-Fi |
| NFR-PERF-06 | WhatsApp ayrıştırma **p95 ≤ 8 sn** | ≤ 30 satırlık mesaj |
| NFR-PERF-07 | Genel API okuma uçları **p95 < 300 ms**, yazma **p95 < 600 ms** | Pilot yükü |
| NFR-PERF-08 | Eşzamanlılık: aynı SKU için 20 paralel onay isteğinde satılabilir stok aşılmaz | Otomatik test |

### 6.2 Kullanılabilirlik & erişilebilirlik
| ID | Gereksinim |
|---|---|
| NFR-UX-01 | **WCAG 2.2 AA** (kontrast ≥ 4.5:1 metin, 3:1 UI; klavye erişimi; odak görünürlüğü; hedef boyutu ≥ 24×24 px web, ≥ 48×48 dp mobil). |
| NFR-UX-02 | Arayüz **önce Türkçe**; metinler i18n anahtarlarıyla (sonradan İngilizce eklenebilir). Türkçe karakterlerde arama/sıralama doğru (İ/ı, Ş/ş, Ğ/ğ; `tr-TR` collation). |
| NFR-UX-03 | Sayı/para biçimi `tr-TR`: `1.234,56 ₺`; tarih `GG.AA.YYYY`, saat 24 saat, saat dilimi varsayılan `Europe/Istanbul`. |
| NFR-UX-04 | Varsayılan para birimi **TRY**; ürün ve sipariş bazında tek para birimi (MVP). Döviz (USD/EUR) alış fiyatı bilgi amaçlı P1. |
| NFR-UX-05 | **KDV oranları**: %0, %1, %10, %20 tanımlı; varsayılan %20; tenant ek oran tanımlayabilir; oran ürün ve satır bazında. |
| NFR-UX-06 | Web: masaüstü öncelikli, 360 px genişliğe kadar duyarlı. Mobil: Android 10+, iOS 16+. |
| NFR-UX-07 | Her liste/ekran için boş, yükleniyor, hata durumu tanımlı. |

### 6.3 Güvenilirlik & operasyon
| ID | Hedef |
|---|---|
| NFR-REL-01 | Pilot dönemi erişilebilirlik **%99,5 / ay** (planlı bakım hariç, mesai dışı duyurulu). GA hedefi %99,9. |
| NFR-REL-02 | RPO ≤ 24 saat (pilot), ≤ 1 saat (GA, PITR); RTO ≤ 4 saat. |
| NFR-REL-03 | Stok bakiyesi ile ledger toplamı tutarlılık kontrolü gece çalışır; fark = 0 olmalı, fark varsa alarm. |
| NFR-REL-04 | Yapılandırılmış log, korelasyon ID, hata izleme; kişisel veri loglanmaz. |
| NFR-MOB-01 | Mobilde barkod okutma düşük ışıkta ve hasarlı etikette; harici okuyucu desteği. |
| NFR-MOB-02 | Kısa bağlantı kopmalarında (≤ 5 dk) toplama işlemleri yerel kuyrukta tutulur, bağlantıda idempotent gönderilir. |

### 6.4 Güvenlik & uyum
| ID | Gereksinim |
|---|---|
| NFR-SEC-01 | Çapraz tenant erişimi = kritik hata; her uç için otomatik tenant izolasyon testi. |
| NFR-SEC-02 | Aktarımda TLS 1.2+, dinlenmede şifreleme (sağlayıcı düzeyi). |
| NFR-SEC-03 | KVKK uyumu; veri Türkiye/AB bölgesinde barındırma tercihi (AQ-05). |
| NFR-SEC-04 | Gizli bilgiler repo'da yok; `.env.example` yalnızca değişken adları. |

### 6.5 Ölçeklenebilirlik (pilot → GA)
| Boyut | Pilot | GA hedefi |
|---|---|---|
| Tenant | 2–5 | 500 |
| Ürün / tenant | 10.000 | 100.000 |
| Sipariş / tenant / gün | 200 | 2.000 |
| Eşzamanlı kullanıcı / tenant | 15 | 100 |

---

## 7. Başarı metrikleri

### 7.1 Ürün KPI'ları
| Metrik | Tanım | Hedef (pilot sonu) |
|---|---|---|
| Kayıttan ilk siparişe süre | Tenant oluşturma → ilk Onaylandı sipariş | Medyan ≤ 1 iş günü |
| İçe aktarma başarı oranı | İlk denemede hatasız tamamlanan satır / toplam | ≥ %95 |
| WhatsApp eşleşme doğruluğu | Kullanıcının değiştirmeden kabul ettiği "Kesin" satır oranı | ≥ %85 |
| Belirsizlik oranı | Belirsiz + Eşleşmedi satır / toplam | ≤ %20 (öğrenmeyle düşen) |
| Mesajdan taslağa süre | Yapıştırma → taslak kaydı | Medyan ≤ 2 dk |
| Haftalık aktif tenant oranı | Son 7 günde ≥ 1 sipariş oluşturan tenant | ≥ %80 |
| Mobil toplama kullanımı | Mobilde tamamlanan toplama görevi / toplam görev | ≥ %70 |

### 7.2 Pilot operasyon KPI'ları
| Metrik | Ölçüm | Hedef |
|---|---|---|
| Çift satış olayı | Satılabilir negatife düşen onaylı sipariş | **0** |
| Stok doğruluğu | Sayım farkı olan SKU / sayılan SKU | ≤ %2 (pilot 3. ayı) |
| Hatalı toplama | Yanlış/eksik ürünle sevk / sevk edilen sipariş | ≤ %0,5 |
| Sipariş işleme süresi | Onaylandı → Sevk Edildi medyanı | Pilot başlangıcına göre %30 azalma |
| Ledger tutarlılığı | Gece kontrol farkı | 0 |
| Erişilebilirlik | Aylık uptime | ≥ %99,5 |
| Kullanıcı memnuniyeti | Pilot sonu NPS / CSAT | NPS ≥ 30 |

## 8. Varsayımlar
1. Pilot işletmeler masaüstü/dizüstü bilgisayar ve Android telefon (veya uygun fiyatlı el terminali) kullanır; depoda Wi-Fi vardır.
2. Ürün ve stok verisi mevcut Excel dosyalarından aktarılabilir durumdadır (temizlik gerekebilir).
3. WhatsApp siparişleri Türkçe, gündelik dil, kısaltma ve yazım hataları içerir; çoğu ≤ 30 satırdır.
4. MVP'de tek para birimi (TRY) yeterlidir; döviz bazlı satış pilot için kritik değildir.
5. Sabit 6 rol pilot işletmelerin yetki ihtiyacını karşılar.
6. Fatura/e-Fatura mevcut muhasebe yazılımında kalmaya devam eder.
7. AI sağlayıcısı (LLM) API üzerinden kullanılabilir, Türkçe performansı yeterlidir; maliyet ek modül fiyatıyla karşılanır.
8. Barkodların büyük kısmı EAN-13 veya tenant'ın kendi Code128 etiketleridir.
9. Pilot süresince ödeme alınmaz.

## 9. Ürün sahibine açık sorular
| ID | Soru | Etki |
|---|---|---|
| AQ-01 | **Kısmen karar verildi (OD-02, 2026-11-09):** Temel 999 kullanıcı / 999 depo / sınırsız ürün, barkod/mobil toplama dahil. Profesyonel limitleri ve fiyat aralığı açık. | SUB-09, modül mağazası metinleri |
| AQ-02 | **Karar verildi (OD-09, 2026-11-09):** evet — `sales` kendi siparişini onaylayabilir; tenant ayarı `sales_can_approve_orders` yeni tenant'ta varsayılan **açık**, sahip/yönetici Ayarlar → İş kuralları'ndan kapatabilir (mevcut tenant'ların kayıtlı değeri değişmez). | ORD-10, yetki matrisi |
| AQ-03 | LED pilotu için seri numarası takibi MVP'de mi (sevkte seri no okutma)? Öneri: MVP'de opsiyonel serbest alan, tam modül P2. | Kapsam, WHT, veri modeli |
| AQ-04 | **Karar verildi (OD-09, 2026-11-09):** stok eksiye düşmez; "uyar" / "izin ver" yoktur. "Rafta var ama sisteme girilmedi" durumu mal kabul veya sayım/düzeltme ile çözülür. | INV-12, onboarding |
| AQ-05 | Veri barındırma bölgesi (AB — Frankfurt vs. Türkiye) ve KVKK yurt dışı aktarım yaklaşımı? | Altyapı, sözleşmeler |
| AQ-06 | AI sağlayıcı seçimi ve aylık AI bütçesi / tenant başı kota? **Kota karar verildi (OD-08, 2026-11-09):** tenant başına ayda 300 ayrıştırma (`ai_parses_per_month`); AI, tenant açıkça açana kadar kapalı. | WAI-09, WAI-11, maliyet |
| AQ-07 | Pilot işletmelerin gerçek (anonimleştirilmiş) ürün dosyası örneği test için alınabilir mi, yoksa tamamen sentetik mi çalışılacak? | IMP doğrulaması, AI eşleşme kalitesi |
| AQ-08 | Deneme süresi 30 gün mü, pilot tenant'lar için süresiz mi? | SUB-05 |
| AQ-09 | Fiyatlar KDV dahil mi gösterilsin (sektör alışkanlığı) yoksa hariç mi? Öneri: tenant ayarı, varsayılan hariç. | TEN-05, ORD-04 |
| AQ-10 | **Karar verildi (OD-09, 2026-11-09):** kısmi onay MVP'de **yok**; onay ya hep ya hiç (stok yetmezse reddedilir). | ORD-06, ORD-08 |
