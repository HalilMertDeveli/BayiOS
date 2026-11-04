# BayiOS — Ürün Backlog'u

| Alan | Değer |
|---|---|
| Sürüm | 0.1 — 2026-11-04 |
| Sahip | Product Manager |
| Kaynak | `docs/product/PRD.md` (gereksinim ID'leri), `docs/product/roles-permissions.md`, `docs/product/user-flows.md` |
| Öncelik | **P0** = MVP için zorunlu · **P1** = MVP hedefi / pilot sırasında · **P2** = MVP sonrası |

Hikâye formatı: "Bir <rol> olarak <ihtiyaç> istiyorum, böylece <fayda>." P0 hikâyeler Given/When/Then kabul kriterleri taşır. Tüm örnek veriler sentetiktir.

## Özet

| Epic | Ad | PRD | P0 | P1 | P2 | Toplam |
|---|---|---|---|---|---|---|
| [E01](#e01--platform-temeli) | Platform temeli | PLT | 6 | 1 | 0 | 7 |
| [E02](#e02--tenant--onboarding) | Tenant & onboarding | TEN | 6 | 3 | 0 | 9 |
| [E03](#e03--katalog) | Katalog | CAT | 7 | 3 | 0 | 10 |
| [E04](#e04--toplu-içe-aktarma) | Toplu içe aktarma | IMP | 6 | 2 | 1 | 9 |
| [E05](#e05--stok--depo) | Stok & depo | INV | 9 | 1 | 0 | 10 |
| [E06](#e06--siparişler) | Siparişler | ORD | 7 | 4 | 1 | 12 |
| [E07](#e07--whatsapp-asistanı-v1) | WhatsApp asistanı v1 | WAI | 5 | 3 | 1 | 9 |
| [E08](#e08--depo-görevleri-mobil) | Depo görevleri (mobil) | WHT | 6 | 5 | 0 | 11 |
| [E09](#e09--müşteriler--bayiler) | Müşteriler & bayiler | CUS | 3 | 1 | 1 | 5 |
| [E10](#e10--raporlar--dışa-aktarma) | Raporlar & dışa aktarma | REP | 5 | 2 | 0 | 7 |
| [E11](#e11--saas--modüller) | SaaS & modüller | SUB | 4 | 2 | 0 | 6 |
| [E12](#e12--güvenlik--operasyon) | Güvenlik & operasyon | SEC | 6 | 1 | 0 | 7 |
| | **Toplam** | | **70** | **28** | **4** | **102** |

> Sayılar aşağıdaki tablolardan türetilmiştir; hikâye eklendiğinde güncellenmelidir.

### Önerilen MVP sıralaması (dikey dilimler)
1. **Dilim 1 — Temel:** E01, E12 (kimlik, tenant izolasyonu, audit), E02 (kayıt, sektör, depo).
2. **Dilim 2 — Veri:** E03 (katalog), E04 (ürün + stok içe aktarma).
3. **Dilim 3 — Stok çekirdeği:** E05 (ledger, mal kabul, düzeltme, rezervasyon motoru + eşzamanlılık testleri).
4. **Dilim 4 — Sipariş:** E06 (manuel sipariş, durum makinesi, iptal), E09 (müşteri, fiyat listesi).
5. **Dilim 5 — Depo:** E08 (mobil toplama, barkod, sevk), E05 (sayım, transfer).
6. **Dilim 6 — Farklılaştırıcı:** E07 (WhatsApp asistanı), E10 (raporlar), E11 (entitlement).

---

## E01 — Platform temeli

| ID | Hikâye | Öncelik | PRD |
|---|---|---|---|
| US-PLT-001 | Bir geliştirici olarak her tenant kaydının `tenant_id` taşımasını ve API'nin her istekte üyeliği doğrulamasını istiyorum, böylece çapraz tenant veri sızıntısı olmaz. | P0 | PLT-01 |
| US-PLT-002 | Bir kullanıcı olarak web ve mobilden tüm işlemlerin yalnızca API üzerinden yapılmasını istiyorum, böylece iş kuralları tek yerde uygulanır. | P0 | PLT-02 |
| US-PLT-003 | Bir admin olarak kim, neyi, ne zaman değiştirdi görmek istiyorum, böylece hataların kaynağını bulabilirim. | P0 | PLT-03 |
| US-PLT-004 | Bir kullanıcı olarak uzun süren işlerin (import/export) arka planda çalışmasını ve durumunu görmeyi istiyorum, böylece ekranda beklemem. | P0 | PLT-04 |
| US-PLT-005 | Bir kullanıcı olarak durum değiştiren isteklerin tekrarlandığında çift etki yaratmamasını istiyorum, böylece bağlantı kopsa da çift sipariş/çift hareket oluşmaz. | P0 | ORD-09 |
| US-PLT-006 | Bir kullanıcı olarak arayüzün Türkçe, `tr-TR` sayı/tarih biçimiyle çalışmasını istiyorum, böylece alıştığım biçimde okurum. | P0 | NFR-UX-02/03 |
| US-PLT-007 | Bir kullanıcı olarak uygulama içi bildirim merkezi ve mobil push almak istiyorum, böylece önemli olayları kaçırmam. | P1 | PLT-05 |

**Kabul kriterleri (P0)**
- **US-PLT-001**
  - Given A tenant'ının üyesi bir kullanıcı, When B tenant'ına ait bir ürünü ID ile isterse, Then API 404 döner ve olay güvenlik loguna yazılır.
  - Given yeni bir API ucu eklendiğinde, When CI çalışır, Then otomatik çapraz tenant testi bu uç için de koşar ve başarısızsa build kırılır.
  - Given askıya alınmış üyelik, When geçerli token ile istek gelirse, Then 403 döner.
- **US-PLT-002**
  - Given istemci uygulamaları, When kod incelenir, Then veritabanına doğrudan yazma yapan istemci kodu yoktur (DB rol yetkileri istemci anahtarı için yazmayı reddeder).
  - Given geçersiz durum geçişi isteği, When API'ye gelir, Then 409/422 ile anlamlı hata kodu döner.
- **US-PLT-003**
  - Given bir ürün fiyatı değiştirildiğinde, When admin denetim kaydını açar, Then kullanıcı, zaman, alan, eski ve yeni değer görünür.
  - Given denetim kaydı, When herhangi bir rol değiştirmeye/silmeye çalışırsa, Then işlem mümkün değildir (yalnızca ekleme).
- **US-PLT-004**
  - Given 10.000 satırlık içe aktarma başlatıldığında, When kullanıcı sayfadan ayrılırsa, Then iş devam eder ve tekrar açıldığında ilerleme yüzdesi görünür.
  - Given iş geçici hata ile düştüğünde, When yeniden deneme politikası çalışır, Then en fazla 3 deneme yapılır ve kalıcı hata durumunda iş "Hatalı" olur, kullanıcı bilgilendirilir.
- **US-PLT-005**
  - Given aynı `Idempotency-Key` ile iki "sipariş onayla" isteği, When ikisi de işlenir, Then tek rezervasyon oluşur ve ikinci yanıt ilkinin aynısıdır.
  - Given anahtar farklı gövdeyle tekrar kullanılırsa, When istek gelir, Then 422 `idempotency.key_reused` döner.
- **US-PLT-006**
  - Given 1234,5 TRY tutarı, When listede gösterilir, Then `1.234,50 ₺` olarak görünür.
  - Given "İstanbul", "ışık", "Şanzıman" içeren ürünler, When "istanbul" veya "isik" aranırsa, Then Türkçe büyük/küçük harf ve aksan duyarsız eşleşme döner.

---

## E02 — Tenant & onboarding

| ID | Hikâye | Öncelik | PRD |
|---|---|---|---|
| US-TEN-001 | Bir işletme sahibi olarak e-postamla kaydolup işletmemi oluşturmak istiyorum, böylece sistemi hemen kullanmaya başlarım. | P0 | TEN-01, TEN-02 |
| US-TEN-002 | Bir işletme sahibi olarak sektörümü seçmek istiyorum, böylece kategoriler, özel alanlar ve birimler hazır gelir. | P0 | TEN-03 |
| US-TEN-003 | Bir işletme sahibi olarak logo, para birimi, KDV ve fiyat gösterimini ayarlamak istiyorum, böylece belgeler işletmeme uygun olur. | P0 | TEN-04, TEN-05 |
| US-TEN-004 | Bir admin olarak birimleri ve dönüşüm katsayılarını tanımlamak istiyorum, böylece kutu/adet/metre ile satış yapabilirim. | P0 | TEN-06 |
| US-TEN-005 | Bir admin olarak personeli rol ile davet etmek ve rolünü değiştirmek istiyorum, böylece herkes yalnızca gerekeni görür. | P0 | TEN-07, TEN-08 |
| US-TEN-006 | Bir işletme sahibi olarak negatif stok politikasını seçmek istiyorum, böylece stoğum olmadan satış yapılıp yapılmayacağını belirlerim. | P0 | TEN-12 |
| US-TEN-007 | Bir işletme sahibi olarak demo veriyle denemek ve sonra tek tıkla temizlemek istiyorum, böylece gerçek veriye geçmeden sistemi öğrenirim. | P1 | TEN-09 |
| US-TEN-008 | Bir işletme sahibi olarak kurulum kontrol listesi görmek istiyorum, böylece eksik adımları bilirim. | P1 | TEN-10 |
| US-TEN-009 | Bir kullanıcı olarak birden çok işletmeye üye olup aralarında geçiş yapmak istiyorum, böylece tek hesapla çalışırım. | P1 | TEN-11 |

**Kabul kriterleri (P0)**
- **US-TEN-001**
  - Given geçerli e-posta ve şifre, When kayıt formu gönderilir ve e-posta doğrulanırsa, Then tenant oluşturulur ve kullanıcı `owner` olur.
  - Given doğrulanmamış e-posta, When kullanıcı panele erişmeye çalışırsa, Then doğrulama ekranına yönlendirilir.
  - Given kayıtlı e-posta, When tekrar kayıt denenirse, Then hesap varlığını sızdırmayan genel mesaj ve giriş/sıfırlama önerisi gösterilir.
- **US-TEN-002**
  - Given "Motosiklet yedek parça" seçimi, When onboarding tamamlanır, Then şablondaki kategoriler, özel alanlar (marka/model/yıl uyumluluğu, OEM no) ve birimler tenant'ta oluşur.
  - Given şablondan gelen özel alan, When admin yeni alan eklerse, Then şablon değişmez, yalnızca tenant'ın alanı eklenir.
- **US-TEN-003**
  - Given ayarlar boş bırakıldığında, When tenant oluşur, Then para birimi TRY, KDV %20, fiyat gösterimi "KDV hariç" olur.
  - Given 2 MB'den büyük veya desteklenmeyen formatta logo, When yüklenirse, Then açıklayıcı hata gösterilir ve kayıt değişmez.
- **US-TEN-004**
  - Given "1 kutu = 10 adet" tanımı, When 2 kutu stok girişi yapılırsa, Then bakiye temel birimde 20 adet artar.
  - Given hareketlerde kullanılmış bir birim, When silinmek istenirse, Then engellenir, pasifleştirme önerilir.
- **US-TEN-005**
  - Given admin, When `sales` rolüyle davet gönderir, Then davet 7 gün geçerlidir ve kabul edildiğinde kullanıcı yalnızca sales izinlerine sahip olur.
  - Given tenant'ta tek owner, When owner'ın kaldırılması veya rolünün düşürülmesi istenirse, Then işlem reddedilir.
  - Given admin, When başka bir admin'i askıya almaya çalışırsa, Then 403 döner.
- **US-TEN-006**
  - Given politika "izin verme" ve satılabilir 3, When 5 adetlik sipariş onaylanmaya çalışılırsa, Then onay reddedilir ve eksik satır gösterilir.
  - Given politika "uyar", When aynı durum oluşursa, Then kullanıcıdan ek teyit istenir ve teyitte onay gerçekleşir, audit'e "negatif stok teyidi" yazılır.

---

## E03 — Katalog

| ID | Hikâye | Öncelik | PRD |
|---|---|---|---|
| US-CAT-001 | Bir admin olarak ürünü SKU, barkod, marka, kategori, birim, fiyat ve KDV ile oluşturmak istiyorum, böylece satış ve stok takibi yapabilirim. | P0 | CAT-01 |
| US-CAT-002 | Bir admin olarak ürünü pasifleştirmek veya arşivlemek istiyorum, böylece silmeden satıştan kaldırırım ve geçmiş korunur. | P0 | CAT-02 |
| US-CAT-003 | Bir admin olarak ürüne birden çok barkod ve alternatif kod (OEM, tedarikçi kodu) eklemek istiyorum, böylece her kodla bulunabilir. | P0 | CAT-03 |
| US-CAT-004 | Bir admin olarak sektöre özel alanları doldurmak ve bunlara göre filtrelemek istiyorum, böylece teknik özellikle ürün bulunur. | P0 | CAT-05 |
| US-CAT-005 | Bir satış personeli olarak motosiklet marka/model/yıla göre uyumlu parçaları bulmak istiyorum, böylece doğru parçayı satarım. | P0 | CAT-06 |
| US-CAT-006 | Bir admin olarak çoklu fiyat listesi tanımlamak ve müşteri gruplarına atamak istiyorum, böylece bayiye özel fiyat otomatik gelir. | P0 | CAT-08, CAT-09 |
| US-CAT-007 | Bir satış personeli olarak 10.000 ürün arasında ad, SKU, barkod veya kodla anında arama yapmak istiyorum, böylece müşteriye hızlı yanıt veririm. | P0 | CAT-10, NFR-PERF-01 |
| US-CAT-008 | Bir admin olarak ürün varyantları (renk/ölçü) tanımlamak istiyorum, böylece her varyantın stoğunu ayrı izlerim. | P1 | CAT-04 |
| US-CAT-009 | Bir admin olarak ürün görselleri eklemek istiyorum, böylece depo personeli doğru ürünü tanır. | P1 | CAT-07 |
| US-CAT-010 | Bir admin olarak seçili ürünlerde toplu durum/kategori/fiyat listesi değişikliği yapmak istiyorum, böylece tek tek düzenlemem. | P1 | CAT-14 |

**Kabul kriterleri (P0)**
- **US-CAT-001**
  - Given tenant'ta `FRN-CG125-ON-MU` SKU'su var, When aynı SKU ile ürün oluşturulursa, Then "Bu SKU zaten kullanılıyor" hatası döner.
  - Given geçersiz EAN-13 kontrol hanesi, When barkod kaydedilirse, Then uyarı gösterilir (iç barkod formatı seçilirse kabul edilir).
  - Given fiyat değişikliği, When kaydedilir, Then fiyat geçmişine eski/yeni değer, kullanıcı ve zaman yazılır (CAT-13).
- **US-CAT-002**
  - Given pasif ürün, When sipariş satırına eklenmeye çalışılırsa, Then seçilemez ve "Pasif ürün" etiketi görünür; stokta görünmeye devam eder.
  - Given arşivlenmiş ürün, When ürün listesi varsayılan filtreyle açılırsa, Then görünmez; geçmiş siparişlerde ürün bilgisi korunur.
  - Given hiçbir rol, When ürünü fiziksel silmeye çalışırsa, Then API bu işlemi sunmaz.
- **US-CAT-003**
  - Given ürüne OEM kodu `90001-SNT-001` eklenmiş, When bu kod aramada veya barkod okutmada kullanılırsa, Then ürün tek sonuç olarak gelir.
  - Given bir barkod başka ürüne atanmış, When aynı barkod ikinci ürüne eklenirse, Then reddedilir.
- **US-CAT-004**
  - Given "Pixel pitch" filtrelenebilir seçim alanı, When kullanıcı "P3.91" filtreler, Then yalnızca bu değere sahip ürünler listelenir.
  - Given zorunlu özel alan boş, When ürün kaydedilirse, Then alan düzeyinde hata gösterilir.
- **US-CAT-005**
  - Given ürün "Honda / CG 125 / 2015–2021" uyumluluğuna sahip, When "CG 125" + yıl 2018 filtrelenirse, Then ürün listelenir; yıl 2022 filtrelenirse listelenmez.
  - Given bir ürüne 3 uyumluluk satırı, When ürün detayı açılırsa, Then tüm satırlar tablo halinde görünür ve düzenlenebilir.
- **US-CAT-006**
  - Given "Bayi A" listesi %15 iskonto kuralı ve ürün özel fiyatı yok, When Bayi A grubundaki müşteriye sipariş açılırsa, Then satır fiyatı perakende × 0,85 gelir.
  - Given listede ürüne özel sabit fiyat, When aynı sipariş açılırsa, Then sabit fiyat yüzde kuralını ezer.
- **US-CAT-007**
  - Given 10.000 ürünlü tenant, When ad parçası, SKU, barkod veya OEM kodu aranırsa, Then API yanıtı p95 < 500 ms'dir (yük testiyle kanıtlanır).
  - Given arama sonucu, When listelenir, Then her satırda satılabilir stok ve satış fiyatı görünür (rol izni varsa).

---

## E04 — Toplu içe aktarma

| ID | Hikâye | Öncelik | PRD |
|---|---|---|---|
| US-IMP-001 | Bir admin olarak Excel/CSV dosyamı yükleyip kolonları alanlara eşlemek istiyorum, böylece dosyamı yeniden düzenlemem. | P0 | IMP-01, IMP-03 |
| US-IMP-002 | Bir admin olarak içe aktarmadan önce doğrulama sonucunu ve önizlemeyi görmek istiyorum, böylece hatalı veriyi sisteme sokmam. | P0 | IMP-05, IMP-07, IMP-08 |
| US-IMP-003 | Bir admin olarak SKU veya barkodla mevcut ürünleri güncellemek, yenileri oluşturmak ve tekrarları engellemek istiyorum, böylece çift ürün oluşmaz. | P0 | IMP-06, IMP-09 |
| US-IMP-004 | Bir yönetici olarak stok miktarlarını ürünlerden ayrı içe aktarmak istiyorum, böylece açılış stoğu ve sayım sonuçlarını hareket olarak girerim. | P0 | IMP-02, IMP-11 |
| US-IMP-005 | Bir admin olarak 10.000 satırlık dosyayı arka planda 2 dakikanın altında işlemek istiyorum, böylece beklemem. | P0 | IMP-10, NFR-PERF-03 |
| US-IMP-006 | Bir admin olarak yanlış içe aktarmayı geri almak istiyorum, böylece hata korkusu olmadan çalışırım. | P0 | IMP-12, IMP-13 |
| US-IMP-007 | Bir admin olarak AI'ın kolon eşleme önermesini istiyorum, böylece farklı tedarikçi dosyalarını hızlı eşlerim. | P1 | IMP-04 |
| US-IMP-008 | Bir admin olarak eşleme şablonumu kaydetmek ve sektör şablon dosyasını indirmek istiyorum, böylece sonraki yüklemeler hızlanır. | P1 | IMP-03, IMP-14 |
| US-IMP-009 | Bir admin olarak tek kolondaki birleşik teknik değerleri (ör. "P3.91/500x500/İç") AI ile bölmek istiyorum, böylece tedarikçi dosyası temizlemem. | P2 | — |

**Kabul kriterleri (P0)**
- **US-IMP-001**
  - Given Windows-1254 kodlu, `;` ayırıcılı CSV, When yüklenir, Then Türkçe karakterler bozulmadan okunur ve ayırıcı otomatik tespit edilir.
  - Given zorunlu hedef alan (SKU, ad) eşlenmemiş, When "Devam" denirse, Then ilerlenemez ve eksik eşleme vurgulanır.
  - Given 20 MB üzeri veya 50.000 satır üzeri dosya, When yüklenirse, Then reddedilir ve bölme önerisi gösterilir.
- **US-IMP-002**
  - Given 1.000 satırın 37'si hatalı, When doğrulama biter, Then özet "963 geçerli / 37 hatalı" gösterir ve hatalı satırlar satır no + kolon + hata ile listelenir.
  - Given hatalı satırlar, When "Hatalı satırları indir" tıklanırsa, Then orijinal kolonlar + "Hata" kolonu içeren Excel iner.
  - Given "1.234,56" ve "1234.56" değerleri, When fiyat kolonunda doğrulanırsa, Then ikisi de 1234,56 olarak kabul edilir.
- **US-IMP-003**
  - Given eşleşme anahtarı SKU ve mod "ikisi", When mevcut SKU'lu satır gelirse, Then ürün güncellenir; yeni SKU'lu satır gelirse oluşturulur.
  - Given dosyada aynı SKU iki satırda, When doğrulanırsa, Then iki satır da "dosya içi tekrar" hatası alır.
  - Given aynı dosya (aynı içerik hash'i) daha önce başarıyla içe aktarılmış, When tekrar yüklenirse, Then "Bu dosya <tarih>'te içe aktarıldı" uyarısı gösterilir.
- **US-IMP-004**
  - Given stok içe aktarma "mutlak" modda, ürünün bakiyesi 8 ve dosyada 10, When çalıştırılırsa, Then +2 tutarında `import` hareketi oluşur; bakiye doğrudan yazılmaz.
  - Given dosyada bilinmeyen SKU, When stok içe aktarılırsa, Then satır hatalı olur; ürün oluşturulmaz.
- **US-IMP-005**
  - Given 10.000 satırlık geçerli ürün dosyası, When çalıştırılır, Then uçtan uca süre < 2 dk'dır (CI performans testi).
  - Given iş tamamlandığında, When kullanıcı başka sayfadadır, Then uygulama içi ve e-posta bildirimi alır.
- **US-IMP-006**
  - Given 3 gün önce tamamlanmış içe aktarma, When "Geri al" seçilirse, Then etki özeti (arşivlenecek N ürün, geri dönecek N alan, ters kayıt N hareket) gösterilir ve onayla uygulanır.
  - Given içe aktarmayla oluşan ürün sonradan siparişe girmiş, When geri alınırsa, Then ürün silinmez, arşivlenir ve uyarı listelenir.
  - Given 7 günden eski içe aktarma, When geri alma denenirse, Then buton pasiftir ve sebep gösterilir.

---

## E05 — Stok & depo

| ID | Hikâye | Öncelik | PRD |
|---|---|---|---|
| US-INV-001 | Bir yönetici olarak her ürünün fiziksel, ayrılmış ve satılabilir miktarını depo bazında görmek istiyorum, böylece satış kararını doğru veririm. | P0 | INV-01, INV-15 |
| US-INV-002 | Bir işletme sahibi olarak her stok değişikliğinin değiştirilemez bir hareket kaydından gelmesini istiyorum, böylece stok her zaman açıklanabilir olur. | P0 | INV-02, NFR-REL-03 |
| US-INV-003 | Bir yönetici olarak birden fazla depo ve raf lokasyonu tanımlamak istiyorum, böylece ürünün nerede olduğunu bilirim. | P0 | INV-03, INV-04 |
| US-INV-004 | Bir yönetici olarak mal kabulü taslak olarak girip onaylamak istiyorum, böylece gelen ürünler stoğa doğru girer. | P0 | INV-05 |
| US-INV-005 | Bir yönetici olarak (kör) stok sayımı başlatıp fark raporunu onaylamak istiyorum, böylece sistem stoğu fiziksel stokla eşleşir. | P0 | INV-06 |
| US-INV-006 | Bir yönetici olarak sebep kodlu manuel stok düzeltmesi yapmak istiyorum, böylece istisnaları kayıt altına alırım. | P0 | INV-07 |
| US-INV-007 | Bir yönetici olarak depolar arası transfer yapmak istiyorum, böylece eksik depoyu diğerinden beslerim. | P0 | INV-08 |
| US-INV-008 | Bir satış personeli olarak onaylanan siparişin stoğu ayırmasını ve aynı ürünün iki kez satılamamasını istiyorum, böylece müşteriye verdiğim söz tutulur. | P0 | INV-12, INV-13, INV-14 |
| US-INV-009 | Bir yönetici olarak müşteri iadesini satılabilir veya hasarlı olarak almak istiyorum, böylece iade ürün doğru stoğa girer. | P0 | INV-09 |
| US-INV-010 | Bir yönetici olarak hasar/fire kaydı yapmak istiyorum, böylece kayıplar raporlanır. | P1 | INV-10 |

**Kabul kriterleri (P0)**
- **US-INV-001**
  - Given fiziksel 20, onaylı siparişlerde 7 ayrılmış, When stok ekranı açılır, Then Rafta 20 / Ayrıldı 7 / Satılabilir 13 görünür.
  - Given 2 depo, When ürün detayı açılırsa, Then depo bazında ve toplam bakiye ile son 50 hareket listelenir.
- **US-INV-002**
  - Given herhangi bir stok değişikliği (kabul, sevk, düzeltme, sayım, transfer, iade, import), When gerçekleşir, Then aynı transaction'da hareket kaydı ve bakiye güncellenir; biri başarısızsa ikisi de geri alınır.
  - Given kaydedilmiş bir hareket, When düzenlenmek veya silinmek istenirse, Then API buna izin vermez; düzeltme ters kayıtla yapılır.
  - Given gece tutarlılık işi, When çalışır, Then her ürün × depo için bakiye = hareket toplamıdır; fark varsa alarm üretilir.
- **US-INV-003**
  - Given raf kodu `A-03-2`, When aynı depoda tekrar oluşturulursa, Then reddedilir.
  - Given stoğu olan depo, When pasifleştirilmek istenirse, Then engellenir ve önce transfer/düzeltme önerilir.
  - Given Temel paket, When ikinci depo oluşturulmak istenirse, Then `403 entitlement.required` döner ve modül mağazası yönlendirmesi gösterilir.
- **US-INV-004**
  - Given 3 satırlı taslak mal kabul, When onaylanırsa, Then her satır için ilgili depo/rafa giriş hareketi oluşur ve belge düzenlenemez hale gelir.
  - Given aynı tedarikçi + belge no ile ikinci kabul, When kaydedilirse, Then mükerrer uyarısı gösterilir.
  - Given alış fiyatı girilmiş satır, When onaylanırsa, Then ürünün son alış fiyatı güncellenir ve fiyat geçmişine yazılır.
- **US-INV-005**
  - Given kör sayım, When personel mobilde sayım yapar, Then sistem miktarı gösterilmez.
  - Given sayım açıkken 2 adet sevk edilmiş ürün, When fark hesaplanır, Then fark anlık görüntü + sayım süresince hareketlere göre düzeltilmiş olur.
  - Given fark raporu onaylandığında, When işlenir, Then her fark için "sayım farkı" sebepli düzeltme hareketi oluşur.
- **US-INV-006**
  - Given sebep kodu seçilmemiş düzeltme, When kaydedilirse, Then reddedilir.
  - Given `sales` rolü, When düzeltme API'sini çağırırsa, Then 403 döner.
  - Given politika "izin verme" ve düzeltme sonucu fiziksel < ayrılmış, When kaydedilirse, Then düzeltme reddedilir (`409 inventory.insufficient_available`) ve etkilenen siparişler gösterilir; önce rezervasyon azaltılır/iptal edilir.
- **US-INV-007**
  - Given kaynakta satılabilir 5, When 6 adet transfer gönderilmek istenirse, Then reddedilir.
  - Given gönderilen 10 adet transfer, When hedefte 9 teslim alınırsa, Then hedefte +9 giriş olur, 1 adet fark raporlanır ve transfer "Kısmi teslim" durumunda kalır.
  - Given transfer "Yolda", When kaynak veya hedef satılabilir sorgulanırsa, Then yoldaki miktar hiçbirinde satılabilir sayılmaz.
- **US-INV-008**
  - Given satılabilir 5 ve iki farklı kullanıcı aynı anda 3'er adetlik sipariş onaylarsa, When istekler paralel işlenir, Then yalnızca biri onaylanır, diğeri "yetersiz stok" ile reddedilir (eşzamanlılık testi: 20 paralel istek).
  - Given onaylı sipariş, When sevk edilir, Then fiziksel stok düşer ve ilgili rezervasyon aynı transaction'da kapanır; satılabilir değişmez.
  - Given onaylı sipariş iptal edilir, When işlenir, Then rezervasyon serbest kalır ve satılabilir artar.
- **US-INV-009**
  - Given 10 adet sevk edilmiş satır, When 11 adet iade girilirse, Then reddedilir.
  - Given iade "hasarlı" işaretli, When onaylanırsa, Then hasarlı depo/stoğa girer, satılabilir değişmez.

---

## E06 — Siparişler

| ID | Hikâye | Öncelik | PRD |
|---|---|---|---|
| US-ORD-001 | Bir satış personeli olarak müşteri, adres ve satırlarla manuel sipariş oluşturmak istiyorum, böylece telefon siparişlerini de girerim. | P0 | ORD-01…03 |
| US-ORD-002 | Bir satış personeli olarak siparişte ara toplam, iskonto ve oran bazlı KDV dökümünü görmek istiyorum, böylece müşteriye doğru tutarı söylerim. | P0 | ORD-04 |
| US-ORD-003 | Bir yönetici olarak siparişin net bir durum akışında ilerlemesini istiyorum, böylece her siparişin nerede olduğunu bilirim. | P0 | ORD-05, ORD-12 |
| US-ORD-004 | Bir yönetici olarak siparişleri onaylamak istiyorum, böylece stok ayrılır ve depo hazırlığa başlar. | P0 | ORD-06, ORD-10 |
| US-ORD-005 | Bir satış personeli olarak siparişi sebep belirterek iptal etmek istiyorum, böylece ayrılan stok serbest kalır. | P0 | ORD-07 |
| US-ORD-006 | Bir satış personeli olarak siparişleri durum, müşteri, ürün ve tarihe göre bulmak istiyorum, böylece müşteriye hızlı bilgi veririm. | P0 | ORD-11, ORD-14 |
| US-ORD-007 | Bir depo personeli olarak sevkiyatı kargo bilgisiyle onaylamak istiyorum, böylece stok düşer ve müşteri bilgilendirilebilir. | P0 | ORD-15, INV-13 |
| US-ORD-008 | Bir yönetici olarak kısmi sevkiyat yapmak istiyorum, böylece stokta olanı bekletmeden gönderirim. | P1 | ORD-08 |
| US-ORD-009 | Bir yönetici olarak onaylı siparişin satırlarını düzenlemek istiyorum, böylece müşteri değişikliklerini rezervasyonla tutarlı uygularım. | P1 | ORD-16 |
| US-ORD-010 | Bir satış personeli olarak proforma ve sevk listesini PDF almak istiyorum, böylece müşteriye ve depoya iletirim. | P1 | ORD-13 |
| US-ORD-011 | Bir işletme sahibi olarak tutar eşiği üzerindeki siparişlerin yönetici onayı gerektirmesini istiyorum, böylece büyük riskleri kontrol ederim. | P1 | ORD-10 |
| US-ORD-012 | Bir depo personeli olarak mobilden sipariş oluşturmak istiyorum, böylece tezgâh satışlarını girerim. | P2 | ORD-01 |

**Kabul kriterleri (P0)**
- **US-ORD-001**
  - Given müşteri "Bayi A" grubunda, When ürün satırı eklenir, Then fiyat Bayi A listesinden, KDV ürün oranından gelir.
  - Given `sales` rolü ve iskonto sınırı %0, When satır fiyatı değiştirilmek istenirse, Then alan salt okunurdur.
  - Given pasif ürün, When satır olarak eklenmek istenirse, Then seçilemez.
- **US-ORD-002**
  - Given %20 ve %10 KDV'li satırlar, When toplamlar hesaplanır, Then KDV dökümü oran bazında ayrı satırlarda ve genel toplam 2 ondalık, yarım-yukarı yuvarlamayla gösterilir.
  - Given satır iskontosu %10 ve tutar iskontosu birlikte girilmeye çalışılırsa, When kaydedilir, Then yalnızca biri kabul edilir (tek iskonto tipi/satır).
- **US-ORD-003**
  - Given "Toplanıyor" durumundaki sipariş, When "Taslak"a geçirilmek istenirse, Then API 409 `invalid_transition` döner.
  - Given her durum değişikliği, When gerçekleşir, Then zaman çizelgesine kullanıcı ve zamanla yazılır.
  - Given sipariş, When oluşturulur, Then tenant bazında sıralı numara (ör. SIP-2026-000123) alır; numaralar tekrarlanmaz.
- **US-ORD-004**
  - Given satılabilir stok yeterli, When yönetici onaylar, Then durum Onaylandı olur, tüm satırlar için rezervasyon tek transaction'da oluşur ve toplama görevi açılır.
  - Given bir satırda stok yetersiz ve politika "izin verme", When onaylanır, Then hiçbir satır rezerve edilmez ve eksik satırlar listelenir.
  - Given tenant kuralı "Satış personeli onaylayabilir: kapalı", When `sales` onay çağırırsa, Then 403 döner.
- **US-ORD-005**
  - Given Onaylandı sipariş, When sebep seçilip iptal edilirse, Then rezervasyon serbest kalır, açık toplama görevi iptal olur.
  - Given Toplanıyor sipariş, When iptal edilirse, Then toplanan ürünler için "Rafa geri koy" görevi oluşur ve mobilde aktif toplayıcıya uyarı gider.
  - Given Sevk Edildi sipariş, When iptal denenirse, Then iptal reddedilir ve "İade oluştur" önerilir.
- **US-ORD-006**
  - Given 5.000 sipariş, When müşteri adı veya sipariş no ile aranırsa, Then sonuç p95 < 500 ms gelir.
  - Given "Onay Bekliyor" sekmesi, When açılırsa, Then yalnızca o durumdaki siparişler ve sayaç görünür.
- **US-ORD-007**
  - Given Paketlendi sipariş, When kargo firması ve koli sayısıyla sevk onaylanırsa, Then fiziksel çıkış hareketleri oluşur, durum Sevk Edildi olur.
  - Given aynı sevk isteği iki kez gönderilirse (idempotency key aynı), When işlenir, Then stok bir kez düşer.

---

## E07 — WhatsApp asistanı v1

| ID | Hikâye | Öncelik | PRD |
|---|---|---|---|
| US-WAI-001 | Bir satış personeli olarak WhatsApp mesajını yapıştırıp ürün, miktar ve birimlerin otomatik çıkarılmasını istiyorum, böylece siparişi elle yazmam. | P0 | WAI-01, WAI-02, WAI-10 |
| US-WAI-002 | Bir işletme sahibi olarak AI'ın yalnızca kataloğumdaki ürünleri eşlemesini ve asla fiyat/stok/SKU uydurmamasını istiyorum, böylece yanlış teklif verilmez. | P0 | WAI-03, WAI-05 |
| US-WAI-003 | Bir satış personeli olarak belirsiz satırlarda aday listesinden seçim yapmak istiyorum, böylece doğru ürün siparişe girer. | P0 | WAI-04 |
| US-WAI-004 | Bir yönetici olarak AI çıktısının yalnızca taslak olmasını ve insan onayı olmadan kesinleşmemesini istiyorum, böylece kontrol bizde kalır. | P0 | WAI-06 |
| US-WAI-005 | Bir işletme sahibi olarak AI sağlayıcısına minimum veri gönderilmesini istiyorum, böylece KVKK riskim azalır. | P0 | WAI-09 |
| US-WAI-006 | Bir satış personeli olarak düzelttiğim eşleşmelerin hatırlanmasını istiyorum, böylece aynı ifade sonraki mesajda doğru eşleşir. | P1 | WAI-07 |
| US-WAI-007 | Bir satış personeli olarak mesajdaki telefon/isimden müşteri önerisi almak istiyorum, böylece müşteriyi aramam. | P1 | WAI-08 |
| US-WAI-008 | Bir işletme sahibi olarak aylık AI kullanımımı ve kotamı görmek istiyorum, böylece maliyeti kontrol ederim. | P1 | WAI-11 |
| US-WAI-009 | Bir işletme sahibi olarak resmî WhatsApp Business API ile mesajların otomatik gelmesini istiyorum, böylece kopyala-yapıştır yapılmaz. | P2 | WAI-12 |

**Kabul kriterleri (P0)**
- **US-WAI-001**
  - Given sector-scenarios.md'deki "Örnek mesaj 1", When ayrıştırılır, Then 3 ürün satırı (3 takım, 1 adet, 2 kutu) ve 1 not satırı üretilir.
  - Given ≤ 30 satırlık mesaj, When ayrıştırılır, Then p95 ≤ 8 sn içinde sonuç döner; AI hatası/zaman aşımında metin korunarak manuel sipariş formuna geçilir.
  - Given AI WhatsApp modülü entitlement'ı yok, When ekran açılırsa, Then "Modülü etkinleştir" boş durumu ve manuel forma geçiş seçeneği gösterilir.
- **US-WAI-002**
  - Given katalogda olmayan "cg 150 kuba" ifadesi, When ayrıştırılır, Then satır "Bulunamadı" olur; yeni ürün/SKU oluşturulmaz.
  - Given mesajda "tanesi 150 olsun", When taslak oluşur, Then satır fiyatı müşterinin fiyat listesinden gelir ve ifade not olarak eklenir.
  - Given AI yanıtında katalogda olmayan bir SKU dönerse, When backend doğrularsa, Then bu eşleşme atılır ve satır "Bulunamadı" olur (sunucu tarafı katalog doğrulaması).
- **US-WAI-003**
  - Given "cg 125 ön balata" için 2 aday, When satır gösterilir, Then adaylar SKU, ad, uyumluluk, fiyat ve satılabilir stokla listelenir (en fazla 5).
  - Given çözülmemiş Belirsiz/Bulunamadı satır, When "Onaya gönder" tıklanırsa, Then işlem engellenir ve çözülmemiş satırlara odaklanılır.
- **US-WAI-004**
  - Given ayrıştırma tamamlandığında, When kaydedilir, Then sipariş durumu her zaman Taslak'tır; AI hiçbir durum geçişi yapamaz.
  - Given aynı müşteri + aynı metin hash'i son 24 saatte, When tekrar ayrıştırılırsa, Then mevcut taslağa bağlantılı mükerrer uyarısı gösterilir.
- **US-WAI-005**
  - Given telefon numarası içeren mesaj, When AI sağlayıcısına gönderilirse, Then numara maskelenmiş olur (loglarla doğrulanır).
  - Given AI isteği, When oluşturulur, Then yalnızca (maskelenmiş) mesaj metni gönderilir; katalog, fiyat ve müşteri verisi gönderilmez (ADR 0010).

---

## E08 — Depo görevleri (mobil)

| ID | Hikâye | Öncelik | PRD |
|---|---|---|---|
| US-WHT-001 | Bir yönetici olarak onaylanan siparişlerden toplama görevi oluşmasını ve personele atamak istiyorum, böylece iş dağılımı net olur. | P0 | WHT-01, WHT-02 |
| US-WHT-002 | Bir depo personeli olarak bana atanan ve havuzdaki görevleri telefonumda görmek istiyorum, böylece sıradaki işi bilirim. | P0 | WHT-03 |
| US-WHT-003 | Bir depo personeli olarak toplama listesini raf sırasıyla görmek istiyorum, böylece depoda gereksiz yürümem. | P0 | WHT-04 |
| US-WHT-004 | Bir depo personeli olarak ürünü barkodla okutup doğrulamak istiyorum, böylece yanlış ürün göndermem. | P0 | WHT-05 |
| US-WHT-005 | Bir depo personeli olarak eksik ürünü tek dokunuşla bildirmek istiyorum, böylece yönetici hemen haberdar olur. | P0 | WHT-06 |
| US-WHT-006 | Bir depo personeli olarak barkodla stok sorgulamak istiyorum, böylece ürünün hangi rafta ne kadar olduğunu görürüm. | P0 | WHT-10 |
| US-WHT-007 | Bir depo personeli olarak paketlemede ikinci okutma ve koli sayısı girmek istiyorum, böylece paket doğruluğu artar. | P1 | WHT-07 |
| US-WHT-008 | Bir depo personeli olarak raf okutup içeriğini görmek istiyorum, böylece raf düzenini kontrol ederim. | P1 | WHT-09 |
| US-WHT-009 | Bir depo personeli olarak mal kabulü mobilde barkodla yapmak istiyorum, böylece kâğıt kullanmam. | P1 | WHT-11 |
| US-WHT-010 | Bir depo personeli olarak yeni görev atandığında bildirim almak istiyorum, böylece uygulamayı sürekli kontrol etmem. | P1 | WHT-12 |
| US-WHT-011 | Bir yönetici olarak görev sürelerini görmek istiyorum, böylece verimliliği ölçerim. | P1 | WHT-13 |

**Kabul kriterleri (P0)**
- **US-WHT-001**
  - Given tenant ayarı "otomatik görev", When sipariş onaylanırsa, Then havuzda toplama görevi oluşur.
  - Given havuzdaki görev, When yönetici bir personele atarsa, Then görev o personelin listesine taşınır ve audit'e yazılır.
  - Given iki personel aynı havuz görevini aynı anda alırsa, When istekler işlenir, Then yalnızca biri alır, diğerine "Görev başkası tarafından alındı" gösterilir.
- **US-WHT-002**
  - Given warehouse_staff, When görev listesini açar, Then yalnızca kendine atanan ve havuzdaki görevleri görür; fiyat bilgisi görünmez.
  - Given liste boş, When açılırsa, Then "Şu an görev yok" boş durumu ve yenile eylemi görünür.
- **US-WHT-003**
  - Given satırlar A-02-1, E-01-2, C-05-3 raflarında, When toplama ekranı açılırsa, Then satırlar A → C → E sırasıyla listelenir.
  - Given rafı tanımsız ürün, When listelenir, Then en sonda "Raf tanımsız" etiketiyle gösterilir.
- **US-WHT-004**
  - Given satırdaki ürünün barkodu okutulursa, When eşleşir, Then miktar +1 artar, yeşil geri bildirim + kısa titreşim verilir (< 300 ms).
  - Given siparişte olmayan ürün okutulursa, When eşleşmez, Then kırmızı uyarı + hata sesi + "Yanlış ürün" metni gösterilir, miktar değişmez.
  - Given istenen miktar dolmuşken aynı ürün tekrar okutulursa, When işlenir, Then "Fazla miktar" uyarısı verilir, sayılmaz.
  - Given Bluetooth/klavye-wedge harici okuyucu, When okutma yapılır, Then kamera ile aynı davranış elde edilir.
- **US-WHT-005**
  - Given 2 kutu istenen satırda 1 kutu bulundu, When "Eksik bildir" → "Yetersiz" seçilir, Then satır eksikli işaretlenir ve yöneticiye bildirim düşer.
  - Given eksikli satırlı görev, When "Toplamayı bitir" denirse, Then görev "Eksik var" durumunda kapanır ve sipariş yönetici kararı bekler.
- **US-WHT-006**
  - Given barkod veya SKU, When stok sorgusu yapılırsa, Then ürün, depo-raf bazlı Rafta ve Satılabilir miktarları görünür.
  - Given bilinmeyen barkod, When okutulursa, Then "Ürün bulunamadı" ve (yetki varsa) "Mevcut ürüne barkod ekle" seçeneği gösterilir.

---

## E09 — Müşteriler & bayiler

| ID | Hikâye | Öncelik | PRD |
|---|---|---|---|
| US-CUS-001 | Bir satış personeli olarak müşteri kartını iletişim, vergi ve adres bilgileriyle tutmak istiyorum, böylece siparişte hızlı seçerim. | P0 | CUS-01, CUS-05 |
| US-CUS-002 | Bir yönetici olarak müşterileri bayi gruplarına ayırıp fiyat listesi atamak istiyorum, böylece özel fiyatlar otomatik uygulanır. | P0 | CUS-02 |
| US-CUS-003 | Bir satış personeli olarak müşterinin sipariş geçmişini ve en çok aldığı ürünleri görmek istiyorum, böylece belirsiz siparişleri doğru yorumlarım. | P0 | CUS-04 |
| US-CUS-004 | Bir admin olarak müşteriye özel ürün fiyatı tanımlamak istiyorum, böylece istisna anlaşmaları uygularım. | P1 | CUS-03 |
| US-CUS-005 | Bir işletme sahibi olarak cari bakiye ve vade takibi istiyorum, böylece tahsilatı izlerim. | P2 | CUS-06 |

**Kabul kriterleri (P0)**
- **US-CUS-001**
  - Given telefon "0532 000 00 01" (sentetik) farklı biçimlerde girilmiş ("+90532…", "532…"), When arama yapılırsa, Then normalize edilmiş numarayla aynı müşteri bulunur.
  - Given siparişi olan müşteri, When silinmek istenirse, Then yalnızca pasifleştirilebilir.
- **US-CUS-002**
  - Given müşteri "Bayi B" grubuna taşınırsa, When yeni sipariş açılırsa, Then Bayi B fiyatları uygulanır; mevcut siparişlerin fiyatı değişmez.
  - Given grubu olmayan müşteri, When sipariş açılırsa, Then tenant'ın varsayılan fiyat listesi uygulanır.
- **US-CUS-003**
  - Given müşteri detayı, When açılırsa, Then son siparişler (durum, tutar) ve son 90 günde en çok alınan 10 ürün listelenir.
  - Given WhatsApp taslak ekranında belirsiz satır, When adaylar gösterilir, Then müşterinin daha önce aldığı aday "Daha önce aldı" etiketi taşır.

---

## E10 — Raporlar & dışa aktarma

| ID | Hikâye | Öncelik | PRD |
|---|---|---|---|
| US-REP-001 | Bir işletme sahibi olarak bugünkü siparişleri, bekleyen onayları, açık görevleri ve kritik stoğu tek panoda görmek istiyorum, böylece günü tek bakışta yönetirim. | P0 | REP-01 |
| US-REP-002 | Bir yönetici olarak stok seviyesi ve kritik stok raporlarını görmek istiyorum, böylece zamanında sipariş veririm. | P0 | REP-02, REP-03, CAT-12 |
| US-REP-003 | Bir işletme sahibi olarak günlük/aralık sipariş raporu istiyorum, böylece satış performansını izlerim. | P0 | REP-04 |
| US-REP-004 | Bir yönetici olarak stok hareket raporunu filtreleyip dışa aktarmak istiyorum, böylece farkları incelerim. | P0 | REP-07, INV-11 |
| US-REP-005 | Bir işletme sahibi olarak tüm verimi (ve her raporu) CSV/Excel olarak dışa aktarmak istiyorum, böylece verime her zaman sahip olurum. | P0 | REP-08, REP-09 |
| US-REP-006 | Bir yönetici olarak depo görev raporunu görmek istiyorum, böylece personel performansını değerlendiririm. | P1 | REP-05 |
| US-REP-007 | Bir işletme sahibi olarak en çok satan ürünleri görmek istiyorum, böylece stok planlarım. | P1 | REP-06 |

**Kabul kriterleri (P0)**
- **US-REP-001**
  - Given owner veya manager, When pano açılırsa, Then 4 KPI kartı (bugünkü siparişler durum bazlı, onay bekleyen, açık görev, kritik stok sayısı) 2 sn içinde yüklenir.
  - Given KPI kartı, When tıklanırsa, Then ilgili filtrelenmiş listeye gider.
- **US-REP-002**
  - Given ürünün kritik seviyesi 10 ve satılabilir 8, When kritik stok raporu açılırsa, Then ürün listede görünür ve eksik miktar (2) gösterilir.
  - Given depo filtresi, When uygulanırsa, Then kritik seviye depo bazında değerlendirilir.
- **US-REP-003**
  - Given tarih aralığı, When rapor alınırsa, Then sipariş adedi, tutar (KDV hariç/dahil), kaynak (manuel/WhatsApp) ve kullanıcı kırılımı gösterilir; İptal siparişler ayrı gösterilir.
- **US-REP-004**
  - Given ürün + tarih filtresi, When rapor açılırsa, Then hareketler tip, miktar, depo, raf, referans belge, kullanıcı ile listelenir ve toplam bakiye ile mutabıktır.
- **US-REP-005**
  - Given Temel paket dahil herhangi bir paket, When liste veya rapor dışa aktarılırsa, Then işlem hiçbir entitlement kontrolüne takılmaz (yalnızca rol izni).
  - Given owner, When tam veri dışa aktarma istenirse, Then arka plan işi tüm tenant verisini ZIP içinde CSV olarak üretir ve dosya 24 saat boyunca indirilebilir (her indirmede yetki kontrolü sonrası ≤ 5 dk ömürlü yeni imzalı URL — threat-model T7); işlem audit'e yazılır.
  - Given abonelik bitmiş tenant, When owner dışa aktarma isterse, Then işlem çalışır.

---

## E11 — SaaS & modüller

| ID | Hikâye | Öncelik | PRD |
|---|---|---|---|
| US-SUB-001 | Bir platform yöneticisi olarak tenant'a paket ve ek modül entitlement'ı atamak istiyorum, böylece ödeme altyapısı olmadan da modülleri yönetebilirim. | P0 | SUB-01, SUB-02, SUB-08 |
| US-SUB-002 | Bir işletme sahibi olarak entitlement'ı olmayan özelliklerin backend'de engellenmesini istiyorum, böylece paket kuralları güvenilir olur. | P0 | SUB-03 |
| US-SUB-003 | Bir işletme sahibi olarak 30 gün tüm modülleri denemek istiyorum, böylece değeri görerek karar veririm. | P0 | SUB-05 |
| US-SUB-004 | Bir işletme sahibi olarak abonelik bitse de verimin silinmemesini ve dışa aktarabilmeyi istiyorum, böylece verime güvenirim. | P0 | SUB-06, SUB-07 |
| US-SUB-005 | Bir işletme sahibi olarak modül mağazasında paketleri ve modülleri görüp talep etmek istiyorum, böylece ihtiyacım olanı açtırırım. | P1 | SUB-04 |
| US-SUB-006 | Bir işletme sahibi olarak paket limitlerini (kullanıcı, depo) ve kullanımımı görmek istiyorum, böylece ne zaman yükseltmem gerektiğini bilirim. | P1 | SUB-09 |

**Kabul kriterleri (P0)**
- **US-SUB-001**
  - Given platform yöneticisi, When tenant'a "AI WhatsApp asistanı" modülünü açarsa, Then değişiklik en geç 60 sn içinde API'de etkili olur ve audit'e yazılır.
  - Given tenant kullanıcısı (owner dahil), When entitlement değiştirme ucunu çağırırsa, Then 403 döner.
- **US-SUB-002**
  - Given Temel paket, When çoklu depo/transfer/raf API'leri çağrılırsa, Then `403 entitlement.required` ve modül (feature) kodu döner.
  - Given istemci menüyü gizlemiş olsa bile, When doğrudan API çağrılırsa, Then aynı kontrol uygulanır (otomatik test).
- **US-SUB-003**
  - Given yeni tenant, When oluşturulur, Then deneme bitiş tarihi (+30 gün) atanır ve tüm modüller açıktır; panoda kalan gün görünür.
  - Given deneme süresi bitti ve paket atanmadı, When gün dolar, Then tenant Temel'e düşer, veri korunur ve owner bilgilendirilir.
- **US-SUB-004**
  - Given Profesyonel'den Temel'e düşen ve 3 deposu olan tenant, When depolar açılırsa, Then depolar ve stoklar görüntülenir; yeni transfer başlatılamaz; mevcut stok raporları ve dışa aktarma çalışır.
  - Given herhangi bir paket durumu, When ledger, rezervasyon, audit, 2FA, dışa aktarma kullanılırsa, Then hiçbiri entitlement kontrolüne takılmaz.

---

## E12 — Güvenlik & operasyon

| ID | Hikâye | Öncelik | PRD |
|---|---|---|---|
| US-SEC-001 | Bir kullanıcı olarak güvenli oturum (kısa ömürlü token, yenileme, mobilde güvenli depolama) istiyorum, böylece hesabım korunur. | P0 | SEC-01 |
| US-SEC-002 | Bir işletme sahibi olarak API'nin rol × izin matrisini her uçta uygulamasını istiyorum, böylece personel yetkisi dışına çıkamaz. | P0 | roles-permissions.md |
| US-SEC-003 | Bir işletme sahibi olarak kaba kuvvet ve aşırı isteklere karşı hız sınırlaması istiyorum, böylece hizmet ve hesaplar korunur. | P0 | SEC-03 |
| US-SEC-004 | Bir işletme sahibi olarak KVKK aydınlatma, veri işleme kaydı ve veri silme talebi sürecinin olmasını istiyorum, böylece yasal yükümlülüklerimi karşılarım. | P0 | SEC-04 |
| US-SEC-005 | Bir işletme sahibi olarak günlük yedek ve test edilmiş geri yükleme istiyorum, böylece veri kaybı yaşamam. | P0 | SEC-05, NFR-REL-02 |
| US-SEC-006 | Bir operasyon sorumlusu olarak yapılandırılmış log, hata izleme ve uptime izleme istiyorum, böylece pilotta %99,5 hedefini tutarız. | P0 | NFR-REL-01, NFR-REL-04 |
| US-SEC-007 | Bir owner/admin olarak 2FA (TOTP) kullanmak ve tenant için zorunlu kılmak istiyorum, böylece yetkili hesaplar daha güvenli olur. | P1 | SEC-02 |

**Kabul kriterleri (P0)**
- **US-SEC-001**
  - Given süresi dolmuş erişim token'ı, When istek yapılırsa, Then 401 döner ve istemci yenileme token'ı ile sessizce yeniler.
  - Given askıya alınan kullanıcı, When yenileme denerse, Then reddedilir ve oturum kapanır.
  - Given mobil uygulama, When token saklanırsa, Then platform güvenli deposunda (Keychain/Keystore) tutulur.
- **US-SEC-002**
  - Given roles-permissions.md matrisi, When CI çalışır, Then her rol × uç kombinasyonu için tablo-temelli yetki testi koşar.
  - Given warehouse_staff, When sipariş detay API'si çağrılırsa, Then fiyat alanları yanıtta yer almaz.
- **US-SEC-003**
  - Given aynı hesaba 5 başarısız giriş, When 6. deneme yapılırsa, Then hesap 15 dk geçici kilitlenir ve kullanıcıya e-posta gider.
  - Given tenant başına dakikalık limit aşımı, When istek gelirse, Then 429 ve `Retry-After` döner.
- **US-SEC-004**
  - Given kayıt ekranı, When kullanıcı kaydolursa, Then KVKK aydınlatma metni gösterilir ve onay kaydı (sürüm, zaman) saklanır.
  - Given owner veri silme talebi, When oluşturulursa, Then talep platform onay kuyruğuna düşer; gerçek silme yalnızca ürün sahibi onayıyla ve önce tam dışa aktarma sunularak yapılır.
- **US-SEC-005**
  - Given günlük yedekleme, When pilot öncesi geri yükleme tatbikatı yapılır, Then RTO ≤ 4 saat içinde ayrı ortama geri yükleme ve tutarlılık kontrolü başarılıdır; sonuç `docs/operations/` altında kayıtlıdır.
- **US-SEC-006**
  - Given API isteği, When loglanır, Then korelasyon ID, tenant ID, süre, sonuç kodu bulunur; e-posta, telefon, şifre, token loglanmaz.
  - Given uptime izleme, When sağlık ucu 3 ardışık kontrolde başarısızsa, Then alarm üretilir.

---

## Bağımlılıklar (özet)
| Hikâye | Bağımlı olduğu |
|---|---|
| US-INV-008 (rezervasyon) | US-INV-002 (ledger), US-PLT-005 (idempotency) |
| US-ORD-004 (onay) | US-INV-008, US-CAT-006 (fiyat listesi) |
| US-WAI-001…004 | US-CAT-003, US-CAT-005, US-CAT-007 (arama altyapısı), US-ORD-001 |
| US-WHT-003/004 | US-INV-003 (raf), US-CAT-003 (barkod) |
| US-IMP-004 (stok import) | US-INV-002 |
| US-SUB-002 | US-SUB-001, US-PLT-001 |
| Tüm epikler | US-PLT-001, US-SEC-002 |

## Ürün sahibi kararı bekleyen konular
PRD §9 (AQ-01 … AQ-10) ve `docs/design/design-system-plan.md` §9 (D-01 … D-03).
