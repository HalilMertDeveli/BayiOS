# BayiOS — Alarmlar (öneri eşikleri)

| Alan | Değer |
|---|---|
| Sahip | DevOps / SRE |
| Durum | v1 — belge + örnek yapılandırma (#55). **Hiçbir alarm sistemi kurulmadı**; backend ve bildirim kanalı ürün sahibi kararı |
| Örnek kural dosyası | `infra/ops/observability/alert-rules.example.yml` (Prometheus kuralları; OTel Collector'ün Prometheus adlarıyla) |
| İlgili | `observability.md` §3–§4, `runbooks/` |
| Son güncelleme | 2026-11-07 |

Önem: **kritik** = hemen müdahale (pilot döneminde ürün sahibine/nöbetçiye anlık bildirim), **uyarı** = aynı iş günü içinde bakılır.
Eşikler 1–2 tenant'lı pilot hacmi içindir; ilk iki haftanın gerçek trafiğine göre ayarlanır.

## 1. Stok açısından kritik sinyaller

| Alarm | Sinyal | Eşik | Önem | Runbook |
|---|---|---|---|---|
| Rezervasyon reddi artışı | `bayios_inventory_reservation_shortages_total` | 15 dk'da > 10 **ve** aynı sürede onaylanan siparişin 3 katından fazla | uyarı | `runbooks/stock-inconsistency.md` |
| Eksi stok onayı (OD-09 sonrası tetikleyici: stok eksiye düşmez, sayaç hiç artmamalı) | `bayios_inventory_negative_stock_confirmed_total` | 1 saatte > 0 (kritik — hata) | kritik | `runbooks/stock-inconsistency.md` |
| Defter tutarsızlığı | `infra/ops/sql/ledger-reconciliation.sql` sonucu | > 0 satır (günlük çalıştırma önerisi; zamanlanmış job ileride) | kritik (AUTOPILOT §5 durdurma koşulu) | `runbooks/stock-inconsistency.md` |
| Dead job | `bayios_jobs_dead_lettered_total` (job tipine göre) | 15 dk'da > 0 | uyarı | `runbooks/job-queue-stuck.md` |
| Kuyruk gecikmesi | `bayios_jobs_queue_lag_seconds` / readiness `degraded` | > 5 dk (5 dk sürerse) uyarı; > 30 dk kritik | uyarı / kritik | `runbooks/job-queue-stuck.md` |
| İçe aktarma hataları | `bayios_imports_failed_total` | 1 saatte ≥ 3 | uyarı | `runbooks/job-queue-stuck.md` |
| Migration hatası | CI/deploy migration adımı (`ci-migrations`, deploy workflow'u) kırmızı | her hata | kritik — deploy otomatik durur (migration → API → web sırası) | `runbooks/rollback.md` §3 |
| 5xx oranı | `http_server_request_duration_seconds_count{status=5xx}` / tümü, `bayios_http_server_errors_total` | 5 dk'da > %2 **ve** ≥ 5 hata | kritik | `runbooks/incident-response.md` |

## 2. Diğer sinyaller

| Alarm | Sinyal | Eşik | Önem | Runbook |
|---|---|---|---|---|
| API erişilemiyor | dış uptime yoklaması `GET /health/ready` | 2 dk boyunca 503/erişilemez | kritik | `runbooks/incident-response.md` |
| AI düşüşü | `bayios_ai_fallbacks_total{reason!="quota_exceeded"}` | 15 dk'da ≥ 5 | uyarı | `runbooks/ai-provider-outage.md` |
| Kimlik doğrulama fırtınası | 401 oranı | 10 dk boyunca > %30 | uyarı | `runbooks/auth-provider-outage.md` |
| İşlenmemiş istisna | `bayios_errors_unhandled_total` | 15 dk'da ≥ 10 | uyarı | `runbooks/incident-response.md` |
| Tenant erişim reddi artışı | `Tenant access denied` log satırları (Warning) | aynı kullanıcıdan 10 dk'da ≥ 20 | uyarı (IDOR denemesi?) | `runbooks/tenant-data-leak.md` |
| Supabase kotası | Supabase panosu (DB boyutu, egress) | free planda %80 | uyarı | — |

## 3. Uygulama notları
- Metrikler yalnızca `OTEL_EXPORTER_OTLP_ENDPOINT` ayarlıysa dışarı çıkar (`observability.md` §2). Kurallar Prometheus biçiminde yazıldı; Grafana Cloud/Mimir doğrudan kullanır, diğer backend'lerde aynı sorgular kendi diline çevrilir.
- Alarm metinleri ve etiketleri kişisel veri içermez (etiketler yalnızca job tipi / route şablonu / kod).
- Migration hatası API metriği değildir: GitHub Actions bildirimleri (repo "Watch → Custom → Actions") ve korumalı ortam onayı ile izlenir.
- Bildirim kanalı ve nöbet (kim, hangi saat) ürün sahibi kararıdır; pilotta tek kişi + e-posta/Telegram önerilir.
