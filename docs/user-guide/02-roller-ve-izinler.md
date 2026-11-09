# 2. Kullanıcı rolleri ve izinler

## Amaç
Her çalışanın yalnızca işine gereken ekranları görmesini ve işlemleri yapmasını sağlamak.
Bir kullanıcının bir işletmede **tek bir rolü** vardır. Roller sabittir; yeni rol tanımlanamaz.

> **Ekip yönetimi:** Çalışanları **Ayarlar → Ekip** ekranından davet eder, rollerini değiştirir, üyeliği askıya alır
> veya yeniden açarsınız. İşletme sahibi yönetici ve altındaki rolleri, yönetici operasyon müdürü ve altındaki rolleri
> yönetir; sahiplik yalnızca **sahiplik devri** ile verilir ve son işletme sahibi askıya alınamaz. Davet e-posta ile
> gönderilmez: oluşan bağlantıyı (7 gün geçerli) kişiye siz iletirsiniz; kabul için kişinin aynı e-posta adresiyle
> kaydolup adresini doğrulamış olması gerekir.

## Roller

| Rol (ekrandaki adı) | Kısaca |
|---|---|
| **İşletme sahibi** | Her şeyi yapar; paket ve modülleri yönetir. |
| **Yönetici** | Sahibin yaptığı işlerin çoğu; paket/modül açıp kapatamaz. |
| **Operasyon müdürü** | Sipariş onayı, depo, stok işlemleri, görev atama. |
| **Satış** | Müşteri ve sipariş oluşturma, WhatsApp siparişi. Stok düzeltemez. |
| **Depo personeli** | Mal kabul, toplama, paketleme, sevk. Fiyat göremez. |
| **İzleyici** | Yalnızca görüntüler; rapor alır. |

## Kim ne yapabilir? (bugün ekranda olan işler)

✓ = yapabilir · – = yapamaz

| İş | Sahip | Yönetici | Op. müdürü | Satış | Depo | İzleyici |
|---|---|---|---|---|---|---|
| Ürünleri görmek | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Ürün eklemek / düzenlemek / arşivlemek | ✓ | ✓ | ✓ | – | – | – |
| Özel alan tanımlamak | ✓ | ✓ | – | – | – | – |
| Satış fiyatlarını görmek | ✓ | ✓ | ✓ | ✓ | – | ✓ |
| Fiyat listesi oluşturmak / fiyat girmek | ✓ | ✓ | – | – | – | – |
| Müşteriye / bayi grubuna fiyat listesi atamak | ✓ | ✓ | ✓ | – | – | – |
| Stoğu görmek | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| Mal kabul | ✓ | ✓ | ✓ | – | ✓ | – |
| Stok düzeltme | ✓ | ✓ | ✓ | – | – | – |
| Depo ve raf tanımlamak | ✓ | ✓ | ✓ | – | – | – |
| Müşteri eklemek / düzenlemek | ✓ | ✓ | ✓ | ✓ | – | – |
| Bayi grubu yönetmek | ✓ | ✓ | ✓ | – | – | – |
| Sipariş oluşturmak ve onaya göndermek | ✓ | ✓ | ✓ | ✓ | – | – |
| Siparişi onaylamak (stok ayırmak) | ✓ | ✓ | ✓ | ✓¹ | – | – |
| Siparişi iptal etmek | ✓ | ✓ | ✓ | ✓² | – | – |
| WhatsApp siparişi hazırlamak | ✓ | ✓ | ✓ | ✓ | – | – |
| Toplama görevini personele atamak, oluşturmak, önceliğini değiştirmek, yeniden dağıtmak | ✓ | ✓ | ✓ | – | – | – |
| Aynı depoda raf taşıma | ✓ | ✓ | ✓ | – | ✓ | – |
| Havuzdan görev üstlenmek (**Üstlen**) | – | – | ✓ | – | ✓ | – |
| Toplama, paket kontrolü, sevk onayı | ✓ | ✓ | ✓ | – | ✓ | – |
| Raporları görmek ve indirmek | ✓ | ✓ | ✓ | ✓³ | – | ✓ |
| Ayarlar (sipariş ayarları) | ✓ | ✓ | – | – | – | – |
| Paket ve modülleri görmek | ✓ | ✓ | – | – | – | – |
| Modülü etkinleştirmek / kapatmak | ✓ | – | – | – | – | – |
| Müşteriyi KVKK için anonimleştirmek | ✓ | ✓ | – | – | – | – |

1. Satış personelinin onay yetkisi işletme kuralına bağlıdır; varsayılan **açık**.
2. Satış personeli yalnızca **kendi oluşturduğu**, **Taslak** veya **Onay Bekliyor** durumundaki siparişleri iptal edebilir.
3. Satış personeli yalnızca sipariş raporlarını (Sipariş durumları, En çok sipariş edilen ürünler) görür.

### Depo personeline özel
- Siparişlerden yalnızca **kendisine atanmış veya havuzdaki görevlere bağlı** olanları görür; fiyat ve tutarlar gösterilmez
  ("Fiyat ve tutarlar rolünüz gereği gösterilmiyor; miktarlar ve sevkiyat durumu görünür.").
- Giriş yaptığında doğrudan **Görevler** ekranı açılır.
- Onaylanmamış bir sevkiyatı iptal edemez; bunu görev atama yetkisi olan bir yönetici yapar.

### Satış personeline özel
- Başkasının oluşturduğu taslağı düzenleyemez ("Bu taslağı yalnızca oluşturan kişi veya yetkili bir yönetici düzenleyebilir.").
- İskonto ve manuel fiyat, işletmenin satış personeli için belirlediği sınıra bağlıdır. Sınır varsayılan **%0**'dır, yani iskonto ve manuel fiyat giremez.

## Sık hatalar ve mesajlar
| Mesaj | Anlamı |
|---|---|
| Bu işlem için yetkiniz yok. Gerekirse işletme yöneticinizle görüşün. | Rolünüzün izni yok. |
| Salt okunur görünüm | Ekranı görebilirsiniz ama değiştiremezsiniz. |
| Satış personelinin sipariş onaylaması işletme ayarlarında kapalı. Siparişi onay için yöneticinize iletin. | İşletme bu kuralı kapatmış. |
| İşletmenizde satış personeli için iskonto sınırı %0; iskonto ve manuel fiyat giremezsiniz. Fiyatları fiyat listesinden bırakın. | İskonto alanlarını boş bırakın. |
| İskonto en fazla %X olabilir. … | Daha düşük iskonto girin veya yöneticiye onaylatın. |
| Yalnızca kendi oluşturduğunuz siparişleri iptal edebilirsiniz. Bu sipariş için yöneticinize başvurun. | Satış personeli için iptal sınırı. |
| Bu işletmeye erişiminiz yok. Lütfen erişebildiğiniz bir işletme seçin. | Başka bir işletme seçin. |

## İpuçları
- Menüde bir bölümü göremiyorsanız rolünüz o bölüme izin vermiyordur.
- Satış personelinin onay yetkisi, iskonto sınırı, alış fiyatı görünürlüğü ve depo personelinin barkod atama / mal kabul
  onayı gibi kurallar **Ayarlar → İş kuralları** ekranından (sahip ve yönetici) değiştirilir. İki adımlı doğrulama
  seçeneği henüz uygulanmıyor.
