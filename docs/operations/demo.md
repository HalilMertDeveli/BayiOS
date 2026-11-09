# Demo işletmeler (sentetik) — varsayılan: Demo Toptan Ticaret (genel); örnek: #56 Motor, #57 LED

Ürün sektörden bağımsızdır (`phase0-gate.md` §5.2 "Ürün yönü"): **varsayılan demo**, "Sade başlangıç" (genel)
şablonuyla kurulan genel bir B2B toptancı/distribütördür. Motor yedek parça ve LED elektronik demoları yalnızca
isteğe bağlı sektör örnekleridir.

Demolar tamamen **sentetik** işletmelerdir; yalnızca **public BayiOS API** üzerinden ve gerçek Supabase Auth
oturumlarıyla (sahip + davet edilen ekip) kurulur. Hiçbir iş tablosuna doğrudan yazılmaz; hiçbir gerçek firma, kişi,
telefon, adres, fiyat listesi kullanılmaz. Veri tohumlu RNG ile **deterministiktir**: aynı komut her zaman aynı
kataloğu, fiyatları, stoğu ve siparişleri üretir (tek istisna: genel demoda bazı siparişlerin "istenen teslim" tarihi
kurulum gününe göre +1…+7 gündür).

| | **Demo Toptan Ticaret (varsayılan)** | Demo Motor Yedek Parça (#56) | Demo LED Elektronik (#57) |
|---|---|---|---|
| `--demo` | `genel` | `motor` | `led` |
| Slug | `demo-genel` | `demo-motor-yedek-parca` | `demo-led-elektronik` |
| Şablon | `general` ("Sade başlangıç") | `motorcycle_parts` | `led_electronics` |
| Sahip (yalnız yerel) | `sahip@demo-genel.example.test` | `sahip@demo-motor.example.test` | `sahip@demo-led.example.test` |
| Ekip (yalnız yerel) | `mudur@` (manager), `satis@` (sales), `depo@` (warehouse_staff) — `@demo-genel.example.test` | — (yalnız sahip) | — (yalnız sahip) |

## Çalıştırma (tek komut)

```bash
bash infra/ci/e2e-stack.sh up              # yerel gerçek yığın: Supabase Auth + Postgres 17 + EF migration + API (+ web)
npm --prefix apps/web run demo:seed         # varsayılan: genel demo (Demo Toptan Ticaret)
npm --prefix apps/web run demo:seed -- --demo all          # genel + motor + led
npm --prefix apps/web run demo:seed -- --demo motor,led    # yalnız sektör örnekleri
npm --prefix apps/web run demo:seed -- --help              # seçenekler
```

- Seçenekler: `--demo genel|motor|led|all` (virgülle birden fazlası), `--size full|small`, `--suffix x1`,
  `--credentials <dosya>`, `--summary <dosya>` (sayımlar JSON).

- Yalnızca yerel yığına çalışır: API/Auth/Mailpit adresi `localhost`/`127.0.0.1` değilse komut reddeder.
- Seeder sıfır bağımlılıklı Node betiğidir (`apps/web/e2e-local/demo/seed.mjs`, Node 24); `npm ci` gerekmez.
- Publishable key (yerel geliştirme anahtarı, gizli değil) sırayla `SUPABASE_PUBLISHABLE_KEY` ortam değişkeninden,
  `apps/web/.env.local` dosyasından veya `npx -y supabase@2.122.1 status` çıktısından okunur.
- Varsayılan adresler: API `http://localhost:5280` (`E2E_API_URL`), Auth `http://127.0.0.1:54321` (`SUPABASE_URL`),
  Mailpit `http://127.0.0.1:54324` (`MAILPIT_URL`). Web portu 3200 doluysa yığını `E2E_WEB_PORT=3300` ile başlatın
  (API CORS'u o porta göre ayarlanır) ve tarayıcıda o portu açın.
- `--suffix x1` aynı demonun ayrı bir kopyasını kurar (slug, ad, sahip ve ekip e-postalarına ek gelir) — gerçek demoya
  dokunmadan seeder'ı denemek için.
- Süre (yerel, tam boy): motor/LED demo başına ~8 sn; genel demo ekip kayıtları (Mailpit onayı) ve depo belgeleriyle
  biraz daha uzun; tekrar çalıştırma < 1 sn.

### Demo girişleri

Giriş: `http://localhost:3200/giris` (veya `E2E_WEB_PORT`). Şifreler ilk çalıştırmada **her kullanıcı için ayrı ve
rastgele üretilir** ve git'e girmeyen `.demo/credentials.json` dosyasına (`"<demo>:<e-posta>"` anahtarı, rolüyle)
yazılır (`.gitignore`: `.demo/`). `DEMO_PASSWORD` ortam değişkeni tüm demo kullanıcıları için şifreyi sabitler (yalnız
yerelde). Bu hesaplar sentetiktir; başka hiçbir yerde kullanılmaz, gerçek kimlik bilgisi repoya asla girmez. Komut
sonunda her demonun sahip ve ekip e-postaları listelenir.

Genel demoda rol başına giriş, video için farklı bakış açıları verir: sahip/müdür tüm menü ve panolar; satışçı
siparişler, müşteriler, fiyatlar (transfer/sayım yok); depo görevlisi doğrudan `/depo/gorevler` ("Görevlerim":
kendisine atanmış Acil ve Yüksek öncelikli görevler), transferler ve sayımlar.

### Tekrar çalıştırma (idempotent) ve sıfırlama

- Sahip kullanıcı zaten varsa giriş yapılır; aynı slug'lı demo kiracısı **tamsa hiçbir şey değişmez** ("already
  seeded — nothing changed") ve sayımlar yeniden raporlanır. Ekip daveti yalnızca kiracı ilk kurulurken yapılır; ekip
  kullanıcıları Auth'ta zaten varsa (yığın `up` ile sıfırlandıysa) aynı şifreyle giriş yapıp yeni daveti kabul eder.
- Yarıda kalmış (eksik) bir demo kiracısı bulunursa seeder durur: kiracı silme API'si yoktur ve yarım veriyi tamamlamak
  deterministikliği bozar. **Sıfırlama:** iş verisi yığının geçici Postgres'indedir → `bash infra/ci/e2e-stack.sh up`
  (Postgres konteyneri her `up`ta sıfırdan kurulur) ve tekrar `demo:seed`. Auth kullanıcıları yerel Supabase'de kalır
  ve yeniden kullanılır; onları da silmek için `E2E_SUPABASE_NO_BACKUP=1 bash infra/ci/e2e-stack.sh down`.
- `.demo/credentials.json` silinirse mevcut kullanıcıya yeni şifreyle girilemez: dosyayı geri getirin veya Auth'u
  yukarıdaki gibi sıfırlayın.
- Seeder mevcut (tam) bir demoyu **güncellemez**. #136 öncesi kurulmuş bir demoda LED ürün adları eski küçük harfle
  ("i̇ç mekân") ve siparişler fatura adresi olmadan kalır; yeni veri için yukarıdaki gibi sıfırlayıp yeniden kurun.

## Genel demo içeriği — Demo Toptan Ticaret (tam boy, `--size full`)

Temizlik, kâğıt & hijyen, ambalaj, kırtasiye & ofis, küçük hırdavat ve ikram sarfı satan sentetik bir toptancı.
Veri modülü saf fonksiyondur (`apps/web/e2e-local/demo/data-genel.mjs`, `genelDemo(size)`); Vitest ile yığınsız test
edilir (`apps/web/src/test/demo-datasets.test.ts`).

| Konu | İçerik |
|---|---|
| Şablon ve ayarlar | `general` ("Sade başlangıç": adet, koli, kg). Seeder API ile ekler: birimler paket, rulo, kutu, litre; 6 kategori; özel alanlar "Koli içi adet" (tam sayı) ve "Menşe" (Yerli üretim / İthal, filtrelenebilir) |
| Ürün (API + içe aktarma) | 78 (68 + 10 XLSX tedarikçi kataloğu, Imports API) |
| Varyant / barkod | 127 / 127 (GS1 mağaza içi aralık **202**; hacim, renk, ölçü, boyut, beden, koku varyantları) |
| Markalar | 6 kategori markası + "Demo Toptan Özel Seri" (hepsi uydurma) |
| Depo / raf | **Merkez Depo** (raflı, varsayılan): A-01 Temizlik, A-02 Kâğıt & Hijyen, B-01 Ambalaj, B-02 Kırtasiye, C-01 Hırdavat, C-02 Mutfak & İkram, R-01 taşma rafı · **Şube Depo** (rafsız) |
| Fiyat listeleri | Liste fiyatı (varsayılan; çöp torbası 20+, tuvalet kâğıdı 10+, koli 10+, A4 kâğıt 10+ kademe) + **Bayi listesi** (%10, kolide 25+ kademe) + **Kurumsal liste** (%5) |
| Müşteri grupları / müşteri | Bayi (5), Kurumsal (5), Market (5; varsayılan liste) = 15; her müşteride teslimat ("Teslimat") + fatura ("Fatura") adresi |
| Ekip | sahip + müdür (`manager`) + satışçı (`sales`) + depo görevlisi (`warehouse_staff`): sahip Members API ile davet eder, her biri Supabase Auth'a kaydolur (Mailpit onayı) ve daveti kabul eder |
| Stok | Mal kabulleriyle (ledger), kategori rafına; Temizlik / Kâğıt & Hijyen / Kırtasiye Şube Depo'da da var; 2 kalem kritik seviyede (`TEM-CAM`, `OFS-ZIMBA`); `HRD-SUTERAZI` hiç stoklanmaz |
| Siparişler (17 + WhatsApp) | taslak 2, onay bekliyor 1, **onaylandı 4** (toplama görevleri: **Acil** — depo görevlisine atanmış, yarın teslim; Yüksek; Normal; Düşük — havuzda), **toplanıyor 1** (kısmen toplanmış, Yüksek, depo görevlisinde), paketlendi 1, kısmi sevk + kalanı kapatıldı 1 (hasarlı eksik), sevk edildi 3, tamamlandı 2, iptal 2 (biri onaydan sonra) + WhatsApp mesajından taslak 1 |
| Raf taşıma (#168) | `OFS-SEFFAFBANT` 6 ve `HRD-KELEPCE-200` 5 → R-01 (stok hareketlerinde görünür) |
| Transferler (#168, iki aşamalı) | Merkez → Şube: **1 yolda** (gönderildi, teslim alınmadı; 3 satır), 1 tam teslim alındı, **1 eksik teslim alındı** (2 paket karton tabak eksik) ve kayıp olarak kapatıldı ("Farkla kapatıldı", "Fark var") |
| Sayım (#168) | Merkez Depo B-02 (Kırtasiye) rafında **açık sayım**: ilk 5 satır sayılmış (ikisinde fark: −1 ve +2), kalanı bekliyor — "onaya gönder / farkları uygula" videoda canlı yapılabilir |
| Minimum stok / kritik stok | Motor/LED ile aynı kural (aşağıda): 4 kritik, 1 depo eşiği (Şube Depo), 2 kritik olmayan, 1 eşiksiz-stoksuz |

Sayım özeti (`--summary`) genel demo için ayrıca `membersByRole` (owner/manager/sales/warehouse_staff = 1),
`activeTasksByPriority`, `assignedActiveTasks`, `transfersByStatus` (`in_transit` 1, `received` 1, `closed` 1),
`transfersWithDiscrepancy` 1 ve `stockCountsByStatus` (`open` 1) verir. CI (`demo seed (twice)`) bunları doğrular.

Küçük sürüm (`--size small`, E2E): kategori başına 2 ürün (12 ürün, 26 varyant), 3 müşteri, 5 sipariş (taslak,
Acil + Düşük onaylı, kısmen toplanan, sevk edilen), 1 yolda + 1 farkla kapatılan transfer, 1 raf taşıma, B-01'de açık
sayım.

### Video akışı önerisi (genel demo)

1. Sahip: Pano (kritik stok kutucuğu) → Siparişler (her durum) → Ürünler (varyantlar, Menşe filtresi) → Fiyat
   listeleri (Bayi/Kurumsal, kademeler) → Müşteriler.
2. `/depo/gorevler` "Tüm görevler": Acil / Yüksek / Normal / Düşük, atanan kişi, istenen teslim tarihi; havuzdaki
   görevi "Personele ata".
3. Depo görevlisi girişi: "Görevlerim" → Acil görevi barkodla topla → paket kontrolü → sevk.
4. `/depo/transferler`: yoldaki transferi Şube Depo'da teslim al; "Farkla kapatıldı" transferin fark raporu.
5. `/depo/sayimlar`: açık sayımı tamamla, farkları gözden geçir ve uygula. `/stok/raf-tasima`: raf taşıma.
6. Raporlar: kritik stok, en çok sipariş edilenler (CSV/XLSX).

## Sektör örnekleri içeriği — Motor ve LED (tam boy, `--size full`)

| | Motor | LED |
|---|---|---|
| Ürün (API + içe aktarma) | 77 (69 + 8 XLSX) | 62 (47 + 15 XLSX) |
| Varyant / barkod | 120 / 120 (GS1 mağaza içi aralık 200, gerçek ürünle çakışmaz) | 70 / 70 (aralık 201) |
| Özel alan değerleri | uyumlu marka (çoklu), model, model yılı aralığı, OEM no (`90001-SNT-###`), taraf, garanti; varyantta parça tipi (Orijinal/Muadil/Yan sanayi) | pixel pitch, kullanım, modül ölçüsü, parlaklık, tarama, LED tipi, güç, voltaj, IP sınıfı, kart ailesi uyumu, seri no takibi bayrağı |
| Depo / raf | Merkez Depo (raflı, 9 raf, varsayılan) + Dükkân | İstanbul Merkez (raflı, 8 raf, varsayılan) + Ankara Şube + Hasarlı/İade |
| Fiyat listeleri | Perakende (varsayılan) + Bayi A (%12, fren balatasında 10+ kademe) + Bayi B (%6) | Liste fiyatı (varsayılan; modüllerde 50+ / 200+ kademe) + Entegratör (%8) |
| Bayi grupları / müşteri (adresli) | Bayi A, Bayi B, Perakende / 12; her müşteride varsayılan teslimat ("Dükkân") + fatura ("Fatura") adresi | Entegratör, Ajans, Kurulum / 10; teslimat ("Merkez") + fatura ("Muhasebe") adresi |
| İçe aktarma | 1 XLSX (aksesuar/sarf; fiyat + garanti alanı), Imports API: yükle → eşleme → kuru doğrulama → çalıştır | 1 XLSX (güç kaynağı, kablo (metre), montaj, yedek LED; fiyat + teknik alanlar) |
| Stok | Mal kabulleriyle (ledger), kategori rafına; bazı kalemler kritik seviyede (2); bir kalem hiç stoklanmaz; 7 minimum stok eşiği (aşağıda) | Aynı; kablo metre (ondalık) |
| Siparişler (17) | taslak 3 (biri WhatsApp), onay bekliyor 2, onaylandı/rezerve 2, toplanıyor 1, paketlendi 1, kısmi sevk + kalanı kapatıldı 1, sevk edildi 3, tamamlandı 2, iptal 2 (biri onaydan sonra, rezervasyon serbest) | aynı dağılım; proje siparişi (24 kabinet + kartlar + PSU + kablo) dahil |
| WhatsApp | §1.3 mesajı; kural tabanlı ayrıştırıcı (AI kapalı) → insan onaylı taslak | §2.3 proje mesajı → taslak |

Sipariş akışları gerçek uç noktalarla yürür: gönder/onayla (If-Match), toplama görevi atama + başlatma + raf/barkod
okutma, eksik bildirimi, tamamla → taslak sevkiyat → paket kontrolü → sevk onayı, `close-remaining`, `complete`,
`cancel`.

- **Adresler (#136):** her sipariş müşterinin teslimat **ve** fatura adresini açıkça seçer (`shippingAddressId`,
  `billingAddressId`); gönderimden itibaren siparişte ikisinin de kopyası (snapshot) durur, sipariş detayında
  "Siparişte kayıtlı adres yok" görünmez; taslaklar müşterinin varsayılan adreslerini önizler. Tek istisna taslakken
  (hiç gönderilmeden) iptal edilen siparişdir: gönderilmediği için kopya yoktur. Tam boy demoda sayım özeti
  `ordersWithBillingAddress` = 16 / 17.
- **Minimum stok eşikleri (#137):** siparişlerden sonraki gerçek bakiyelerden (deterministik; SKU sırası, önce demonun
  düşük stoklu kalemleri) `POST /inventory/thresholds/bulk` ile, tam sayı (adet/takım/kutu):
  - 4 varyant tüm depolarda kritik (satılabilir ≤ minimum), biri tam eşit (ör. LED `VID-ISL-2K` 2 / min. 2);
  - bunlardan biri ikinci depoda (Motor: Dükkân, LED: Ankara Şube) stoğunun altında bir depo eşiği alır → genelde ve
    ana depoda kritik, o depoda kritik değil;
  - 2 varyantta stoğun yarısı kadar minimum (tanımlı ama kritik değil);
  - eşiksiz ve 0 stoklu bir varyant (Motor `KPR-NMAX155-AYN`, LED `KAB-P391-IC-500-AS`; hiç mal kabulü yapılmaz,
    siparişte kullanılmaz): raporda yalnızca "stokta olmayanlar dahil" ile görünür.
  Pano "Kritik stok" kutucuğu ve rapor bu dört ürünü gösterir. Sayım özeti: `stockThresholds` 7, `criticalStock` 4,
  `criticalStockWithOutOfStock` 5 (her iki demo). Eşikler kiracı ilk kurulurken yazılır; ikinci koşu hiçbir şey
  değiştirmez.
- **Türkçe küçük harf (#136):** ürün adlarındaki kullanım etiketi `toLocaleLowerCase("tr-TR")` ile küçültülür
  ("İç mekân" → "iç mekân"). SKU / mesaj eşleştirmedeki `toLowerCase()` bilinçli olarak kalır: kodlar ASCII'dir ve
  `tr-TR` ile `VID-ISL` → `vıd-ısl` olur, eşleşme bozulur.
- **"Kalanı kapat" sonrası etiket (#133):** API siparişi `shipped` yapar ama `fulfillmentStatus = partial` bırakır
  (kapatılan kalan hiç sevk edilmedi; bunun `full` sayılıp sayılmayacağı ürün kararı, açık). Veri olduğu gibi
  kalır; arayüz `shipped`/`completed` + `partial` durumunu "Kısmi sevk" yerine **"Sevk Edildi · Kalan kapatıldı"**
  rozetleriyle gösterir (liste ve detay). Sevkiyat yalnızca kalan kalmadığında `shipped` yaptığı için bu birleşim
  yalnızca `close-remaining` sonrası oluşur.

## Uçtan uca demo senaryoları (gerçek yığın, mock yok)

`apps/web/e2e-local/demo-genel.spec.ts`, `demo-motor.spec.ts`, `demo-led.spec.ts` — her biri demonun küçük sürümünü
(`--size small`) kendi benzersiz kiracısıyla (ve genelde benzersiz ekibiyle) API'den kurar, sonra hikâyeyi web
arayüzünde yürür. `npm --prefix apps/web run test:e2e:demo` (yalnız demolar) veya `test:e2e:real` (CI `e2e real stack`
işi, tümü). Tam boy demoların hepsi ayrıca CI `demo seed (twice)` işinde (`--demo all`) iki kez kurulur; ikinci
koşu hiçbir şeyi değiştirmemeli, genel demonun özellik kapsamı (ekip, Acil görev, yoldaki ve farklı transfer, açık
sayım, kritik stok, sipariş durumları) sayım özetinden doğrulanır. Veri modülü yığınsız Vitest ile test edilir
(`src/test/demo-datasets.test.ts`: deterministiklik, SKU/barkod tekilliği, referans bütünlüğü, sentetik iletişim).

- **Genel (varsayılan):** sahip + davetli ekip ile küçük demo → sayım özeti (4 rol, Acil/Yüksek/Düşük görev, 1 yolda +
  1 farkla kapatılmış transfer, 1 açık sayım, 4 kritik stok) → sahip `/depo/gorevler` "Tüm görevler"de Acil görevi,
  atanan depo görevlisini ve istenen teslim tarihini görür → `/depo/transferler`'de "Yolda" ve "Farkla kapatıldı · Fark
  var" → `/depo/sayimlar`'da açık sayım → depo görevlisi ayrı oturumda doğrudan `/depo/gorevler`e iner, "Görevlerim"de
  Acil görev ve "Toplamaya devam et".

- **Motor:** bayi (Bayi A) WhatsApp'tan fren seti ister → inceleme (SKU satırları önerilir ve onaylanır, "cg 125 arka
  balata" belirsiz → elle Muadil seçilir; ekranda fiyat yok) → taslak, fiyatlar Bayi A listesinden (API teklifiyle
  aynı, perakendeden düşük) → onaya gönder + onayla (rezervasyon /stok'ta) → raf + barkodla toplama (Orijinal balata
  okutulunca "Yanlış ürün") → paket kontrolü → sevk → stok raftan düştü, rezervasyon tüketildi.
- **LED:** tedarikçi kataloğu (XLSX) Imports API ile toplu içe aktarılır, ürün listesinde görünür → varsayılan
  listeye arayüzden 20+ kademesi → raflı mal kabul (barkod) → 25 adetlik sipariş kademe fiyatını alır → toplamada 3
  adet "Hasarlı" eksik → kısmi sevk → kalanı kapatma → /stok (rafta 18, ayrılmış 0) → "En çok sipariş edilenler"
  raporu CSV ve XLSX indirilir (gerçek dosya; CSV'de SKU var).

## Bilinen boşluklar (ürün)

1. **Seri numarası / garanti takibi yok (#57 kabul kriteri "Seri numaralı ürün senaryosu").** `serial_warranty`
   modülü katalogda "kullanılamaz"; seri no alanı, okutma, tekrar reddi ve garanti kaydı yok. Demo bunu taklit etmez:
   kontrol kartı ve video işlemcide yalnızca şablonun `seri_no_takibi = evet` bayrağı vardır. Senaryonun ihtiyacı:
   varyant düzeyinde "seri takipli" ayarı; mal kabulde seri no listesi (okutma/yapıştırma); toplama/paket
   kontrolünde her birim için seri okutma, aynı sevkiyatta tekrarın reddi (L-05) ve başka sevkiyatta sevk edilmiş seri
   uyarısı; sevkiyat satırında seri kaydı + müşteri/garanti başlangıcı; iade/değişimde seri ile arama
   (§2.4 "SN: … arızalı"); seri bazlı stok hareketi raporu. Bunlar gelince E2E'ye "25 kart = 25 farklı seri" adımı
   eklenir.
2. ~~Personel daveti yok~~ — #129 ile davet API'si var; **genel demo** müdür, satışçı ve depo görevlisini davet eder
   (her biri kaydolup kabul eder) ve görevleri depo görevlisine atar. Motor/LED örnekleri bilinçli olarak tek sahip
   kullanıcıyla kalır (sahip süpervizör yetkisiyle görevi kendine atar).
3. ~~İçe aktarma ekranı yok~~ — #94 ile `/ice-aktarma` sihirbazı ve geçmişi var (gerçek yığın:
   `real-stack-imports.spec.ts`). Seeder ve LED demo senaryosu kataloğu yine Imports API ile yükler (aynı yardımcı,
   `demo seed (twice)` ile ortak).
4. ~~Kalanı kapatma için web eylemi yok~~ — #129 ile sipariş detayında "Kalanı kapat" var; LED senaryosu bu adımı
   arayüzden yapar. Açık kalan: kapatılan kalan sonrası `fulfillmentStatus` değeri (yukarıda, #133).
