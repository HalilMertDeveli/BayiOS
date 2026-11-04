# STATUS — Gerçek proje durumu

> Yalnızca doğrulanmış durum. Son güncelleme: 2026-11-05 (Dalga 2b: stok defteri, katalog alanları birleşti).
> İş emirleri, bağımlılık grafiği ve aktif atamalar: `docs/operations/work-orders.md`.

## Mevcut faz
Phase 0 **KOŞULLU GEÇTİ** (`docs/operations/phase0-gate.md`). `preview`'de: platform + CI (#62, #64, #73), tasarım sistemi + kabuk (#65), auth + tenancy (#67), Idempotency-Key (#74),
katalog (#75), sektör şablonları + özel alanlar + işletme ayarları (#77), stok defteri + depo/raf (#80).
Kritik yolda sıradaki: müşteri + fiyat (#31, #78) → sipariş + rezervasyon (#32, #33).

## GitHub
- Repo: https://github.com/HalilMertDeveli/BayiOS (public) · dallar: `main`, `preview` (ikisi de ruleset korumalı)
- Rulesets: PR zorunlu, force-push/silme yasak, review thread çözümü zorunlu, zorunlu kontroller:
  `api build & test`, `web lint, typecheck, test & build`, `dependency review`. Bypass yok.
- Güvenlik: secret scanning + push protection, CodeQL, Dependabot (hedef `preview`).
- Birleşen PR'lar (`preview`, squash, tüm kontroller yeşil): #62–#65, #67, #72–#75, #77, #80.
- Zorunlu kontroller: `api build & test`, `web lint, typecheck, test & build`, `dependency review`, `ci gate` (path-filtreli işleri toplar).
- Kapalı issue'lar (Phase 0 dışı): #6, #8, #9, #11, #12, #14, #16, #17, #19, #26, #27, #30, #68, #71.

## Durum panosu (aktif)
| Job | Hat | Issue | Branch | Durum |
|---|---|---|---|---|
| 05 Web giriş + onboarding | B | #13 | `feature/13-web-auth-onboarding` | IN_PROGRESS (yerel Supabase) |
| 11 ön koşul: müşteri + fiyat | A | #31, #78 | `feature/31-customers-pricing` | IN_PROGRESS |
| Platform: audit + iş kuyruğu/outbox | A | #79 | `feature/79-audit-jobs-outbox` | IN_PROGRESS |
| 06 Flutter temeli | C | #7 | — | BLOCKED (SDK) |

Tamamlanan iş paketleri ve ayrıntı: `docs/operations/work-orders.md`.

## Test durumu (`preview`, son CI)
| Alan | Sonuç |
|---|---|
| API (`dotnet test`, Testcontainers Postgres 17) | 202 geçti (39 birim, 163 entegrasyon; JWT, RLS, çapraz tenant, idempotency, stok eşzamanlılığı dahil) |
| Stok eşzamanlılığı | 20 paralel rezervasyon / 5 stok → tam 5; son birim yarışı; deadlock yok; kayıp güncelleme yok |
| Migration (5 modül) | idempotent script, iki kez uygulama, model drift yok |
| Web lint / typecheck / birim / Playwright + axe / build | geçti |
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
| OD-01…OD-12 kararları | İlgili işler |

## Sonraki adım
#31/#78 + #79 birleşince: JOB 11 sipariş + rezervasyon (#32, #33), JOB 08 toplu import (#21–#25, iş kuyruğuna bağlı), web ürün ekranları (#18, #13'e bağlı).
