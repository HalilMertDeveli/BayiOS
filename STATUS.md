# STATUS — Gerçek proje durumu

> Yalnızca doğrulanmış durum. Son güncelleme: 2026-11-06 (pilot hazırlığı: demo işletmeler, ekip/davet, pano, pilot blokerleri kapandı).
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
- Birleşen PR'lar: #62–#65, #67, #72–#75, #77, #80–#82, #84, #85, #88, #90, #91, #93, #95, #97, #99–#101, #103, #104, #106–#110, #112–#114, #116–#118, #119, #120, #122–#127, #130–#132, #134 (toplam 49).
- Zorunlu kontroller: `api build & test`, `web lint, typecheck, test & build`, `dependency review`, `ci gate` (path-filtreli işler dahil: container, migrations, `e2e real stack`).

## Durum panosu (aktif)
Aktif ajan yok. Son dalgalar:
- Pilot blokerleri (#129) kapandı: üye daveti + ekip/rol yönetimi + iş kuralları (#132 API, #134 web), marka/kategori/birim
  ekranları + sipariş Tamamla/Kalanı kapat + rol bazlı pano (#131), operatör deneme kısaltma betiği (infra/ops).
- Demo işletmeler (#130): Demo Motor + Demo LED, yalnızca public API ile tohumlanıyor, `npm --prefix apps/web run demo:seed`;
  demo E2E'leri gerçek yığın CI işinde. #57'de seri no senaryosu yok (özellik yok — ürün kararı).
- Kullanıcı kılavuzu + ~110 UAT senaryosu: PR #128 (CI tekrar koşuyor).
- Önceki: AI WhatsApp (#120, #122, varsayılan kapalı), paketler/entitlement (#124), raporlar (#125), web (#126).

Açık takip işleri: #94 (web import sihirbazı — kalan son pilot eksiği), #121 (AI canlı öncesi), #133, #98, #89 (auth.* audit),
#83, #76, #70 (deploy migration adımı), #66. Bekleyen büyük işler: release adayı (#60), Flutter (#7, #41–#45, BLOCKED),
stok sayımı/transfer (#29), gözlemlenebilirlik/yedek (#55), bayi portalı (JOB 13), dış entegrasyonlar (JOB 15).

İş emri durumu: `docs/operations/work-orders.md`.

## Test durumu (`preview`, son CI)
| Alan | Sonuç |
|---|---|
| API (`dotnet test`, Testcontainers Postgres 17) | 852 geçti (1 canlı AI testi yalnızca anahtar varken) |
| Güvenlik korumaları | DB guard'ları (RLS/policy/grant/trigger/SECURITY DEFINER), OpenAPI IDOR taraması, X-Tenant-Id sahteciliği, rol + K/O izin taraması — gerçek hata yok |
| Gerçek yığın E2E (CI) | kayıt → onboarding → ürün → mal kabul → fiyat → müşteri → sipariş → onay → toplama → paketleme → sevkiyat → stok; ikinci tenant izolasyonu (~4 dk) ; WhatsApp yapıştır → incele → taslak ; demo motor + LED; davet → kabul; katalog referansları |
| Performans (10k ürün) | katalog arama p95 ≤ 15 ms, bakiye arama p95 ~20 ms (yerel); CI'da p95 < 500 ms assert |
| Web lint / typecheck / birim / Playwright + axe / build | geçti (580 birim, 264 e2e — mock) |
| Migration (12 script) | idempotent, iki kez uygulama, drift yok |
| API container (Trivy) | 0 zafiyet |
| Flutter | BLOCKED (SDK yok) |

## Bilinen kısıtlar
- `main` onayı "0 zorunlu onay + PR" ile korunuyor (tek kişilik hesap); sahip onayı süreç kuralı, ajan `main`'e merge etmez.
- Web dev bağımlılıklarında 16 orta seviye npm audit bulgusu (production: 0).
- Mevcut local Postgres volume'u olanlar initdb rolleri için bir kez `docker compose -f infra/docker/compose.yml down -v` çalıştırmalı.

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
Web import sihirbazı (#94) → release candidate raporu (#60). Ürün sahibi kararları (#61) canlıya çıkışın ön koşulu: Supabase + hosting + Vercel, Anthropic anahtarı + model, paket kuralları, KVKK metni, seri no kapsamı, Flutter SDK.
