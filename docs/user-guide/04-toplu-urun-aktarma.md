# 4. Excel/CSV ile toplu ürün aktarma

> **Durum: web ekranı henüz hazır değil.** Menüdeki **İçe aktarma** sayfası şu an
> "Bu modül henüz geliştiriliyor" mesajını gösterir. Toplu aktarmanın altyapısı (dosya yükleme, önizleme,
> hata raporu, geri alma) BayiOS sunucusunda hazırdır ve test edilmiştir; ancak işletme kullanıcılarının kullanacağı
> ekran (içe aktarma sihirbazı) henüz geliştiriliyor. Pilotta toplu aktarma gerekiyorsa dosyanızı BayiOS ekibine iletin.

Aşağıdaki kurallar sihirbaz geldiğinde de geçerli olacak, sunucuda bugün uygulanan kurallardır.

## Amaç
Yüzlerce ürünü, fiyatı veya stok miktarını tek tek girmek yerine bir Excel ya da CSV dosyasından aktarmak.

## Nasıl işler (sunucudaki akış)
1. **Dosya yükleme** — `.xlsx` veya `.csv`; en fazla **10 MB** ve **10.000 satır**. Makro içeren ya da bozuk dosyalar reddedilir.
2. **Kolon eşleme** — dosyadaki başlıklar BayiOS alanlarıyla eşleştirilir. Sistem öneri sunar ama otomatik uygulamaz;
   eşlemeyi siz onaylarsınız. Eşleme şablon olarak kaydedilip sonraki dosyalarda tekrar kullanılabilir.
3. **Önizleme (deneme çalıştırması)** — hiçbir şey kaydedilmeden her satır kontrol edilir: hangi ürünle eşleştiği,
   dosya içinde tekrar eden satırlar ve satır hataları.
4. **Hata raporu** — hatalı satırlar Excel'de açılabilen bir CSV dosyası olarak indirilebilir.
5. **Başlatma** — aktarma arka planda çalışır; iptal edilirse işlenmiş kısımlar kalır, kalan satırlar işlenmez.
   Hata veya iptalden sonra kaldığı satırdan devam ettirilebilir.
6. **Geri alma** — tamamlanan bir aktarma **7 gün içinde ve yalnızca bir kez** geri alınabilir:
   oluşturulan ürünler arşivlenir, değişen alanlar ve fiyatlar eski değerine döner.
   Aktarmadan sonra elle değiştirilmiş bir kayda dokunulmaz; o satır "çakışma" olarak işaretlenir.

## Kimler yapabilir?
| İş | Sahip | Yönetici | Op. müdürü | Diğerleri |
|---|---|---|---|---|
| Ürün, fiyat, müşteri aktarma | ✓ | ✓ | – | – |
| Stok aktarma | ✓ | ✓ | ✓ | – |
| Aktarmayı geri alma | ✓ | ✓ | – | – |

## İpuçları (dosyanızı hazırlarken)
- İlk satırda kolon başlıkları olsun; her ürün bir satırda.
- SKU'lar benzersiz olsun; aynı SKU dosyada iki kez geçerse önizlemede tekrar olarak işaretlenir.
- Gerçek müşteri bilgisi veya fiyat listesini e-posta ile paylaşırken dikkatli olun; dosyayı yalnızca BayiOS ekibinin belirttiği kanaldan iletin.
- Ekran adımları ve ekranda göreceğiniz hata mesajları, sihirbaz yayımlandığında bu sayfaya eklenecek.
