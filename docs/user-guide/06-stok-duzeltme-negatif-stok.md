# 6. Stok düzeltme ve negatif stok politikası

## Amaç
Sayım farkı, hasar/fire veya iade gibi durumlarda stoğu **sebebiyle birlikte** düzeltmek ve stok yetmediğinde
ne olacağını belirlemek.

## Adımlar

### Stok düzeltme (`/stok/duzeltme`)
Yalnızca işletme sahibi, yönetici ve operasyon müdürü.
1. **Stok** sayfasında **Stok düzelt** düğmesine basın.
2. **Ürün (barkod veya SKU)** kutusunda ürünü okutun ya da yazıp Enter'a basın. Yanlışsa **Ürünü değiştir**.
3. **Depo** ve (raf takibi açıksa) **Raf/göz** seçin. Seçilen yerin güncel stoğu gösterilir.
4. **Düzeltme türü** seçin:
   - Stok ekle (düzeltme girişi)
   - Stok düş (düzeltme çıkışı)
   - Hasar / fire çıkışı
   - İade girişi
5. **Miktar**ı pozitif girin; yönü düzeltme türü belirler.
6. **Sebep** yazın (zorunlu, en fazla 500 karakter; ör. "Sayım farkı, raf A-01"). Hareket geçmişinde görünür.
7. **Düzeltmeyi kaydet**. "Stok düzeltildi" mesajı çıkar; **Hareket geçmişini gör** ile kontrol edebilirsiniz.

### Negatif stok politikası
**Ayarlar → Sipariş ayarları → Stok yetmediğinde** alanından seçilir (işletme kurulumunda da sorulur).
Sipariş onayında ve stok çıkışında satılabilir stok eksiye düşecekse uygulanır:

| Seçenek | Ne olur |
|---|---|
| **İzin verme (önerilen)** | İşlem yapılmaz; "Stok yetersiz" mesajı çıkar. |
| **Uyar, devam etmeye izin ver** | "Stok eksiye düşecek" penceresi açılır; ürün bazında istenen ve satılabilir miktar listelenir. **Yine de kaydet** derseniz işlem yapılır ve stok eksiye düşer. **Vazgeç** derseniz hiçbir şey kaydedilmez. |
| **İzin ver** | İşlem sorulmadan yapılır. |

**Ayrılmış (rezerve) stok düzeltmeyle düşülemez.** Onaylı siparişlere ayrılan miktar, hangi politika seçilirse seçilsin
düzeltme çıkışıyla azaltılamaz.

### Minimum stok (kritik stok eşiği)
Bir ürünün **satılabilir** miktarı (eldeki − ayrılmış) minimum stoğa **eşit veya altına** inince ürün "kritik" sayılır:
**Stok** listesinde **Kritik** rozeti çıkar, panodaki **Kritik stok** kutusunda sayılır ve **Kritik stok** raporunda
listelenir. Minimum stok girmek stoğu değiştirmez (hareket oluşmaz). Yalnızca işletme sahibi, yönetici ve operasyon müdürü
girebilir; diğer roller değerleri görür.
1. **Genel minimum:** **Ürünler** → ürünü açın → **Minimum stok** bölümünde her varyant için değer girin ve
   **Minimum stoku kaydet**e basın. Boş bırakılan varyant takip edilmez; değeri silip kaydederseniz eşik kaldırılır.
   "Adet" gibi kesirsiz birimlerde tam sayı girin.
2. **Depoya özel eşik:** **Stok** → ürünün SKU'suna tıklayın → **Minimum stok** bölümünde depo satırına değer girin.
   O depoda genel eşiğin yerine geçer; boş bırakılırsa o depoda genel eşik geçerlidir.
3. **Hangi eşik geçerli?** Tek bir depo seçiliyken: depo eşiği, yoksa genel eşik. **Tüm depolar** görünümünde (pano ve
   depo seçilmemiş rapor): genel eşik, yoksa depo eşiklerinin toplamı; satılabilir miktar da tüm depoların toplamıdır.
4. Kritik stok raporu varsayılan olarak bu eşikleri kullanır. **Eşiği olmayan, stoğu tükenmiş ürünleri de göster** ile
   eşik girilmemiş ama satılabilir miktarı 0 veya altında olan ürünler de eklenir. **Kritik eşik** kutusuna bir sayı
   yazarsanız tüm aktif ürünler bu sabit eşiğe göre listelenir (eski davranış).

## Sık hatalar ve mesajlar
| Mesaj | Ne yapmalı |
|---|---|
| Stok düzeltme yetkiniz yok | Rolünüz düzeltmeye izin vermiyor. |
| Düzeltme sebebini yazın (en fazla 500 karakter). | Sebep alanını doldurun. |
| Satılabilir stok yetersiz. Siparişler için ayrılmış (rezerve) stok düzeltmeyle düşülemez. | Daha az miktar girin veya ilgili siparişi iptal edin. |
| Stok yetersiz: işlem negatif stok kuralına takıldı. | Politika "İzin verme"; önce mal kabul yapın. |
| Düzeltme kaydedilmedi: eksi stok onaylanmadı. | "Stok eksiye düşecek" penceresinde Vazgeç dediniz. |
| Bu depoda/rafta henüz stok kaydı yok. | Bilgi mesajıdır; giriş türünde düzeltme yapabilirsiniz. |
| Stok kayıtları meşgul, otomatik olarak yeniden deneniyor… | Bekleyin. |
| Bu birim kesirli olamaz; 0 veya daha büyük bir tam sayı girin. | Minimum stoğu tam sayı girin (ör. Adet). |
| Henüz minimum stok tanımlanmadı | Pano/rapor bilgisi: ürünlere minimum stok girin. |

## İpuçları
- Sayım farklarını düzenli düzeltin ve sebebi açık yazın; raporlarda "Düzeltme (giriş/çıkış)" ve "Hasar / fire" ayrı görünür.
- Depo veya raf sayımı için **Sayımlar** ekranını kullanın; farklar onayla "Sayım düzeltmesi" olarak işlenir. Bkz. [Transfer, raf taşıma ve sayım](15-transfer-sayim-raf-tasima.md).
