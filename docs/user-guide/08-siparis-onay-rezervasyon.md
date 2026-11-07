# 8. Sipariş oluşturma, onay ve stok ayırma

## Amaç
Müşteri siparişini kaydetmek, onaylamak ve onayda stoğu o sipariş için **ayırmak** (rezervasyon).
Stok depodan fiziksel olarak sevkiyat onayında düşer (bkz. [Toplama, paketleme ve sevkiyat](10-toplama-paketleme-sevkiyat.md)).

## Sipariş durumları
**Taslak → Onay Bekliyor → Onaylandı → Toplanıyor → Paketlendi → Sevk Edildi → Tamamlandı**, ya da **İptal**.
Sevkiyat durumu ayrıca gösterilir: Sevk yok / Kısmi sevk / Tamamı sevk edildi.

## Adımlar

### 1. Taslak oluşturma (`/siparisler/yeni`)
Sahip, yönetici, operasyon müdürü ve satış.
1. **Siparişler** sayfasında **Yeni sipariş** düğmesine basın.
2. **Müşteri** seçin (ad veya kodun bir kısmı; tam vergi no / telefon / WhatsApp). Yalnızca aktif müşteriler çıkar.
3. **Depo** (boşsa varsayılan depo), **Fiyatlarda KDV** (Fiyat listesine göre / KDV hariç / KDV dahil),
   **İstenen teslim tarihi**, **Not** ve gerekirse teslimat/fatura adresi seçin.
4. **Ürün ekle** kutusunda barkod, SKU veya ürün adı yazıp Enter'a basın. Aynı ürün ikinci kez eklenmez; miktarı satırdan değiştirin.
5. Satırda **miktar** girin. Yetkiniz varsa **manuel birim fiyat** (boş: fiyat listesinden) ve **iskonto** (Oran (%) / Tutar) girebilirsiniz.
6. **Taslak oluştur** (sonraki kayıtlarda **Taslağı kaydet**). Fiyatlar, iskontolar, KDV ve toplamlar kaydedince sunucuda hesaplanır;
   "Kaydedince fiyatlanır" yazan satırlar kayıttan sonra fiyat alır.

### 2. Onaya gönderme
Sipariş sayfasında **Onaya gönder**. Göndermek için en az bir satır gerekir ve **fiyatsız satır olmamalıdır**.
- İşletmenizde **sipariş onayı zorunluysa** sipariş "Onay Bekliyor" olur.
- **Onay zorunlu değilse** ve gönderen kişinin onay yetkisi varsa sipariş hemen onaylanır ve stok ayrılır
  ("Sipariş otomatik onaylandı; stok ayrıldı"). Diğer kullanıcıların ve WhatsApp'tan gelen siparişler yine onay bekler.

### 3. Onay ve stok ayırma
Onay yetkisi olan kişi sipariş sayfasında:
- **Onayla ve stok ayır** — tüm satırlar için stok **tek seferde** ayrılır (ya hepsi ya hiçbiri). Onayla birlikte depo için
  **toplama görevi** otomatik oluşur.
- **Reddet** — ret sebebi yazılır; sipariş taslağa döner, satış ekibi düzeltip yeniden gönderir.
- Gönderen kişi **Onaydan geri çek** ile siparişi taslağa alabilir.

### 4. İptal
**Siparişi iptal et** → **İptal sebebi** seçin (Müşteri vazgeçti, Stok yok, Fiyat anlaşmazlığı, Mükerrer sipariş, Diğer) ve
**Açıklama** yazın. İptal geri alınamaz; onaylı siparişte ayrılan stok serbest kalır. Kısmi sevk yapılmış sipariş iptal edilemez.

### Liste ve arama (`/siparisler`)
Sipariş numarasının bir kısmıyla (ör. 000123) arayın; **Durum**, **Kaynak** (Manuel, WhatsApp (AI), İçe aktarma, Mobil),
tarih ve müşteriye göre süzün. Sipariş sayfasında zaman çizelgesi, satırlar, KDV dökümü ve sevkiyatlar görünür.

## Sık hatalar ve mesajlar
| Mesaj | Ne yapmalı |
|---|---|
| Sipariş oluşturma yetkiniz yok | Rolünüz sipariş oluşturamaz. |
| Onaya göndermek için en az bir satır ekleyin. | Ürün ekleyin. |
| Fiyatı olmayan satırlar var. Fiyat listesine fiyat ekleyin veya yetkili bir kullanıcıdan manuel fiyat girmesini isteyin. | Fiyat listesini tamamlayın. |
| Önce taslaktaki değişiklikleri kaydedin. | **Taslağı kaydet**, sonra gönderin. |
| Stok yetersiz: siparişteki hiçbir satır için stok ayrılmadı (ya hep ya hiç). | Ekranda "Stoğu yetmeyen satırlar" listelenir. Mal kabul yapın veya siparişi reddedip miktarları düzenleyin. |
| Stok eksiye düşecek → **Yine de onayla** / **Yine de gönder ve onayla** | Negatif stok ayarı "Uyar" ise çıkar; onaylarsanız satılabilir stok eksiye düşer. |
| Bu müşteri aktif değil; pasif veya arşivlenmiş müşteriye sipariş verilemez. | Müşteriyi aktif yapın. |
| Bu ürün aktif değil; siparişe eklenemez. | Ürünü aktif yapın. |
| İskontoyu ya oran ya tutar olarak girin, ikisini birden değil. | Birini silin. |
| Bu adres seçili müşterinin teslimat adresi değil. … | Listeden adres seçin. |
| Bu işlem siparişin şu anki durumunda yapılamaz; … Güncel hâli yüklendi. | Sipariş başkası tarafından değiştirilmiş; yeniden bakın. |
| Yalnızca taslak siparişler düzenlenebilir; … | Onay sonrası satır değiştirilemez. |
| Kendi siparişlerinizi yalnızca Taslak veya Onay bekliyor durumundayken iptal edebilirsiniz; … | Satış personeli: yöneticinize başvurun. |
| Sipariş şu an başka bir işlem tarafından kullanılıyor. … | Birkaç saniye bekleyip tekrar deneyin. |

## İpuçları
- Sipariş numaraları **Ayarlar**'daki önekle oluşur (ör. SIP-2026-000123).
- Depoda eksik bildirilirse sipariş sayfasında "Depoda eksik bildirildi" uyarısı görünür.
- Kısmen sevk edilen siparişte kalan miktar gönderilmeyecekse sipariş sayfasında **Kalanı kapat**'a basın ve bir sebep
  seçin; kalan rezervasyon serbest bırakılır (sipariş onaylama yetkisi gerekir). Sevkiyat tamamlanan siparişi teslimattan
  sonra **Tamamla** ile kapatın.
