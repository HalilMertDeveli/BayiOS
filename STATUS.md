# STATUS — Gerçek proje durumu

> Yalnızca doğrulanmış durum. Son güncelleme: 2026-11-05 (yayın öncesi sağlamlaştırma dalgası tamamlandı).
> İş emirleri, bağımlılık grafiği ve aktif atamalar: `docs/operations/work-orders.md`.

## Mevcut faz
Çekirdek iş akışı `preview`'de API + web olarak tamam ve gerçek yığında (web + API + Postgres + Supabase Auth) CI'da
uçtan uca test ediliyor: kayıt/onboarding → katalog + özel alanlar → toplu içe aktarma → stok defteri + depo/raf →
müşteri + fiyat listeleri → sipariş + rezervasyon → toplama/paketleme/sevkiyat → stok çıkışı. Web izinleri `/me`'den
(koşullu/kendi kayıtları dahil). Flutter BLOCKED (SDK). Hosting/preview URL'leri sahip onayına bağlı.

## GitHub
- Repo: https://github.com/HalilMertDeveli/BayiOS (public) · dallar: `main`, `preview` (ikisi de ruleset korumalı)
- Rulesets: PR zorunlu, force-push/silme yasak, review thread çözümü zorunlu. Bypass yok.
- Güvenlik: secret scanning + push protection, CodeQL, Dependabot (hedef `preview`).
- Birleşen PR'lar: #62–#65, #67, #72–#75, #77, #80–#82, #84, #85, #88, #90, #91, #93, #95, #97, #99–#101, #103, #104, #106–#110, #112–#114, #116–#118.
- Zorunlu kontroller: `api build & test`, `web lint, typecheck, test & build`, `dependency review`, `ci gate` (path-filtreli işler dahil: container, migrations, `e2e real stack`).

## Durum panosu (aktif)
Aktif ajan yok. Yayın öncesi sağlamlaştırma dalgası tamamlandı: platform operasyonları (#113: audit 2 yıl saklama, kuyruk
temizliği, sıkı job kaydı, hata temizleyici), içe aktarma geri alma + hız sınırı + ham dosya temizliği + takılı import
süpürücü (#116), sipariş ayarları + ayrı 403 kodları (#114), KVKK: anonimleştirme sipariş adres kopyalarını maskeler (#117),
web: kod tabanlı hata mesajları, sipariş ayarları ekranı, adres seçimi, form sıfırlama (#118).

Açık takip işleri: #89 (member.* / auth.* audit olayları), #94 (web import sihirbazı, Excel hata raporu, AI önerisi),
#98 depo karşılama devamı, #83 onboarding devamı, #76 Supabase bootstrap, #66 Figma. Bekleyen büyük işler: AI WhatsApp
(#46–#50), raporlar (#53), entitlements (#51–#52), Flutter (#7, #41–#45, BLOCKED).

İş emri durumu: `docs/operations/work-orders.md`.

## Test durumu (`preview`, son CI)
| Alan | Sonuç |
|---|---|
| API (`dotnet test`, Testcontainers Postgres 17) | 631 geçti |
| Güvenlik korumaları | DB guard'ları (RLS/policy/grant/trigger/SECURITY DEFINER), OpenAPI IDOR taraması, X-Tenant-Id sahteciliği, rol + K/O izin taraması — gerçek hata yok |
| Gerçek yığın E2E (CI) | kayıt → onboarding → ürün → mal kabul → fiyat → müşteri → sipariş → onay → toplama → paketleme → sevkiyat → stok; ikinci tenant izolasyonu (~4 dk) |
| Performans (10k ürün) | katalog arama p95 ≤ 15 ms, bakiye arama p95 ~20 ms (yerel); CI'da p95 < 500 ms assert |
| Web lint / typecheck / birim / Playwright + axe / build | geçti (371 birim, 175 e2e — mock) |
| Migration (9 modül) | idempotent, iki kez uygulama, drift yok |
| API container (Trivy) | 0 zafiyet |
| Flutter | BLOCKED (SDK yok) |

## Bilinen kısıtlar
- `main` onayı "0 zorunlu onay + PR" ile korunuyor (tek kişilik hesap); sahip onayı süreç kuralı, ajan `main`'e merge etmez.
- Web dev bağımlılıklarında 16 orta seviye npm audit bulgusu (production: 0).
- Mevcut local Postgres volume'u olanlar initdb rolleri için bir kez `docker compose -f infra/docker/compose.yml down -v` çalıştırmalı.
- Koşullu (K) ve kendi kayıtları (O) izinleri şimdilik reddediliyor (#69).

## Blokerler (ürün sahibi aksiyonu) — ayrıntı #61
| Konu | Etki |
|---|---|
| Flutter SDK + Android SDK kurulumu | JOB 06 / Hat C tamamen bekliyor |
| Supabase preview projesi onayı (ücretsiz slot) | Preview DB; dev local Postgres ile sürer |
| API hosting seçimi (öneri Fly.io fra, ~$10–20/ay) | API preview URL'si |
| Vercel projesi bağlama / Pro (ticari kullanım) | Web preview URL'si |
| Lisans | Repo lisansı |
| OD-01…OD-12 + marka rengi, KVKK metni, anonimleştirme yetkisi, import limitleri, mal kabul notu, sipariş onayında fiyat yeniden çözümü, toplama raf değişimi (#98) | İlgili işler |

## Sonraki adım
Ürün sahibi seçimine göre: AI WhatsApp (#46–#50; canlı test için Anthropic anahtarı onayı), raporlar/entitlements, veya kalan takip işleri. Gerçek preview URL'si için Supabase + hosting + Vercel onayı; Flutter için SDK kurulumu.
