# BayiOS — İş Emirleri, Çalışma Hatları ve Bağımlılık Grafiği (V3)

> 60 günlük plan (`ROADMAP.md`) artık **bağımlılık tabanlı iş havuzu** olarak kullanılır; hafta numaraları teslim
> takvimi değil, öncelik sırasıdır. Bir işin başlaması yalnızca gerçek bağımlılıklarına bağlıdır.
> İş emri durumu her değişiklikte burada ve `STATUS.md`'de güncellenir. Kullanıcı yalnızca Orkestra Şefi ile konuşur.

## 1. Çalışma hatları (kalıcı)

| Hat | Kapsam | Sahip ajan(lar) |
|---|---|---|
| A — Architecture & Backend | Domain, ASP.NET Core API, auth, yetki, tenancy, sipariş/stok kuralları | principal-architect, backend-lead, security-engineer |
| B — Web & UX | Next.js paneli, tasarım sistemi, responsive UI, API entegrasyonu | ux-designer, frontend-lead |
| C — Flutter & Mobile | Flutter, depo ekranları, barkod, FCM | mobile-lead |
| D — Data & Inventory | PostgreSQL şeması, migration'lar, katalog, import, stok hareketleri, depo modeli | database-engineer, backend-lead |
| E — QA & Security | Otomatik testler, tenant izolasyonu, regresyon, E2E, performans | qa-lead, security-engineer |
| F — DevOps & Delivery | CI, Docker, preview, hosting, gözlemlenebilirlik, release | devops-engineer |

Varsayılan kapasite: 1 Şef + 3 aktif uzman görevi (en çok 5). Aynı dosya kümesine iki yazar atanmaz.

## 2. Sahiplik kuralları (çakışma önleme)

| Alan | Tek yazar |
|---|---|
| `services/api/src/**/Migrations`, `infra/docker/initdb/**` | Hat A/D'de o an aktif olan **tek** backend ajanı. Migration'lar sıralı birleşir. |
| `packages/contracts/openapi.json` | API'yi değiştiren ajan (drift testi üretir). Web/mobil yalnızca okur ve `gen:api` çalıştırır. |
| `apps/web/**` | Hat B ajanı |
| `apps/mobile/**` | Hat C ajanı |
| `.github/**`, `services/api/Dockerfile`, `infra/deploy/**` | Hat F ajanı |
| `STATUS.md`, `ROADMAP.md`, bu belge | Yalnızca Orkestra Şefi |

Sözleşme önce gelir: backend uç noktası `openapi.json`'a girmeden web/mobil onu gerçek servis gibi kullanmaz.
Henüz olmayan uçlar için web/mobil **açıkça işaretli mock adapter** kullanır (`*.mock.ts`, `Mock*Repository`).

## 3. Ajan yürütme sözleşmesi (her iş emrinin başına eklenir)

`AUTOPILOT.md §2` görev sözleşmesi + şu kurallar: yalnızca atanan kapsam; ayrı feature branch ve worktree;
başka ajanın dosyalarını ezmeme; gereksiz refactor/abstraction yok; test kapatma veya gevşetme yok; sahte veriyle
production davranışı taklidi yok; `main`'e dokunma yok. Rapor Şef'e gider: değişen dosyalar, çalıştırılan komutlar ve
gerçek çıktıları, commit/PR, açık riskler. Ajanlara master prompt geçmişi değil, yalnızca bu sözleşme + issue +
ilgili dosyalar verilir.

## 4. İş emri ↔ backlog eşlemesi ve durum

Durumlar: `NOT_STARTED · READY · IN_PROGRESS · BLOCKED · IN_REVIEW · CI_FAILED · PREVIEW_TESTING · DONE`

| Job | Ad | Hat | Issue'lar | Bağımlılık | Öncelik | Durum |
|---|---|---|---|---|---|---|
| 01 | Ürün mimarisi ve sözleşmeler | A | #4 (kapalı), #10 (auth sözleşmesi kısmı) | — | P0 | DONE (Phase 0 belgeleri + ADR 0001–0010); API sözleşmesi uç bazında JOB 04/07/09/11 ile büyür |
| 02 | UX/UI tasarım sistemi | B | #9 | 01 | P2 | DONE (PR #65; Figma → #66) |
| 03 | DevOps ve CI temeli | F | #8, #10 (preview kısmı) | — | P0 | DONE (PR #62, #64) · ci-gate + migration CI IN_PROGRESS (#70, #71) · preview URL BLOCKED (sahip onayı) |
| 04 | Identity ve tenancy | A | #11, #12, #15 (izolasyon testleri) | 01 | P0 | DONE (PR #67) · takip: #68 idempotency IN_PROGRESS, #69 K/O izinleri NOT_STARTED |
| 05 | Next.js web platformu | B | #9 (kabuk), #13 | 02, 04 (gerçek auth için) | P1 | Kabuk DONE (PR #65) · #13 READY (Supabase Auth ortamına bağlı) |
| 06 | Flutter temeli | C | #7 (mobil kısmı), #15 | 01 | P2 | BLOCKED (Flutter/Android SDK yok — #61) |
| 07 | Katalog ve varyantlar | D | #14, #16–#20 | 04 | P1 | DONE (#75, #77) · #18 web ekranları NOT_STARTED · #20 performans NOT_STARTED |
| 08 | Toplu import motoru | D | #21–#25 | 07 | P1 | NOT_STARTED |
| 09 | Inventory ledger ve rezervasyon | D | #26, #27, #30 | 04, 07 (kimlik sözleşmesi) | P0 | DONE (#80) |
| 10 | Depo yönetimi | D/C | #28, #29, #36–#39, #41–#43 | 09, 11 (picking) | P1 | NOT_STARTED |
| 11 | Sipariş ve fiyatlandırma | A | #31–#35, #78 | 07, 09 | P1 | #31 + #78 IN_PROGRESS · #32–#35 sıradaki |
| 12 | AI WhatsApp ayrıştırıcı | A | #46–#50 | 07, 11 (taslak sözleşmesi) | P2 | NOT_STARTED · #46 (veri sözleşmesi + sentetik küme) 07 sonrası erken başlayabilir |
| 13 | Bayi portalı | B/A | yeni issue açılacak (MVP sonrası) | 04, 07, 09, 11 | P3 | NOT_STARTED |
| 14 | SaaS entitlements | A/B | #51, #52 (asgari kısmı W3'te, bkz. ROADMAP notu) | 04 | P2 | NOT_STARTED |
| 15 | Dış entegrasyonlar | A | yeni issue açılacak (MVP sonrası) | 07, 09, 11 | P3 | NOT_STARTED |
| 16 | QA, güvenlik, regresyon | E | #15, #20, #25, #30, #35, #40, #54, #58 | özelliklerle birlikte | P0 | Sürekli; test altyapısı (xUnit v3, Vitest) DONE, Playwright/Testcontainers JOB 02/04 ile gelir |
| 17 | Demo ve kullanıcı dokümanı | E | #56, #57, #59 | ilgili ekranlar | P2 | NOT_STARTED |
| 18 | Operasyon ve release hazırlığı | F | #55, #60 | altyapı + kritik modüller | P1 | NOT_STARTED |

JOB 13 ve 15 için issue, ilgili çekirdek (04/07/09/11) DONE olduğunda açılır; boşta bekleyen spekülatif issue açılmaz.

## 5. Bağımlılık grafiği

```
JOB01 ─┬─> JOB04 ─┬─> JOB07 ─┬─> JOB08
       │          │          ├─> JOB09 ─┬─> JOB11 ─┬─> JOB10 (picking)
       │          │          │          │          ├─> JOB12
       │          │          │          │          └─> JOB13, JOB15
       │          │          │          └─> JOB10 (raf/mal kabul)
       │          └─> JOB14, JOB05(#13 gerçek auth)
       ├─> JOB02 ─> JOB05 (kabuk)
       └─> JOB06 (BLOCKED: SDK)
JOB03 (bağımsız) ──> JOB18
JOB16: her işin test kapısı; JOB17: ekranlar hazır oldukça
```

Kritik yol: **04 → 07 → 09 → 11 → 10 → (06 mobil depo)**. Bu zincir dışındaki işler kapasite boşaldıkça paralel yürür.

## 6. Dalga planı (takvim değil, hazır olma sırası)

| Dalga | İşler | Giriş koşulu |
|---|---|---|
| 0 | Mevcut durum denetimi | — (tamamlandı, `STATUS.md`) |
| 1 | 04 (backend platform + auth + tenancy), 02/05 (#9 tasarım sistemi + kabuk), 03 (container + preview hazırlığı) · 06 SDK gelince | PR #62 `preview`'de |
| 2 | 07, 09, 08 hazırlığı, 16 | 04 `preview`'de |
| 3 | 11, 10, 12 hazırlığı, web sipariş ekranları, Flutter depo ekranları | 07 + 09 |
| 4 | 13, 14, 15, raporlar | çekirdek kararlı |
| 5 | 17, 18, tam regresyon, pilot | — |

## 7. Token ve maliyet kontrolü

- Ajanlara yalnızca iş emri + issue + ilgili dosya yolları verilir; repo taraması ajan kendi kapsamıyla sınırlar.
- Aynı soru için birden çok ajan çalıştırılmaz; araştırma sonucu belgeye yazılır ve tekrar kullanılır.
- CI loglarından yalnızca başarısız adımın kuyruğu okunur.
- Model: Şef ve P0 işler (auth/tenancy, stok, sipariş, güvenlik) Opus 5.5. Daha ucuz model kullanımı yalnızca ürün
  sahibi onayıyla.
- Ölçüm: Ajan bitiş bildirimleri ajan başına gerçek token sayısını verir; plan limiti doluluğu (haftalık %) uygulama
  üzerinden okunur. Para cinsinden maliyet görülemez; tahmini değer ölçüm gibi sunulmaz.
  Dalga 1 ölçümü: 3 ajan ≈ 676k token (F 134k, B 218k, A 324k); haftalık limit %62 → %63.

## 8. Aktif atamalar

Dalga 1 (DONE): PR #64 (container/preview hazırlığı), #65 (tasarım sistemi + kabuk), #67 (auth + tenancy).

| Hat | Job | Issue | Branch | Ajan | Başlangıç |
|---|---|---|---|---|---|
| A | 04 takip | #68 Idempotency-Key | `feature/68-idempotency-key` | backend (worktree) | 2026-11-04 |
| D | 07 | #16, #17 | `feature/16-catalog-core` | catalog backend + DB (worktree) | 2026-11-04 |
| F | 03 | #70, #71 | `chore/70-71-ci-gate-migrations` | devops (worktree) | 2026-11-04 |
| C | 06 | #7 | — | — | BLOCKED: Flutter SDK |

Migration'lar modül içinde (ADR 0005): Catalog ajanı yalnızca Catalog migration'larını, #68 ajanı yalnızca platform migration'ını yazar.
Ortak dosyalar (`Program.cs`, `BayiOS.slnx`, `Directory.Packages.props`, `openapi.json`): ikinci birleşen PR `preview`'i alıp çakışmayı çözer.
