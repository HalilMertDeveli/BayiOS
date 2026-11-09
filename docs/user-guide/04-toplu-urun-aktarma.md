# 4. Excel/CSV ile toplu ürün aktarma

## Amaç
Yüzlerce ürünü veya fiyatı tek tek girmek yerine bir Excel (`.xlsx`) ya da CSV dosyasından aktarmak.
Ekran: menüde **İçe aktarma**. Bugün iki tür desteklenir: **Ürünler** ve **Fiyat listesi kalemleri**.
Stok ve müşteri aktarma henüz kullanıma açılmadı.

## Kimler yapabilir?
| İş | Sahip | Yönetici | Op. müdürü | Diğerleri |
|---|---|---|---|---|
| Ürün aktarma (katalog yazma yetkisiyle) | ✓ | ✓ | – | – |
| Fiyat listesi aktarma / ürünle birlikte fiyat aktarma (fiyat yazma yetkisiyle) | ✓ | ✓ | – | – |
| Aktarmayı geri alma | ✓ | ✓ | – | – |

Operasyon müdürü menüde **İçe aktarma**yı görür; ancak yalnızca stok aktarabildiği ve stok aktarma henüz açılmadığı için
ekranda "İçe aktarma için yetkiniz yok" mesajı görünür. Satış, depo personeli ve izleyici bu menüyü görmez.

## İçe aktarma geçmişi
**İçe aktarma** sayfası, işletmedeki tüm aktarmaları yeniden eskiye listeler: dosya adı, tür, durum, aktarmayı yapan
kullanıcı, tarih ve sonuç sayıları (eklendi / güncellendi / başarısız; önizlemedeyse eklenecek / güncellenecek / hatalı).
Dosya adına tıklayarak aktarmayı açabilir, yarım kalan bir aktarmaya kaldığı adımdan devam edebilirsiniz. Liste uzunsa
**Daha fazla göster** ile eski kayıtlar yüklenir.

## Adım adım
Yeni bir aktarma için **Yeni içe aktarma** düğmesine basın. Ekranın üstündeki adım çubuğu nerede olduğunuzu gösterir
(telefonda "Adım 3/6: Kolon eşleme" şeklinde).

1. **Tür** — *Ürünler* veya *Fiyat listesi kalemleri*ni seçin. Fiyat listesi aktarıyorsanız fiyatların yazılacağı
   **hedef fiyat listesini** de burada seçin. Yetkiniz olmayan tür seçilemez ve nedeni altında yazar.
2. **Dosya** — dosyayı kutuya sürükleyip bırakın veya **bilgisayarınızdan seçin**.
   - Yalnızca `.xlsx` ve `.csv`; en fazla **20 MB** ve **10.000 satır**; ilk satır kolon başlıkları olmalı.
   - Eski Excel (`.xls`) ve makrolu (`.xlsm`) dosyalar kabul edilmez; Excel'de **Excel Çalışma Kitabı (.xlsx)** olarak kaydedin.
   - Ekran bu kontrolleri yüklemeden önce yapar; son kararı sunucu verir (dosya içeriği, makro, bozuk dosya, satır sınırı).
   - Bir işletme saatte en fazla **10 dosya** yükleyebilir. Sınıra ulaşıldığında ekran ne kadar beklemeniz gerektiğini
     yazar ve süre dolana kadar **Yükle ve devam et** düğmesi kapalı kalır.
3. **Kolon eşleme** — dosyanızdaki her kolon için bir BayiOS alanı seçin (veya **— Aktarılmayacak —**).
   - BayiOS başlıklara bakarak eşleme **önerir**: öneriler ön seçili gelir ve yanlarında **Öneri %95** gibi bir etiket olur.
     Öneriler siz **Kaydet ve önizle**ye basana kadar kaydedilmez; her birini kontrol edip değiştirebilirsiniz.
   - Her kolonun altında dosyadan örnek değerler görünür. Bir alan yalnızca bir kolona eşlenebilir; aynı alanı başka bir
     kolona seçerseniz önceki kolondan kaldırılır.
   - **Zorunlu alanlar** listesi neyin eksik olduğunu gösterir. Ürünlerde: SKU, ürün adı, birim ve KDV oranı
     ("Yalnızca mevcut kayıtları güncelle" modunda yalnızca eşleşme anahtarı). Fiyat listesinde: fiyat ve eşleşme anahtarı.
   - **Özel alanlar** (ör. Renk) "Özel alan: …" olarak listelenir.
   - **Eşleşme anahtarı**: satırlar mevcut kayıtlarla **SKU** (varsayılan) veya **Barkod** ile eşleştirilir.
   - **Aktarma modu**: *Yoksa ekle, varsa güncelle* · *Yalnızca yeni kayıtları ekle* · *Yalnızca mevcut kayıtları güncelle*.
   - Ürün aktarırken **Fiyat** kolonunu eşlerseniz bir **hedef fiyat listesi** seçmeniz gerekir (fiyat yazma yetkisi
     gerekir; yetkiniz yoksa fiyat kolonu seçilemez).
   - **Şablon**: "Bu eşlemeyi şablon olarak kaydet" ile eşlemeyi bir adla saklayın. Aynı başlıklara sahip sonraki
     dosyalarda **Kayıtlı şablonlar** bölümünden seçip **Şablonu uygula** ile tek seferde doldurabilirsiniz.
   - Bu dosya daha önce tamamlanmış bir aktarmada kullanıldıysa ekran uyarır ve önceki aktarmaya bağlantı verir.
4. **Önizleme** — hiçbir şey kaydedilmeden her satır kontrol edilir (deneme çalıştırması). Sonuçta dört sayı görürsünüz:
   **Eklenecek**, **Güncellenecek**, **Atlanacak**, **Hatalı**.
   - Satırlar tablosu sayfalıdır; **Tümü / Hatalı / Eklenecek / Güncellenecek / Atlanacak** filtreleriyle daraltılır.
     Hata varsa tablo hatalı satırlarla açılır; her satırda hangi kolonda ne sorun olduğu yazar.
   - **Hata raporunu indir (CSV)** hatalı satırları, Türkçe Excel'in doğrudan açtığı bir CSV dosyası olarak indirir.
   - Hataları dosyada düzeltip yeniden yükleyebilir, **Eşlemeye dön** ile eşlemeyi değiştirebilir ya da hatasız
     satırlarla devam edebilirsiniz (düğme "N satırı aktar" yazar; hatalı satırlar aktarılmaz).
5. **Aktarma** — aktarma arka planda çalışır; ilerleme çubuğu ve sayılar kendiliğinden güncellenir. Sayfadan
   ayrılabilirsiniz; durumu daha sonra geçmişten görürsünüz.
   - **Aktarmayı iptal et**: işlenmiş satırlar kalır, kalan satırlar işlenmez. İptal veya hata sonrası
     **Kaldığı yerden devam et** ile sürdürülebilir; işlenmiş satırlar tekrar işlenmez.
6. **Sonuç** — eklenen, güncellenen, atlanan ve başarısız satır sayıları; **Ürünlere git** (veya **Fiyat listesine git**)
   bağlantısı; gerekirse hata raporu.

## Geri alma
Tamamlanan bir aktarma, tamamlandıktan sonra **7 gün içinde ve yalnızca bir kez** geri alınabilir. Sonuç ekranında son
tarih yazar; **Geri al** düğmesi yalnızca bu süre içinde ve geri alma yetkisi olanlara görünür. Onay penceresi ne olacağını
açıkça yazar:
- Aktarmayla **eklenen ürünler silinmez, arşivlenir** — listelerden kalkar, geçmiş kayıtlar (sipariş, stok hareketi) korunur.
  Fiyat listesi aktarmasında eklenen fiyatlar listeden kaldırılır.
- **Güncellenen** alanlar ve fiyatlar aktarmadan önceki değerine döner.
- Aktarmadan sonra elle değiştirilmiş kayıtlara **dokunulmaz**; bu satırlar **çakışma** olarak işaretlenir.
- Stok miktarları değişmez.

Geri alma da arka planda çalışır. Bittiğinde **Geri alındı**, **Çakışma (dokunulmadı)**, **Geri alınamadı** sayıları
görünür; satır tablosundaki **Çakışmalar** filtresiyle hangi kayıtların olduğu gibi bırakıldığını görüp gerekirse elle
düzeltebilirsiniz. Geri alma yarıda kalırsa (**Geri alma durdu**) **Geri almayı sürdür** ile kaldığı yerden devam eder.

## Ekranda görebileceğiniz mesajlar
| Mesaj | Ne yapmalı |
|---|---|
| "Dosya 20 MB sınırını aşıyor." | Dosyayı parçalara bölüp ayrı ayrı yükleyin. |
| "Dosyada 10.000'den fazla satır var." | Dosyayı parçalara bölün. |
| "Eski Excel biçimi (.xls) desteklenmiyor." / "Makro içeren Excel dosyaları (.xlsm) kabul edilmez." | Excel'de `.xlsx` olarak kaydedin. |
| "Dosyanın içeriği uzantısıyla uyuşmuyor." / "Dosya okunamadı; bozuk olabilir." | Dosyayı Excel'de açıp yeniden kaydedin. |
| "Bu işletme için saatlik dosya yükleme sınırına ulaşıldı. Yaklaşık N dakika sonra tekrar deneyin." | Belirtilen süre kadar bekleyin. |
| "“Ürün adı” alanı zorunlu; bir kolonla eşleyin." | Eksik zorunlu alanı bir kolona eşleyin. |
| "Fiyatların yazılacağı fiyat listesini seçin." | Hedef fiyat listesini seçin veya fiyat kolonunu "Aktarılmayacak" yapın. |
| "Yüklenen dosya saklama süresi (30 gün) dolduğu için silindi." | Dosyayı yeniden yükleyerek yeni bir aktarma başlatın. |
| "Bu aktarma zaten geri alındı veya şu an geri alınıyor." | Geri alma bir kez yapılır; sonucu sayfada görün. |
| "Geri alma süresi (tamamlandıktan sonra 7 gün) doldu." | Bu aktarma artık geri alınamaz; gerekirse kayıtları elle düzeltin. |

## İpuçları (dosyanızı hazırlarken)
- İlk satırda kolon başlıkları olsun; her ürün bir satırda.
- Başlıkları Türkçe ve açık yazın ("Stok Kodu", "Ürün Adı", "Birim", "KDV", "Fiyat") — öneriler daha isabetli olur.
- Birimler katalogda tanımlı olmalı (ör. `adet`); tanımlı olmayan birim önizlemede hata verir.
- SKU'lar benzersiz olsun; aynı SKU dosyada iki kez geçerse önizlemede hatalı satır olarak işaretlenir.
- Gerçek müşteri bilgisi veya fiyat listesi içeren dosyaları yalnızca BayiOS'a yükleyin; e-posta ile dolaştırmayın.
