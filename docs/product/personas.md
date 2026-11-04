# BayiOS — Personalar

Tüm kişiler ve işletmeler sentetiktir. Personalar pilot sektörlerden (motosiklet yedek parça, LED/elektronik toptan) türetilmiştir.

## Özet

| # | Persona | Sistem rolü | Ana cihaz | Teknoloji okuryazarlığı | Birincil modüller |
|---|---|---|---|---|---|
| P1 | Kemal — İşletme sahibi | owner | Dizüstü + akıllı telefon (mobil tarayıcı) | Orta | Pano, raporlar, onay, ayarlar, modüller |
| P2 | Elif — Ofis / satış personeli | sales | Masaüstü PC (çift ekran), WhatsApp Web | Orta-yüksek | WhatsApp asistanı, siparişler, müşteriler, katalog arama |
| P3 | Murat — Depo / operasyon yöneticisi | manager | Dizüstü + Android telefon | Orta | Depo görevleri, mal kabul, sayım, transfer, stok |
| P4 | Yusuf — Depo toplayıcı / sevkiyatçı | warehouse_staff | Android telefon veya el terminali | Düşük-orta | Mobil görev listesi, toplama, barkod, stok sorgu |
| P5 | Selin — Admin / katalog sorumlusu | admin | Masaüstü PC | Yüksek | Toplu içe aktarma, katalog, kullanıcılar, fiyat listeleri |
| P6 | Hakan — Bayi müşteri (gelecek) | dealer_user (rezerve) | Akıllı telefon | Orta | Bayi portalı (MVP dışı); bugün WhatsApp ile sipariş verir |

Ek rol: `viewer` (ör. dış muhasebeci) — yalnızca okuma ve dışa aktarma; ayrı persona olarak modellenmemiştir.

---

## P1 — Kemal, işletme sahibi
- **Profil:** 47 yaş, "Yıldız Moto Yedek Parça" (sentetik) kurucusu; 9 çalışan, 1 merkez depo + 1 dükkân arka deposu. ~8.500 aktif SKU.
- **Hedefler:**
  - Hangi üründen ne kadar olduğunu ve neyin kritik seviyede olduğunu tek bakışta görmek.
  - Çift satış ve "var dedik yok çıktı" kaynaklı müşteri kaybını bitirmek.
  - Personele bağımlılığı azaltmak (raf bilgisi, müşteri fiyatları sistemde olsun).
  - Kurulumun günler değil saatler sürmesi.
- **Acılar:**
  - Stok Excel'i haftada bir güncelleniyor; gerçeği yansıtmıyor.
  - Bayilere özel fiyatlar kafasında veya not defterinde.
  - Önceki ERP denemesi aylarca sürdü ve bırakıldı.
- **Davranış:** Gün içinde telefondan pano kontrol eder; büyük siparişleri kendisi onaylamak ister; aylık maliyete hassastır.
- **Başarı ölçütü:** "Akşam telefondan bakınca bugün kaç sipariş çıktı, neler eksik, görebiliyorum."
- **Tasarım çıkarımı:** Mobil tarayıcıda okunabilir pano; tek dokunuşla onay; sade, teknik olmayan dil.

## P2 — Elif, ofis / satış personeli
- **Profil:** 29 yaş; günde 40–80 WhatsApp siparişi alıyor (çoğu bayi ustalardan). WhatsApp Web açık, sipariş bilgisini Excel'e/deftere geçiriyor.
- **Hedefler:**
  - Mesajı kopyala-yapıştır ile siparişe dönüştürmek; ürünleri tek tek aramamak.
  - Müşteriye hızlı "var/yok, fiyat şu" yanıtı vermek.
  - Doğru müşteri fiyatını otomatik görmek.
- **Acılar:**
  - "CG 125 debriyaj balatası" gibi ifadelerde hangi SKU olduğu belirsiz; yanlış ürün → iade.
  - Aynı mesajı iki kez işleyip çift sipariş girme riski.
  - Depoya sözlü/yazılı sipariş iletmek; "hazırlandı mı?" diye sürekli sormak.
- **Davranış:** Klavye kısayollarını sever, hızlı çalışır, çok sekmeli. Hata yapınca geri alabilmek ister.
- **Başarı ölçütü:** "Mesajı yapıştırıyorum, 1 dakikada onaya hazır sipariş çıkıyor; emin olmadığı satırları bana soruyor."
- **Tasarım çıkarımı:** Klavye dostu taslak ekranı, belirsiz satırlar için açık aday seçimi, satılabilir stok ve fiyat satır içinde görünür, sipariş durumu canlı.

## P3 — Murat, depo / operasyon yöneticisi
- **Profil:** 38 yaş; 4 depo personelini yönetiyor. LED toptancısında (sentetik "Parlak Ekran Teknoloji") mal kabul, sayım ve sevkiyattan sorumlu.
- **Hedefler:**
  - Toplama işlerini adil ve hızlı dağıtmak; kim ne yapıyor görmek.
  - Mal kabulü ve sayımı kâğıtsız yapmak.
  - Raf düzenini sistemde tutmak, yeni personelin ilk gün toplayabilmesi.
- **Acılar:**
  - Sayım farklarının kaynağı bulunamıyor.
  - Konteyner geldiğinde yüzlerce modül/kart kabulü saatler sürüyor.
  - Eksik ürün sevkte fark ediliyor, müşteri arıyor.
- **Davranış:** Gün boyu ayakta; telefonla depoda, öğle ve akşam dizüstünde raporlara bakar.
- **Başarı ölçütü:** "Eksik çıkan ürünü toplama anında görüyorum, sayım farkı hareket geçmişinden çözülüyor."
- **Tasarım çıkarımı:** Görev panosu (atanmamış/devam/eksikli), mobilde mal kabul ve sayım, fark raporu, sebep kodları.

## P4 — Yusuf, depo toplayıcı / sevkiyatçı
- **Profil:** 23 yaş; lise mezunu; telefonu iyi kullanır ama karmaşık formlardan kaçınır. Eldivenle çalışabilir, depo ışığı loş, gürültülü.
- **Hedefler:**
  - Ne toplayacağını, hangi raftan, kaç adet olduğunu net görmek.
  - Yanlış ürünü almadan önce uyarılmak.
  - Ekstra yazı yazmadan işi bitirmek.
- **Acılar:**
  - Benzer kodlu parçalar (ör. aynı balatanın iki model yılı) karışıyor.
  - Kâğıt liste kayboluyor/okunmuyor.
  - Eksik ürünü kime söyleyeceğini bilmiyor.
- **Davranış:** Tek elle kullanım; ekranın alt yarısına dokunur; ses/titreşim geri bildirimine güvenir.
- **Başarı ölçütü:** "Okut, yeşil yandı, sonraki raf."
- **Tasarım çıkarımı:** Büyük dokunma alanları (≥ 48 dp), yüksek kontrast, tek ana eylem butonu altta, barkod-öncelikli akış, sesli/titreşimli hata, "Eksik bildir" tek dokunuşla.

## P5 — Selin, admin / katalog sorumlusu
- **Profil:** 33 yaş; LED toptancısında satın alma + katalog. Tedarikçi fiyat listeleri Excel, her ay güncelleniyor; ~3.200 SKU, teknik özellik ağırlıklı.
- **Hedefler:**
  - Tedarikçi Excel'ini kolonları elle düzenlemeden içe aktarmak.
  - Fiyat güncellemesini stoktan bağımsız yapmak.
  - Teknik özellikleri (pixel pitch, modül ölçüsü, kontrol kartı) filtrelenebilir tutmak.
- **Acılar:**
  - Her içe aktarmada kolon adları farklı.
  - Yanlış yüklemenin geri alınamaması korkusu.
  - Aynı ürünün iki kez oluşması.
- **Davranış:** Detaycı; önizleme ve hata raporunu okur; Excel'de düzeltip tekrar yükler.
- **Başarı ölçütü:** "Eşlemeyi bir kez yapıyorum, sonraki ay sistem hatırlıyor; hata olursa geri alıyorum."
- **Tasarım çıkarımı:** Adım adım sihirbaz, kayıtlı eşleme şablonları, satır hata indirme, geri alma butonu ve açık geri alma etkisi özeti.

## P6 — Hakan, bayi müşteri (gelecek persona)
- **Profil:** 41 yaş; ilçede motosiklet tamirhanesi sahibi; haftada 3–5 kez parça siparişi verir.
- **Bugün:** WhatsApp sesli/yazılı mesajla sipariş verir, fotoğraf gönderir ("bundan 2 tane").
- **Hedefler:** Stok ve kendi fiyatını görmek, siparişin ne zaman çıkacağını bilmek.
- **Acılar:** Yanıt beklemek; yanlış parça gelmesi; fiyatın her seferinde sorulması.
- **MVP etkisi:** Doğrudan kullanıcı değil. Ancak WhatsApp ayrıştırmanın girdisini o üretir; yazım tarzı (kısaltma, model yılı, "geçen seferki gibi") test verisine yansıtılmalı. `dealer_user` rolü rezerve edilir.

---

## Personalar × kritik ihtiyaç matrisi

| İhtiyaç | P1 | P2 | P3 | P4 | P5 |
|---|---|---|---|---|---|
| Doğru satılabilir stok | ●●● | ●●● | ●●● | ●● | ●● |
| Hızlı ürün arama | ● | ●●● | ●● | ●● | ●●● |
| WhatsApp → sipariş | ●● | ●●● | – | – | – |
| Mobil barkod | ● | – | ●●● | ●●● | ● |
| Toplu içe aktarma | ● | – | ● | – | ●●● |
| Raporlar / dışa aktarma | ●●● | ● | ●● | – | ●● |
| Yetki / kullanıcı yönetimi | ●●● | – | ● | – | ●● |
