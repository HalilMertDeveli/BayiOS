# infra/deploy — deploy hattı ve hosting şablonları

> **Durum (#218, OD-12 — yalnızca ücretsiz katmanlar):** preview API **Render** free (Frankfurt, kök dizindeki
> [`render.yaml`](../../render.yaml)), preview web **Vercel** Hobby (`apps/web`, [`apps/web/vercel.json`](../../apps/web/vercel.json)),
> DB Supabase `bayios-preview`. Production deploy edilmedi: `deploy-production.yml` `PRODUCTION_DEPLOY_ENABLED` ayarlanana
> kadar kapalı; production hostu (öneri Fly.io) ürün sahibi kararında.

| Dosya | Amaç |
|---|---|
| `../../render.yaml` | Preview API Blueprint'i: Docker, `plan: free`, `frankfurt`, `branch: preview`, `/health`, `autoDeployTrigger: checksPass`, sırlar yalnızca adla (`sync: false`) |
| `fly.production.toml` | API production şablonu (always-on, min 1 makine — **maliyet onayı gerekir**) |
| `preflight.sh` | Hangi sırların **var olduğuna** bakar (değer basmaz): hiçbiri yok → atla; eksik → kırmızı; hepsi → devam |
| `apply-migrations.sh` | Deploy migration adımı: `manifest.txt` sırasıyla idempotent EF script'lerini **yalnızca `bayios_migrator`** ile uygular (başka rolü ve `sslmode=require`'sız URL'yi reddeder), ardından `infra/supabase/bootstrap/verify.sql` |
| `smoke.sh` | `/health` 200, `/health/ready` 200, `/api/v1/meta` ortamı |

Sıra (değişmez): **migration → API → web.** Preview'da bunu Render'ın `checksPass` tetikleyicisi sağlar: `migrate` check'i
yeşil olmadan API deploy edilmez. Akışın tamamı, rollback ve ürün sahibi kurulum listesi:
[`docs/operations/deploy-and-rollback.md`](../../docs/operations/deploy-and-rollback.md).

## İmaj
- Kaynak: `services/api/Dockerfile`, chiseled ASP.NET 10 runtime, non-root (UID 1654), port 8080.
- CI'da derlenip yerelde smoke edilir (`deploy-preview.yml` `prepare`, `ci-container.yml`). Preview'da imajı Render aynı
  Dockerfile'dan derler; production'da CI **değişmez etiketle** push eder (`registry.fly.io/<app>:<vX.Y.Z>-<sha12>`).
  Hostta release/pre-deploy komutu ile migration çalıştırılmaz.

Ücretli plan, kart veya plan yükseltmesi gerektiren hiçbir adım ürün sahibi onayı olmadan yapılmaz (OD-12).
