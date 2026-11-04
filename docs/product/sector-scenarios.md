# BayiOS — Sektör Senaryoları

Tüm işletmeler, kişiler, telefonlar, ürün kodları ve fiyatlar **sentetiktir**. Gerçek marka adları yalnızca ürün uyumluluğunu tarif etmek için genel kategori örneği olarak kullanılır; gerçek fiyat listesi içermez.

İlke: Sektör farkları yalnızca **sektör şablonu, özel alan, birim, rol, kural ve modül** ile karşılanır. Aşağıdaki hiçbir senaryo tenant'a özel kod gerektirmez.

---

## 1. Motosiklet yedek parça bayisi

### 1.1 İşletme profili (sentetik)
| Alan | Değer |
|---|---|
| İşletme | Yıldız Moto Yedek Parça Ltd. (sentetik) |
| Ölçek | 9 çalışan, 2 depo (Merkez, Dükkân), ~8.500 aktif SKU |
| Müşteri | ~350 bayi/tamirhane (3 bayi grubu: A, B, Perakende) |
| Sipariş kanalı | %75 WhatsApp, %20 telefon, %5 dükkân |
| Paket | Profesyonel + AI WhatsApp asistanı |

### 1.2 Sektör şablonu
**Özel alanlar**
| Alan | Tip | Filtre | Örnek |
|---|---|---|---|
| Motosiklet markası | Çoklu seçim (uyumluluk satırında) | Evet | Honda, Yamaha, Bajaj, Mondial, Kuba |
| Model | Metin (uyumluluk satırında) | Evet | CG 125, NMAX 155, Pulsar NS200 |
| Model yılı aralığı | Sayı (başlangıç–bitiş) | Evet | 2015–2021 |
| OEM / orijinal parça no | Metin (alternatif kod) | Aramada | 90001-SNT-001 (sentetik format) |
| Parça tipi | Seçim | Evet | Orijinal, Muadil, Yan sanayi |
| Taraf/konum | Seçim | Evet | Ön, Arka, Sağ, Sol |
| Garanti (ay) | Sayı | Hayır | 6 |

**Kategoriler (şablon):** Motor, Debriyaj, Fren, Elektrik, Aktarma (zincir-dişli), Süspansiyon, Kaporta, Filtre & Yağ, Lastik & Jant, Aksesuar.

**Birimler:** adet, takım (ör. balata takımı = 1 set), kutu (ör. 1 kutu buji = 10 adet), litre (motor yağı), metre (kablo).

**Uyumluluk modeli:** Bir ürün birden çok (marka, model, yıl aralığı) satırına bağlanır. Arama "CG 125 2018 fren balatası" ifadesinde uyumluluk + kategori + ad birlikte kullanılır.

### 1.3 Uçtan uca senaryo — "Sabah WhatsApp siparişi"

**Örnek mesaj 1 (bayi usta, kısaltmalı):**
```
Selamun aleyküm abi
cg 125 ön balata 3 takım
nmax zincir dişli seti 1
ns200 buji 2 kutu
bir de geçen seferki fiyattan olsun
```

**Adımlar**
1. Elif (sales) mesajı WhatsApp Web'den kopyalar, BayiOS "WhatsApp'tan sipariş" ekranına yapıştırır, müşteri olarak "Usta Ali Motor (sentetik)" seçer.
2. AI ayrıştırma sonucu:

| Satır | Mesaj | Ayrıştırma | Eşleşme | Durum |
|---|---|---|---|---|
| 1 | cg 125 ön balata 3 takım | ürün: ön fren balatası, model: CG 125, miktar 3, birim takım | 2 aday: `FRN-CG125-ON-OR` (orijinal), `FRN-CG125-ON-MU` (muadil) | **Belirsiz** |
| 2 | nmax zincir dişli seti 1 | ürün: zincir-dişli seti, model: NMAX, miktar 1 | `AKT-NMAX155-ZDS` | **Kesin** |
| 3 | ns200 buji 2 kutu | ürün: buji, model: Pulsar NS200, miktar 2, birim kutu | `ELK-BUJI-NS200` (1 kutu = 10 adet) | **Kesin** |
| 4 | geçen seferki fiyattan olsun | fiyat ifadesi | — | **Not** (fiyat değiştirilmez) |

3. Elif satır 1 için müşterinin geçmişine bakar (son siparişte muadil almış), muadil seçer. Sistem bu seçimi "cg 125 ön balata → muadil" eş anlamlı ipucu olarak (tenant içi) saklar.
4. Fiyatlar müşterinin "Bayi A" listesinden gelir; satılabilir stok satır içinde görünür (ör. `FRN-CG125-ON-MU`: satılabilir 14).
5. Taslak sipariş kaydedilir → Elif "Onaya gönder" veya (tenant ayarı izin veriyorsa) doğrudan "Onayla".
6. Onayda rezervasyon: satılabilir 14 → 11 (3 takım rezerve).
7. Toplama görevi oluşur, havuza düşer. Yusuf (warehouse_staff) görevi alır; liste raf sırasıyla gelir: `A-02-1` (balata), `C-05-3` (zincir seti), `E-01-2` (buji).
8. Yusuf `FRN-CG125-ON-OR` (orijinal) kutusunu okutur → **hata sesi + "Yanlış ürün: siparişte muadil var"**. Doğru kutuyu okutur → yeşil.
9. Buji rafında yalnızca 1 kutu var → "Eksik bildir: 1 kutu eksik". Murat (manager) bildirim alır, Dükkân deposunda 3 kutu görür, transfer başlatır ya da kalan için kısmi sevk kararı verir.
10. Paketlendi → Sevk Edildi (kargo firması, takip no). Fiziksel stok düşer, rezervasyon kapanır.

### 1.4 Ek örnek mesajlar (test verisi)
```
abi yamaha ybr 125 2016 arka amortisör sağ sol 1er tane
```
→ Taraf alanı ile 2 ürün (sağ/sol) veya 1 takım; katalogda takım yoksa 2 satır önerilir.

```
90001-SNT-001 den 5 adet
```
→ OEM alternatif kod tam eşleşme → Kesin.

```
mondial 100 karbüratör tamir takımı 2 + 1 lt 10w40 yağ 12 tane
```
→ Tek satırda iki ürün; "1 lt" ürün varyantı (ambalaj), "12 tane" miktar; birim adet.

```
honda activa var mı stokta, varsa ayna takımı 4
```
→ Soru + sipariş karışık; yalnızca sipariş kısmı satırlaşır, soru not olarak kalır.

### 1.5 Kenar durumları
| # | Durum | Beklenen davranış |
|---|---|---|
| M-01 | Aynı model için orijinal + muadil (belirsiz) | Belirsiz; adaylar fiyat ve satılabilir stok ile listelenir; müşterinin son tercihi işaretlenir |
| M-02 | Model yılı mesajda yok, ürün yıl aralığına göre ayrışıyor | Belirsiz; yıl aralıkları adaylarda gösterilir |
| M-03 | Model katalogda yok ("cg 150 kuba") | Eşleşmedi; kullanıcı elle arar veya satırı siler; AI ürün yaratmaz |
| M-04 | Birim çelişkisi: "2 kutu" ama ürün yalnızca adet satılıyor | Uyarı: birim dönüşümü tanımlı değil; kullanıcı birim seçer |
| M-05 | Takım vs. tek ("balata 3" — takım mı adet mi?) | Ürünün varsayılan satış birimi kullanılır, satır "birim teyidi" etiketi alır |
| M-06 | Mesajda fiyat ("tanesi 150 olsun") | Fiyat değiştirilmez; not olarak eklenir; fiyat değişikliği yetkiye bağlı manuel işlem |
| M-07 | Sağ/sol çift parça | Taraf özel alanı; ikisi istenmişse 2 satır |
| M-08 | Aynı mesaj iki kez yapıştırılır | Aynı müşteri + aynı metin hash'i son 24 saatte varsa uyarı: "Bu mesajdan oluşturulmuş taslak var: SIP-…" |
| M-09 | Satılabilir yetersiz (rafta var ama başka siparişe rezerve) | Onay engellenir (politika "izin verme"); satırda "Rezerve: 12, Satılabilir: 0" açıklaması |

---

## 2. LED ekran / elektronik toptancısı

### 2.1 İşletme profili (sentetik)
| Alan | Değer |
|---|---|
| İşletme | Parlak Ekran Teknoloji A.Ş. (sentetik) |
| Ölçek | 14 çalışan, 3 depo (İstanbul Merkez, Ankara Şube, Hasarlı/İade), ~3.200 SKU |
| Müşteri | ~180 kurulum firması, reklam ajansı, sistem entegratörü |
| Ürünler | LED modül, kabinet, kontrol kartı (gönderici/alıcı), güç kaynağı, kablo (metre), video işlemci |
| Paket | Profesyonel + AI WhatsApp asistanı (+ Seri/Garanti ilgisi yüksek — bkz. PRD AQ-03) |

### 2.2 Sektör şablonu
**Özel alanlar**
| Alan | Tip | Filtre | Örnek |
|---|---|---|---|
| Pixel pitch (mm) | Ondalık seçim | Evet | P1.86, P2.5, P3.91, P4, P10 |
| Kullanım | Seçim | Evet | İç mekân, Dış mekân, Yarı dış |
| Modül ölçüsü (mm) | Metin/seçim | Evet | 320×160, 250×250, 500×500 |
| Çözünürlük (piksel) | Metin | Hayır | 128×64 |
| Parlaklık (nit) | Sayı | Evet | 800, 5500 |
| Tarama (scan) | Seçim | Evet | 1/8, 1/16, 1/32 |
| Kontrol kartı uyumu | Çoklu seçim | Evet | Gönderici/alıcı kart ailesi (genel tanım) |
| LED tipi | Seçim | Evet | SMD2121, SMD1515, DIP |
| Güç (W) | Sayı | Hayır | 40 |
| Seri no takibi | Evet/hayır | Hayır | Evet (kontrol kartı, video işlemci) |

**Kategoriler:** LED Modül, Kabinet, Kontrol Kartı, Video İşlemci, Güç Kaynağı, Kablo & Konnektör, Montaj Aparatı, Yedek LED/Lamba.

**Birimler:** adet, kutu (ör. 1 kutu = 20 modül), koli, metre (kablo), m² (bilgi amaçlı hesap — P2).

### 2.3 Uçtan uca senaryo — "Proje siparişi + seri numaralı kartlar"

**Örnek mesaj 2 (sistem entegratörü):**
```
Merhaba Selin Hanım,
AVM projesi için:
- P3.91 iç mekan modül 500x500 kabinet 24 adet
- P2.5 modül 320x160 2 kutu (yedek)
- alıcı kart 24
- gönderici kart 1
- 5V 40A güç kaynağı 30 adet
- flat kablo 50 metre
Teslim cuma olursa iyi olur. Teşekkürler
```

**Adımlar**
1. Elif (sales) mesajı yapıştırır, müşteri "Mavi Sahne Entegrasyon (sentetik)".
2. AI ayrıştırma:

| Satır | Ayrıştırma | Eşleşme | Durum |
|---|---|---|---|
| 1 | Kabinet, P3.91, iç mekân, 500×500, 24 adet | `KAB-P391-IC-500` | Kesin |
| 2 | Modül, P2.5, 320×160, 2 kutu | `MOD-P25-IC-320` (iç) ve `MOD-P25-DS-320` (dış) | **Belirsiz** (kullanım belirtilmemiş) |
| 3 | Alıcı kart, 24 | 2 kart ailesi aday | **Belirsiz** |
| 4 | Gönderici kart, 1 | 3 aday | **Belirsiz** |
| 5 | Güç kaynağı 5V 40A, 30 | `PSU-5V40A` | Kesin |
| 6 | Flat kablo, 50 metre | `KBL-FLAT-16P` (birim metre) | Kesin |
| — | "Teslim cuma" | Teslim tarihi önerisi (en yakın cuma) | Kullanıcı onayı |

3. Elif belirsiz satırları seçer: kabinetle uyumlu kart ailesi (özel alan "kontrol kartı uyumu" eşleşmesi adayları sıralar).
4. Satılabilir kontrol: `KAB-P391-IC-500` İstanbul'da 18, Ankara'da 10. Onay ekranı uyarır: "İstanbul deposunda 6 eksik". Murat transfer başlatır (Ankara → İstanbul, 6 adet) veya sipariş deposu bazında bölünür (P1).
5. Onay → rezervasyon. Toplama görevi.
6. Toplamada kontrol kartları **seri numaralı**: Yusuf her kartın seri barkodunu okutur (MVP'de seri no sevk satırına serbest kayıt olarak eklenir; tam seri/garanti modülü P2 — AQ-03).
7. Paket kontrolü: 25 kart = 25 farklı seri no; tekrar okutulan seri no reddedilir.
8. Sevk: koli sayısı 9, kargo/ambar bilgisi. Fiziksel düşüm.

### 2.4 Ek örnek mesajlar (test verisi)
```
p10 dış mekan modül 1/4 scan 40 tane fiyat atar mısın
```
→ Fiyat sorusu: taslak sipariş + not; AI fiyat yazmaz, fiyat fiyat listesinden ekranda görünür, yanıtı insan verir.

```
geçen ay aldığımız video işlemcinin aynısından 2
```
→ Müşteri geçmişinde son video işlemci ürünü aday olarak önerilir (Belirsiz, tek aday) — kullanıcı teyidi zorunlu.

```
SN: LX24A0098812 arızalı geldi değişim
```
→ Sipariş değil, iade/değişim talebi; ayrıştırıcı "sipariş satırı bulunamadı — iade olabilir" der; kullanıcı iade akışına yönlendirilir.

### 2.5 Kenar durumları
| # | Durum | Beklenen davranış |
|---|---|---|
| L-01 | Pixel pitch yazım çeşitliliği ("p3.91", "P 3,91", "3.9") | Normalizasyon; ondalık virgül/nokta eşdeğer; en yakın değer **önerilir**, kesin sayılmaz |
| L-02 | İç/dış mekân belirtilmemiş | Belirsiz; özel alan farkı adaylarda vurgulanır |
| L-03 | Kutu ↔ adet (1 kutu = 20 modül) | Birim dönüşümüyle rezervasyon temel birimde (40 adet) |
| L-04 | Metre ile satılan kablo, ondalık (12,5 m) | Ondalık miktar izinli (birim ayarı); stok ondalık |
| L-05 | Seri numarası tekrar okutma | Aynı sevkiyatta reddedilir; farklı sevkiyatta daha önce sevk edilmiş seri uyarısı (P2) |
| L-06 | Hasarlı iade | İade "hasarlı" → Hasarlı/İade deposuna; satılabilir stoğa girmez |
| L-07 | Proje siparişi çok depolu | MVP: sipariş tek depodan; eksik transferle tamamlanır. Sipariş bölme P1 |
| L-08 | Tedarikçi Excel'inde teknik değerler tek kolonda ("P3.91/500x500/İç") | İçe aktarmada kolon bölme yok (MVP); hata satırı + şablon önerisi; AI ayrıştırma ile kolon bölme P2 |

---

## 3. Sektörler arası karşılaştırma

| Konu | Motosiklet yedek parça | LED / elektronik | Ortak çözüm |
|---|---|---|---|
| Ürün kimliği | OEM no, model uyumluluğu | Teknik özellik kombinasyonu | Alternatif kod + özel alan + uyumluluk |
| Belirsizlik kaynağı | Model yılı, orijinal/muadil, taraf | İç/dış, pitch, kart ailesi | Belirsiz satır + aday listesi + insan seçimi |
| Birimler | adet, takım, kutu, litre | adet, kutu, metre | Birim dönüşüm tablosu |
| Seri no | Nadiren | Kart, işlemci için yaygın | MVP: sevk satırında serbest kayıt; modül P2 |
| Sipariş boyutu | Küçük, sık (3–15 satır) | Büyük, seyrek (5–40 satır), proje | Aynı sipariş akışı |
| İade | Yanlış parça (model uyumsuz) | Arızalı / hasarlı | İade sebep kodu + hasarlı depo |
| Kritik ekran | WhatsApp taslak ekranı, uyumluluk araması | İçe aktarma, teknik filtre | — |

## 4. Gelecek sektörler (şablon hazırlığı)
| Sektör | Ek özel alan / birim ihtiyacı |
|---|---|
| Hırdavat | Ölçü (mm, inç), malzeme; birim: kg, paket |
| Elektrik malzemesi | Kesit (mm²), akım (A), IP sınıfı; birim: metre, makara |
| Oto yedek parça | Motosiklet şablonunun genişletilmişi (araç marka/model/motor kodu/yıl) |
| Giyim | Beden/renk varyant matrisi; birim: adet, seri (asorti) |
