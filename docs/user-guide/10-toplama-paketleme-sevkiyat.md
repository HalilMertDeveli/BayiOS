# 10. Toplama, paketleme ve sevkiyat (telefon tarayıcısından)

## Amaç
Onaylanan siparişin ürünlerini raftan doğru toplamak, kolilere kontrol ederek koymak ve sevk ederek stoktan düşmek.
Bu ekranlar telefonun internet tarayıcısında rahat kullanılacak şekilde tasarlanmıştır. Barkod okuyucu bağlıysa
okutma kutusuna okutmanız yeterlidir; yoksa kodu yazıp Enter'a basın.

**Akış:** Sipariş onaylanır → toplama görevi otomatik oluşur (ayar kapalıysa yönetici siparişten oluşturur) → toplanır →
taslak sevkiyat oluşur → (isteğe bağlı) paket kontrolü → **Sevkiyatı onayla** → stok düşer, sipariş "Sevk Edildi" olur.

### Görev otomatik oluşmuyorsa
**Ayarlar → Sipariş ayarları → Depo** bölümünde **Sipariş onaylanınca toplama görevi otomatik oluşturulsun** kapalıysa
onay stoğu yine ayırır ama görev oluşmaz. Görev atama yetkisi olan kişi (sahip, yönetici, operasyon müdürü) onaylı
siparişin sayfasındaki **Toplama görevi** bölümünde **Öncelik** seçer ve **Toplama görevi oluştur**'a basar. Görev depo
havuzuna düşer; bölümde öncelik, durum ve **Görevi aç** bağlantısı görünür.

## Adımlar

### 1. Görev listesi (`/depo/gorevler`)
Depo personeli giriş yapınca bu ekran açılır. Sekmeler: **Görevlerim**, **Havuz**, (yöneticiler için) **Tüm görevler**.
**Durum**: Aktif (bekleyen + toplanan) / Biten (tamamlanan + iptal) / Tümü. **Öncelik**: Tüm öncelikler / Acil / Yüksek /
Normal / Düşük (Tüm görevler sekmesinde).
Her görevde sipariş numarası, **müşteri adı**, varsa **İstenen teslim** tarihi ve **Öncelik: Acil / Yüksek / Normal / Düşük**
rozeti yazar. Telefonda (mobil iş listesinde) görevler önceliğe göre sıralanır; Acil en üsttedir.
- **Üstlen** — havuzdaki görevi kendinize alırsınız. İki kişi aynı anda üstlenirse yalnızca biri alır.
- **Bana ata**, **Personele ata** / **Başkasına ata**, **Havuza bırak** — görev atama yetkisi olanlar (sahip, yönetici, operasyon müdürü).
  **Kime** listesinde yalnızca toplama yetkisi olan personel çıkar.
- **Önceliği değiştir** — görev atama yetkisi olanlar; açılan pencerede yeni önceliği seçip **Kaydet**.
- **Toplamaya başla** / **Toplamaya devam et** — görev ekranını açar.

### 2. Toplama (`/depo/gorevler/{görev}`)
1. Görev size atanmadıysa **Üstlen ve başla**.
2. Satırlar **raf sırasıyla** listelenir ("Toplama listesi (raf sırası)"); üstte **Şu anki satır** görünür.
3. Raf takibi olan depoda önce **Raf barkodu**nu, sonra **Ürün barkodu veya SKU**'yu okutun. Raf takibi yoksa doğrudan ürünü okutun.
   Aynı ürünü tekrar okutmak 1 adet ekler; çok adetli ürünlerde **Okutma başına adet**i artırabilirsiniz.
   Başka rafa geçmek için **Rafı değiştir**, başka satıra geçmek için **Bu satıra geç**.
4. Ürün rafta yoksa, hasarlıysa veya yetmiyorsa **Eksik bildir** → **Sebep** (Rafta bulunamadı / Hasarlı / Raftaki miktar yetersiz) ve
   **Eksik miktar** girin. Sipariş işaretlenir, yönetici bilgilendirilir.
5. Tüm satırlar toplanınca veya eksik bildirilince **Görevi tamamla**. Eksik varsa "Eksikle tamamlansın mı?" sorusuna **Eksikle tamamla** deyin.
6. "Toplama tamamlandı — Toplananlardan taslak sevkiyat oluşturuldu." mesajı çıkar. **Sevkiyata git**.

**Raf değişirse:** Bir raf pasife alınırsa o raftaki açık satırlar sistemce boş stoğu olan aktif raflara taşınır. Taşınamayan
satırda **Raf pasif** uyarısı görünür. Ürünü başka bir rafta bulursanız önce o rafı, sonra ürünü okutun: sistem ayırmayı o
rafa taşır, liste güncellenir ("… (başka raftan)" mesajı). Bulamazsanız **Eksik bildir**. Liste her zaman sistemin
verdiği yürüme sırasıyla gösterilir.

**Yönetici işlemleri** (görev ekranında, görev atama yetkisi olanlar):
- **Öncelik** seçip **Önceliği kaydet**.
- **Yeniden dağıt** — **Yeniden dağıtılacak raf** listesinden "Pasif raflardaki tüm açık miktar"ı veya tek bir rafı seçin.
  Açık miktar, ayrılmış stoğuyla birlikte boş stoğu olan aktif raflara taşınır. Sonuçta kaç birimin taşındığı ve kaçının
  taşınamadığı (başka rafta yeterli satılabilir stok yok) yazar. Toplanan ve eksik bildirilen miktar yerinde kalır.

### 3. Paket kontrolü (isteğe bağlı) (`/depo/sevkiyatlar/{sevkiyat}`)
1. **Paket kontrolü (isteğe bağlı)** bölümünde her ürünü koliye koyarken okutun (her okutma 1 adet). Yeni koli için **Yeni koli**.
2. Satırlar "Tamam" ya da "Uyuşmuyor" olarak işaretlenir. Yanlış okutmayı satırdaki geri al düğmesiyle düzeltin.
3. Eksik ürün varsa önce satırda **Eksik bildir**; sonra **Paket kontrolünü kaydet (N koli)**.

### 4. Sevk onayı
1. **Sevkiyatı onayla** bölümünde isterseniz **Kargo firması**, **Takip numarası**, **Koli sayısı** girin.
2. **Sevkiyatı onayla** → "… sevk edilsin mi?" penceresinde **Sevk et**.
3. Ayrılan stok depodan **bir kez** düşer, sipariş "Sevk Edildi" (veya kısmi sevk) olur. **Bu işlem geri alınamaz.**
   Sayfanın altında **Stok hareketleri** görünür.

### Sevkiyat listesi (`/depo/sevkiyatlar`)
Durum: Sevk bekleyen (taslak + paketlenen) / Sevk edilen / Tümü.
Onaylanmamış sevkiyatı yalnızca görev atama yetkisi olan bir yönetici **Sevkiyatı iptal et** ile iptal edebilir (sebep zorunlu):
stok hareketi olmaz, sipariş "Paketlendi"de kalır, toplanan ürünler için **Rafa geri koyma** görevi oluşur.

## Sık hatalar ve mesajlar
| Mesaj | Ne yapmalı |
|---|---|
| Önce raf (lokasyon) barkodunu okutun, sonra ürünü. | Raf etiketini okutun. |
| Yanlış raf: bu ürün okutulan raftan toplanmıyor. Listede gösterilen rafa gidin. | Doğru rafa gidin. |
| Bu raf barkodu bu depoda yok. Raf etiketini kontrol edin. | Etiketi kontrol edin. |
| Yanlış ürün: okutulan ürün bu görevde yok. Ürünü rafına bırakın ve listedeki ürünü okutun. | Doğru ürünü alın. |
| Bu barkod/SKU hiçbir ürünle eşleşmiyor. Etiketi kontrol edip tekrar okutun. | Tekrar okutun veya SKU yazın. |
| Fazla toplama: bu satırda istenenden fazla ürün okuttunuz. Fazlasını rafa geri koyun. | Fazlayı rafa bırakın. |
| Eksik miktarı, satırda açık kalan miktardan fazla olamaz. | Miktarı düzeltin. |
| Tüm satırlar toplanmadan veya eksik bildirilmeden görev tamamlanamaz. | Kalan satırları bitirin. |
| Bu görevi az önce başka biri aldı. Liste yenilendi; havuzdan başka bir görev seçin. | Başka görev seçin. |
| Bu görev size atanmamış. Havuzdan üstlenin veya yöneticinizden atama isteyin. | Görevi üstlenin. |
| Sipariş artık toplanabilir durumda değil (iptal edilmiş veya kapanmış olabilir). Yöneticinize danışın. | Yöneticiye sorun. |
| Paketlenen miktar toplanandan fazla. Fazla ürünü koliden çıkarın. | Fazlayı çıkarın. |
| Paketlenen miktarlar toplananla aynı değil. Eksik ürünleri önce eksik olarak bildirin. | Önce **Eksik bildir**. |
| Bu sevkiyat zaten onaylanmış; stok yalnızca bir kez düşüldü. | İşlem tamam; tekrar gerekmez. |
| Görev meşgul; bir kez daha deneniyor… / Kayıt meşgul; bir kez daha deneniyor… | Bekleyin; gerekirse **Tekrar gönder**. |
| Siparişin zaten aktif bir toplama görevi veya onaylanmamış sevkiyatı var. | Görev zaten var; **Görevi aç** ile ona gidin. |
| Bu siparişte toplanacak açık ürün yok. | Ayrılmış ve açık ürün kalmamış; görev gerekmez. |
| Kısmen yeniden dağıtıldı … birim taşınamadı | Stok gelince tekrar **Yeniden dağıt** deyin veya eksik bildirin. |
| Ayrılmış ürünün bir kısmı artık rafta değil (toplanmış, paketlenmiş veya eksik bildirilmiş) … | Görevi yenileyip tekrar deneyin. |

## İpuçları
- Görevi yarıda bırakırsanız **Toplamaya devam et** ile kaldığınız yerden sürdürürsünüz.
- Ayrı bir BayiOS telefon uygulaması henüz yoktur; telefonun tarayıcısını kullanın.
