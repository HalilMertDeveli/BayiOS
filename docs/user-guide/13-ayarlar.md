# 13. Ayarlar: sipariş onayı, numara öneki, KDV

## Amaç
İşletmenizin sipariş kurallarını belirlemek. Yalnızca **işletme sahibi** ve **yönetici** değiştirebilir;
diğer roller "Ayarları değiştirme yetkiniz yok" mesajını görür.

## Adımlar (`/ayarlar`)
**Sipariş ayarları** bölümünde:

### Onay ve numaralandırma
- **Siparişler onaydan geçsin**
  - Açık: gönderilen siparişler "Onay bekliyor" durumuna geçer; stok onayda ayrılır.
  - Kapalı: onay yetkisi olan bir kullanıcının gönderdiği sipariş hemen onaylanır ve stok ayrılır.
    Diğer kullanıcıların ve WhatsApp'tan gelen siparişler yine onay bekler.
- **Sipariş numarası öneki** — 1–10 karakter, A–Z ve 0–9, harfle başlar (ör. SIP). Yeni siparişler
  `SIP-2026-000123` biçiminde numaralanır; mevcut numaralar değişmez.

### Stok ve KDV
- **Stok eksiye düşmez** — sabit kural, değiştirilemez. Onay, stok çıkışı ve sevkiyat satılabilir stoğun altına inemez. Ayrıntı: [Stok düzeltme ve negatif stok](06-stok-duzeltme-negatif-stok.md).
- **Varsayılan KDV oranı** — yeni ürünlerde önerilir; ürün kartından değiştirilebilir.
- **Fiyat gösterimi** — ekranlarda fiyatların KDV dahil mi hariç mi gösterileceği.

Değişiklik yapınca "Kaydedilmemiş değişiklikler var." yazar. **Ayarları kaydet** ile kaydedin
ya da **Değişiklikleri geri al** ile vazgeçin. Başarılıysa "Sipariş ayarları kaydedildi" bildirimi çıkar.

### Diğer ayarlar
- **Özel alanlar** — bkz. [Ürünler ve özel alanlar](03-urunler-ve-ozel-alanlar.md).
- **Paket ve modüller** (sahip ve yönetici) — bkz. [Paket, deneme süresi ve modüller](12-paket-deneme-moduller.md).
- **Ekip** — çalışan daveti, rol değiştirme, askıya alma, sahiplik devri; bkz. [Roller ve izinler](02-roller-ve-izinler.md).
- **İş kuralları** (sahip ve yönetici) — satış personeli onayı ve iskonto sınırı, alış fiyatı görünürlüğü, depo personeli
  barkod atama / mal kabul onayı; "Stok eksiye düşmez" sabit kural olarak gösterilir (değiştirilemez, OD-09). İki adımlı doğrulama seçeneği "Yakında" olarak görünür ve
  henüz uygulanmaz.
- **Marka, kategori ve birimler** — bkz. [Ürünler ve özel alanlar](03-urunler-ve-ozel-alanlar.md).
- İşletme profili (logo, vergi no, adres, telefon) ayarları **henüz geliştiriliyor**.

## Sık hatalar ve mesajlar
| Mesaj | Ne yapmalı |
|---|---|
| Sipariş numarası önekini girin. | Önek boş bırakılamaz. |
| Önek bir harfle (A–Z) başlamalı. | İlk karakter harf olmalı. |
| Yalnızca İngilizce büyük harf (A–Z) ve rakam (0–9) kullanın; boşluk, tire ve Türkçe karakter olmaz. | Ç, Ş, İ gibi harfleri ve tireyi kaldırın. |
| 0 ile 100 arasında, en fazla 2 ondalıklı bir KDV oranı girin. | Oranı düzeltin. |
| Ayarlar başkası tarafından değiştirildi | **Güncel ayarları yükle** deyip değişikliklerinizi yeniden yapın. |
| Ayarlar yüklenemedi | **Tekrar dene**. |

## İpuçları
- Öneki yıl başında değiştirmek numaralandırmayı daha anlaşılır yapar; numaralar önek ve yıl bazında sıralanır.
- Satış personelinin onay yetkisi, iskonto sınırı, alış fiyatı görünürlüğü, depo personelinin barkod atama/mal kabul onayı
  gibi kurallar bu sayfada değil, **Ayarlar → İş kuralları** sayfasındadır.
