# BayiOS — Veri Politikası (KVKK odaklı)

| Alan | Değer |
|---|---|
| Sahip | Security Engineer |
| Durum | Taslak v0.1 — hukuki danışmanlık değildir; canlıya çıkıştan önce KVKK uzmanı/avukat incelemesi gerekir |
| Son güncelleme | 2026-11-04 |

## 1. Roller (KVKK terminolojisi)
- **Veri sorumlusu:** BayiOS'u kullanan işletme (tenant). Kendi müşterilerinin ve personelinin verisini sisteme girer.
- **Veri işleyen:** BayiOS (hizmet sağlayıcı), tenant adına ve talimatıyla işler.
- **Alt işleyenler:** bölüm 6'daki sağlayıcılar.
- BayiOS, tenant hesabının sahibi (owner) ile ilgili iletişim/faturalama verisi için ayrıca veri sorumlusudur.
- Gereken belgeler (canlıdan önce, ürün sahibi + hukuk): Aydınlatma metni, Veri İşleme Sözleşmesi (DPA) şablonu,
  gizlilik politikası, VERBİS kayıt yükümlülüğünün değerlendirilmesi, yurt dışı aktarım dayanağı (bölüm 7).

## 2. Veri envanteri

| Kategori | Alanlar | Kaynak | Amaç | Hassasiyet | Saklama yeri |
|---|---|---|---|---|---|
| Personel (tenant kullanıcıları) | ad soyad, e-posta, telefon (opsiyonel), rol, son giriş zamanı | Davet / kayıt | Kimlik doğrulama, yetki, bildirim | Kişisel veri | Supabase Auth (`auth.users`) + `identity.user_profiles` + `identity.memberships` |
| Cihaz | FCM token, platform, uygulama sürümü | Mobil uygulama | Push bildirimi | Kişisel veri (cihaz tanımlayıcı) | `notifications.device_tokens` |
| Müşteri (bayi/alıcı) iletişim | firma adı, yetkili adı, telefon, e-posta, vergi no (firma ise) | Tenant girişi / import | Sipariş, sevkiyat, iletişim | Kişisel veri (şahıs firmasında vergi no = TCKN olabilir → **özel dikkat**) | `customers` |
| Adres | teslimat / fatura adresi | Tenant girişi / import | Sevkiyat | Kişisel veri | `customer_addresses` |
| WhatsApp metni | serbest metin (isim, telefon içerebilir) | Kullanıcı yapıştırır | Sipariş taslağı | Kişisel veri olabilir; güvenilmeyen veri | `ai_parse_requests` (kısa süreli) |
| İşlem verisi | sipariş, stok hareketi, fiyat | Sistem | Ana iş | Ticari sır (kişisel değil) | İş tabloları |
| Audit | aktör kullanıcı ID, IP hash, user agent özeti | Sistem | Güvenlik, hesap verebilirlik | Kişisel veri (dolaylı) | `audit_events` |
| Dosyalar | import dosyaları, belge görselleri | Yükleme | Import / belge | Kişisel veri içerebilir | Supabase Storage (private) |
| Teknik log | correlation id, rota, süre, durum kodu, tenant id | Sistem | İşletim | PII içermemeli | Log sağlayıcısı (seçilecek) |

**İşlenmeyen veriler (tasarım gereği):** özel nitelikli kişisel veri (sağlık, biyometrik vb.), ödeme kartı verisi (ödeme
eklenirse sağlayıcının barındırdığı sayfa kullanılır), TCKN için ayrı alan açılmaz — şahıs firması vergi no alanı opsiyonel
ve maskeli gösterilir.

## 3. Veri minimizasyonu
- Telefon personel için opsiyonel; push yeterliyse SMS yok.
- AI'ya yalnızca ayrıştırma için gerekli metin gider (katalog/fiyat verisi gönderilmez, eşleştirme sunucuda — ADR 0010); müşteri adres/telefonu gönderilmez. Metindeki
  telefon numaraları gönderim öncesi maskelenir (`+90 5** *** ** 12`).
  Uygulanan (#47): `TurkishText.MaskPersonalData` Türk mobil numaralarını (son 2 hane kalır) ve e-posta adreslerini
  (`[e-posta]`) maskeler; barkodlara dokunmaz. Sağlayıcıya ve veritabanına giden metin maskelenmiş metindir.
  Kişi adları (#121) **en iyi çaba** ile, yalnızca bağlamla tanınıp `[ad]` ile maskelenir: kendini tanıtma ("Ben Ahmet",
  "adım …", "isim:", "alıcı:", "teslim alacak kişi …"), hitap öncesi ("Ahmet Bey", "Ayşe Hanım", "Mehmet abi/abla"),
  unvan sonrası ("Sayın …", "Sn. …") ve selamlama sonrası cümle sonu ("Merhaba Ahmet,"). **Sınırlar:** bağlamsız yazılmış
  adlar ("Ahmet 0532…" ya da yalnızca "Ahmet"), küçük harfle yazılmış adlar, firma/mağaza adları, imza satırları ve adres
  içindeki adlar tanınmaz; bu konumlarda büyük harfle başlayan bir ürün kelimesi de maskelenebilir (fazla maskeleme yalnızca
  incelemeyi etkiler, sipariş kesinleşmeden insan onayı zorunludur). Tam ad tanıma (NER) yoktur; mesajda kişi verisi
  kalabileceği için sağlayıcı aktarımı §7 opt-in'ine bağlıdır.
  AI özelliği sunucuda varsayılan kapalıdır (`Ai:Enabled=false`) ve ayrıca **tenant bazında opt-in** gerektirir (§7).
- Bildirim yükünde kişi adı/fiyat yok.
- Log ve hata izlemede gövde loglanmaz; yalnızca allowlist alanlar.
- Preview/dev ortamlarında **yalnızca sentetik veri** (gerçek müşteri verisi kopyalanmaz, prod dump'ı alınmaz).

## 4. Saklama süreleri (önerilen; ürün sahibi + hukuk onayı gerekir)

| Veri | Süre | Gerekçe / not |
|---|---|---|
| Aktif tenant iş verisi | Abonelik süresince | Hizmet |
| Sipariş ve stok hareketleri | Abonelik + tenant talebine göre; fatura ile ilişkili kayıtlar için ticari/vergi mevzuatı süreleri tenant sorumluluğundadır | VUK/TTK saklama yükümlülükleri tenant'ındır; BayiOS export sağlar |
| Tenant kapanışı sonrası | 30 gün geri alma penceresi → kalıcı silme | Yanlışlıkla kapatmaya karşı |
| Yedekler | Sağlayıcı yedek rotasyonu (Supabase planına göre) — silinen veri yedeklerden rotasyonla düşer | Plan seçiminde doğrulanacak |
| AI ayrıştırma ham metni | 30 gün (sonra yalnızca yapılandırılmış taslak kalır) — uygulanan: `integrations.ai_parse_requests` maskelenmiş metni, ayrıştırma çıktısını ve inceleme satırlarını saatlik `integrations.ai_text_retention` job'ı `Ai:TextRetentionDays` (varsayılan 30, en az 1) sonra NULL'lar; sayılar, token'lar ve sipariş bağı kalır. Idempotency yanıt kopyası 24 saatte silinir | Minimizasyon |
| AI öğrenilmiş eşleştirmeler (`integrations.learned_aliases`, #121) | Tenant silene kadar (`DELETE /ai/aliases/{id}`, denetimli) | Mesaj metni değil, yalnızca onaylanan satırın normalize ürün ifadesi → varyant (isteğe bağlı müşteri); kişi adı maskelemesinden sonra üretilir |
| Import dosyaları | İş tamamlandıktan 30 gün sonra | Hata ayıklama |
| Export dosyaları | 7 gün | Tek seferlik indirme |
| Audit olayları | 2 yıl (değerlendirilecek) — uygulanan: veritabanı tabanı 2 yıl, `Audit:Retention` (varsayılan/en az 730 gün) ile uzatılabilir; saatlik `platform.audit_retention` job'ı siler | Güvenlik. Append-only kuralının tek istisnası; tasarım: data-model §5.11, threat-model §7 |
| Arka plan işleri (`platform.jobs`) | Başarılı: 7 gün, `dead`: 30 gün (`Jobs:SucceededRetention` / `Jobs:DeadRetention`) | İşletim; yük yalnızca kimlik içerir, `last_error` temizlenmiş (ADR 0006) |
| Outbox mesajları | İşlendikten 7 gün sonra (`Jobs:OutboxRetention`) | İşletim; teslim job'ları ayrı saklanır |
| Teknik loglar | 30 gün | İşletim |
| FCM token | Cihaz çıkışı veya 90 gün etkisizlik | Minimizasyon |

## 5. Tenant talepleri: dışa aktarma ve silme
- **Dışa aktarma (owner):** Tüm tenant verisi CSV/JSON (zip) olarak, arka plan işiyle; indirme kısa ömürlü signed URL.
  Bu özellik ücret duvarına konmaz (CLAUDE.md/ürün kuralı). Audit: `data_export.tenant_requested`.
- **Tekil kişi talebi (ilgili kişi başvurusu):** Tenant (veri sorumlusu) başvuruyu alır; BayiOS araçları ile müşteri/personel
  kaydını bulur, dışa aktarır, düzeltir veya anonimleştirir. Sipariş geçmişi bütünlüğü için silme yerine **anonimleştirme**
  (ad → "Silinmiş müşteri #<kısa id>", iletişim alanları null).
  Uygulanan (#115): `POST /customers/{id}/anonymize` aynı transaction'da müşterinin **tüm** siparişlerindeki (taslak,
  onay bekleyen, onaylı, sevk edilmiş, kapanmış) teslimat/fatura adres snapshot'larını da maskeler: adres başlığı, adres
  satırı 2, posta kodu, iletişim adı ve telefonu → null; adres satırı 1 → "Silinmiş adres". İlçe, il ve ülke korunur
  (kaba konum; tek başına kişiyi belirlemez, bölgesel satış geçmişi ve sevkiyat istatistiği için gerekir). Onay
  sonrası değişmezlik trigger'ı yalnızca bu maskelemeye izin verir (data-model §5.8). Tekrar çağrı idempotenttir.
  Audit `customer.anonymized` yalnızca sayıları içerir (kaldırılan adres, maskelenen sipariş), kişisel veri içermez.
  Kapsam dışı (bilinen): sipariş notu (`orders.notes`) gibi serbest metinler otomatik taranmaz; içlerindeki kişisel
  veri tenant tarafından elle düzeltilmelidir.
- **Tenant silme:** owner talebi + e-posta doğrulama → 30 gün bekleme → kalıcı silme işi (tüm tenant satırları + storage
  önekleri) → tamamlandı audit kaydı (tenant dışı sistem audit'inde, PII'siz). Gerçek veri silme ürün sahibi onayı gerektiren
  işlemler listesindedir; otomasyon yalnızca tenant'ın kendi talebiyle ve test edilmiş iş akışıyla çalışır.
- Supabase Auth kullanıcısı birden fazla tenant'a üye olabilir; tenant silmede kullanıcı yalnızca başka üyeliği yoksa silinir.

## 6. Alt işleyenler (sub-processors)

| Sağlayıcı | Amaç | İşlenen veri | Bölge tercihi | Not / doğrulanacak |
|---|---|---|---|---|
| Supabase | PostgreSQL, Auth, Storage | Tüm iş verisi, kullanıcı kimlikleri, dosyalar | `eu-central-1` (Frankfurt) | DPA imzalanacak; yedek bölgesi doğrulanacak |
| Vercel | Web uygulaması barındırma (SSR/edge) | İstek metadata'sı, SSR sırasında geçen veri; DB'ye doğrudan erişim yok | Fonksiyon bölgesi `fra1` seçilecek | Hobby planı **ticari kullanıma izin vermez** (bkz. operations/risk-register.md); DPA |
| Anthropic | WhatsApp metni ayrıştırma (Claude API) | Maskelenmiş mesaj metni (katalog/fiyat verisi yok — ADR 0010) | ABD (varsayılan) — bölge seçeneği doğrulanacak | Ticari koşullar/veri saklama ve eğitim kullanımı koşulları resmi kaynaktan doğrulanıp DPA'ya eklenecek |
| Google / Firebase | FCM push bildirimi | FCM cihaz token'ı, bildirim metni (PII'siz) | Küresel | FCM ücretsiz (firebase.google.com/pricing, 2026-11-04 kontrol edildi) |
| API host (seçilecek) | ASP.NET Core API + worker | Bellek içi tüm iş verisi, loglar | Frankfurt / AB | `operations/hosting-options.md` |
| GitHub | Kaynak kod, CI | Kod; **müşteri verisi yok** | — | Public repo kuralları (bölüm 10) |
| Log/hata izleme (opsiyonel, seçilecek) | Gözlemlenebilirlik | PII'siz loglar | AB | Seçimde DPA |

Yeni alt işleyen eklemek = bu tabloyu güncellemek + tenant'lara bildirim (DPA'da taahhüt edilecek süre).

## 7. Veri yerleşimi ve yurt dışı aktarım
- Birincil veri AB'de (Supabase Frankfurt). API host da AB'de (Frankfurt veya yakın) seçilir.
- KVKK md. 9 kapsamında yurt dışı aktarım (AB dahil Türkiye dışı tüm aktarımlar) için 2024 değişikliğiyle getirilen
  mekanizmalar (yeterlilik kararı / standart sözleşme vb.) hukukçu ile değerlendirilmelidir. **Bu belge hukuki dayanağı
  belirlemez; ürün sahibi aksiyonu olarak işaretlidir.**
- Anthropic (ABD) ve Firebase (küresel) aktarımları ayrıca değerlendirilir; AI özelliği tenant bazında açılıp kapatılabilir
  olmalıdır (opt-in).
  Uygulanan (#121): `integrations.ai_settings` (tenant başına, RLS FORCE), **varsayılan kapalı**. Kapalıyken
  `POST /ai/order-parses` 409 `ai.opt_in_required` ile reddedilir; hiçbir şey saklanmaz, kuyruğa alınmaz, sayılmaz.
  Açıp kapatma yalnızca owner/admin (`tenant.settings.manage`, `PUT /ai/settings`) ve `ai.settings_changed` (önce/sonra)
  olarak denetlenir. Kuyrukta bekleyen bir ayrıştırma, iş çalışmadan önce opt-in kapatılırsa sağlayıcıya gönderilmez
  (`ai.opt_in_revoked` ile başarısız olur). Metnin sağlayıcıya gitmesi için tenant opt-in'i, `ai_order_parsing` hakkı ve
  sunucu bayrağı (`Ai:Enabled` + anahtar) birlikte gerekir; hukuki aktarım dayanağı yine ürün sahibi aksiyonudur.

## 8. Loglama ve PII kuralları
1. İstek/yanıt gövdesi loglanmaz. Allowlist: method, route şablonu, durum kodu, süre, `tenant_id`, `user_id` (UUID), `correlation_id`.
2. E-posta, telefon, adres, ad, WhatsApp metni, signed URL, JWT, `Authorization`/`Cookie` başlıkları **asla** loglanmaz.
3. Serilog/`ILogger` için redaction enricher; unit test ile doğrulanır (`LogRedactionTests`).
4. Hata izleme aracında `sendDefaultPii=false` ve gövde yakalama kapalı.
5. Audit kayıtlarında IP, sunucu tarafı gizli anahtarla HMAC-SHA-256 ile hash'lenir (`Audit:IpHashKey`, en az 32 bayt; ortam başına ayrı, secret store'da). Anahtar yoksa **IP hash'i hiç yazılmaz** (`ip_hash = NULL`; anahtarsız SHA-256 IPv4 için geri çevrilebilir). 32 bayttan kısa anahtar başlangıçta reddedilir (API açılmaz). Anahtar değişirse eski hash'ler yeni olanlarla karşılaştırılamaz.
6. Destek amaçlı tenant verisine erişim yalnızca owner izniyle ve audit'lenerek (ileride "destek erişimi" özelliği).
7. Job/outbox `last_error` alanına yalnızca temizlenmiş hata yazılır (istisna tipi + değerleri ayıklanmış mesaj; SQL parametresi/`Detail` yok) — ADR 0006, `JobErrorSanitizerTests`.

## 9. Sır yönetimi (ortam bazında)

| Sır | Local/dev | Preview/staging | Production |
|---|---|---|---|
| Postgres bağlantı dizesi (app rolü) | `.env` (git-ignored), docker compose varsayılanları | API host env (preview projesi) + GitHub Environment `preview` | API host env + GitHub Environment `production` (onaylı) |
| Postgres migration rolü | `.env` | GitHub Environment `preview` secret | GitHub Environment `production` (reviewer: owner) |
| Supabase URL + publishable/anon key (public) | `.env` | Vercel env (Preview) / `--dart-define` | Vercel env (Production) / `--dart-define` |
| Supabase service role / secret key | Kullanılmaz (mümkünse) | API host env | API host env |
| JWT JWKS URL / issuer | `.env` | API host env | API host env |
| Audit IP HMAC anahtarı (`Audit__IpHashKey`, ≥ 32 bayt) | Boş bırakılabilir (IP hash yok) | API host env (ayrı anahtar) | API host env (ayrı anahtar) |
| Anthropic API anahtarı | Geliştiricinin kendi test anahtarı, `.env`; varsayılan mock | API host env (ayrı anahtar, düşük harcama limiti) | API host env (ayrı anahtar, bütçe alarmı) |
| FCM servis hesabı JSON | `bayios-dev` projesi, `.env` dosya yolu | `bayios-dev` (API host secret) | `bayios-prod` (API host secret) |
| Vercel token | — | GitHub secret (gerekirse; Vercel Git entegrasyonu tercih) | Aynı |
| API host deploy token | — | GitHub Environment `preview` | GitHub Environment `production` |
| Mobil imzalama anahtarı (Android keystore / iOS sertifika) | Debug anahtarı | — | CI secret veya Codemagic; yerelde yedek (ürün sahibi kasası) |

Kurallar: her ortam ayrı anahtar; anahtar rotasyonu yılda bir veya sızıntı şüphesinde hemen; sırlar sohbet/issue/PR'a
yazılmaz; `.env.example` yalnızca değişken adları içerir.

## 10. Public repo kuralları
- Gerçek anahtar, connection string, müşteri verisi, gerçek fiyat listesi, gerçek firma adı **asla** commit edilmez.
- Seed ve fixture'lar sentetik (ör. "Demo Motor Yedek Parça Ltd.", `@example.com` e-postaları, `+90 555 000 00 xx`).
- GitHub secret scanning + push protection açık; gitleaks CI'da; `.gitignore`: `.env*` (`.env.example` hariç), `*.keystore`,
  `google-services.json` (prod), `GoogleService-Info.plist` (prod), `appsettings.*.local.json`.
- Sızıntı olursa: anahtar **önce** iptal/rotasyon, sonra geçmiş temizliği (geçmiş temizliği tek başına yetmez; public repo forklanmış olabilir).
- Ekran görüntüleri ve test artefaktları sentetik veriyle üretilir.
