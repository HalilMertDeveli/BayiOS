# BayiOS — Runbook'lar

| Runbook | Ne zaman |
|---|---|
| [incident-response.md](incident-response.md) | Her olayın başlangıç noktası: önem derecesi, roller, iletişim, olay sonrası inceleme |
| [rollback.md](rollback.md) | API imajı / web (Vercel) geri alma, migration ileri-düzeltme politikası, veri olayı ve yedekten dönüş |
| [tenant-data-leak.md](tenant-data-leak.md) | Bir tenant'ın verisinin başka tenant'a/yetkisiz kişiye görünmüş olabileceği şüphesi — KVKK ihlal bildirimi zaman çizelgesi |
| [stock-inconsistency.md](stock-inconsistency.md) | Bakiye ≠ defter, beklenmeyen eksi stok, rezervasyon reddi artışı |
| [job-queue-stuck.md](job-queue-stuck.md) | Kuyruk gecikmesi, dead job, içe aktarma hataları |
| [ai-provider-outage.md](ai-provider-outage.md) | Anthropic API hatası / zaman aşımı / kota |
| [auth-provider-outage.md](auth-provider-outage.md) | Supabase Auth / JWKS sorunu, 401 fırtınası |

Ortak kurallar:
- Önce **etkiyi durdur**, sonra kök neden. Kanıtı (log satırı, correlation id, sorgu çıktısı) olay notuna yaz; kişisel veri kopyalama.
- Production'da veri silme/değiştirme, `main` merge, production deploy/migration **ürün sahibi onayı** gerektirir (AUTOPILOT.md §4). Ajanlar production'a elle/MCP ile müdahale etmez.
- Tenant izolasyon hatası ve stok tutarsızlığı AUTOPILOT.md §5 **durdurma koşuludur**: ilgili iş `BLOCKED`, `STATUS.md`'ye yazılır.
- Sorgular varsayılan olarak `BEGIN READ ONLY;` içinde çalıştırılır.
- Gözlemlenebilirlik ayrıntısı: `../observability.md`, alarm eşikleri: `../alerts.md`, yedek: `../backup-and-restore.md`.
