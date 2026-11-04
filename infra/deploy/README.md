# infra/deploy — preview hosting şablonları

> **Durum: DEPLOY EDİLMEDİ.** Bu dizindeki dosyalar yalnızca hazırlıktır. BayiOS için hiçbir Fly.io / Vercel / Supabase
> hesabı, uygulaması veya projesi oluşturulmadı. Hosting sağlayıcısı henüz **ürün sahibi tarafından onaylanmadı**
> (öneri: Fly.io `fra` — `docs/operations/hosting-options.md`).

| Dosya | Amaç |
|---|---|
| `fly.preview.toml` | API preview uygulaması için Fly.io yapılandırma şablonu (`fra`, auto-stop, `/health` kontrolü, sırlar yalnızca adla) |

Akışın tamamı (preview/production ayrımı, deploy, rollback, migration sırası, Vercel web preview kurulumu):
[`docs/operations/deploy-and-rollback.md`](../../docs/operations/deploy-and-rollback.md).

## İmaj
- Kaynak: `services/api/Dockerfile` (build context `services/api`), chiseled ASP.NET 10 runtime, non-root (UID 1654), port 8080.
- CI: `.github/workflows/ci-container.yml` imajı derler, çalıştırıp `GET /health` = 200 doğrular, Trivy ile tarar. Registry'ye **push etmez**.
- İmajda kabuk yok → Dockerfile'da `HEALTHCHECK` yok; sağlık kontrolü dışarıdan (`fly.preview.toml` `[[http_service.checks]]`).

## Ürün sahibinin onaylaması gerekenler (sırayla)
1. **Hosting sağlayıcısı + maliyet** — Fly.io free tier yok, hesap açılışında kart gerekir. Preview: `shared-cpu-1x` / 512 MB,
   auto-stop ile boşta ~0; sürekli açık kalsa 1 GB için resmi fiyat $7,73/ay (512 MB yaklaşık yarısı — **tahmini, panelde teyit edilmeli**).
   Harcama limiti/alarm kurulmalı.
2. **`bayios-preview` Supabase projesi** — free plan, `eu-central-1`; org'un 2 aktif free proje kotasının 2.'sini kullanır
   (`docs/operations/environments.md` §4).
3. **Vercel projesi** (Git entegrasyonu, root `apps/web`) — Hobby planı ticari olmayan kullanım içindir; ticari pilot öncesi Pro onayı.
4. Onaydan sonra: GitHub Environment `preview` + deploy token'ı (`FLY_API_TOKEN`, uygulamaya kısıtlı deploy token) — sır olarak, repoya değil.

Onay gelmeden `fly launch`, `fly deploy`, `vercel link` veya Supabase proje oluşturma **yapılmaz**.
