# BayiOS — 60 İş Günlük Yol Haritası

12 hafta / 60 iş günü hedef programı. Süreler teslim sözü değildir; kritik yol, kalite kapıları ve dış bağımlılıklara göre güncellenir.
Her hafta bir GitHub **milestone**, her gün bir GitHub **issue**'dur (kabul kriterleri issue'da). Faz atlanmaz.

Başlangıç: 2026-11-04 (Gün 01). Takvim iş günü bazlıdır.

## Kritik yol
```
Phase 0 (W1) → Repo/CI/iskelet (W2) → Auth+Tenancy (W3) → Katalog (W4) → Inventory ledger (W6)
  → Sipariş+rezervasyon (W7) → Depo hazırlama/sevkiyat (W8) → Flutter depo akışı (W9) → Stabilizasyon (W12)
```
Kritik yol dışı (paralel yürütülebilir): Import (W5, W4'e bağlı), AI WhatsApp (W10, W4+W7'ye bağlı),
Entitlements/raporlar (W11), tasarım sistemi (W2'den itibaren), hosting/preview hazırlığı (W2–W3, ürün sahibi kararına bağlı).

## Dış bağımlılıklar (ürün sahibi)
| Bağımlılık | Gerektiği an | Etkisi |
|---|---|---|
| Flutter SDK + Android SDK kurulumu | W2 Gün 07 | Mobil iskelet BLOCKED |
| Supabase preview projesi onayı | W2 Gün 10 | Preview ortamı; dev local Postgres ile sürer |
| API hosting sağlayıcı seçimi | W3 sonu | API preview URL'si |
| Firebase projesi | W9 Gün 44 | Push bildirimleri |
| Apple Developer hesabı / macOS CI | W9 Gün 45 | iOS build/test |
| Anthropic API anahtarı (preview) | W10 Gün 47 | AI ayrıştırma canlı testi (öncesi mock) |
| Lisans seçimi | W2 Gün 06 | Repo lisans dosyası |

---

## Hafta 1 — Organizasyon, PRD ve Mimari (M01)
| Gün | İş | Çıktı |
|---|---|---|
| 01 | Ortam/model/connector/yetki/maliyet denetimi | `docs/operations/connectors.md` |
| 02 | Orkestra sistemi, ajan tanımları, görev sözleşmeleri, süreklilik | `CLAUDE.md`, `AUTOPILOT.md`, `.claude/agents/`, `STATUS.md` |
| 03 | PRD, persona, sektör senaryoları, MVP kapsamı, rol matrisi | `docs/product/*`, `BACKLOG.md` |
| 04 | Sistem mimarisi, domain/veri modeli, API stratejisi, ADR'ler | `docs/architecture/*`, `docs/adr/*` |
| 05 | Tehdit modeli, test stratejisi, risk kaydı, **Phase 0 kalite kapısı** | `docs/security/*`, `docs/testing/*`, `docs/operations/*` |

**Çıkış:** Belgeler çelişkisiz; mimari + güvenlik review'dan BLOCKER bulgu kalmamış.

## Hafta 2 — Repository ve Platform Foundation (M02)
| Gün | İş |
|---|---|
| 06 | Public repo, rulesets (`main`, `preview`), issue/PR şablonları, milestones, issues, Dependabot, secret scanning |
| 07 | ASP.NET Core API iskeleti (health, OpenAPI, katmanlar, testler), Next.js iskeleti, Flutter iskeleti (SDK'ya bağlı) |
| 08 | Docker compose (Postgres), GitHub Actions CI (api/web/contracts), CodeQL, dependency review |
| 09 | Tasarım token'ları, temel UI bileşenleri, uygulama kabuğu (sidebar/header), Figma bileşen kütüphanesi |
| 10 | Supabase dev/preview planı, auth sözleşmesi, API↔DB health, web/mobil→API smoke testi, Vercel preview |

**Çıkış:** Tüm uygulamalar ayağa kalkıyor, CI yeşil, en az web preview doğrulanıyor.

## Hafta 3 — Auth ve Multi-Tenancy (M03)
| Gün | İş |
|---|---|
| 11 | Supabase Auth entegrasyonu, API JWT doğrulama (JWKS, issuer, audience) |
| 12 | Tenant, membership, rol→izin sistemi, RLS; çapraz tenant negatif testleri |
| 13 | Web kayıt/giriş ve işletme onboarding sihirbazı |
| 14 | Sektör şablonları, özel alan tanımları, işletme ayarları, logo |
| 15 | Flutter giriş/oturum yenileme, personel daveti, tenant izolasyon test paketi |

> Phase 0 kapısı notu: Platform altyapısı — UnitOfWork (paylaşılan bağlantı/transaction), `Idempotency-Key`, audit yazımı, `platform.jobs`/`outbox` tabloları — ve asgari entitlement altyapısı (plan/deneme kaydı, `RequireEntitlement`) bu hafta (Gün 12–15) kurulur; onboarding'de oluşturulan varsayılan depo (Gün 13) Warehouse modülünün asgari depo tablosunu gerektirir. W4 sonrası tüm oluşturma uçları ve W6'daki Pro kapıları (çoklu depo/raf) buna bağlıdır; W11 Gün 51 paket/limit/mağaza kapsamını tamamlar.

**Çıkış:** Birden çok işletme güvenle oluşturulup yönetilebiliyor.

## Hafta 4 — Ürün Kataloğu (M04)
16 Ürün/varyant/kategori/marka/birim veri modeli · 17 Catalog API (CRUD, arama, filtre, keyset sayfalama, ETag) ·
18 Web ürün listesi + düzenleme · 19 Motor/LED özel alanları, varyant senaryoları, fiyat listeleri temeli ·
20 10.000 ürünlük sentetik katalog ile performans/yetki/tutarlılık testleri.

**Çıkış:** İşletmeler sektörlerine göre katalog yönetebiliyor; p95 arama < 500 ms.

## Hafta 5 — Toplu İçe Aktarma (M05)
21 Excel/CSV parser, dosya doğrulama, güvenli yükleme · 22 Sütun eşleştirme sihirbazı + AI sütun önerisi ·
23 SKU/barkod eşleştirme, duplicate kontrolü, önizleme, satır hata raporu · 24 Import job kuyruğu, ilerleme, iptal, yeniden deneme ·
25 10k satır, tekrar yükleme, fiyat/stok ayrımı, güvenli geri alma testleri.

> Phase 0 kapısı notu: Stok içe aktarma (US-IMP-004) ledger'a (W6 Gün 26–27), müşteri içe aktarma Customers modülüne (W7 Gün 31) bağlıdır. W5'te ürün ve fiyat listesi importu tamamlanır; stok/müşteri import tipleri ilgili modül hazır olunca (W6 Gün 27 / W7 Gün 31) etkinleştirilir ve testleri o günlere taşınır.

**Çıkış:** Ürünler tek tek girilmek zorunda değil; aynı dosya iki kez yüklenince çoğalma yok.

## Hafta 6 — Stok ve Depo (M06)
26 Inventory ledger + hareket modeli · 27 Fiziksel/rezerve/satılabilir hesapları · 28 Depo, raf/lokasyon, mal kabul ·
29 Sayım, düzeltme, transfer, iade, hasar · 30 Eşzamanlılık, negatif stok, transaction, audit testleri.

**Çıkış:** Stok doğruluğu kritik senaryolarda testle kanıtlı.

## Hafta 7 — Sipariş Yönetimi (M07)
31 Müşteri/bayi modeli, fiyat grupları · 32 Sipariş API, kalemler, fiyat/iskonto/KDV hesabı, durum makinesi ·
33 Onay + stok rezervasyonu (all-or-nothing) · 34 Web sipariş listesi/detay/arama/filtre ·
35 İptal, kısmi işlem, idempotency, entegrasyon testleri.

**Çıkış:** Sipariş ve stok kayıtları her senaryoda tutarlı.

## Hafta 8 — Depo Hazırlama ve Sevkiyat (M08)
36 Toplama görevi + personele atama · 37 Raf bazlı toplama listesi, barkod akışı · 38 Paketleme, eksik ürün bildirimi ·
39 Sevkiyat onayı, stok çıkışı, hareket geçmişi · 40 Siparişten sevkiyata E2E, regresyon, SignalR durum güncellemeleri.

**Çıkış:** Sipariş fiziksel depodan güvenilir şekilde çıkarılabiliyor.

## Hafta 9 — Flutter Mobil (M09)
41 Depo dashboard, görev listesi, sipariş detayı · 42 Barkod/QR tarama + toplama ekranı · 43 Mal kabul ve yetkili stok işlemleri ·
44 FCM, cihaz kaydı, bağlantı hatası yönetimi · 45 Web↔mobil gerçek akışlar, Android smoke, iOS build (hesaba bağlı).

**Çıkış:** Flutter gerçek API'ye bağlı çalışan istemci.

## Hafta 10 — AI WhatsApp Asistanı (M10)
46 Ayrıştırma veri sözleşmesi + sentetik değerlendirme kümesi · 47 Claude API ile güvenli ayrıştırma servisi ·
48 Katalog eşleştirme, güven skoru, insan onay ekranı · 49 Motor/LED örnekleri, belirsiz/yanlış eşleşme testleri ·
50 Maliyet limitleri, hata/yeniden deneme, değerlendirme raporu.

**Çıkış:** WhatsApp mesajından doğrulanabilir taslak sipariş; belirsiz satır sessizce seçilmiyor.

## Hafta 11 — SaaS, Güvenlik, Operasyon (M11)
51 Paket/entitlement modeli · 52 Modül mağazası, deneme modu, limit testleri · 53 Ürün/stok/sipariş/AI raporları, CSV/Excel dışa aktarma ·
54 Güvenlik denetimi, tenant escape testleri, bağımlılık güncellemeleri, erişilebilirlik · 55 Gözlemlenebilirlik, yedek/geri yükleme, rollback, runbook'lar.

## Hafta 12 — Stabilizasyon ve Pilot (M12)
56 Motosiklet yedek parça sentetik demo + E2E · 57 LED/elektronik sentetik demo + E2E · 58 Tam regresyon, yük, eşzamanlılık, mobil smoke ·
59 UAT hazırlığı, hata düzeltmeleri, kullanıcı kılavuzları · 60 Release candidate raporu, ölçülen kalite, kalan riskler, pilot onay sunumu.

**Çıkış:** Pilot için test edilebilir RC. Canlıya çıkış yalnızca ürün sahibi onayıyla.
