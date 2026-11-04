# STATUS — Gerçek proje durumu

> Yalnızca doğrulanmış durum. Son güncelleme: 2026-11-04 (Gün 01 oturumu).

## Mevcut faz
Phase 0 tamamlandı (**KOŞULLU GEÇTİ**, `docs/operations/phase0-gate.md`). Hafta 2 — Platform Foundation başladı.

## GitHub
- Repo: https://github.com/HalilMertDeveli/BayiOS (public)
- Dallar: `main` (korumalı), `preview` (korumalı), `chore/7-platform-skeleton`
- Rulesets (`main`, `preview`): PR zorunlu, force-push/silme yasak, zorunlu kontroller: `api build & test`, `web lint, typecheck, test & build`, `dependency review`. Bypass yok.
- Güvenlik: secret scanning + push protection, private vulnerability reporting, Dependabot alerts + security updates açık.
- 12 milestone, 60 gün issue'su (#1–#60, issue no = gün no), sahip kararları: #61.
- Kapalı: #1–#5 (Phase 0). Açık PR: #62 → `preview`.

## Delegasyon
Agent Teams etkin değil → Claude Code alt ajanları (+ worktree). Bu oturumda 4 alt ajan çalıştı: Product, Architecture, Security/QA/DevOps, Phase 0 gate review.

## Test durumu (yerel, PR #62)
| Alan | Sonuç |
|---|---|
| API (`dotnet test`) | 13/13 geçti |
| Web lint / typecheck / test / build | temiz / temiz / 4/4 / başarılı |
| Tarayıcı smoke (web→API) | başarılı |
| Flutter | BLOCKED (SDK yok) |

## Bilinen kısıtlar
- `main` onayı teknik olarak "0 zorunlu onay + PR" ile korunuyor: tek kişilik hesapta sahip kendi PR'ını onaylayamadığı için. Sahip onayı süreç kuralıdır (AUTOPILOT.md); ajan `main`'e merge etmez.
- Web geliştirme bağımlılıklarında 16 orta seviye npm audit bulgusu (production: 0).

## Blokerler (ürün sahibi aksiyonu) — ayrıntı #61
| Konu | Etki |
|---|---|
| Flutter SDK + Android SDK kurulumu | #7 mobil kısmı, Hafta 9 |
| Supabase preview projesi onayı (ücretsiz slot) | #10 preview DB |
| API hosting seçimi (öneri Fly.io fra, ~$10–20/ay) | API preview URL'si |
| Vercel projesi bağlama / Pro (ticari kullanım) | Web preview URL'si |
| Lisans | Repo lisansı |
| OD-01…OD-12 kararları | İlgili haftalar |

## Sonraki adım
PR #62 CI → `preview` merge · #9 tasarım sistemi · #10 Supabase/preview · Hafta 3 hazırlığı (UnitOfWork, auth).
