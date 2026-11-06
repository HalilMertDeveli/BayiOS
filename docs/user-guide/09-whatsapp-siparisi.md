# 9. WhatsApp siparişi

## Amaç
Müşterinin WhatsApp'tan yazdığı sipariş mesajını kopyalayıp yapıştırarak hızla **taslak sipariş** hazırlamak.

**Temel kurallar**
- BayiOS mesajdaki satırları **yalnızca sizin kataloğunuzla** eşleştirir. Fiyat, stok veya ürün kodu **uydurmaz**.
- Mesajda geçen fiyatlar kullanılmaz; fiyat, taslak oluşunca müşterinin fiyat listesinden gelir.
- **Her satırı bir kişi onaylamadan taslak oluşmaz.** Oluşan taslak da normal sipariş akışından geçer ve onayı yine bir kişi verir.
- BayiOS WhatsApp'a **mesaj göndermez**; mesajı siz kopyalayıp yapıştırırsınız.

**AI mı, kural tabanlı mı?** Mesaj iki yoldan biriyle okunur; ekranda hangisinin kullanıldığı yazar:
- **AI** — "Mesaj AI ile okundu ve yalnızca kataloğunuzla eşleştirildi. Her satırı kontrol edip onaylayın."
- **Kural tabanlı** — AI kapalıyken, günlük AI sınırı dolduğunda veya AI hizmeti yanıt vermediğinde kullanılır.
  Pilotta AI ayrıştırma **kapalıdır**; bu yüzden mesajlar kural tabanlı ayrıştırıcıyla okunur
  ("AI ayrıştırma bu ortamda kapalı. Mesaj kural tabanlı ayrıştırıcıyla okundu; satırları dikkatle kontrol edin.").

Kimler: işletme sahibi, yönetici, operasyon müdürü, satış. Bu özellik **AI WhatsApp asistanı** ek modülüne bağlıdır
(ücretsiz deneme süresince açıktır; bkz. [Paket, deneme süresi ve modüller](12-paket-deneme-moduller.md)).

## Adımlar

### 1. Mesajı yapıştırın (`/siparisler/whatsapp`)
1. **Siparişler** sayfasında **WhatsApp siparişi** düğmesine basın.
2. İsterseniz **Müşteri (isteğe bağlı)** seçin; inceleme ekranında da seçebilirsiniz. Müşteri bilgisi AI'ya gönderilmez.
3. **WhatsApp mesajı** kutusuna mesajı olduğu gibi yapıştırın (en fazla 4.000 karakter, 150 satır).
   Telefon numaraları ve e-posta adresleri ayrıştırmadan önce gizlenir.
4. **Mesajı ayrıştır** düğmesine basın.

Aynı sayfanın altında henüz onaylamadığınız ayrıştırmalar listelenir ("Bekleyen inceleme yok" yazıyorsa bekleyen yok).

![Mesaj girişi](../../apps/web/docs/screenshots/desktop-whatsapp-giris.png)

### 2. Satırları inceleyin (`/siparisler/whatsapp/{ayrıştırma}`)
Her satır kartında mesajdaki metin, okunan ürün ifadesi ve miktar görünür. Satır durumu üçten biridir:
- **Eşleşti** — önerilen ürün gösterilir. **"Satır N: önerilen ürünü onaylıyorum"** kutusunu işaretleyin
  ya da **Başka ürün seç**.
- **Belirsiz** — birden çok aday ürün listelenir; **hiçbiri önceden seçili değildir**. Doğru olanı siz seçin.
- **Eşleşmedi** — **katalogda ara** kutusuyla SKU, barkod veya ürün adını arayıp seçin.

Sonra:
- **Miktar**ı kontrol edin. Mesajda miktar yoksa "Mesajda miktar yok; siz girin." yazar. Mesajdaki birim
  ürünün birimiyle uyuşmuyorsa uyarı çıkar; miktarı ürünün birimine göre girin.
- Siparişe girmeyecek satır için **Satır N: siparişten çıkar** (geri almak için **Satırı geri al**).
- **Müşteri**, **Depo** ve **Sipariş notu**nu kontrol edin (mesaj metni siparişe kopyalanmaz).
- Üstteki **Uyarılar** bölümünü okuyun (ör. mesajda fiyat geçiyor, önceki siparişe atıf var, talimat benzeri ifade yok sayıldı).

![İnceleme ekranı](../../apps/web/docs/screenshots/desktop-whatsapp-inceleme.png)

### 3. Taslağı oluşturun
Tüm satırlar hazır olunca "Tüm satırlar hazır. …" yazar. **Taslak siparişi oluştur** düğmesine basın.
"Taslak sipariş oluşturuldu — Fiyatlar fiyat listesinden geldi. Siparişi kontrol edip onaya gönderin." mesajı çıkar.
Sipariş "WhatsApp (AI)" kaynağıyla kaydedilir ve **her zaman onay bekler** (sipariş onayı kapalı olsa bile).

Mesaj sipariş değilse **Ayrıştırmayı reddet** deyin; sipariş oluşmaz.

![Taslağa hazır](../../apps/web/docs/screenshots/desktop-whatsapp-hazir.png)

## Sık hatalar ve mesajlar
| Mesaj | Ne yapmalı |
|---|---|
| WhatsApp mesajını yapıştırın. | Kutu boş. |
| Mesaj en fazla … karakter / satır olabilir; mesajı bölerek gönderin. | Mesajı ikiye bölün. |
| Saatlik ayrıştırma sınırı doldu — İşletmeniz bu saat için WhatsApp mesajı ayrıştırma sınırına ulaştı. … sonra tekrar deneyin; mesajınız bu sayfada duruyor. | Belirtilen süre kadar bekleyin. |
| Adaylardan birini seçin veya satırı çıkarın. | Belirsiz satırda aday seçin. |
| Ürünü katalogda arayıp seçin veya satırı çıkarın. | Eşleşmeyen satırda ürün arayın. |
| Önerilen ürünü onaylayın veya başka bir ürün seçin. | Onay kutusunu işaretleyin. |
| Tüm satırlar çıkarıldı. En az bir satırı tutun veya ayrıştırmayı reddedin. | Bir satırı geri alın ya da reddedin. |
| Mesajda sipariş satırı bulunamadı | Ayrıştırmayı reddedin veya siparişi elle oluşturun. |
| Müşteri seçin. | Müşteri seçmeden taslak oluşmaz. |
| Taslak sipariş oluşturma yetkiniz yok. Ayrıştırmayı yetkili bir kullanıcıya iletin. | Sipariş oluşturma izni gerekir. |
| Bu ayrıştırma siz incelerken başka biri tarafından tamamlandı veya reddedildi. … | Güncel hâline bakın. |
| Mesaj metni saklama süresi dolduğu için silindi; … | Mesajı yeniden yapıştırın. |
| WhatsApp siparişi ayrıştırma kapalı | Paketinizde AI WhatsApp asistanı modülü yok; mevcut ayrıştırmaları yine inceleyip onaylayabilir veya reddedebilirsiniz. |

## İpuçları
- Mesajı düzenlemeden yapıştırın; ayrıştırıcı "3 adet", "2 koli" gibi ifadeleri kendisi okur.
- "Geçen seferki gibi" gibi ifadeler otomatik çözülmez; ürünü siz seçin.
- Belirsiz satırda en yüksek puanlı aday her zaman doğru değildir; SKU'yu kontrol edin.
