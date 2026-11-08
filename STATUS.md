# STATUS — Gerçek proje durumu

> Yalnızca doğrulanmış durum. Son güncelleme: 2026-11-08 (Faz C mobil tamam — iOS ve canlı push sahip adımı bekliyor; Faz B kalanları sahip kararında).
> İş emirleri, bağımlılık grafiği ve aktif atamalar: `docs/operations/work-orders.md`. Yol haritası: #148.

## Mevcut faz
**Faz C (Flutter mobil, Android) — tamam.** Flutter 3.47.9 (`D:/DevTools/flutter`), `apps/mobile`.
- Birleşenler: #165 temel (giriş/oturum, tenant seçimi, API istemcisi, sözleşme testi, ci-mobile; ADR 0011) · #166 pano, görev
  listesi, barkodlu toplama (#41, #42) · #167 mal kabul, stok sorgu, düzeltme (#43) · #169 web↔mobil gerçek akış testi + CI
  Android emülatör işi (#45) · #170 Notifications modülü, cihaz kaydı, görev atamasında FCM (varsayılan kapalı), çevrimdışı
  yönetimi (#44). #7, #15, #41–#44 kapandı.
- Sahip adımı bekleyen: iOS (#45: macOS runner onayı + Apple Developer hesabı) · push'u açmak için Firebase projesi
  (`docs/operations/push-notifications.md` §3) · gerçek cihazda kamera okutma denemesi.
- Açık iş: #168 stok transferi + sayım (API'de uç yok).

**Faz B (canlıya hazırlık) — mühendislik kısmı tamam, sahip kararları bekleniyor.** `preview` @ `1955dbd`.
- Birleşenler: #155 (#152 odak/URL kancaları) · #156 deploy pipeline + migration adımı, Supabase bootstrap, gitleaks
  (#70, #76) · #157 gözlemlenebilirlik, yedek/geri yükleme provası, rollback, runbook'lar (#55) · #158 güvenlik denetimi,
  WCAG 2.2 AA denetimi, k6 yük + rezervasyon soak, fixture yarışı (#54, #58, #153) · #159 açık yönlendirme (M-1) ·
  #161 web güvenlik başlıkları/CSP, API rate limit, sipariş no kilidi, L-1…L-4, benzersiz id (#160) · #162 erişim reddi
  audit olayları (#89) · #163 AI ayrıştırma 202 + arka plan işi, tenant opt-in (varsayılan kapalı), öğrenilmiş eşleştirmeler (#121).
- Ürün sahibini bekleyen: #138 hesaplar (Supabase/Fly/Vercel/domain) → #10 preview doğrulaması ve #70 ilk gerçek deploy;
  #139 hukuki metinler → #83 KVKK onayı; #61 kararlar (AI anahtarı + model, plan limitleri, gözlem backend'i, RPO/RTO,
  alarm kanalı, marka rengi); #121 canlı değerlendirme + Anthropic aktarımının hukuki dayanağı; #98, #147.
- Denetimden kalan riskler (`docs/security/audit-2026-11.md`): `script-src 'unsafe-inline'` (PPR/nonce uyumsuz), JS'ten
  okunabilir Supabase çerezi (BFF gerekir), tenant başına toplam limit önerisi, CAPTCHA web entegrasyonu, a11y A-2/A-3/A-5 (Low).

Faz A (pilota hazır sürüm) tamam — RC-1 raporu `docs/operations/release-candidate.md`: kontrollü pilota uygun, production'a değil.

## GitHub
- Repo: https://github.com/HalilMertDeveli/BayiOS (public) · dallar: `main`, `preview` (ikisi de ruleset korumalı)
- Rulesets: PR zorunlu, force-push/silme yasak, review thread çözümü zorunlu. Bypass yok.
- Güvenlik: secret scanning + push protection, CodeQL, Dependabot (hedef `preview`). 2026-11-07: açık secret-scanning,
  code-scanning ve Dependabot uyarısı 0.
- Birleşen PR'lar: #62–#65, #67, #72–#75, #77, #80–#82, #84, #85, #88, #90, #91, #93, #95, #97, #99–#101, #103, #104, #106–#110, #112–#114, #116–#120, #122–#128, #130–#132, #134, #135, #149–#151, #154–#159, #161–#167, #169, #170 (toplam 70).
- Zorunlu kontroller: `api build & test`, `web lint, typecheck, test & build`, `dependency review`, `ci gate` (path-filtreli işler dahil: container, migrations, `e2e real stack`).

## Durum panosu (aktif)
Aktif ajan yok. Faz B dalgası (2026-11-07) yukarıda. Faz C dalgası (2026-11-08) tamam. Sıradaki: ürün sahibi kararları (#138, #139, #61); sonra Faz D pilot (#140),
Faz E büyüme. Faz başlangıcı ürün sahibinin "başla" demesine bağlı.

İş emri durumu: `docs/operations/work-orders.md`.

## Test durumu
Son ölçüm (`preview` @ `1955dbd`, PR #163 CI): API 989 test · 988 geçti · 0 kaldı · 1 atlandı (canlı AI) · web Vitest
704/704 · Playwright 296/296 · gerçek yığın E2E, k6 short, migration drift, CodeQL, gitleaks yeşil. Yük: `docs/testing/load-2026-11.md`.

### RC-1 ölçümü (`preview` @ `ba83e85`, 2026-11-07 — ayrıntı ve komutlar: `docs/operations/release-candidate.md` §3)
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
| Flutter | analyze temiz · 283 test · web↔mobil gerçek akış (Android emülatör, CI) geçti · iOS BLOCKED (macOS) |

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
| Lisans | Repo lisansı |
| OD-01…OD-12 + marka rengi, anonimleştirme yetkisi, import limitleri, mal kabul notu, sipariş onayında fiyat yeniden çözümü, toplama raf değişimi (#98), seri no kapsamı (#57/#147), Anthropic anahtarı + model | İlgili işler |

## Sonraki adım
Ürün sahibi: RC raporunu (`docs/operations/release-candidate.md`) inceleyip #138/#139 ve #61 kararlarını vermek; "başla"
denirse Faz B (deploy hattı, Supabase bootstrap, izleme/yedek, güvenlik denetimi, yük testi) → preview ortamında pilot (#140).
