# STATUS — Gerçek proje durumu

> Bu dosya yalnızca doğrulanmış durumu içerir. Son güncelleme: 2026-11-04 (Gün 01).

## Mevcut faz
**Phase 0 — Organizasyon ve mimari temel (Hafta 1).**

## Tamamlananlar
- Ortam denetimi (bkz. `docs/operations/connectors.md`)
- Orkestra kuralları: `CLAUDE.md`, `AUTOPILOT.md`; 12 uzman rol: `.claude/agents/`
- `ROADMAP.md` (60 gün, 12 milestone)

## Devam edenler
- Phase 0 belgeleri (ürün, mimari, güvenlik/test/operasyon)

## Delegasyon yöntemi
Agent Teams etkin değil → Claude Code alt ajanları + git worktree izolasyonu.

## Blokerler (ürün sahibi aksiyonu)
| Konu | Etki | Gerekli aksiyon |
|---|---|---|
| Flutter SDK / Android SDK yok | Mobil iskelet ve testler | Flutter + Android Studio kurulumu |
| Supabase CLI yok | Yerel Supabase akışı | Kurulum (resmî yöntem: `docs/operations/connectors.md`) |
| BayiOS Supabase projesi yok | Preview ortamı | Maliyet raporu sonrası onay |
| API hosting seçilmedi | API preview URL'si | `docs/operations/hosting-options.md` üzerinden karar |
| Lisans seçilmedi | Repo lisansı | Karar |

## GitHub
Repository: henüz oluşturulmadı.

## Test durumu
Henüz kod yok.
