# Bilinen sınırlamalar

Pilot sürümünde (Kasım 2026) **henüz olmayan** veya **kısıtlı** özellikler. Bu liste değiştikçe güncellenir.
Son kontrol: 2026-11-07, `preview` `ba83e85` (release candidate raporu: `docs/operations/release-candidate.md`).

## Henüz olmayan özellikler
| Özellik | Bugünkü durum / geçici çözüm |
|---|---|
| **Telefon uygulaması (Android/iOS)** | Yok. Depo işleri telefonun internet tarayıcısından yapılır. |
| **İşletme profili ekranı (logo, vergi no, adres, telefon)** | Ekran yok (Ayarlar'da "İşletme profili ayarları henüz geliştiriliyor." yazar). Logo yüklenemez. Gerekirse BayiOS ekibine bildirin. |
| **Davet e-postası** | BayiOS davet e-postası göndermez. **Ayarlar → Ekip**'te oluşturulan davet bağlantısını davet edilen kişiye siz iletirsiniz. Daveti kabul etmek için kişinin e-posta adresini doğrulamış olması gerekir. Bkz. [Roller ve izinler](02-roller-ve-izinler.md). |
| **Stok ve müşteri toplu aktarma** | **İçe aktarma** ekranında yalnızca *Ürünler* ve *Fiyat listesi kalemleri* vardır. Açılış stoğunu [Mal kabul](05-depo-raf-mal-kabul.md) ile girin. Bkz. [Toplu aktarma](04-toplu-urun-aktarma.md). |
| **AI ile kolon önerisi (içe aktarma)** | Kolon önerileri başlık adlarına ve kayıtlı şablonlara göre yapılır; AI kullanılmaz. Öneriler kendiliğinden uygulanmaz, siz onaylarsınız. |
| **Minimum stoğu dosyadan aktarma** | Yok. Minimum stok ürün düzenleme ekranından (depoya özel eşik için ürünün stok sayfasından) girilir. Bkz. [Stok düzeltme](06-stok-duzeltme-negatif-stok.md#minimum-stok-kritik-stok-eşiği). |
| **Kritik stok bildirimi** | Bildirim (e-posta/telefon) gitmez. Kritik ürünler panodaki **Kritik stok** kutusunda, **Stok** listesindeki **Kritik** rozetinde ve kritik stok raporunda görünür. |
| **Transfer ve sayım için ayrı telefon uygulaması** | Transfer teslim alma ve sayım telefonun tarayıcısından yapılır ([kılavuz](15-transfer-sayim-raf-tasima.md)); kamerayla barkod okuma yoktur, el terminali veya klavye-okuyucu kullanın. |
| **İki adımlı doğrulama (2FA)** | Yok. **Ayarlar → İş kuralları**'ndaki seçenek "Yakında · henüz uygulanmıyor" olarak görünür; işaretlemek giriş akışını değiştirmez. |
| **Üyeliği askıya alınan kişinin oturumu** | Askıya alınan kişi bir sonraki işleminde işletmeye erişemez; ancak oturumu kapatılmaz (başka işletmelerine erişmeye devam eder). |
| **Ürün görseli, tedarikçi kartı** | Ürüne görsel eklenemez. Tedarikçi bilgisi yalnızca mal kabulde **Referans (irsaliye / fatura no)** alanına yazılabilir; ürün kartında tedarikçi alanı yoktur. |
| **Seri numarası / garanti takibi** | Yok. "Teknik özellikli ürünler" örnek şablonundaki "Seri no takibi" yalnızca Evet/Hayır bilgisidir. Kapsam ürün sahibi kararı bekliyor. |
| **Ekrandan demo verisi yükleme** | Yok. Pilot öncesi demo işletmeler BayiOS ekibi tarafından hazırlanır. |
| **Ödeme, fatura, paket değişikliği** | Yok. Her şey ücretsiz denemedir; kart bilgisi istenmez. |
| **Bayi portalı** (bayilerin kendi siparişini girmesi) | Yok ("Yakında"). |
| **Resmî WhatsApp entegrasyonu** | Yok. BayiOS WhatsApp'a mesaj göndermez/almaz; mesajı siz kopyalayıp yapıştırırsınız. |
| **AI ile WhatsApp ayrıştırma (canlı)** | Pilotta kapalı. Mesajlar kural tabanlı ayrıştırıcıyla okunur; her satırı siz onaylarsınız. AI, ürün sahibi onayından sonra açılacaktır. |
| **ERP / e-Fatura / kargo / pazar yeri entegrasyonları, gelişmiş analitik** | Yok ("Yakında"). |
| **Önizleme/yayın adresi** | Pilotun kullanacağı internet adresi barındırma onayından sonra paylaşılacaktır. |

Pilot hazırlığında eklenenler (artık sınırlama değil): Ekip ekranı (davet, rol değiştirme, askıya alma, sahiplik devri),
İş kuralları ekranı, marka/kategori/birim ekranları, rol bazlı pano, siparişte **Tamamla** ve **Kalanı kapat**,
içe aktarma sihirbazı ve geçmişi, ürün/depo bazında minimum stok ve eşiğe göre kritik stok.

## Ürün sahibinin vereceği açık kararlar
Aşağıdaki konular karar bekliyor; karar verildiğinde ilgili kılavuz sayfaları güncellenecektir.

- Anthropic API anahtarı ve AI model seçimi (canlı AI ayrıştırma için); işletmeler için AI'nın varsayılan açık/kapalı olması.
- Barındırma (API sunucusu), veritabanı önizleme projesi ve web yayını onayları (GitHub #138).
- KVKK aydınlatma metni, kullanım koşulları ve müşteri anonimleştirme yetkisinin kimde olacağı (GitHub #139).
- Paket sınırları (depo, kullanıcı, AI ayrıştırma sayıları şu an geçicidir) ve deneme süresi.
- Temel pakette barkodlu toplama doğrulamasının olup olmayacağı.
- Toplu aktarma limitleri.
- Mal kabul notu, sipariş onayında fiyatın yeniden hesaplanması (GitHub #98). Toplama sırasında raf değişimi kuralları
  2026-11-09'da karara bağlandı (bkz. [Toplama, paketleme ve sevkiyat](10-toplama-paketleme-sevkiyat.md)).
- Seri numarası takibinin kapsamı (GitHub #57, #147).

Kaynak: proje durum belgesi (`STATUS.md`, "Blokerler") — ayrıntılar GitHub #61'de.
