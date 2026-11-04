# STATUS — Gerçek proje durumu

> Yalnızca doğrulanmış durum. Son güncelleme: 2026-11-04 (V3 paralel yürütme başlangıcı).
> İş emirleri, bağımlılık grafiği ve aktif atamalar: `docs/operations/work-orders.md`.

## Mevcut faz
Phase 0 **KOŞULLU GEÇTİ** (`docs/operations/phase0-gate.md`). Platform iskeleti + CI `preview`'de (PR #62).
V3 ile plan bağımlılık tabanlı iş havuzuna çevrildi; Dalga 1 paralel yürütülüyor.

## GitHub
- Repo: https://github.com/HalilMertDeveli/BayiOS (public) · dallar: `main`, `preview` (ikisi de ruleset korumalı)
- Rulesets: PR zorunlu, force-push/silme yasak, review thread çözümü zorunlu, zorunlu kontroller:
  `api build & test`, `web lint, typecheck, test & build`, `dependency review`. Bypass yok.
- Güvenlik: secret scanning + push protection, CodeQL, Dependabot (hedef `preview`).
- Birleşen: PR #62 → `preview` (squash, tüm kontroller yeşil). Kapalı issue'lar: #1–#6, #8.

## Durum panosu (Dalga 1)
| Job | Hat | Issue | Branch | PR | Durum | Test |
|---|---|---|---|---|---|---|
| 04 Identity/Tenancy | A | #11, #12 | `feature/12-auth-tenancy-foundation` | — | IN_PROGRESS | — |
| 02/05 Tasarım sistemi + kabuk | B | #9 | `feature/9-design-system-shell` | — | IN_PROGRESS | — |
| 03 Container + preview hazırlığı | F | #10 | `chore/10-container-preview` | — | IN_PROGRESS | — |
| 06 Flutter temeli | C | #7 | — | — | BLOCKED (SDK) | — |

## Test durumu (`preview`, PR #62 CI)
| Alan | Sonuç |
|---|---|
| API (`dotnet test`) | 12/12 geçti |
| Web lint / typecheck / test / build | geçti |
| CodeQL (csharp, js/ts, actions) | geçti; log injection bulgusu kod düzeltmesiyle kapatıldı (correlation id yalnızca GUID) |
| Flutter | BLOCKED (SDK yok) |

## Bilinen kısıtlar
- `main` onayı "0 zorunlu onay + PR" ile korunuyor (tek kişilik hesap); sahip onayı süreç kuralı, ajan `main`'e merge etmez.
- Web dev bağımlılıklarında 16 orta seviye npm audit bulgusu (production: 0).
- Bu ortam ajan bazlı token telemetrisi sağlamıyor; maliyet raporları ajan sayısı/kapsamıyla sınırlı.

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
Dalga 1 PR'larını doğrula ve `preview`'e birleştir → Dalga 2: JOB 07 Katalog, JOB 09 Inventory (04'e bağlı), JOB 16 test genişletme.
