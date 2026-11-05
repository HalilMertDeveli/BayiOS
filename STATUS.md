# STATUS — Gerçek proje durumu

> Yalnızca doğrulanmış durum. Son güncelleme: 2026-11-05 (AI WhatsApp ayrıştırma backend + web onay ekranı).
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
- Birleşen PR'lar: #62–#65, #67, #72–#75, #77, #80–#82, #84, #85, #88, #90, #91, #93, #95, #97, #99–#101, #103, #104, #106–#110, #112–#114, #116–#118, #119, #120, #122.
- Zorunlu kontroller: `api build & test`, `web lint, typecheck, test & build`, `dependency review`, `ci gate` (path-filtreli işler dahil: container, migrations, `e2e real stack`).

## Durum panosu (aktif)
Aktif ajan yok. Son dalga: AI WhatsApp sipariş ayrıştırma (#120 backend, #122 web onay ekranı). Özellik varsayılan kapalı
(`Ai:Enabled=false`); kapalıyken kural tabanlı ayrıştırıcı çalışır. Değerlendirme kümesi (125 mesaj) deterministik yolda CI'da:
satır yakalama %100, doğru eşleşme %96,9, yanlış eşleşme %0 (kümeye göre ayarlı — gerçek trafikte düşük beklenir).
Canlı Claude doğrulaması API anahtarı onayına bağlı. Canlı öncesi: #121 (202 + job akışı, tenant opt-in, takma adlar).

Açık takip işleri: #121, #89 (member.*/auth.* audit), #94 (web import sihirbazı, Excel hata raporu), #98, #83, #76, #66.
Bekleyen büyük işler: raporlar (#53), entitlements (#51–#52), Flutter (#7, #41–#45, BLOCKED).

İş emri durumu: `docs/operations/work-orders.md`.

## Test durumu (`preview`, son CI)
| Alan | Sonuç |
|---|---|
| API (`dotnet test`, Testcontainers Postgres 17) | 685 geçti (270 birim, 415 entegrasyon; 1 canlı AI testi yalnızca anahtar varken) |
| Güvenlik korumaları | DB guard'ları (RLS/policy/grant/trigger/SECURITY DEFINER), OpenAPI IDOR taraması, X-Tenant-Id sahteciliği, rol + K/O izin taraması — gerçek hata yok |
| Gerçek yığın E2E (CI) | kayıt → onboarding → ürün → mal kabul → fiyat → müşteri → sipariş → onay → toplama → paketleme → sevkiyat → stok; ikinci tenant izolasyonu (~4 dk) ; WhatsApp yapıştır → incele → taslak |
| Performans (10k ürün) | katalog arama p95 ≤ 15 ms, bakiye arama p95 ~20 ms (yerel); CI'da p95 < 500 ms assert |
| Web lint / typecheck / birim / Playwright + axe / build | geçti (427 birim, 186 e2e — mock) |
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
Ürün sahibi kararları: Anthropic API anahtarı (canlı AI değerlendirmesi) ve model seçimi (Opus 5.5 ~0,014 $/mesaj, Haiku 5.5 ~0,0004 $/mesaj), tenant AI opt-in varsayılanı; ardından #121. Alternatif: raporlar + entitlements (#51–#53). Preview URL için Supabase + hosting + Vercel onayı.
