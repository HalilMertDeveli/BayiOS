# 7. Müşteriler, bayi grupları ve fiyat listeleri

## Amaç
Müşterilerinizi (bayilerinizi) kaydetmek, onları gruplamak ve her müşteriye doğru fiyatın otomatik gelmesini sağlamak.

**Fiyat sırası:** müşteriye özel liste → bayi grubunun listesi → varsayılan liste.
Siparişte fiyat bu sırayla aranır; hiçbirinde yoksa satır "Fiyat yok" olarak kalır.

## Adımlar

### Fiyat listesi (`/fiyat-listeleri`) — önce bunu kurun
Liste oluşturma ve fiyat girme: işletme sahibi ve yönetici. Görüntüleme: satış fiyatı görebilen roller.
1. **Yeni liste** düğmesine basın.
2. **Liste adı** (ör. Bayi A 2026, Perakende), **Liste türü** (Satış / Alış (maliyet)), **Para birimi**,
   **Fiyatlar KDV dahil** (kapalıysa fiyatlar KDV hariç girilir), **Geçerlilik başlangıcı / bitişi** (boş: hemen / süresiz).
3. İsterseniz **Taban listeden türet**: bir **Taban liste** ve **İskonto (%)** seçin; fiyatlar taban listeden iskontoyla hesaplanır.
4. Para birimi için ilk satış listesi otomatik **varsayılan** olur. Sonradan **… için varsayılan satış listesi yap** ile değiştirilebilir.
5. **Listeyi oluştur**.
6. Liste sayfasında **Fiyat ekle / güncelle** bölümünde **Ürün okut veya ara**; her satıra **En az miktar** ve **birim fiyat** girin.
   Aynı ürüne farklı "en az miktar" ile **kademeli fiyat** verebilirsiniz (ör. 1 adet ve üzeri, 10 adet ve üzeri). **Kaydet**.
7. Bir müşterinin hangi fiyatı göreceğini **Fiyat sorgula** (`/fiyat-listeleri/sorgula`) ekranında deneyin:
   müşteri, tarih ve ürün seçince birim fiyat ve fiyatın geldiği kaynak (Müşteriye özel liste / Bayi grubu listesi / Varsayılan liste) gösterilir.

### Bayi grupları (`/musteriler/gruplar`)
İşletme sahibi, yönetici ve operasyon müdürü.
1. **Müşteriler** sayfasında **Bayi grupları**'na, sonra **Yeni grup**'a basın.
2. **Grup adı**, **Fiyat listesi** (yalnızca satış listeleri) ve **Varsayılan iskonto (%)** girin. **Grubu ekle**.
3. Grubu arşivlemek gruptaki müşterilerin fiyatlarını değiştirebilir; onay penceresinde
   "Gruptaki müşterilerin fiyatlarının değişebileceğini anladım" kutusunu işaretlemeniz istenir.

### Müşteri (`/musteriler`)
İşletme sahibi, yönetici, operasyon müdürü ve satış.
1. **Yeni müşteri** düğmesine basın.
2. **Müşteri kodu** (boşluksuz, ör. C-001), **Firma / müşteri adı**, **Yetkili kişi**, **Vergi no / TCKN**
   (10 haneli VKN veya 11 haneli TCKN; TCKN listelerde maskeli görünür), **Vergi dairesi**, **Telefon**, **E-posta**.
3. **WhatsApp numaraları**: her satıra bir numara, ülke koduyla (ör. +905321234567); en fazla 5.
4. **Bayi grubu** ve **Müşteriye özel fiyat listesi** (yalnızca atama yetkisi olanlar), **Vade (gün)**, **Para birimi**, **Kredi limiti**.
5. **Aktif** (pasif müşteri yeni siparişte önerilmez) ve **İç not** (yalnızca ekibiniz görür). **Müşteriyi ekle**.
6. Kaydettikten sonra **adres** ekleyin: **Adres türü** (Teslimat / Fatura), **Adres adı**, adres satırları, **İlçe**, **İl**,
   **Posta kodu**, **Ülke kodu** (TR), **Teslim alacak kişi**, **Telefon**. Bir türün ilk adresi otomatik varsayılan olur.
7. Müşteri listesinde ad, kod, vergi no, telefon veya WhatsApp numarasıyla arayabilirsiniz.

## Sık hatalar ve mesajlar
| Mesaj | Ne yapmalı |
|---|---|
| Bu müşteri kodu zaten kullanılıyor. Farklı bir kod girin. | Kodu değiştirin. |
| Vergi numarası 10 (VKN) veya 11 (TCKN) haneli olmalı. | Haneyi kontrol edin. |
| WhatsApp numaralarını ülke koduyla girin, ör. +905321234567. | Başına +90 ekleyin. |
| En fazla 5 WhatsApp numarası girilebilir. | Fazla numarayı silin. |
| Bir müşteriye en fazla 50 adres eklenebilir. … | Kullanılmayan adresi silin. |
| Varsayılan adresin türü değiştirilemez. … | Önce başka adresi varsayılan yapın. |
| Arşivlenmiş müşteri değiştirilemez. Önce müşteriyi arşivden çıkarın. | **Arşivden çıkar**. |
| Bu adla bir bayi grubu zaten var. Farklı bir ad girin. | Grup adını değiştirin. |
| Bayi grubu ve fiyat listesi atama yetkiniz yok; … | Yöneticiye atatın. |
| Bu adla bir fiyat listesi zaten var. Farklı bir ad girin. | Liste adını değiştirin. |
| Varsayılan liste pasif yapılamaz veya arşivlenemez. … | Önce başka listeyi varsayılan yapın. |
| Taban liste başka bir para biriminde. … / Türetilmiş liste, taban listeyle aynı KDV modunu kullanmalı. | Aynı para birimi ve KDV modundaki listeyi seçin. |
| Taban liste tek seviyelidir: … | Türetilmiş bir listeden yeniden türetme yapılamaz. |
| Aynı ürün ve en az miktar bu listede iki kez var. | Tekrar eden satırı silin. |
| İşaretli satırları düzeltip tekrar kaydedin. Hiçbir satır kaydedilmedi. | Hatalı satırları düzeltin; liste ya hep birlikte kaydedilir ya hiç. |
| Arşivlenmiş fiyat listesi değiştirilemez. | Liste arşivlenmiştir. |

## İpuçları
- Fiyat listesi arşivleme **geri alınamaz**; o listeyi kullanan müşteriler bir sonraki listeye (grup veya varsayılan) düşer.
- Kredi limitini binlik ayırıcı olmadan yazın (ör. 250000 veya 250000,50).
- WhatsApp numarası doğru girilirse WhatsApp siparişinde müşteri bu numarayla bulunabilir.
