# STATUS — Gerçek proje durumu

> Yalnızca doğrulanmış durum. Son güncelleme: 2026-11-07 (release candidate RC-1 ölçüldü, #60; Faz A tamam).
> İş emirleri, bağımlılık grafiği ve aktif atamalar: `docs/operations/work-orders.md`. Yol haritası: #148.

## Mevcut faz
**Faz A (pilota hazır sürüm) tamam** — #60 RC raporu ile kapanıyor; kalan iki madde ürün sahibi kararı bekliyor (#98, #57'nin
seri no kısmı → #147). Release candidate: `preview` @ `ba83e85` — rapor `docs/operations/release-candidate.md`.
Öneri: barındırma onayı + Supabase Auth ayarlarından sonra preview ortamında **kontrollü pilota uygun, production'a değil**.

Çekirdek iş akışı `preview`'de API + web olarak tamam ve gerçek yığında (web + API + Postgres + Supabase Auth) uçtan uca
test ediliyor: kayıt/onboarding → katalog + özel alanlar → içe aktarma sihirbazı → stok defteri + depo/raf + minimum stok →
müşteri + fiyat listeleri → sipariş + rezervasyon → toplama/paketleme/sevkiyat → stok çıkışı → raporlar. Flutter BLOCKED
(SDK). Hosting/preview URL'leri sahip onayına bağlı (#138).

## GitHub
- Repo: https://github.com/HalilMertDeveli/BayiOS (public) · dallar: `main`, `preview` (ikisi de ruleset korumalı)
- Rulesets: PR zorunlu, force-push/silme yasak, review thread çözümü zorunlu. Bypass yok.
- Güvenlik: secret scanning + push protection, CodeQL, Dependabot (hedef `preview`). 2026-11-07: açık secret-scanning,
  code-scanning ve Dependabot uyarısı 0.
- Birleşen PR'lar: #62–#65, #67, #72–#75, #77, #80–#82, #84, #85, #88, #90, #91, #93, #95, #97, #99–#101, #103, #104, #106–#110, #112–#114, #116–#120, #122–#128, #130–#132, #134, #135, #149–#151 (toplam 54).
- Zorunlu kontroller: `api build & test`, `web lint, typecheck, test & build`, `dependency review`, `ci gate` (path-filtreli işler dahil: container, migrations, `e2e real stack`).

## Durum panosu (aktif)
Aktif ajan yok. Son dalga (2026-11-07, Faz A):
- #149 web içe aktarma sihirbazı + geçmiş (#94 kapandı) · #151 ürün/depo bazında minimum stok eşikleri, eşiğe göre kritik
  stok, pano kutusu, `Min. stok` kolonu (#137 kapandı) · #150 demo verisi, kararsız test kök nedenleri, UI gözlemleri
  (#136, #133 kapandı).
- #60 RC raporu + kılavuz/UAT/tehdit modeli düzeltmeleri (bu güncelleme). Ölçüm sırasında bulunan test altyapısı yarışı: #153.
- Önceki: pilot blokerleri (#129: ekip/davet, iş kuralları, marka/kategori/birim, Tamamla/Kalanı kapat, pano), demo
  işletmeler (#130), kullanıcı kılavuzu + UAT (#128), AI WhatsApp (#120, #122, varsayılan kapalı), paketler (#124), raporlar (#125).

Sıradaki: **Faz B** (ürün sahibi "başla" demeden başlamaz) — #138 canlı ortam hesapları ve #139 hukuki hazırlık (ürün sahibi),
#70 deploy migration adımı · #76 Supabase bootstrap · #10 preview doğrulaması, #55 izleme/yedek/runbook, #54 güvenlik
denetimi · #58 regresyon/yük, #121 AI canlı öncesi, #89 erişim reddi audit'i · #83 onboarding devamı, #152, #153.
Sonra Faz C (Flutter, BLOCKED), Faz D pilot (#140), Faz E büyüme.

İş emri durumu: `docs/operations/work-orders.md`.

## Test durumu (`preview` @ `ba83e85`, 2026-11-07 — ayrıntı ve komutlar: `docs/operations/release-candidate.md` §3)
| Alan | Sonuç |
|---|---|
| API (`dotnet test`, Testcontainers Postgres 17) | 875 toplam · 874 geçti · 0 kaldı · 1 atlandı (canlı AI testi, yalnızca anahtar varken) — CI 2 dk 42 sn, yerel 114 sn. Bir yerel koşuda fixture kapanış yarışı (#153) |
| Eşzamanlılık | 17 test, gerçek PostgreSQL + paralel istek (rezervasyon, onay, iptal↔sevk, sevk onayı, okutma, davet koltuğu, sahip düşürme, iş kuyruğu) — yeşil |
| Güvenlik korumaları | DB guard'ları (RLS/policy/grant/trigger/SECURITY DEFINER), OpenAPI IDOR taraması, X-Tenant-Id sahteciliği, rol + K/O izin taraması — gerçek hata yok |
| Gerçek yığın E2E | 10/10 geçti, yeniden deneme yok (yerel izole yığın, 1,4 dk): kayıt → onboarding; sipariş → sevkiyat + stok; ikinci tenant izolasyonu; WhatsApp → taslak; demo motor + LED (kalanı kapat dahil); davet → kabul; katalog ekranları; içe aktarma sihirbazı + geri alma |
| Performans (10k ürün, Release) | katalog arama p95 ≤ 14,4 ms, bakiye arama p95 ≤ 19,6 ms, kritik stok p95 25,9 ms, 4.162 satır XLSX 260 ms (yerel); bütçe p95 < 500 ms. Yük testi yok (#58) |
| Web lint / typecheck / birim / Playwright + axe / build | geçti — 665 birim (91 dosya), 283 e2e (mock) |
| Bağımlılık açıkları | NuGet 0 (16 proje, transitive dahil) · npm production 0 · npm dev 8 orta |
| Migration (12 modül, 24 migration) | idempotent, iki kez uygulama, drift yok (ci-migrations) |
| API container (Trivy) | HIGH/CRITICAL 0 |
| Flutter | BLOCKED (SDK yok) |

## Bilinen kısıtlar
- `main` onayı "0 zorunlu onay + PR" ile korunuyor (tek kişilik hesap); sahip onayı süreç kuralı, ajan `main`'e merge etmez.
- Web dev bağımlılıklarında 8 orta seviye npm audit bulgusu (production: 0).
- Davet kabulündeki e-posta doğrulaması canlı Supabase'de "Confirm email" + "Secure email change" ayarına dayanır; API
  varsayılanı `Invitations:TrustUserMetadataEmailVerified=true` (sertleştirme: access-token hook, #60 yorumu).
- Kullanıcıya görünen eksikler: `docs/user-guide/bilinen-sinirlamalar.md`.
- Mevcut local Postgres volume'u olanlar initdb rolleri için bir kez `docker compose -f infra/docker/compose.yml down -v` çalıştırmalı.

## Blokerler (ürün sahibi aksiyonu) — ayrıntı #61, #138, #139
| Konu | Etki |
|---|---|
| #138 Supabase projesi (Confirm email + Secure email change açık), API hosting (öneri Fly.io fra, ~$10–20/ay), Vercel (ticari: Pro), alan adı, GitHub Environments | Preview/production ortamı; pilot bunu bekliyor |
| #139 KVKK aydınlatma metni, kullanım koşulları, gizlilik politikası, şirket/fatura | Gerçek işletme verisiyle pilot |
| Flutter SDK + Android SDK kurulumu | Faz C tamamen bekliyor |
| Lisans | Repo lisansı |
| OD-01…OD-12 + marka rengi, anonimleştirme yetkisi, import limitleri, mal kabul notu, sipariş onayında fiyat yeniden çözümü, toplama raf değişimi (#98), seri no kapsamı (#57/#147), Anthropic anahtarı + model | İlgili işler |

## Sonraki adım
Ürün sahibi: RC raporunu (`docs/operations/release-candidate.md`) inceleyip #138/#139 ve #61 kararlarını vermek; "başla"
denirse Faz B (deploy hattı, Supabase bootstrap, izleme/yedek, güvenlik denetimi, yük testi) → preview ortamında pilot (#140).
