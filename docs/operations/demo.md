# Pilot demoları (sentetik) — #56 Motor yedek parça, #57 LED/elektronik

İki tamamen **sentetik** işletme, yalnızca **public BayiOS API** üzerinden ve işletme sahibinin gerçek Supabase Auth
oturumuyla kurulur. Hiçbir iş tablosuna doğrudan yazılmaz; hiçbir gerçek firma, kişi, telefon, adres, fiyat listesi
kullanılmaz. Veri tohumlu RNG ile **deterministiktir**: aynı komut her zaman aynı kataloğu, fiyatları, stoğu ve
siparişleri üretir.

| | Demo Motor Yedek Parça (#56) | Demo LED Elektronik (#57) |
|---|---|---|
| Slug | `demo-motor-yedek-parca` | `demo-led-elektronik` |
| Sektör şablonu | `motorcycle_parts` | `led_electronics` |
| Sahip (yalnız yerel) | `sahip@demo-motor.example.test` | `sahip@demo-led.example.test` |

## Çalıştırma (tek komut)

```bash
bash infra/ci/e2e-stack.sh up              # yerel gerçek yığın: Supabase Auth + Postgres 17 + EF migration + API (+ web)
npm --prefix apps/web run demo:seed         # iki demo; --demo motor|led, --size full|small
```

- Yalnızca yerel yığına çalışır: API/Auth/Mailpit adresi `localhost`/`127.0.0.1` değilse komut reddeder.
- Seeder sıfır bağımlılıklı Node betiğidir (`apps/web/e2e-local/demo/seed.mjs`, Node 24); `npm ci` gerekmez.
- Publishable key (yerel geliştirme anahtarı, gizli değil) sırayla `SUPABASE_PUBLISHABLE_KEY` ortam değişkeninden,
  `apps/web/.env.local` dosyasından veya `npx -y supabase@2.122.1 status` çıktısından okunur.
- Varsayılan adresler: API `http://localhost:5280` (`E2E_API_URL`), Auth `http://127.0.0.1:54321` (`SUPABASE_URL`),
  Mailpit `http://127.0.0.1:54324` (`MAILPIT_URL`). Web portu 3200 doluysa yığını `E2E_WEB_PORT=3300` ile başlatın
  (API CORS'u o porta göre ayarlanır) ve tarayıcıda o portu açın.
- `--suffix x1` aynı demonun ayrı bir kopyasını kurar (slug, ad ve sahip e-postasına ek gelir) — gerçek demoya
  dokunmadan seeder'ı denemek için.
- Süre (yerel, tam boy): demo başına ~8 sn; tekrar çalıştırma < 1 sn.

### Demo girişleri

Giriş: `http://localhost:3200/giris` (veya `E2E_WEB_PORT`). Şifreler ilk çalıştırmada **rastgele üretilir** ve
git'e girmeyen `.demo/credentials.json` dosyasına yazılır (`.gitignore`: `.demo/`). `DEMO_PASSWORD` ortam değişkeni
tüm demo kullanıcıları için şifreyi sabitler (yalnız yerelde). Bu hesaplar sentetiktir; başka hiçbir yerde kullanılmaz,
gerçek kimlik bilgisi repoya asla girmez.

### Tekrar çalıştırma (idempotent) ve sıfırlama

- Sahip kullanıcı zaten varsa giriş yapılır; aynı slug'lı demo kiracısı **tamsa hiçbir şey değişmez** ("already
  seeded — nothing changed") ve sayımlar yeniden raporlanır.
- Yarıda kalmış (eksik) bir demo kiracısı bulunursa seeder durur: kiracı silme API'si yoktur ve yarım veriyi tamamlamak
  deterministikliği bozar. **Sıfırlama:** iş verisi yığının geçici Postgres'indedir → `bash infra/ci/e2e-stack.sh up`
  (Postgres konteyneri her `up`ta sıfırdan kurulur) ve tekrar `demo:seed`. Auth kullanıcıları yerel Supabase'de kalır
  ve yeniden kullanılır; onları da silmek için `E2E_SUPABASE_NO_BACKUP=1 bash infra/ci/e2e-stack.sh down`.
- `.demo/credentials.json` silinirse mevcut kullanıcıya yeni şifreyle girilemez: dosyayı geri getirin veya Auth'u
  yukarıdaki gibi sıfırlayın.
- Seeder mevcut (tam) bir demoyu **güncellemez**. #136 öncesi kurulmuş bir demoda LED ürün adları eski küçük harfle
  ("i̇ç mekân") ve siparişler fatura adresi olmadan kalır; yeni veri için yukarıdaki gibi sıfırlayıp yeniden kurun.

## Demo içeriği (tam boy, `--size full`)

| | Motor | LED |
|---|---|---|
| Ürün (API + içe aktarma) | 77 (69 + 8 XLSX) | 62 (47 + 15 XLSX) |
| Varyant / barkod | 120 / 120 (GS1 mağaza içi aralık 200, gerçek ürünle çakışmaz) | 70 / 70 (aralık 201) |
| Özel alan değerleri | uyumlu marka (çoklu), model, model yılı aralığı, OEM no (`90001-SNT-###`), taraf, garanti; varyantta parça tipi (Orijinal/Muadil/Yan sanayi) | pixel pitch, kullanım, modül ölçüsü, parlaklık, tarama, LED tipi, güç, voltaj, IP sınıfı, kart ailesi uyumu, seri no takibi bayrağı |
| Depo / raf | Merkez Depo (raflı, 9 raf, varsayılan) + Dükkân | İstanbul Merkez (raflı, 8 raf, varsayılan) + Ankara Şube + Hasarlı/İade |
| Fiyat listeleri | Perakende (varsayılan) + Bayi A (%12, fren balatasında 10+ kademe) + Bayi B (%6) | Liste fiyatı (varsayılan; modüllerde 50+ / 200+ kademe) + Entegratör (%8) |
| Bayi grupları / müşteri (adresli) | Bayi A, Bayi B, Perakende / 12; her müşteride varsayılan teslimat ("Dükkân") + fatura ("Fatura") adresi | Entegratör, Ajans, Kurulum / 10; teslimat ("Merkez") + fatura ("Muhasebe") adresi |
| İçe aktarma | 1 XLSX (aksesuar/sarf; fiyat + garanti alanı), Imports API: yükle → eşleme → kuru doğrulama → çalıştır | 1 XLSX (güç kaynağı, kablo (metre), montaj, yedek LED; fiyat + teknik alanlar) |
| Stok | Mal kabulleriyle (ledger), kategori rafına; bazı kalemler kritik seviyede (2) | Aynı; kablo metre (ondalık) |
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
- **Türkçe küçük harf (#136):** ürün adlarındaki kullanım etiketi `toLocaleLowerCase("tr-TR")` ile küçültülür
  ("İç mekân" → "iç mekân"). SKU / mesaj eşleştirmedeki `toLowerCase()` bilinçli olarak kalır: kodlar ASCII'dir ve
  `tr-TR` ile `VID-ISL` → `vıd-ısl` olur, eşleşme bozulur.
- **"Kalanı kapat" sonrası etiket (#133):** API siparişi `shipped` yapar ama `fulfillmentStatus = partial` bırakır
  (kapatılan kalan hiç sevk edilmedi; bunun `full` sayılıp sayılmayacağı ürün kararı, açık). Veri olduğu gibi
  kalır; arayüz `shipped`/`completed` + `partial` durumunu "Kısmi sevk" yerine **"Sevk Edildi · Kalan kapatıldı"**
  rozetleriyle gösterir (liste ve detay). Sevkiyat yalnızca kalan kalmadığında `shipped` yaptığı için bu birleşim
  yalnızca `close-remaining` sonrası oluşur.

## Uçtan uca demo senaryoları (gerçek yığın, mock yok)

`apps/web/e2e-local/demo-motor.spec.ts`, `demo-led.spec.ts` — her biri demonun küçük sürümünü (`--size small`) kendi
benzersiz kiracısıyla API'den kurar, sonra hikâyeyi web arayüzünde yürür. `npm --prefix apps/web run test:e2e:demo`
(yalnız demolar) veya `test:e2e:real` (CI `e2e real stack` işi, tümü).

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
2. **Personel daveti yok** (#83): `sales`, `warehouse_staff`, `manager` kullanıcıları demo kiracısına eklenemez
   (`GET /members` var, davet/üyelik API'si yok). Demolar tek sahip kullanıcıyla çalışır; sahibin süpervizör yetkisiyle
   toplama görevi kendine atanır. Davet API'si gelince seeder her role bir sentetik kullanıcı ekleyecek şekilde
   genişletilmeli.
3. ~~İçe aktarma ekranı yok~~ — #94 ile `/ice-aktarma` sihirbazı ve geçmişi var (gerçek yığın:
   `real-stack-imports.spec.ts`). Seeder ve LED demo senaryosu kataloğu yine Imports API ile yükler (aynı yardımcı,
   `demo seed (twice)` ile ortak).
4. ~~Kalanı kapatma için web eylemi yok~~ — #129 ile sipariş detayında "Kalanı kapat" var; LED senaryosu bu adımı
   arayüzden yapar. Açık kalan: kapatılan kalan sonrası `fulfillmentStatus` değeri (yukarıda, #133).
