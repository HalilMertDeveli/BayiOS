# infra/deploy — deploy hattı ve hosting şablonları

> **Durum: DEPLOY EDİLMEDİ.** BayiOS için hiçbir Fly.io / Vercel / Supabase hesabı, uygulaması veya projesi yok (#138).
> Hat hazır ve sırlar yokken güvenli: `deploy-preview.yml` "skipped: secrets missing" ile yeşil no-op,
> `deploy-production.yml` `PRODUCTION_DEPLOY_ENABLED` ayarlanana kadar kapalı.

| Dosya | Amaç |
|---|---|
| `fly.preview.toml` | API preview uygulaması (`fra`, auto-stop, `/health` kontrolü, sırlar yalnızca adla) |
| `fly.production.toml` | API production şablonu (always-on, min 1 makine — **maliyet onayı gerekir**) |
| `preflight.sh` | Hangi sırların **var olduğuna** bakar (değer basmaz): hiçbiri yok → atla; eksik → kırmızı; hepsi → devam |
| `apply-migrations.sh` | Deploy migration adımı: `manifest.txt` sırasıyla idempotent EF script'lerini **yalnızca `bayios_migrator`** ile uygular (başka rolü ve `sslmode=require`'sız URL'yi reddeder), ardından `infra/supabase/bootstrap/verify.sql` |
| `smoke.sh` | `/health` 200, `/health/ready` 200, `/api/v1/meta` ortamı |

Sıra (değişmez): **migration → API → web.** Akışın tamamı, rollback ve ürün sahibi kurulum listesi:
[`docs/operations/deploy-and-rollback.md`](../../docs/operations/deploy-and-rollback.md).

## İmaj
- Kaynak: `services/api/Dockerfile`, chiseled ASP.NET 10 runtime, non-root (UID 1654), port 8080.
- CI'da derlenir; registry'ye yalnızca deploy job'ı ve yalnızca sırlar varsa **değişmez etiketle** push eder
  (`registry.fly.io/<app>:<git-sha>`; production `:<vX.Y.Z>-<sha12>`). Fly builder'ı ve `release_command` kullanılmaz.

Onay gelmeden `fly launch`, `fly deploy`, `vercel link` veya Supabase proje oluşturma **yapılmaz**.
