# BayiOS — Tasarım Sistemi Planı

| Alan | Değer |
|---|---|
| Sürüm | 0.1 (plan) |
| Hazırlayan | Product Manager (UX Designer ile birlikte sahiplenilecek) |
| Hedef | Web (Next.js) ve mobil (Flutter) için ortak token ve bileşen dili |
| Durum | Taslak — Hafta 2'de Figma'da ekran tasarımlarına dönüştürülecek |

## 1. UX ilkeleri
(Kaynak: `.claude/agents/ux-designer.md`, personalar)

1. **Az tıklama:** Sık işler (WhatsApp'tan sipariş, onay, okut-topla) en fazla 3 adım. Birincil eylem her ekranda tek ve belirgin.
2. **Sade sol menü:** En fazla 8 birinci seviye öğe; entitlement'ı olmayan modüller menüde "kilitli" değil, modül mağazasında görünür.
3. **Net durum etiketleri:** Her sipariş, görev, içe aktarma durumu renk + metin + ikon ile (yalnızca renge dayanmaz).
4. **Güçlü arama:** Global arama (Ctrl+K) — ürün (ad/SKU/barkod/OEM), sipariş no, müşteri, telefon.
5. **Hızlı tablolar:** Sunucu taraflı sayfalama/filtre, yoğun/rahat görünüm, sabit başlık, kolon seçimi, klavye gezinme.
6. **Mobilde büyük dokunma alanları:** ≥ 48×48 dp; birincil eylem ekranın altında başparmak bölgesinde; tek elle kullanım.
7. **Tutarlı boş / yükleniyor / hata durumları:** Her liste ve form için tanımlı; hata metni "ne oldu + ne yapmalı".
8. **WCAG 2.2 AA:** Kontrast, odak görünürlüğü, klavye erişimi, hedef boyutu, hareket azaltma.
9. **Türkçe, kısa, teknik olmayan dil:** "Rezervasyon" yerine kullanıcıya "Ayrıldı"; "Satılabilir" ve "Rafta" kavramları sabit sözlükle.
10. **Güven veren AI:** AI çıktısı her zaman öneri olarak işaretlenir (ikon + "Öneri" etiketi), kaynak metin yanında görünür; onay insanda.
11. **Geri alınabilirlik:** Mümkün olan yerde "Geri al" (toast içinde), geri alınamayan işlemlerde etki özeti + onay diyaloğu.

## 2. Tasarım token planı

Token isimlendirme: `kategori.rol.varyant` (ör. `color.text.muted`). Kaynak tek JSON (W3C Design Tokens formatı) → web için CSS değişkenleri, Flutter için `ThemeExtension` üretilir. Açık tema MVP; koyu tema altyapısı hazır, P2.

### 2.1 Renk
| Grup | Token örnekleri | Not |
|---|---|---|
| Marka | `color.brand.primary`, `.primary-hover`, `.primary-subtle` | Kurumsal, sakin ton (lacivert/petrol ailesi önerisi); kesin değer Figma'da |
| Nötr | `color.neutral.0…950` (11 adım) | Arka plan, kenarlık, metin |
| Metin | `color.text.primary`, `.secondary`, `.muted`, `.inverse`, `.link` | `primary` ve `secondary` beyaz üzerinde ≥ 4.5:1 |
| Yüzey | `color.surface.page`, `.card`, `.raised`, `.overlay`, `.sunken` | |
| Kenarlık | `color.border.default`, `.strong`, `.focus` | Odak halkası ≥ 3:1 |
| Geri bildirim | `color.success.*`, `.warning.*`, `.danger.*`, `.info.*` (her biri `fg`, `bg`, `border`) | |
| Stok | `color.stock.ok`, `.low` (kritik altı), `.out` (sıfır), `.reserved` | |
| AI | `color.ai.bg`, `.ai.border`, `.ai.fg` | AI önerilerini ayırt etmek için |

### 2.2 Sipariş ve görev durum renkleri
Her durum: rozet arka planı (`bg`), metin (`fg`), ikon. Metin/zemin kontrastı ≥ 4.5:1.

| Durum (API) | Etiket (TR) | Renk ailesi | İkon önerisi |
|---|---|---|---|
| Draft | Taslak | Nötr gri | kalem |
| PendingApproval | Onay Bekliyor | Amber (warning) | saat |
| Approved | Onaylandı | Mavi (info) | onay işareti |
| Picking | Toplanıyor | Mor | el arabası |
| Packed | Paketlendi | Camgöbeği (teal) | kutu |
| Shipped | Sevk Edildi | Lacivert (brand) | kamyon |
| Completed | Tamamlandı | Yeşil (success) | çift onay |
| Cancelled | İptal | Kırmızı (danger) — soluk | çarpı |
| (alt) PartiallyShipped | Kısmi Sevk | Mavi-amber | yarım kamyon |

| Görev / diğer | Etiket | Renk |
|---|---|---|
| Havuzda | Atanmadı | Nötr |
| Devam ediyor | Devam ediyor | Mor |
| Eksikli | Eksik var | Amber |
| Tamamlandı | Tamamlandı | Yeşil |
| AI satır: Kesin / Belirsiz / Eşleşmedi | Eşleşti / Seçim gerekli / Bulunamadı | Yeşil / Amber / Kırmızı |
| İçe aktarma: Kuyrukta / Çalışıyor / Tamamlandı / Hatalı / Geri alındı | — | Nötr / Mavi / Yeşil / Kırmızı / Gri |

### 2.3 Tipografi
- Yazı ailesi: Türkçe karakter desteği tam, açık kaynak sans-serif (öneri: **Inter**; mobilde aynı veya sistem fontu). Sayısal kolonlarda `tabular-nums`.
- Monospace (SKU, barkod, raf kodu): **JetBrains Mono** veya sistem mono.

| Token | Boyut / satır yüksekliği (web px) | Kullanım |
|---|---|---|
| `type.display` | 30 / 38, 600 | Pano başlıkları (seyrek) |
| `type.h1` | 24 / 32, 600 | Sayfa başlığı |
| `type.h2` | 20 / 28, 600 | Bölüm başlığı |
| `type.h3` | 16 / 24, 600 | Kart başlığı |
| `type.body` | 14 / 20, 400 | Varsayılan metin (web yoğun arayüz) |
| `type.body-lg` | 16 / 24, 400 | Formlar, mobil varsayılan |
| `type.caption` | 12 / 16, 400 | Yardımcı metin, tablo meta |
| `type.mono` | 13 / 20, 500 | Kodlar |
| Mobil ölçek | Tüm değerler +2 px; toplama ekranında miktar `32/40, 700` | Depo okunabilirliği |

### 2.4 Boşluk, yarıçap, gölge
| Token | Değerler |
|---|---|
| `space.*` (4 px tabanı) | 0, 2, 4, 8, 12, 16, 20, 24, 32, 40, 48, 64 |
| `radius.*` | `none` 0, `sm` 4, `md` 6, `lg` 8, `xl` 12, `full` 9999 |
| `elevation.*` | `0` yok, `1` kart (çok hafif), `2` açılır menü/popover, `3` diyalog, `4` toast |
| `size.control.*` | `sm` 32, `md` 36 (web varsayılan), `lg` 44; mobil min 48 dp |
| `motion.*` | `fast` 120 ms, `base` 200 ms, `slow` 300 ms; `prefers-reduced-motion` ile devre dışı |
| `z.*` | `dropdown`, `sticky`, `drawer`, `modal`, `toast` |

## 3. Çekirdek bileşenler

| Bileşen | Varyantlar / durumlar | Web | Flutter | Not |
|---|---|---|---|---|
| Button | primary, secondary, ghost, danger; sm/md/lg; loading, disabled, ikonlu | ✓ | ✓ | Tek birincil buton kuralı |
| IconButton | — | ✓ | ✓ | `aria-label` zorunlu |
| Input (text, number, currency, search) | default, focus, error, disabled, readonly; prefix/suffix | ✓ | ✓ | `tr-TR` sayı biçimi; currency ₺ sonek |
| Select / Combobox | tekli, çoklu, aranabilir, asenkron (ürün arama) | ✓ | ✓ | Ürün seçicide SKU + stok + fiyat satırı |
| Checkbox, Radio, Switch | — | ✓ | ✓ | |
| DatePicker | tekil, aralık | ✓ | ✓ | GG.AA.YYYY |
| Table (basit) | — | ✓ | – | |
| DataGrid | sıralama, filtre, kolon seçimi, satır seçimi, toplu işlem çubuğu, sabit başlık, sanal kaydırma, satır içi düzenleme (sipariş satırları) | ✓ | – | 10k ürün performansı |
| List / ListTile | görev, ürün, stok satırı | – | ✓ | |
| Badge / StatusBadge | durum token'larından | ✓ | ✓ | Renk + metin + ikon |
| StockIndicator | satılabilir / rafta / ayrıldı | ✓ | ✓ | Ortak sözlük |
| Toast | success, error, info, undo eylemli | ✓ | ✓ (SnackBar) | 5 sn, geri al varsa 8 sn |
| Dialog / ConfirmDialog | standart, tehlikeli işlem (etki özeti) | ✓ | ✓ (BottomSheet) | |
| Drawer / SidePanel | ürün hızlı görünüm, filtre | ✓ | – | |
| EmptyState | ilk kullanım (eylem çağrısı), filtre sonucu boş, izin yok, modül kilitli | ✓ | ✓ | |
| Skeleton / Spinner | — | ✓ | ✓ | |
| ErrorState | ağ, yetki, bulunamadı | ✓ | ✓ | |
| PageHeader | başlık, breadcrumb, birincil + ikincil eylemler | ✓ | ✓ (AppBar) | |
| SidebarNav | daraltılabilir, rol/entitlement'a göre öğeler, tenant seçici | ✓ | – | |
| BottomNav | — | – | ✓ | Mobil ana gezinme |
| Tabs | — | ✓ | ✓ | Sipariş durum sekmeleri |
| Stepper / Wizard | onboarding, içe aktarma | ✓ | – | |
| FileDropzone | — | ✓ | – | |
| ProgressBar | belirli/belirsiz | ✓ | ✓ | İçe aktarma, toplama ilerlemesi |
| Timeline | sipariş zaman çizelgesi | ✓ | ✓ | |
| ScannerView | kamera önizleme, hedef çerçeve, fener, manuel giriş | – | ✓ | Ses + titreşim geri bildirimi |
| AISuggestion / MatchRow | Kesin / Belirsiz (aday listesi) / Eşleşmedi | ✓ | – | Kaynak metin yanında |
| KPI Card | — | ✓ | ✓ | Pano |
| Form layout | tek/iki kolon, bölümlü | ✓ | ✓ | |

## 4. Sayfa envanteri

### 4.1 Web (Next.js) — owner / admin / manager / sales / viewer
| # | Sayfa | Ana roller | Kritik bileşenler | Öncelik |
|---|---|---|---|---|
| W01 | Giriş / kayıt / şifre sıfırlama | Tümü | Form | P0 |
| W02 | Onboarding sihirbazı (sektör, işletme, ticari ayarlar, depo, veri, ekip) | owner | Stepper, şablon önizleme | P0 |
| W03 | Pano | owner, manager, sales | KPI kartları, bekleyen onaylar, kritik stok, görev özeti | P0 |
| W04 | Ürünler (liste) | Tümü | DataGrid, filtre paneli (özel alanlar), global arama | P0 |
| W05 | Ürün detay / düzenle | admin, manager | Form bölümleri, özel alanlar, uyumluluk tablosu, fiyatlar, depo stokları, hareketler, geçmiş | P0 |
| W06 | İçe aktarma sihirbazı + geçmiş | admin | FileDropzone, kolon eşleme, önizleme, hata tablosu, ilerleme | P0 |
| W07 | Stok (bakiye listesi) | manager, sales | DataGrid, StockIndicator, depo filtresi | P0 |
| W08 | Stok hareketleri | manager, viewer | DataGrid, dışa aktarma | P0 |
| W09 | Mal kabul (liste + form) | manager | Satır düzenleyici, barkod girişi | P0 |
| W10 | Sayım (liste + fark raporu) | manager | DataGrid, fark vurgusu | P0 |
| W11 | Transfer (liste + form) | manager | Form, durum | P0 (Pro) |
| W12 | Depolar ve raflar | admin, manager | Ağaç/liste, raf etiketi yazdırma (P1) | P0 |
| W13 | Siparişler (liste) | sales, manager | Durum sekmeleri, DataGrid | P0 |
| W14 | WhatsApp'tan sipariş | sales | Metin alanı, MatchRow tablosu, müşteri seçici | P0 |
| W15 | Sipariş detay / düzenle | sales, manager | Satır düzenleyici, toplamlar, KDV dökümü, Timeline, eylem çubuğu | P0 |
| W16 | Depo görevleri panosu | manager | Kolonlu görünüm (Atanmadı/Devam/Eksikli/Tamam), atama | P0 |
| W17 | Müşteriler (liste + detay) | sales | DataGrid, adresler, sipariş geçmişi | P0 |
| W18 | Müşteri grupları ve fiyat listeleri | admin | Form, ürün fiyat tablosu | P0 |
| W19 | Raporlar | owner, manager, viewer | Rapor kartları, filtre, dışa aktarma | P0 |
| W20 | Modül mağazası | owner, admin | Paket kartları, modül kartları, talep | P1 |
| W21 | Ayarlar: işletme, ticari (KDV, birim, para), kurallar, ekip & roller, güvenlik, veri dışa aktarma, denetim kaydı | owner, admin | Form, tablo | P0 |
| W22 | Bildirim merkezi | Tümü | Liste | P1 |

**Sol menü (öneri, ≤ 8):** Pano · Siparişler · Ürünler · Stok · Depo görevleri · Müşteriler · Raporlar · Ayarlar (Modül mağazası Ayarlar altında + profil menüsünde).

### 4.2 Mobil (Flutter) — warehouse_staff, manager
| # | Ekran | Kritik etkileşim | Öncelik |
|---|---|---|---|
| M01 | Giriş + tenant seçimi | Büyük alanlar, şifre göster | P0 |
| M02 | Görev listesi (Bana atanan / Havuz) | Çek-yenile, görev al, durum rozetleri | P0 |
| M03 | Toplama ekranı (tarayıcılı) | Raf sırası, büyük miktar göstergesi, ScannerView, doğru/yanlış geri bildirimi, "Eksik bildir", alt sabit birincil buton | P0 |
| M04 | Paket kontrolü + sevk onayı | İkinci okutma, koli sayısı, kargo/takip no | P0 (sevk) / P1 (kontrol) |
| M05 | Mal kabul | Barkod okut → satır, miktar, raf seçimi, bilinmeyen barkod akışı | P1 |
| M06 | Stok sorgulama | Barkod/SKU/ad arama, depo-raf stokları | P0 |
| M07 | Sayım | Raf okut → ürün okut → miktar | P0 |
| M08 | Transfer teslim alma | Okutarak teyit | P1 |
| M09 | Raf görünümü | Raf okut → içerik | P1 |
| M10 | Profil / çıkış / ayarlar (ses, titreşim, harici okuyucu) | — | P0 |

**Mobil gezinme:** BottomNav — Görevler · Tara (hızlı stok sorgu) · Sayım · Profil.

## 5. Duyarlı kırılım noktaları
| Token | Genişlik | Davranış (web) |
|---|---|---|
| `bp.xs` | 0–479 | Tek kolon; sol menü drawer; DataGrid → kart listesi; owner panosu okunabilir |
| `bp.sm` | 480–767 | Tek kolon, genişleyen formlar |
| `bp.md` | 768–1023 | Daraltılmış ikon menü; tablo yatay kaydırma (sabit ilk kolon) |
| `bp.lg` | 1024–1439 | Varsayılan masaüstü; tam menü |
| `bp.xl` | ≥ 1440 | Ayrıntı paneli yan yana (liste + detay) |

Flutter: telefon (< 600 dp) birincil; tablet (≥ 600 dp) iki bölmeli görev listesi + detay (P2). Yatay mod toplama ekranında desteklenir, zorunlu değil.

## 6. Erişilebilirlik kontrol listesi (WCAG 2.2 AA)
- [ ] Metin kontrastı ≥ 4.5:1, büyük metin ve UI bileşenleri ≥ 3:1 (token düzeyinde doğrulanır).
- [ ] Durumlar yalnızca renkle iletilmez (metin + ikon).
- [ ] Tüm etkileşimler klavyeyle yapılabilir; mantıklı sekme sırası; DataGrid'de ok tuşu gezinmesi.
- [ ] Görünür odak göstergesi (2.4.7) ve odağın gizlenmemesi (2.4.11 — sabit başlık/alt çubuk odaklı öğeyi örtmez).
- [ ] Hedef boyutu ≥ 24×24 CSS px (2.5.8); mobil ≥ 48×48 dp.
- [ ] Sürükleme gerektiren işlemlerin alternatifi var (2.5.7) — ör. dosya yükleme için "Dosya seç" butonu, görev atama için menü.
- [ ] Formlarda etiket + hata metni ilişkilendirilmiş (`aria-describedby`); hata özeti formun başında.
- [ ] Tekrarlı giriş istenmez (3.3.7) — sihirbaz adımlarında önceki veriler korunur.
- [ ] Erişilebilir kimlik doğrulama (3.3.8) — şifre yapıştırmaya ve parola yöneticisine izin.
- [ ] Tutarlı yardım konumu (3.2.6).
- [ ] Toast/canlı bölgeler `aria-live` ile duyurulur; kritik hatalar toast'a bırakılmaz.
- [ ] Ekran okuyucu: tablo başlıkları, rozet metinleri, ikon butonların etiketleri; Flutter `Semantics`.
- [ ] `prefers-reduced-motion` desteği; yanıp sönen içerik yok.
- [ ] Yakınlaştırma %200'de yatay kaydırma olmadan okunabilir (tablolar hariç).
- [ ] Dil özniteliği `lang="tr"`; Türkçe büyük/küçük harf dönüşümü (`tr-TR`).
- [ ] Barkod okutma geri bildirimi ses + titreşim + görsel (tek kanala bağlı değil).

## 7. İçerik ve dil kuralları
- Sabit sözlük: Satılabilir, Rafta (fiziksel), Ayrıldı (rezerve), Yolda, Eksik, Taslak, Onay Bekliyor…
- Buton metinleri fiil ile: "Siparişi onayla", "Eksik bildir", "Sevk et".
- Hata metni kalıbı: "<Ne oldu>. <Ne yapmalı>." — ör. "Bu üründen yalnızca 3 adet satılabilir. Miktarı azaltın veya transfer başlatın."
- Para: `1.234,56 ₺`; tarih `04.11.2026`; saat `14:30`.

## 8. Figma çalışma planı
- **Figma MCP bağlı** (kullanıcı planı: **Starter team**). Starter plan sınırları (proje/dosya sayısı, paylaşılan kütüphane ve değişken modu kısıtları) göz önüne alınarak tek tasarım dosyası + sayfalar yapısı önerilir: `00 Kapak`, `01 Token'lar`, `02 Bileşenler`, `03 Web ekranları`, `04 Mobil ekranlar`, `05 Akışlar`.
- **Hafta 2:** Token'ların Figma değişkenleri olarak oluşturulması, çekirdek bileşenler, öncelikli ekranlar: W02, W03, W04, W05, W06, W13, W14, W15, W16, M02, M03, M06.
- Ekranlar sentetik veriyle (sector-scenarios.md örnekleri) doldurulur.
- Tasarım, ekran görüntüsü incelemesi yapılmadan tamamlandı sayılmaz (UX rol kuralı).
- Code Connect / token senkronizasyonu: web CSS değişkenleri ve Flutter tema ile eşleme Hafta 3'te değerlendirilir.

## 9. Açık konular (tasarım)
| # | Konu |
|---|---|
| D-01 | Marka rengi ve logo (BayiOS) — ürün sahibi tercihi |
| D-02 | Tenant logosu dışında tenant'a özel tema (renk) MVP'de yok — onay |
| D-03 | Depo ekranlarında koyu tema (loş depo) ihtiyacı pilotta gözlemlenecek |
