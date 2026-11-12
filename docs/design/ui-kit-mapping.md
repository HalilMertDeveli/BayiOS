# UI kit ↔ BayiOS ekran eşleştirmesi (#222)

Kit: "AI Inventory Management Web App UI Kit" — Lukáš Marek (Figma Community), CC BY 4.0, değiştirilerek kullanılır.
Referans PNG'ler sahibin asset paketindedir (`web-ekranlar/` 1440 px, `mobil-ekranlar/` 375 px); repoya ve uygulamaya
gömülmez. Ekran görüntüleri: `docs/design/screenshots/web/` (1440, açık + koyu), `docs/design/screenshots/mobile/`
(375, açık + koyu).

Durum: **Kit** = kit ekranının düzeni uygulandı · **Türetildi** = kit'te karşılığı yok, en yakın kit stilinden
(kart başlığı, tablo, form, liste kartı) türetildi · **Yok** = BayiOS'ta bu iş yok, ekran yapılmadı.

## Web (`apps/web`, sol sidebar + üst bar, 1440 referans, 768–1024 duyarlı)

| BayiOS ekranı | Rota | Kit referansı | Durum |
|---|---|---|---|
| Uygulama kabuğu (sidebar, üst bar, menü çekmecesi) | tümü | 104 dashboard (sidebar, "Hello" bar), 34/37/43 nav items, 45 top bar | Kit (arama/takvim/zil düğmeleri yok — BayiOS'ta karşılığı yok, işlevsiz düğme eklenmedi) |
| Giriş | `/giris` | 106/208 sign in | Kit (Google/Facebook ile giriş yok — BayiOS desteklemiyor) |
| Kayıt | `/kayit` | 112/214 sign up | Kit |
| Şifre sıfırlama / yeni şifre | `/sifre-sifirla`, `/sifre-yenile` | 196 reset, 204 create new password | Kit (kart düzeni; OTP adımı BayiOS'ta yok) |
| Davet, işletme oluştur/seç | `/davet`, `/isletme-olustur`, `/isletme-sec` | sign-in kartı | Türetildi |
| Pano | `/pano` | 104/206 dashboard | Kit kart başlığı + KPI paneli; grafikler BayiOS verisinin olduğu yerde (bugünkü siparişler durum çubuğu). Kit'teki satış/alış grafiklerinin verisi API'de yok → eklenmedi |
| Siparişler (liste) | `/siparisler` | 172/274 sales orders | Kit tablo (yeşil sipariş no, müşteri avatarı, durum rozetleri); KPI kartları eklenmedi (sayfa verisinden üretmek yanıltıcı olurdu) |
| Sipariş detayı | `/siparisler/[id]` | 175 create sales order (kart/form dili) | Türetildi |
| Sipariş oluştur | `/siparisler/yeni` | 175/277 create new sales order | Kit form kartları |
| WhatsApp'tan sipariş | `/siparisler/whatsapp…` | — | Türetildi |
| Ürünler (liste/detay/yeni, marka-kategori-birim) | `/urunler…` | 118/220 active items, 120/222 add new item | Kit tablo + form |
| Stok, varyant stoğu, düzeltme, raf taşıma | `/stok…` | 164 inventory adjustments, 167 create adjustment | Kit |
| Depo, mal kabul, görevler/toplama, sevkiyat, transfer, sayım | `/depo…` | 122 purchase receives, 168/170 inventory count | Kit tablo/form; toplama ekranı türetildi |
| Müşteriler (liste/detay/yeni, gruplar) | `/musteriler…` | 138/240 customers, 141/243 create customer | Kit |
| Fiyat listeleri | `/fiyat-listeleri…` | — | Türetildi (tablo/form) |
| Raporlar | `/raporlar…` | 134/236 report, 180/183 sales analysis | Kit kart/tablo; grafik içeriği mevcut rapor verisiyle sınırlı |
| İçe aktarma | `/ice-aktarma…` | 103/59 file upload area | Kit yükleme alanı dili |
| Ayarlar (+ ekip, iş kuralları, özel alanlar, paket) | `/ayarlar…` | 146/154–160 settings | Kit kartlar; **Hakkında** bölümü + kredi satırı eklendi |
| Kurulum sihirbazı | `/kurulum` | — | Türetildi |
| Tasarım sistemi vitrini | `/tasarim-sistemi` | bileşen önizlemeleri | Kit bileşenleri |
| **Faturalar** | — | 142/244 invoices, 144/246 create invoice | **Yok** — BayiOS API'de fatura modülü yok (`Billing` = abonelik); e-Fatura #143'te. Sahibine soruldu |
| Giderler, belgeler, entegrasyonlar, satış fişleri, alınan ödemeler, tedarikçiler, satın alma siparişleri | — | 126, 130, 162, 176, 184, 188, 192 | **Yok** — BayiOS kapsamında değil |

## Mobil (`apps/mobile`, depo uygulaması, 375, tek sütun + alt gezinme)
Kit'in mobil ekranları web'in duyarlı hâlidir; alt gezinme çubuğu kit'te yok, kit kart/başlık stilinden türetildi.
Ayrıntılı liste mobil bölümünde (bkz. PR açıklaması ve `docs/design/screenshots/mobile/`).
