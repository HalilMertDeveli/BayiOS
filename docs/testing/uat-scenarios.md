# BayiOS — Pilot UAT senaryo listesi

> Kullanıcı kabul testi (UAT) senaryoları — pilot işletmelerle birlikte yürütülür. Her senaryo bugün web uygulamasında
> çalışan davranışa göre yazılmıştır (kaynak: `apps/web/src`, `packages/contracts/openapi.json`, kullanıcı kılavuzu
> `docs/user-guide/`). Ekranda olmayan özellikler senaryoya alınmamıştır; bkz. `docs/user-guide/bilinen-sinirlamalar.md`.
> Kabul ölçütü (issue #59): Türkçe kullanıcı kılavuzu, UAT senaryo listesi, **açık kritik hata yok**.

## 1. Hazırlık

### 1.1 Ortam ve veri
- Yalnızca **sentetik veri** kullanılır (gerçek müşteri, fiyat listesi, telefon numarası yok). Repo public'tir.
- İki test işletmesi:
  - **M** = "Yıldız Moto Yedek Parça (sentetik)", sektör *Motosiklet yedek parça* (`docs/product/sector-scenarios.md` §1).
  - **L** = "Parlak Ekran Teknoloji (sentetik)", sektör *LED ve elektronik* (§2).
- AI ayrıştırma pilotta kapalıdır (`Ai:Enabled=false`); WhatsApp senaryoları **kural tabanlı** ayrıştırıcıyla yürütülür.
- Tarayıcı: masaüstü Chrome/Edge + bir Android telefonda Chrome (depo senaryoları). Barkod okuyucu (USB/Bluetooth, klavye modu, Enter sonekli) önerilir.

### 1.2 Kullanıcılar
Rol kullanıcıları **Ayarlar → Ekip** ekranından davetle eklenir (UAT-18); davet e-postası gönderilmez, bağlantı kişiye
elle iletilir. Pilot öncesi BayiOS ekibi kullanıcıları hazırlayabilir. Her işletme için: `sahip` (owner), `yonetici` (admin), `mudur` (manager), `satis` (sales), `depo` (warehouse_staff), `izleyici` (viewer).

### 1.3 Ortak ön koşul seti
- **P0**: Hesap ve işletme oluşturulmuş (UAT-01).
- **P1**: P0 + en az 5 ürün, 1 fiyat listesi (varsayılan), 2 müşteri, mal kabulle stok girilmiş.
- **P2**: P1 + depoda raf/göz takibi açık ve raf barkodları etiketli (Profesyonel/deneme).

### 1.4 Sonuç kodları
**G** = geçti · **K** = kaldı (hata kaydı açın: `.github/ISSUE_TEMPLATE/bug.yml`, önem: kritik/yüksek/orta/düşük) · **E** = engellendi (ön koşul sağlanamadı).
"Sonuç" ve "Not" sütunları pilot sırasında doldurulur.

---

## 2. Senaryolar

### 2.1 Hesap açma ve işletme kurulumu
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-01 | Yeni kullanıcı | Yok | `/kayit` → Ad soyad, e-posta, şifre (8+ karakter, harf+rakam) → **Hesap oluştur** → e-postadaki bağlantı → 4 adımlı sihirbaz: ad "Yıldız Moto Yedek Parça", sektör *Motosiklet yedek parça*, TRY, %20, KDV hariç, İzin verme → **İşletmeyi oluştur** | "İşletmeniz hazır" bildirimi; ana ekran açılır. Özel alanlar sayfasında Uyumlu motosiklet markaları, Uyumlu model, Model yılı aralığı, OEM / orijinal parça no, Taraf / konum, Garanti, Parça tipi var; ürün formunda Birim listesinde Adet, Takım, Kutu, Litre, Metre | Motor | | |
| UAT-02 | Yeni kullanıcı | Yok | UAT-01'i sektör *LED ve elektronik* ile tekrarlayın ("Parlak Ekran Teknoloji") | Güç (W), Renk sıcaklığı (K), Voltaj (V), IP sınıfı, Pixel pitch (mm), Kullanım, Modül ölçüsü, Parlaklık, Tarama, LED tipi, Kontrol kartı uyumu, Seri no takibi alanları; birimler Adet, Kutu, Koli, Metre | LED | | |
| UAT-03 | Yeni kullanıcı | M mevcut | Sihirbazda kısa adı M ile aynı girin | "Bu işletme adresi başka bir işletme tarafından kullanılıyor. …"; 1. adıma dönülür | Negatif | | |
| UAT-04 | Yeni kullanıcı | Yok | Kayıtta şifre "abcdefgh" ve farklı tekrar şifresi | "Şifre en az bir harf ve bir rakam içermeli." / "Şifreler eşleşmiyor." | Negatif | | |
| UAT-05 | sahip (iki işletmenin üyesi) | M ve L'de üyelik | Giriş → **İşletme seç** → M; sağ üstten L'ye geçin; L'de ürün listesine bakın | M'nin ürünleri L'de **asla** görünmez; aktif işletme adı doğru | Güvenlik | | |

### 2.2 Roller ve izinler
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-10 | Her rol | P1, 6 rol kullanıcısı | Sırayla her rolle giriş yapın; menüyü not edin | Menü kılavuz §2 tablosuyla uyumlu (ör. depo: Siparişler, Ürünler, Stok, Depo, Görevler, Sevkiyatlar; izleyici'de İçe aktarma ve Ayarlar yok). Depo personeli girişte **Görevler** ekranına düşer | Yetki | | |
| UAT-11 | satis | P1 | `/stok/duzeltme` adresini elle açın | "Stok düzeltme yetkiniz yok" | Negatif (yetkisiz rol) | | |
| UAT-12 | depo | P1, depo'ya atanmış görev | **Siparişler**'e girin; görevine bağlı siparişi açın | Yalnızca görevli siparişler listelenir; "Fiyat ve tutarlar rolünüz gereği gösterilmiyor; …"; müşteri adı boş | Yetki | | |
| UAT-13 | izleyici | P1 | Bir ürünü açın; bir müşteriyi açın | "Salt okunur görünüm"; kaydet/arşivle düğmeleri yok | Negatif (yetkisiz rol) | | |
| UAT-14 | mudur | P1 | **Ayarlar**'a girin | "Ayarları değiştirme yetkiniz yok" (Ayarlar menüde görünür, form kapalı) | Negatif (yetkisiz rol) | | |
| UAT-15 | yonetici | P1 | **Ayarlar → Paket ve modüller** → bir modülde etkinleştir/kapat arayın | Sayfa görünür; düğmeler yok; "Modülleri yalnızca işletme sahibi etkinleştirebilir veya kapatabilir." | Negatif (yetkisiz rol) | | |
| UAT-16 | satis | M'de mudur'un oluşturduğu taslak | Taslağı açıp satır değiştirmeye çalışın; sonra **Siparişi iptal et** arayın | "Bu taslağı başka bir kullanıcı oluşturdu; …"; satırlar salt okunur; iptal düğmesi yok | Negatif (yetkisiz rol) | | |
| UAT-17 | Herhangi | M ve L | M kullanıcısıyla, L'deki bir siparişin adresini (`/siparisler/{L-id}`) açın | "Sipariş bulunamadı" (kayıt varlığına dair ipucu yok) | Güvenlik (kritik) | | |
| UAT-18 | sahip → yeni kullanıcı | M | **Ayarlar → Ekip** → davet: sentetik e-posta, rol Satış → **Davet bağlantısı oluştur** → bağlantıyı başka bir tarayıcıda açın → aynı e-postayla kaydolup adresi doğrulayın → daveti kabul edin | Bağlantı 7 gün geçerli; kabul sonrası kişi M'de Satış rolüyle görünür ve satış menüsünü görür. Farklı e-postayla oturum açılmışsa davet kabul edilmez | Yetki / güvenlik | | Gerçek yığın E2E: `real-stack-invitations.spec.ts` |
| UAT-19 | yonetici, sahip | UAT-18 | yonetici: Satış üyesini Operasyon müdürü yapın; başka bir yöneticiyi yönetmeyi deneyin. sahip: üyeyi **askıya al** → üye bir işlem yapsın → **yeniden aç** | Yönetici yalnızca kendi altındaki rolleri yönetir; askıya alınan üye bir sonraki işleminde işletmeye erişemez, açılınca tekrar erişir; son işletme sahibi askıya alınamaz | Yetki | | Oturum kapatılmaz (bilinen sınırlama) |

### 2.3 Ürünler ve özel alanlar
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-20 | mudur | M | **Yeni ürün**: "CG 125 ön fren balatası", kategori Fren, birim Takım, KDV 20; Uyumlu markalar Honda, Uyumlu model "CG 125", Model yılı 2015–2021, Taraf Ön; 2 varyant: `FRN-CG125-ON-OR` (Parça tipi Orijinal), `FRN-CG125-ON-MU` (Muadil), farklı barkodlar → **Ürünü oluştur** | "Ürün oluşturuldu"; listede görünür; Uyumlu markalar = Honda ve Taraf = Ön filtreleriyle bulunur | Motor | | |
| UAT-21 | mudur | L | **Yeni ürün**: "P3.91 iç mekân kabinet 500x500", kategori Kabinet, birim Adet; Pixel pitch 3,91, Kullanım İç mekân, Modül ölçüsü 500x500; SKU `KAB-P391-IC-500` | Ürün oluşur; Pixel pitch 3,91 virgüllü kabul edilir; Kullanım = İç mekân filtresiyle bulunur | LED | | |
| UAT-22 | mudur | UAT-20 | Yeni ürün, SKU olarak `FRN-CG125-ON-OR` girin | "Bu SKU başka bir üründe kullanılıyor. Farklı bir SKU girin." | Negatif | | |
| UAT-23 | mudur | UAT-20 | Yeni varyantta UAT-20'deki bir barkodu girin | "Bu barkod başka bir üründeki varyantta (SKU …) kullanılıyor. …" | Negatif | | |
| UAT-24 | mudur | UAT-20 | Ürünün tek kalan aktif varyantını arşivlemeyi deneyin | "Ürünün en az bir aktif varyantı olmalı. Bunun yerine ürünü arşivleyin." | Negatif | | |
| UAT-25 | sahip | M | **Ayarlar → Özel alanlar → Yeni alan**: "Renk", Varyant, Tek seçim, seçenekler Siyah/Kırmızı, filtre olarak kullanılsın → **Alanı ekle**; sonra arşivleyin | Alan formlarda/filtrelerde görünür; arşivlenince gizlenir, ürünlerdeki değer silinmez | Motor | | |
| UAT-26 | mudur ve yonetici | UAT-20 | İki tarayıcıda aynı ürünü açın; birinde kaydedin, diğerinde farklı değişiklik kaydedin | İkinci kayıtta "Kayıt başkası tarafından değiştirildi"; **Yeniden yükle** sonrası güncel değer | Eşzamanlılık | | |
| UAT-27 | mudur | M | Ürünü **Arşivle** → listede arayın → ürünü açıp **Arşivden çıkar** | Arşivli ürün listede ve yeni siparişte görünmez; geri alınınca görünür | Motor | | |

### 2.4 Toplu aktarma (web sihirbazı, kılavuz bölüm 4)
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-30 | mudur | M | Menüden **İçe aktarma**'ya girin | "İçe aktarma için yetkiniz yok" (op. müdürü yalnızca stok aktarabilir; stok aktarma henüz yok) | Negatif (yetkisiz rol) | | |
| UAT-31 | sahip | M, sentetik 200 satırlık `.xlsx` (5 hatalı satır, 2 tekrar SKU) | **İçe aktarma → Yeni içe aktarma** → Ürünler → dosya → kolon eşleme (önerileri kontrol edin) → **Kaydet ve önizle** → **Hata raporunu indir (CSV)** → "N satırı aktar" | Önizlemede Hatalı = 7 (5 hata + 2 tekrar SKU); hata CSV'si Excel'de Türkçe karakterlerle açılır; yalnızca geçerli satırlar aktarılır; geçmiş listesinde görünür | Motor | | Gerçek yığın E2E: `real-stack-imports.spec.ts` |
| UAT-32 | sahip | UAT-31 | Bir ürünü elle değiştirin; sonuç ekranında **Geri al**; sonra tekrar geri almayı deneyin | Eklenen ürünler arşivlenir; elle değiştirilen satır "Çakışma (dokunulmadı)"; ikinci geri alma "Bu aktarma zaten geri alındı …" ile reddedilir | Negatif | | |
| UAT-33 | sahip | M | 20 MB üstü dosya (OD-04); 10.000 satır üstü dosya; `.xlsm` dosya | "Dosya 20 MB sınırını aşıyor." / "Dosyada 10.000'den fazla satır var." / "Makro içeren Excel dosyaları (.xlsm) kabul edilmez." | Negatif | | |
| UAT-34 | sahip | M, fiyat listesi Perakende | Fiyat listesi kalemleri aktarması, hedef liste Perakende; eşlemeyi şablon olarak kaydedin; aynı başlıklı ikinci dosyada **Şablonu uygula** | Fiyatlar listeye yazılır; şablon eşlemeyi tek seferde doldurur | Motor | | |

### 2.5 Depo, raf ve mal kabul
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-40 | depo | M, hiç depo yok, ürünler var | **Mal kabul** → okuyucuyla 3 ürün okutun, miktar girin, referans "IRS-0001" → **Mal kabulü kaydet** | "Ana Depo" otomatik oluşur; "Mal kabul kaydedildi"; **Stokta gör** ile Rafta = Satılabilir = girilen miktar | Motor | | |
| UAT-41 | mudur | M (deneme/Profesyonel) | **Depo → Yeni depo** "DUKKAN", raf takibi açık → **Yeni lokasyon** A-01-01 (barkodlu, sıra 1), A-02-01 (sıra 2) | Depo ve lokasyonlar listelenir | Motor | | |
| UAT-42 | depo | UAT-41 | DUKKAN'a mal kabul; satırda raf seçmeden kaydedin | "Bu depoda raf/göz (lokasyon) seçmek zorunlu." | Negatif | | |
| UAT-43 | mudur | UAT-41, DUKKAN'da stok var | DUKKAN'ı düzenleyip raf takibini kapatın | "Depoda stok varken raf/göz takibi açılıp kapatılamaz. …" | Negatif | | |
| UAT-44 | depo | L, kablo ürünü birimi Metre | 12,5 metre flat kablo mal kabulü | Ondalık miktar kabul edilir | LED | | |
| UAT-45 | depo | L, ürün birimi Adet | 2,5 adet mal kabulü | "Bu ürünün birimi küsurat kabul etmiyor; tam sayı girin." | Negatif / LED | | |
| UAT-46 | depo (telefon) | P1 | Telefon tarayıcısında mal kabul ekranında okutma ve kaydetme | Düğmeler parmakla rahat basılır; kayıt düğmesi altta sabit; işlem tamamlanır | Mobil web | | |

### 2.6 Stok düzeltme ve negatif stok
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-50 | mudur | P1 | **Stok düzelt** → ürün okut → Hasar / fire çıkışı, 1, sebep "Kırık ambalaj" → **Düzeltmeyi kaydet** | "Stok düzeltildi"; stok kartında "Hasar / fire" hareketi ve sebep görünür | Motor | | |
| UAT-51 | mudur | P1 | Sebep boş bırakıp kaydedin | "Düzeltme sebebini yazın (en fazla 500 karakter)." | Negatif | | |
| UAT-52 | mudur | Ürünün tüm stoğu onaylı siparişe ayrılmış | Stok düş (düzeltme çıkışı) 1 | "Satılabilir stok yetersiz. Siparişler için ayrılmış (rezerve) stok düzeltmeyle düşülemez." | Negatif (stok yetersiz) | | |
| UAT-53 | sahip | — | **Ayarlar → Sipariş ayarları** ve **İş kuralları** | "Stok eksiye düşmez" **Sabit kural** olarak görünür; seçim kutusu yok (OD-09) | Negatif stok | | |
| UAT-54 | mudur | Satılabilir 2 | Stok düş 5 (onay penceresi çıkmaz) | "Stok yetersiz: işlem negatif stok kuralına takıldı." | Negatif (stok yetersiz) | | |
| UAT-55 | mudur | P1, ürünün satılabilir stoğu 4 | Ürünü açın → **Minimum stok** 5 → **Minimum stoku kaydet**; **Stok** listesi ve **Pano**'ya bakın | Stok hareketi oluşmaz; Stok'ta **Min. stok** 5 ve **Kritik** rozeti; panoda **Kritik stok** kutusu ürünü sayar | Motor | | |
| UAT-56 | mudur | UAT-55, iki depo | **Stok** → ürünün SKU'su → **Minimum stok** bölümünde ikinci depoya 1 girin; kritik stok raporunda depo filtresiyle bakın | İkinci depoda depo eşiği (1), diğer depoda genel eşik (5) geçerli; satış veya depo personeli değer giremez | Motor / yetki | | |

### 2.7 Müşteriler, bayi grupları, fiyat listeleri
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-60 | sahip | M, ürünler | **Fiyat listeleri → Yeni liste** "Perakende" (Satış, TRY, KDV hariç) → fiyat girin; ikinci liste "Bayi A" = Perakende'den %10 iskontolu türetme | İlk liste otomatik varsayılan; Bayi A fiyatları taban − %10 | Motor | | |
| UAT-61 | sahip | UAT-60 | Perakende'de `FRN-CG125-ON-MU` için kademe: en az 1 → 200, en az 10 → 180 | Fiyat sorgula: 5 adet → 200, 12 adet → 180 | Motor | | |
| UAT-62 | mudur | UAT-60 | **Bayi grupları → Yeni grup** "Bayi A", liste Bayi A, varsayılan iskonto 0 | Grup listelenir | Motor | | |
| UAT-63 | satis | UAT-62 | **Yeni müşteri** "Usta Ali Motor (sentetik)", kod C-001, VKN 10 hane, WhatsApp +905000000001 | Müşteri oluşur; satış personeli bayi grubu atayamaz ("Bayi grubu ve fiyat listesi atama yetkiniz yok; …") | Motor / yetki | | |
| UAT-64 | mudur | UAT-63 | Müşteriye Bayi A grubunu atayın; **Fiyat sorgula**'da C-001 + ürün | Kaynak "Bayi grubu listesi", fiyat Bayi A | Motor | | |
| UAT-65 | satis | UAT-63 | İkinci müşteriye kod C-001 verin; VKN 9 hane; WhatsApp "05001234567" | "Bu müşteri kodu zaten kullanılıyor. …"; "Vergi numarası 10 (VKN) veya 11 (TCKN) haneli olmalı."; "WhatsApp numaralarını ülke koduyla girin, …" | Negatif | | |
| UAT-66 | mudur | L | "Mavi Sahne Entegrasyon (sentetik)" müşterisi + teslimat ve fatura adresi | Her türün ilk adresi varsayılan | LED | | |
| UAT-67 | sahip | UAT-60 | Varsayılan Perakende listesini pasif yapın | "Varsayılan liste pasif yapılamaz. Önce başka bir listeyi varsayılan yapın." | Negatif | | |

### 2.8 Sipariş → onay → rezervasyon
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-70 | satis | P1, M'de "Siparişler onaydan geçsin" açık | **Yeni sipariş** → C-001 → `FRN-CG125-ON-MU` 3 → **Taslak oluştur** → **Onaya gönder** | Fiyatlar Bayi A'dan; sipariş no `SIP-<yıl>-…`; durum Onay Bekliyor | Motor | | |
| UAT-71 | mudur | UAT-70, satılabilir 14 | **Onayla ve stok ayır** | Durum Onaylandı; Stok'ta Ayrılmış +3, Satılabilir 14 → 11; Görevler'de toplama görevi oluşur | Motor | | |
| UAT-72 | mudur | Satılabilir 2, sipariş 5 adet, politika İzin verme | Onayla | "Stok yetersiz: siparişteki hiçbir satır için stok ayrılmadı (ya hep ya hiç)."; "Stoğu yetmeyen satırlar" listesi; hiçbir satıra rezervasyon yok | Negatif (stok yetersiz) | | |
| UAT-73 | mudur | M-09: ürün rafta var ama tamamı başka siparişe ayrılmış | Yeni siparişi onaylayın | Onay engellenir; satır Ayrılmış/Satılabilir bilgisini gösterir | Motor / negatif | | |
| UAT-74 | mudur | Stok yetersiz | Onayla | Onaylanmaz; eksik satırlar listelenir, "Yine de onayla" yok (OD-09: stok eksiye düşmez) | Negatif stok | | |
| UAT-75 | mudur | Fiyat listesinde fiyatı olmayan ürünle taslak | **Onaya gönder** | Gönder düğmesi engelli: "Fiyatı olmayan satırlar var; fiyatlanmadan onaya gönderilemez." | Negatif | | |
| UAT-76 | mudur | Onay Bekliyor sipariş | **Reddet** (sebep yazın) | Taslağa döner; zaman çizelgesinde "Reddedildi (taslağa döndü)" ve sebep | Motor | | |
| UAT-77 | mudur | UAT-71 (onaylı, sevk yok) | **Siparişi iptal et** → sebep "Müşteri vazgeçti" + açıklama | İptal; ayrılan stok serbest; toplama görevi iptal olur | Motor | | |
| UAT-78 | sahip | "Siparişler onaydan geçsin" kapalı | mudur ile sipariş gönderin; satis ile sipariş gönderin | mudur: "Sipariş otomatik onaylandı; stok ayrıldı". satis (kural açık): otomatik onaylanır | Ayarlar | | |
| UAT-79 | satis | Satış iskonto sınırı %0 (varsayılan) | Taslakta satıra %5 iskonto girip kaydedin | "İşletmenizde satış personeli için iskonto sınırı %0; …" | Negatif (yetkisiz) | | |
| UAT-80 | mudur ×2 | Onay Bekliyor sipariş, satılabilir tam 3 | İki tarayıcıda iki farklı 3 adetlik siparişi aynı anda onaylayın | Yalnızca biri onaylanır; diğeri "Stok yetersiz" — stok asla çift ayrılmaz | Eşzamanlılık (kritik) | | |
| UAT-81 | mudur | L, `KAB-P391-IC-500` satılabilir 18 | 24 adetlik kabinet siparişi onayı | Engellenir (OD-09); ekranda eksik satır gösterilir | LED / negatif | | |
| UAT-82 | mudur | UAT-114 sonrası kısmi sevk edilmiş sipariş | Sipariş sayfasında **Kalanı kapat** → sebep seçin | Kalan rezervasyon serbest bırakılır (Stok'ta Ayrılmış azalır); zaman çizelgesinde kalanın kapatıldığı görünür | Motor | | Gerçek yığın E2E: `demo-led.spec.ts` |
| UAT-83 | mudur | Sevk edilmiş sipariş | **Tamamla** | "Sipariş tamamlandı"; durum Tamamlandı; tamamlanma zamanı görünür | Motor | | |

### 2.9 WhatsApp siparişi (kural tabanlı)
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-90 | satis | M, P1, sector-scenarios §1.3 ürünleri | **WhatsApp siparişi** → C-001 → §1.3 "Örnek mesaj 1"i yapıştırın → **Mesajı ayrıştır** | Rozet "Kural tabanlı" ve "AI ayrıştırma bu ortamda kapalı. …"; satırlar Eşleşti/Belirsiz/Eşleşmedi olarak gelir; "geçen seferki fiyattan" fiyat olarak kullanılmaz | Motor | | |
| UAT-91 | satis | UAT-90, balata satırı Belirsiz | Belirsiz satırda hiçbir aday seçmeden **Taslak siparişi oluştur**'a bakın; sonra Muadil'i seçin | Aday önceden seçili değil; düğme engelli ("Adaylardan birini seçin veya satırı çıkarın."); seçim sonrası satır Hazır | Negatif (belirsiz satır) | | |
| UAT-92 | satis | UAT-90, Eşleşti satırı | Önerilen ürünü onaylamadan taslak oluşturmayı deneyin | "Önerilen ürünü onaylayın veya başka bir ürün seçin." | Negatif | | |
| UAT-93 | satis | M | "cg 150 kuba 2" (katalogda yok) | Satır Eşleşmedi; katalogda arayıp seçebilir veya **siparişten çıkar**; sistem ürün yaratmaz | Motor / negatif | | |
| UAT-94 | satis | M | "ns200 buji 2 kutu" — ürün birimi Adet | Birim uyuşmazlığı uyarısı; miktarı ürün birimine göre girmeniz istenir | Motor | | |
| UAT-95 | satis | UAT-91 hazır | **Taslak siparişi oluştur** | "Taslak sipariş oluşturuldu"; kaynak "WhatsApp (AI)"; fiyatlar fiyat listesinden; "Siparişler onaydan geçsin" kapalı olsa bile sipariş onay bekler | Motor (kritik kural) | | |
| UAT-96 | satis | L, P1 | sector-scenarios §2.3 "Örnek mesaj 2"yi yapıştırın, müşteri Mavi Sahne | Satırlar okunur; P2.5 modül iç/dış belirsizliği Belirsiz olarak gelir; "Teslim cuma" taslağa kendiliğinden yazılmaz | LED | | |
| UAT-97 | satis | L | "p10 dış mekan modül 1/4 scan 40 tane fiyat atar mısın" | Fiyat uydurulmaz; uyarı "Mesajda fiyat geçiyor; dikkate alınmadı. …" (varsa) | LED | | |
| UAT-98 | satis | Herhangi | Sipariş olmayan bir mesaj ("merhaba nasılsınız") | "Mesajda sipariş satırı bulunamadı"; **Ayrıştırmayı reddet** → "Ayrıştırma reddedildi" | Negatif | | |
| UAT-99 | satis | M | Telefon numarası ve e-posta içeren mesaj | Uyarı "Telefon numarası ve e-posta adresleri ayrıştırmadan önce gizlendi." | Gizlilik | | |
| UAT-100 | depo | M | `/siparisler/whatsapp` adresini açın | "WhatsApp siparişi yetkiniz yok" | Negatif (yetkisiz rol) | | |

### 2.10 Toplama, paketleme, sevkiyat (telefon tarayıcısı)
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-110 | depo (telefon) | UAT-71, P2 | **Görevler → Havuz** → **Üstlen** → **Toplamaya başla** | Görev "Görevlerim"e geçer; satırlar raf sırasıyla | Motor | | |
| UAT-111 | depo ×2 | Havuzda 1 görev | İki telefonda aynı anda **Üstlen** | Yalnızca biri alır; diğeri "Bu görevi az önce başka biri aldı. …" | Eşzamanlılık | | |
| UAT-112 | depo | UAT-110 | Raf okutmadan ürün okutun; yanlış raf okutun; Orijinal varyantı (siparişte Muadil) okutun | Sırasıyla "Önce raf (lokasyon) barkodunu okutun, sonra ürünü.", "Yanlış raf: …", "Yanlış ürün: okutulan ürün bu görevde yok. …"; sunucuda değişiklik yok | Motor / negatif | | |
| UAT-113 | depo | UAT-110 | İstenenden 1 fazla okutun | "Fazla toplama: …" | Negatif | | |
| UAT-114 | depo | Buji satırında rafta 1 kutu (istenen 2) | **Eksik bildir** → Raftaki miktar yetersiz, 1 → **Görevi tamamla** → **Eksikle tamamla** | Sipariş "Depoda eksik bildirildi" ile işaretlenir; taslak sevkiyat toplananlarla oluşur; görev "Eksikle tamamlandı" | Motor | | |
| UAT-115 | depo | UAT-110 tamam | **Sevkiyata git** → paket kontrolünde ürünleri okutun → **Paket kontrolünü kaydet** | Satırlar "Tamam"; koli sayısı kaydedilir | Motor | | |
| UAT-116 | depo | UAT-115 | Kargo firması, takip no gir → **Sevkiyatı onayla** → **Sevk et** | Stok kartında "Sevkiyat" çıkışı; Rafta ve Ayrılmış azalır; sipariş "Sevk Edildi" (eksik varsa Kısmi sevk) | Motor | | |
| UAT-117 | depo | UAT-116 | Aynı sevkiyatı iki kez onaylamaya çalışın (çift tık / ikinci sekme) | Stok yalnızca bir kez düşer; "Bu sevkiyat zaten onaylanmış; …" | Eşzamanlılık (kritik) | | |
| UAT-118 | depo | Taslak sevkiyat | **Sevkiyatı iptal et** arayın | Düğme yok; "Onaylanmamış sevkiyatı görev atama yetkisi olan bir yönetici iptal edebilir." | Negatif (yetkisiz rol) | | |
| UAT-119 | mudur | Taslak sevkiyat | **Sevkiyatı iptal et** (sebep) | Stok hareketi yok; sipariş Paketlendi'de; "Rafa geri koyma" görevi oluşur | Motor | | |
| UAT-120 | mudur | Havuzda görev | **Personele ata** → depo kullanıcısı | Görev depo kullanıcısının "Görevlerim"inde | Motor | | |
| UAT-121 | depo | L, kontrol kartı siparişi | Kartları toplayıp sevk edin | Akış tamamlanır. Not: seri numarası okutma/kayıt **yok** (bilinen sınırlama) — beklenti olarak not edin | LED | | |

### 2.11 Raporlar ve dışa aktarma
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-130 | izleyici | 2.8–2.10 tamam | **Raporlar → Sipariş durumları** → Son 7 gün → **Excel (XLSX)** ve **CSV** | Sayılar sipariş listesiyle tutarlı; dosyalar Excel'de Türkçe karakterlerle açılır | Motor | | |
| UAT-131 | mudur | UAT-55 | **Kritik stok** (eşik kutusu boş); sonra **Kritik eşik** 5 → **Uygula** | Boşken yalnızca satılabilir ≤ kendi minimum stoğu olan ürünler, **Min. stok** kolonu dolu; sabit eşikte satılabilir ≤ 5 olan tüm aktif ürünler. Satılabilir = eldeki − rezerve | Motor | | |
| UAT-132 | mudur | 2.10 tamam | **Toplama görevleri**, **Stok hareketleri** | Tamamlanan / eksikle tamamlanan görevler ve sevkiyat/mal kabul/düzeltme hareketleri görünür | Motor | | |
| UAT-133 | satis | P1 | **Raporlar** | Yalnızca Sipariş durumları ve En çok sipariş edilen ürünler; "Rolünüz yalnızca sipariş raporlarını görebilir." | Negatif (yetkisiz rol) | | |
| UAT-134 | mudur | P1 | Başlangıç > bitiş; 400 günlük aralık | "Başlangıç tarihi bitiş tarihinden sonra olamaz." / "Tarih aralığı en fazla 366 gün olabilir." | Negatif | | |
| UAT-135 | mudur | P1 | Bir saat içinde 11 kez dışa aktarın | 11.'de "Saatlik dışa aktarma sınırına ulaşıldı (işletme başına saatte 10 dosya). …" | Negatif | | |
| UAT-136 | depo | P1 | `/raporlar` adresini açın | Menüde yok; sayfada "Raporları görme yetkiniz yok" | Negatif (yetkisiz rol) | | |

### 2.12 Paket, deneme süresi, modüller (ödeme yok)
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-140 | sahip | Yeni işletme | **Ayarlar → Paket ve modüller** | Profesyonel, "Ücretsiz deneme", bitiş tarihi; "Ücretsiz deneme — ödeme yok" bilgisi; hiçbir yerde ödeme/kart alanı yok | Abonelik | | |
| UAT-141 | sahip | Denemenin bitmesine ≤ 7 gün (BayiOS ekibi ayarlar) | Herhangi bir sayfa | Üstte deneme uyarısı; **X** ile oturum boyunca kapanır | Abonelik | | Bitiş tarihi ekrandan değiştirilemez |
| UAT-142 | sahip, mudur, satis | Deneme bitmiş (BayiOS ekibi ayarlar), 2 aktif depo, raflı depo | Mal kabul, sipariş, onay, toplama, sevk, rapor; **Yeni depo**; **Yeni lokasyon**; yeni WhatsApp mesajı; bekleyen eski ayrıştırmayı onaylayın | Çekirdek işler çalışır; veri silinmez; yeni depo/lokasyon ve yeni ayrıştırma kilitli ("… mevcut paketinizde yok.", "Profesyonel pakette" / "AI WhatsApp asistanı modülünde"); eski ayrıştırma onaylanabilir | Negatif (deneme bitti) | | |
| UAT-143 | sahip | UAT-142 | AI WhatsApp asistanında **Ücretsiz etkinleştir**; sonra **Kapat**; yeniden etkinleştirmeyi deneyin (süre bittikten sonra) | Modül 30 gün açılır; kapatınca veriler kalır; deneme hakkı bitmişse "Bu modülün ücretsiz denemesi daha önce kullanıldı. …" | Abonelik | | |
| UAT-144 | yonetici | Deneme bitmiş | Kilitli depo ekranı | "Paket ve modülleri gör" bağlantısı; satis/mudur'da "Paket değişikliği için işletme sahibiyle görüşün." | Abonelik | | |

### 2.13 Ayarlar
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-150 | sahip | P1 | Önek "YM" → **Ayarları kaydet** → yeni sipariş | "Sipariş ayarları kaydedildi"; yeni sipariş `YM-<yıl>-…`; eski numaralar değişmez | Ayarlar | | |
| UAT-151 | sahip | P1 | Önek "Ş-1"; "1AB" | "Yalnızca İngilizce büyük harf (A–Z) ve rakam (0–9) kullanın; …" / "Önek bir harfle (A–Z) başlamalı." | Negatif | | |
| UAT-152 | sahip, yonetici | P1 | İki tarayıcıda aynı anda ayar değiştirip kaydedin | İkinci kayıtta "Ayarlar başkası tarafından değiştirildi"; **Güncel ayarları yükle** | Eşzamanlılık | | |
| UAT-153 | sahip | P1 | Varsayılan KDV %10 → yeni ürün | Yeni ürün formunda KDV 10 önerilir | Ayarlar | | |
| UAT-154 | sahip, satis | P1 | **Ayarlar → İş kuralları** → "Satış personeli sipariş onaylayabilir" kapatın, kaydedin; satis ile bir siparişi onaylamayı deneyin | Satış personeli onaylayamaz ("Satış personelinin sipariş onaylaması işletme ayarlarında kapalı. …"); kural geri açılınca onaylar | Ayarlar / yetki | | |

### 2.14 KVKK — müşteri anonimleştirme
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-160 | sahip | Sevk edilmiş siparişi olan sentetik müşteri | Müşteri → **Müşteriyi anonimleştir** → adı eksik yazın, sonra tam yazın → **Kalıcı olarak anonimleştir** | Ad eşleşmeden düğme kapalı; sonra "Müşteri anonimleştirildi — Kod … korunuyor"; ad/iletişim/vergi/adresler silinir; müşteri arşivlenir; sipariş geçmişi ve kod kalır | KVKK | | |
| UAT-161 | sahip | UAT-160 | Anonim müşteriyi düzenlemeye çalışın; yeni siparişte arayın | Düzenlenemez; yeni siparişte çıkmaz | Negatif | | |
| UAT-162 | mudur | Herhangi müşteri | Müşteri sayfasında KVKK bölümünü arayın | Bölüm görünmez | Negatif (yetkisiz rol) | | |

### 2.15 Uçtan uca hikâyeler
| ID | Rol | Ön koşul | Adımlar | Beklenen sonuç | Kapsam | Sonuç | Not |
|---|---|---|---|---|---|---|---|
| UAT-170 | satis → mudur → depo | M, P2 | sector-scenarios §1.3 adım 1–10 (transfer adımı hariç; transfer yok): WhatsApp → taslak → onay → toplama (yanlış ürün okutma dahil) → buji eksik → paket → sevk | Her adım kılavuzdaki gibi; stok defteri: mal kabul → rezervasyon → sevkiyat çıkışı tutarlı; rapor sayıları tutarlı | Motor | | |
| UAT-171 | satis → mudur → depo | L, P2 | sector-scenarios §2.3 adım 1–8 (transfer ve seri no hariç) | Belirsiz satırlar insan seçimiyle çözülür; kabinet eksikse onay ekranı eksik satırı gösterir; sevk tamamlanır | LED | | |

---

## 3. Çıkış ölçütü
- Tüm senaryolar G veya kabul edilmiş gerekçeyle E.
- **Açık kritik hata yok.** Kritik sayılanlar: çapraz işletme veri erişimi (UAT-05, UAT-17), stok çift ayrılması/çift düşmesi
  (UAT-80, UAT-117), insan onayı olmadan sipariş kesinleşmesi (UAT-95), veri kaybı.
- Kaldı (K) senaryoları için GitHub issue numarası "Not" sütununa yazılır.

## 4. Bu listede bilerek olmayanlar
Telefon uygulaması, seri numarası, ödeme, bayi portalı, canlı AI ayrıştırma (açılınca UAT-90…99 AI ile tekrarlanacak),
sayım/transfer, stok/müşteri toplu aktarma, işletme profili/logo, 2FA.

Güncelleme (2026-11-07, #60): içe aktarma sihirbazı (#149), ekip/davet (#132, #134), iş kuralları, Tamamla / Kalanı kapat
(#131) ve minimum stok / kritik stok (#151) senaryoları eklendi: UAT-18, 19, 30–34, 55, 56, 82, 83, 131, 154.
