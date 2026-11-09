# 1. Hesap açma ve işletme kurulumu

## Amaç
Kendinize bir kullanıcı hesabı açmak, işletmenizi BayiOS'ta oluşturmak ve kurulum sihirbazıyla depo, ürün, açılış
stoğu ve ekibinizi adım adım hazırlamak. BayiOS sektörden bağımsızdır: daha önce hiç depo programı kullanmamış olsanız da
kurulumu kendi başınıza, hazır varsayılanlarla tamamlayabilirsiniz.
İşletmeyi oluşturan kişi o işletmenin **İşletme sahibi** olur.

## Adımlar

### A. Hesap açma (`/kayit`)
1. Giriş ekranında **Ücretsiz kayıt olun** bağlantısına tıklayın.
2. **Ad soyad**, **E-posta**, **Şifre** ve **Şifre (tekrar)** alanlarını doldurun.
   Şifre en az 8 karakter olmalı, en az bir harf ve bir rakam içermelidir.
3. **Hesap oluştur** düğmesine basın.
4. E-postanıza gelen doğrulama bağlantısına tıklayın. Bağlantı sizi doğrudan işletme oluşturma ekranına götürür.
   (E-posta gelmediyse istenmeyen/spam klasörüne bakın.)

![Kayıt ekranı](../../apps/web/docs/screenshots/desktop-kayit.png)

### B. Giriş ve şifre
- **Giriş yap** ekranında e-posta ve şifrenizi girin.
- Şifrenizi unuttuysanız **Şifremi unuttum** bağlantısıyla e-postanıza sıfırlama bağlantısı isteyin.

![Giriş ekranı](../../apps/web/docs/screenshots/desktop-giris.png)

### C. İşletmeyi oluşturma (`/isletme-olustur`) — 4 adım
1. **İşletme bilgileri**
   - **İşletme adı** (2–200 karakter), ör. "Yıldız Toptan Gıda".
   - **İşletme adresi (kısa ad)**: adı yazarken otomatik önerilir (ör. `yildiz-toptan-gida`).
     Küçük harf, rakam ve tire; 3–63 karakter. Başka bir işletmede kullanılıyorsa değiştirmeniz istenir.
2. **Ürün şablonu** — hazır gelen birimleri ve isterseniz örnek kategori ve özel alanları belirler. **Sade başlangıç**
   önceden seçilidir ve her işletmeye uygundur; emin değilseniz değiştirmeden devam edin. Diğer ikisi yalnızca özel
   alanların neler yapabildiğini gösteren isteğe bağlı örneklerdir:

   | Şablon | Hazır birimler | Hazır kategoriler | Hazır özel alanlar |
   |---|---|---|---|
   | **Sade başlangıç (önerilen)** | Adet, Koli, Kilogram | – | – |
   | **Örnek şablon: Uyumluluk takipli parçalar** | Adet, Takım, Kutu, Litre, Metre | Örnek parça kategorileri | Uyumlu markalar, Uyumlu model, Model yılı aralığı, Orijinal parça no, Taraf / konum, Garanti (ay); varyantta Parça tipi |
   | **Örnek şablon: Teknik özellikli ürünler** | Adet, Kutu, Koli, Metre | Örnek teknik ürün kategorileri | Güç (W), Renk sıcaklığı (K), Voltaj (V), IP sınıfı ve benzeri teknik alanlar, Seri no takibi (Evet/Hayır) |

   Örnek şablonun eklediği kategori ve özel alanları istemezseniz sonradan arşivleyebilirsiniz.

3. **Ticari ayarlar**
   - **Para birimi**: Türk lirası (TRY), ABD doları (USD) veya Euro (EUR). Varsayılan TRY.
   - **Varsayılan KDV oranı**: %20, %10, %1, %0 (KDV'siz). Yeni ürünlere önerilir.
   - **Fiyat gösterimi**: KDV hariç / KDV dahil.
   - **Negatif stok**: seçim yoktur — "Stok eksiye düşmez" sabit kuraldır ve bu adımda bilgi olarak gösterilir.
     Ayrıntı: [Stok düzeltme ve negatif stok](06-stok-duzeltme-negatif-stok.md).
4. **Onay** — özeti kontrol edip **İşletmeyi oluştur** düğmesine basın.
   "İşletmeniz hazır" bildirimi çıkar ve **kurulum sihirbazı** açılır (aşağıda D).

![Ürün şablonu seçimi](../../apps/web/docs/screenshots/desktop-onboarding-sektor.png)
![Ticari ayarlar (telefon)](../../apps/web/docs/screenshots/mobile-onboarding-ticari.png)

### D. Kurulum sihirbazı (`/kurulum`)
İşletme oluşunca sihirbaz sizi adım adım yönlendirir. **Her adım isteğe bağlıdır**: **Bu adımı atla** ile geçebilir,
**Daha sonra tamamla** ile panoya dönebilirsiniz. Nerede kaldığınız verinizden anlaşılır; panodaki **İlk adımlar**
listesinden veya **Ayarlar → Kurulum sihirbazı**ndan istediğiniz zaman devam edersiniz. Sihirbazı işletme sahibi ve
yönetici kullanır.

1. **İşletme bilgileri** — işletme adı, telefon, vergi dairesi, vergi no (VKN 10 hane) / T.C. kimlik no (11 hane).
   Adres alanı henüz yoktur.
2. **Depo** — ilk deponuzun adı ve kodu (öneri: *Merkez Depo*, `MERKEZ`). **Basit raf düzeni oluştur** seçiliyse raflar
   kendiliğinden açılır (ör. ön ek `A`, 10 raf → `A-01 … A-10`). Raf kullanmıyorsanız **Raf kullanmıyorum**u seçin;
   stok depo düzeyinde tutulur. Yarıda kalırsa tekrar denediğinizde yalnızca eksik raflar eklenir.
3. **Ürünler** — iki yol:
   - **Şablonu indir (CSV)** ile hazır şablonu indirin, ürünlerinizle doldurun ve **İçe aktarmayı başlat** ile
     [toplu aktarın](04-toplu-urun-aktarma.md). Şablondaki kolon adları otomatik eşlenir.
   - **Elle ekleyin**: ürün adı, stok kodu (SKU; addan önerilir), isteğe bağlı barkod ve birim. KDV, işletmenin
     varsayılan oranıdır. Fiyatı ve ayrıntıları sonra **Ürünler** ekranından girersiniz.
4. **Açılış stoğu** — şu an elinizdeki miktarları girin (raflı depoda raf da seçin). Kayıt bir **mal kabul** olarak
   stok hareketlerine geçer (referans: "Açılış stoğu"). Bu adım yalnızca stok ekler; stok hiçbir zaman eksiye düşmez.
   İlk 50 ürün listelenir; daha fazlası için **Depo → Mal kabul** ekranında barkod okutun.
5. **Ekip** — e-posta ve rol seçip davet bağlantısı oluşturun. Her rolün ne yaptığı ekranda sade bir cümleyle yazar.
   Bağlantıyı kişiye siz iletirsiniz; BayiOS e-posta göndermez. Bkz. [Roller ve izinler](02-roller-ve-izinler.md).
6. **Bitti** — yaptıklarınızın özeti.

**İlk adımlar (pano):** işletme sahibi ve yönetici panoda depo, ürün, stok, ekip daveti ve ilk sipariş adımlarının
hangilerinin yapıldığını görür. Liste verinizden hesaplanır; **Kapat** ile gizlenir (bu tarayıcıda, bu işletme için).

> **KVKK:** Aydınlatma metni ve onay adımı, onaylı hukuki metin hazır olduğunda kuruluma eklenecektir.

### E. Birden çok işletme
Birden çok işletmenin üyesiyseniz girişten sonra **İşletme seç** ekranı açılır. Sağ üstteki
**İşletme seç** ile istediğiniz zaman değiştirebilirsiniz. Her işletmenin verisi tamamen ayrıdır.

## Sık hatalar ve mesajlar
| Mesaj | Ne yapmalı |
|---|---|
| Şifre en az 8 karakter olmalı. / Şifre en az bir harf ve bir rakam içermeli. | Daha uzun, harf ve rakam içeren şifre seçin. |
| Şifreler eşleşmiyor. | İki şifre alanına aynı şifreyi yazın. |
| Bu e-posta adresiyle zaten bir hesap var. Giriş yapmayı veya şifre sıfırlamayı deneyin. | Giriş yapın ya da **Şifremi unuttum**. |
| E-posta adresiniz henüz doğrulanmadı. … | E-postadaki doğrulama bağlantısına tıklayın. |
| E-posta adresi veya şifre hatalı. | Bilgileri kontrol edin. |
| Bağlantının süresi dolmuş. Lütfen yeni bir bağlantı isteyin. | Yeni doğrulama/sıfırlama bağlantısı isteyin. |
| Çok sayıda deneme yapıldı. Lütfen birkaç dakika bekleyip tekrar deneyin. | Birkaç dakika bekleyin. |
| Bu işletme adresi başka bir işletme tarafından kullanılıyor. Lütfen farklı bir adres girin. | Kısa adı değiştirin. |
| Yalnızca küçük harf (a-z), rakam ve tire kullanın; tire ile başlayamaz veya bitemez. | Türkçe karakter ve boşluk kullanmayın. |
| Devam etmek için bir şablon seçin. | Bir şablon kartına tıklayın (önerilen: Sade başlangıç). |
| İşletmeniz oluşturuldu, ancak ürün şablonu / ticari ayarlar / para birimi kaydedilemedi. | İşletme oluşmuştur. **Kalan adımları tamamla** düğmesine basın; ya da **Şimdilik atla, panoya geç** deyip ayarları sonra **Ayarlar**'dan yapın. |

## İpuçları
- Şablonu yanlış seçtiyseniz endişelenmeyin: özel alanları **Ayarlar → Özel alanlar** ekranından ekleyip arşivleyebilirsiniz.
- KDV ve fiyat gösterimi ayarları sonradan **Ayarlar** sayfasından değiştirilebilir ("Stok eksiye düşmez" kuralı değiştirilemez). Para birimi sonradan ekrandan değiştirilemez.
- "Teknik özellikli ürünler" örnek şablonundaki "Seri no takibi" yalnızca Evet/Hayır bilgisidir; BayiOS henüz seri numarası takibi yapmaz.
- Çalışma arkadaşlarınızı **Ayarlar → Ekip** ekranından davet edersiniz; davet bağlantısını kişiye siz iletirsiniz
  (e-posta gönderilmez). Bkz. [Roller ve izinler](02-roller-ve-izinler.md).
