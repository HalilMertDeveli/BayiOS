# 15. Depolar arası transfer, raf taşıma ve stok sayımı

## Amaç
Malı bir depodan diğerine yolda takibiyle göndermek, aynı depoda raf değiştirmek ve depoyu sayıp farkı
stoğa işlemek.

## Kimler ne yapabilir?

| İşlem | İşletme sahibi, yönetici, operasyon müdürü | Depo personeli |
|---|---|---|
| Transfer oluşturma, gönderme, farkı kapatma, iptal | ✓ | – |
| Transferi teslim alma | ✓ | ✓ |
| Raf taşıma | ✓ | ✓ |
| Sayım başlatma, yeniden saydırma, onaylama, iptal | ✓ | – |
| Sayım girme ve sayımı bitirme | ✓ | ✓ |

Satış ve izleyici rollerinde **Transferler** ve **Sayımlar** menüde görünmez.

## Adımlar

### Depolar arası transfer (`/depo/transferler`)
Transfer iki aşamalıdır: gönderilen mal **teslim alınana kadar yoldadır** ve bu sürede hiçbir depoda satılabilir
görünmez.

1. **Transferler → Yeni transfer**. **Kaynak depo** ve **Hedef depo** seçin (aynı depo olamaz).
2. Ürünleri barkod veya SKU ile okutun. Her satırda kaynakta **satılabilir** miktar yazar; daha fazlasını
   giremezsiniz. Raf takipli kaynakta **kaynak rafı**, raf takipli hedefte isterseniz **planlanan hedef rafı** seçin.
3. **Taslağı kaydet**. Taslak stoğu değiştirmez; yanlışsa **Taslağı iptal et** (sebep zorunlu).
4. **Gönder** → onaylayın. Durum **Yolda** olur; miktar kaynak depodan düşer. Siparişlere ayrılmış stok gönderilmez.
5. Hedef depoda **Teslim al** bölümünde gelen miktarı girin (yolda olan miktar hazır yazılıdır) ve hedef rafı seçin.
   Eksik geldiyse yalnızca geleni yazın; kalan yolda kalır ve durum **Kısmen teslim alındı** olur.
   **Okutarak teslim al** ile ürün barkodu + raf kodu okutarak da teslim alabilirsiniz.
6. Kalan hiç gelmeyecekse **Farkı kapat**: **Kayıp olarak düş** veya **Kaynak depoya iade et**, sebep yazın, onaylayın.
   Durum **Farkla kapatıldı** olur.

**Fark raporu** her satırda gönderilen, teslim alınan, yolda, iade ve kayıp miktarları gösterir. Listede
**Fark var** rozeti eksik teslimi, **Gecikmiş** rozeti 7 günden uzun yolda kalan transferi işaretler.
Stok hareket geçmişinde yoldaki miktar "Yolda (Transfer TRF-…)" olarak görünür.

### Raf taşıma (`/stok/raf-tasima`)
Aynı depoda bir raftan diğerine anında taşıma (yolda aşaması yoktur).
1. **Stok** sayfasında **Raf taşı** düğmesine veya bir satırdaki **Taşı** bağlantısına basın.
2. Depo, ürün, **Kaynak raf**, **Hedef raf** ve **Miktar** girin; **Taşı**.

Varsayılan olarak yalnızca satılabilir miktar taşınır; siparişlere ayrılmış miktar rafta kalır. Rafı tümüyle boşaltmak
için **Ayrılmış (rezerve) ürünleri de taşı** kutusunu işaretleyin: satılabilir stok yetmezse kalan, siparişlere ayrılmış
ve hâlâ rafta duran üründen alınır; o siparişlerin ayırması ve açık toplama görevleri hedef rafa yönlendirilir. Sonuçta
"Bunun N birimi M siparişe ayrılmıştı …" yazar. Toplanmış, paketlenmiş veya eksik bildirilmiş ürün taşınmaz.
Raf taşımayı depo personeli de yapabilir.

### Stok sayımı (`/depo/sayimlar`)
1. **Sayımlar → Yeni sayım**. Depoyu ve kapsamı seçin: **Tüm depo**, **Seçili raflar** (raf takipli depolarda) veya
   **Seçili ürünler**. **Kör sayım** işaretlenirse sayım yapan personel sistemdeki miktarı görmez.
2. **Sayımı başlat**: kapsamdaki stok o anki hâliyle kaydedilir (anlık görüntü). Bir depoda aynı anda tek açık sayım olur.
3. Sayım yapan kişi **Okutarak say** bölümünde raf kodunu, sonra ürünü okutur:
   - **Ekle**: her okutma yazılan miktar kadar ekler (miktar 1 iken her okutma 1 adet).
   - **Yaz**: satırın miktarını yazılan değerle değiştirir (0 = rafta yok).
   Satırlarda da miktar yazıp **Girilenleri kaydet** diyebilirsiniz. Kapsamda olup listede olmayan bir ürün
   okutulursa yeni satır olarak eklenir.
4. **Sayımı bitir**: sayım yönetici onayına gider (sayılmayan satırlar stoğu değiştirmez).
5. Yönetici **Fark raporu**'nda sistem, sayılan ve fark sütunlarını görür. Şüpheli satırları seçip
   **Seçilenleri yeniden saydır** diyebilir.
6. **Onayla ve stoğa işle**: her fark için **Sayım düzeltmesi** hareketi yazılır.

## Sık hatalar ve mesajlar
| Mesaj | Ne yapmalı |
|---|---|
| "Kaynakta satılabilir yalnızca …" / "Kaynakta satılabilir stok yetersiz" | Miktarı azaltın; ayrılmış stok gönderilemez. Gönderim anında stok değiştiyse ürünler listelenir. |
| "Yolda olandan fazlası teslim alınamaz" | Miktarı kontrol edin. Fazla gelen ürünü ayrıca **Mal kabul** ile girin. |
| "Okutulan ürün bu transferde yok" | Ürünü ayırın; transferdeki ürünleri okutun. |
| "Bu raf kodu hedef depoda yok" / "Bu raf kodu bu depoda yok" | Raf etiketini kontrol edin veya rafı listeden seçin. |
| "Bu depoda zaten açık bir sayım var" | Açık sayımı bitirip onaylayın veya iptal edin. |
| "Bazı satırların stoğu sayımdan sonra değişti" | Sayım sırasında satış/mal kabul olmuş. Satırları kontrol edin; düzeltme sayım anındaki bakiyeye göre hesaplanır. Uygunsa **Yine de uygula**. |
| "Sayılan miktar … ayrılmış (rezerve) miktarın altında kalıyor" | Önce ilgili siparişlerin ayrılmış miktarını azaltın, sonra tekrar onaylayın. |
| "Henüz hiçbir satır sayılmadı" | En az bir satırı sayın. |
| "Bu rafta satılabilir yalnızca …" (raf taşıma) | Miktarı azaltın veya **Ayrılmış (rezerve) ürünleri de taşı**'yı işaretleyin. |
| "Kaynak rafta taşınabilecek stok yetersiz (satılabilir + rafta duran ayrılmış ürün)" | Toplanmış, paketlenmiş veya eksik bildirilmiş ürün taşınmaz; miktarı listelenen değere indirin. |
| "Ayrılmış ürünün bir kısmı artık rafta değil …" | Bu arada toplama yapılmış; miktarı azaltıp tekrar deneyin. |

## İpuçları
- Telefonda tarayıcıdan sayım yapabilirsiniz; okuyucu Enter gönderdiğinde okutma kaydedilir.
- Büyük depolarda sayımı raflara bölün (bir sayım en fazla 5.000 satır).
- Kör sayım, sayanın sistem miktarına bakarak yazmasını önler; farkı yönetici inceler.
