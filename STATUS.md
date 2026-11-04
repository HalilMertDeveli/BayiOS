# STATUS — Gerçek proje durumu

> Yalnızca doğrulanmış durum. Son güncelleme: 2026-11-04 (Dalga 1 tamamlandı, Dalga 2 başladı).
> İş emirleri, bağımlılık grafiği ve aktif atamalar: `docs/operations/work-orders.md`.

## Mevcut faz
Phase 0 **KOŞULLU GEÇTİ** (`docs/operations/phase0-gate.md`). Dalga 1 `preview`'de: platform iskeleti + CI (#62), container/preview hazırlığı (#64), tasarım sistemi + kabuk (#65),
auth + tenancy temeli (#67). Dalga 2 (idempotency, katalog, CI kapıları) paralel yürütülüyor.

## GitHub
- Repo: https://github.com/HalilMertDeveli/BayiOS (public) · dallar: `main`, `preview` (ikisi de ruleset korumalı)
- Rulesets: PR zorunlu, force-push/silme yasak, review thread çözümü zorunlu, zorunlu kontroller:
  `api build & test`, `web lint, typecheck, test & build`, `dependency review`. Bypass yok.
- Güvenlik: secret scanning + push protection, CodeQL, Dependabot (hedef `preview`).
- Birleşen PR'lar (`preview`, squash, tüm kontroller yeşil): #62, #63, #64, #65, #67. Kapalı issue'lar: #1–#6, #8, #9, #11, #12.
- Ek CI işi: `api container build, smoke & scan` (henüz zorunlu değil; path filtresi nedeniyle önce `ci-gate` #71).

## Durum panosu
| Job | Hat | Issue | Branch | PR | Durum |
|---|---|---|---|---|---|
| 03 Container + preview hazırlığı | F | #10 | — | #64 | DONE (preview URL'leri sahip onayına bağlı) |
| 02/05 Tasarım sistemi + kabuk | B | #9 | — | #65 | DONE (Figma → #66) |
| 04 Identity/Tenancy | A | #11, #12 | — | #67 | DONE |
| 04 takip: Idempotency-Key | A | #68 | `feature/68-idempotency-key` | — | IN_PROGRESS |
| 07 Katalog çekirdeği | D | #16, #17 | `feature/16-catalog-core` | — | IN_PROGRESS |
| 03 ci-gate + migration CI | F | #70, #71 | `chore/70-71-ci-gate-migrations` | — | IN_PROGRESS |
| 06 Flutter temeli | C | #7 | — | — | BLOCKED (SDK) |

## Test durumu (`preview`, son CI)
| Alan | Sonuç |
|---|---|
| API (`dotnet test`, Testcontainers Postgres 17) | 83/83 geçti (27 birim, 56 entegrasyon; JWT, RLS, çapraz tenant negatif testleri dahil) |
| Web lint / typecheck / birim / Playwright + axe / build | geçti (55 birim, 24 e2e) |
| API container (build, /health smoke, Trivy) | geçti, 0 zafiyet |
| CodeQL (csharp, js/ts, actions) | geçti |
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
Dalga 2 PR'larını doğrula/birleştir → katalog oluşturma uçlarına Idempotency-Key bağla → Dalga 2b: JOB 09 Inventory ledger (#26, #27), web ürün ekranları (#18), sektör şablonları (#14/#19).
