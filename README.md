# BayiOS

**WhatsApp'tan gelen siparişten depodan çıkan son ürüne kadar — stok ve sipariş operasyonlarını basitçe yönetin.**

BayiOS; motosiklet yedek parçacıları, LED/elektronik toptancıları, hırdavatçılar ve benzeri ürün odaklı işletmeler için
modüler, çok kiracılı (multi-tenant) bir **B2B stok, depo ve sipariş yönetimi SaaS** ürünüdür.

> Durum: **Phase 0 — Organizasyon ve mimari temel.** Henüz üretimde değildir. Güncel ilerleme: [`STATUS.md`](STATUS.md).

## Neler yapar (hedef MVP)
- İşletme kaydı, sektör şablonları (motosiklet yedek parça, LED/elektronik), roller ve personel daveti
- Ürün kataloğu: SKU, barkod, varyant, sektöre özel alanlar, fiyat listeleri
- Excel/CSV toplu içe aktarma: sütun eşleştirme, önizleme, tekrar yüklemede çoğalma yok
- Hareket kaydı (ledger) tabanlı stok: fiziksel / rezerve / satılabilir, çoklu depo ve raf
- Sipariş yaşam döngüsü: onayda rezervasyon, sevkiyatta stok çıkışı, iptalde serbest bırakma
- Depo görevleri: toplama, barkod okutma, paketleme, sevkiyat (Flutter mobil uygulama)
- AI WhatsApp sipariş asistanı: yapıştırılan mesajdan taslak sipariş — insan onayı zorunlu

## Mimari
| Katman | Teknoloji |
|---|---|
| API | ASP.NET Core (.NET 10 LTS), modüler monolith, REST + OpenAPI |
| Web | Next.js + React + TypeScript |
| Mobil | Flutter (Android / iOS) |
| Veritabanı | Supabase PostgreSQL |
| Kimlik | Supabase Auth (JWT, API'de doğrulanır) |
| Bildirim | Firebase Cloud Messaging |
| CI/CD | GitHub Actions |

Ayrıntılar: [`docs/architecture/`](docs/architecture/) · Kararlar: [`docs/adr/`](docs/adr/)

## Repo yapısı
```
services/api      ASP.NET Core API
apps/web          Next.js işletme paneli
apps/mobile       Flutter depo uygulaması
packages/contracts OpenAPI sözleşmesi
infra/            Docker, Supabase, deployment
docs/             Ürün, mimari, güvenlik, test, operasyon, tasarım
```

## Geliştirme
Bkz. [`CONTRIBUTING.md`](CONTRIBUTING.md). Branch'ler: `main` (korumalı), `preview` (entegrasyon), `feature/*`, `fix/*`, `chore/*`.

## Güvenlik
Açık bildirimi için [`SECURITY.md`](SECURITY.md). Bu repo herkese açıktır; gerçek anahtar veya müşteri verisi içermez.

## Lisans
Lisans henüz seçilmedi (ürün sahibi kararı bekleniyor). Lisans eklenene kadar tüm hakları saklıdır.
