# BayiOS — Claude çalışma kuralları

BayiOS: çok kiracılı (multi-tenant) B2B stok, depo ve sipariş yönetimi SaaS'ı.
Bu repo bir monorepo'dur. Ana ajan **Chief Orchestrator**'dır; kullanıcı (ürün sahibi) yalnızca onunla konuşur.

## Oturum başlangıcı (her zaman)
1. `STATUS.md`, `ROADMAP.md`, `BACKLOG.md`, `AUTOPILOT.md` oku.
2. `gh issue list --state open`, `gh pr list`, `gh run list -L 10` ile GitHub durumunu kontrol et.
3. Gerçek duruma göre `STATUS.md`'yi güncelle. Bellekten ilerleme uydurma.

## Teknoloji
| Katman | Seçim |
|---|---|
| API | ASP.NET Core (.NET 10 LTS), modüler monolith — `services/api` |
| Web | Next.js 16 + React 19 + TypeScript — `apps/web` |
| Mobil | Flutter (Android/iOS) — `apps/mobile` |
| Veri | Supabase PostgreSQL 17; şema = EF Core migration'ları (modül içinde, ADR 0005). `infra/supabase/` yalnızca bootstrap (DB rolleri, uzantılar, Storage politikaları) |
| Kimlik | Supabase Auth (JWT), API tarafında doğrulanır |
| Push | Firebase Cloud Messaging (yalnızca bildirim) |
| CI | GitHub Actions |

## Değişmez kurallar
- Web ve mobil **yalnızca** ASP.NET Core API üzerinden veri yazar. İstemciden doğrudan DB yazımı yok.
- Her tenant verisi `tenant_id` taşır; API her istekte üyeliği ve rolü doğrular. Çapraz tenant erişimi = kritik hata.
- API veritabanına tablo sahibi olmayan, `NOBYPASSRLS` rolüyle (`bayios_app`) bağlanır; tenant bağlamı her transaction'da `set_config('app.tenant_id', …, true)` ile kurulur. `postgres`/service-role ile uygulama bağlantısı yasak.
- Modüller arası atomik işlemler (ör. sipariş onayı + rezervasyon + outbox) istek başına paylaşılan bağlantı/transaction (UnitOfWork) ile yapılır.
- Stok miktarı doğrudan güncellenmez; tek doğruluk kaynağı `inventory_movements` (ledger). Bakiye tabloları türetilmiştir ve aynı transaction'da güncellenir.
- Rezervasyon sipariş onayında, fiziksel çıkış sevkiyatta yapılır. Eşzamanlılık testleri zorunludur.
- AI (WhatsApp ayrıştırma) fiyat/stok/ürün kodu uyduramaz; insan onayı olmadan sipariş kesinleşmez.
- Repo public: gerçek anahtar, connection string, müşteri verisi, gerçek fiyat listesi **asla** commit edilmez. `.env.example` yalnızca değişken adları içerir.

## Git akışı
- `main`: korumalı. Doğrudan push yok. Merge yalnızca ürün sahibinin açık onayıyla.
- `preview`: entegrasyon dalı; PR + yeşil CI ile merge.
- `feature/<issue>-<ad>`, `fix/<issue>-<ad>`, `chore/<issue>-<ad>`; her branch bir issue'ya bağlı, PR hedefi `preview`.
- Paralel ajanlar ayrı git worktree'de çalışır; iki ajan aynı dosyaya aynı anda dokunmaz.
- Başarısız test atlanmaz, gevşetilmez.

## Onay gerektirenler
Production deploy/migration, `main` merge, ücretli kaynak/plan, domain/DNS, canlı ödeme, gerçek WhatsApp gönderimi, gerçek veri silme, güvenlik korumasını kapatma. Ayrıntı: `AUTOPILOT.md`.

## Komutlar
- API: `dotnet build services/api/BayiOS.sln`, `dotnet test services/api/BayiOS.sln`
- Web: `npm --prefix apps/web run lint|typecheck|test|build`
- Local altyapı: `docker compose -f infra/docker/compose.yml up -d`

## Belgeler
- Ürün: `docs/product/` · Mimari: `docs/architecture/` · Kararlar: `docs/adr/`
- Güvenlik: `docs/security/` · Test: `docs/testing/` · Operasyon: `docs/operations/` · Tasarım: `docs/design/`
- Ajan rolleri: `.claude/agents/` · Organizasyon: `docs/operations/organization.md`
