# Bilinen sınırlamalar

Pilot sürümünde (Kasım 2026) **henüz olmayan** veya **kısıtlı** özellikler. Bu liste değiştikçe güncellenir.

## Henüz olmayan özellikler
| Özellik | Bugünkü durum / geçici çözüm |
|---|---|
| **Telefon uygulaması (Android/iOS)** | Yok. Depo işleri telefonun internet tarayıcısından yapılır. |
| **Çalışan davet etme, rol değiştirme, işletme profili** | Ekran yok ("Ekip, roller ve işletme profili ayarları henüz geliştiriliyor."). Pilotta kullanıcıları BayiOS ekibi tanımlar. |
| **Excel/CSV toplu aktarma ekranı** | Menüdeki **İçe aktarma** sayfası "henüz geliştiriliyor" gösterir. Sunucu tarafı hazırdır; dosyanızı BayiOS ekibine iletin. Bkz. [Toplu aktarma](04-toplu-urun-aktarma.md). |
| **Pano (özet ekran)** | Yalnızca "Sistem durumu" görünür; bekleyen onay ve görev özeti yok. |
| **Marka, kategori ve birim düzenleme ekranı** | Yok. Kategori ve birimler sektör şablonundan gelir. (Ürün formundaki "Önce Ayarlar'dan birim ekleyin." ipucunun karşılığı olan sayfa henüz yoktur.) |
| **Siparişi "Tamamlandı" yapma** | Ekranda düğme yok; sevk edilen sipariş "Sevk Edildi"de kalır. |
| **Kısmi sevkten sonra kalanı kapatma** | Ekranda düğme yok; kalan miktar için rezervasyon açık kalır. Yöneticinize/BayiOS ekibine bildirin. |
| **Sayım ve depolar arası transfer** | Ekran yok. Sayım farkını [Stok düzeltme](06-stok-duzeltme-negatif-stok.md) ile kaydedin. |
| **Ayarlanabilir rol kuralları** (satış personeli onayı, iskonto sınırı, alış fiyatı görünürlüğü, depo personeli barkod atama/mal kabul onayı, 2FA) | Ekranda yok; varsayılan değerlerle çalışır. |
| **Seri numarası / garanti takibi** | Yok. LED şablonundaki "Seri no takibi" yalnızca Evet/Hayır bilgisidir. |
| **Ödeme, fatura, paket değişikliği** | Yok. Her şey ücretsiz denemedir; kart bilgisi istenmez. |
| **Bayi portalı** (bayilerin kendi siparişini girmesi) | Yok ("Yakında"). |
| **Resmî WhatsApp entegrasyonu** | Yok. BayiOS WhatsApp'a mesaj göndermez/almaz; mesajı siz kopyalayıp yapıştırırsınız. |
| **AI ile WhatsApp ayrıştırma (canlı)** | Pilotta kapalı. Mesajlar kural tabanlı ayrıştırıcıyla okunur; her satırı siz onaylarsınız. AI, ürün sahibi onayından sonra açılacaktır. |
| **ERP entegrasyonu, gelişmiş analitik** | Yok ("Yakında"). |
| **Önizleme/yayın adresi** | Pilotun kullanacağı internet adresi barındırma onayından sonra paylaşılacaktır. |

## Ürün sahibinin vereceği açık kararlar
Aşağıdaki konular karar bekliyor; karar verildiğinde ilgili kılavuz sayfaları güncellenecektir.

- Anthropic API anahtarı ve AI model seçimi (canlı AI ayrıştırma için); işletmeler için AI'nın varsayılan açık/kapalı olması.
- Barındırma (API sunucusu), veritabanı önizleme projesi ve web yayını onayları.
- Paket sınırları (depo, kullanıcı, AI ayrıştırma sayıları şu an geçicidir) ve deneme süresi.
- Temel pakette barkodlu toplama doğrulamasının olup olmayacağı.
- KVKK aydınlatma metni ve müşteri anonimleştirme yetkisinin kimde olacağı.
- Toplu aktarma limitleri.
- Mal kabul notu, sipariş onayında fiyatın yeniden hesaplanması, toplama sırasında raf değişimi kuralları.
- Marka rengi ve repo lisansı.

Kaynak: proje durum belgesi (`STATUS.md`, "Blokerler") — ayrıntılar GitHub #61'de.
