# BayiOS — Gözlemlenebilirlik (log, trace, metrik, health, hata izleme)

| Alan | Değer |
|---|---|
| Sahip | DevOps / SRE |
| Durum | v1 — uygulandı (#55). Hiçbir veri varsayılan olarak dışarı gönderilmez; backend seçimi ürün sahibinindir |
| İlgili | `docs/security/data-policy.md` §4, §8 · `alerts.md` · `runbooks/` · ADR 0006 |
| Kod | `services/api/src/BayiOS.Api/Platform/Observability/` · `BayiOS.SharedKernel/Observability/BayiOsMetrics.cs` · `Platform/Jobs/QueueLagHealthCheck.cs` |
| Son güncelleme | 2026-11-07 |

## 1. Yapılandırılmış log

| Ortam | Çıktı |
|---|---|
| `Development` | ASP.NET Core varsayılan konsol (okunabilir metin). Yerel geliştirme içindir |
| `Preview`, `Production` (Development dışı her şey) | **Yalnızca stdout**, satır başına bir JSON nesnesi (`bayios-json` formatter). Başka log sağlayıcısı (Debug, EventLog, EventSource) yok |

Satır biçimi:
```json
{"timestamp":"2026-11-07T10:00:00.000Z","level":"Information","category":"BayiOS.Api.Platform.Observability.RequestLoggingMiddleware","eventId":1,
 "message":"HTTP GET /api/v1/products responded 200 in 12.5 ms (tenant 7c9e…, user 0f8f…)",
 "fields":{"Method":"GET","Route":"/api/v1/products","StatusCode":200,"DurationMs":12.5,"TenantId":"7c9e…","UserId":"0f8f…","CorrelationId":"0f8fad5b…","TraceId":"…","SpanId":"…"}}
```

**İstek başına bir satır** (`RequestLoggingMiddleware`): method (standart değilse `OTHER`), **route şablonu** (`/api/v1/orders/{id}` — ham path ve query string asla), durum kodu, süre (ms), `TenantId`, `UserId` (UUID). `CorrelationId` kapsamdan gelir (`X-Correlation-Id`, yalnızca GUID kabul edilir ve sunucuda yeniden biçimlenir). 5xx = `Error`, `/health*` = `Debug` (gürültü; readiness 503'ü `Warning` ve 5xx sayacına girmez — durum raporudur, sunucu hatası değil), diğerleri `Information`. Kiracı bağlamındaki tüm loglar `TenantId`/`UserId` kapsamını taşır (`TenantContextMiddleware`).

Gürültü/sızıntı yapan framework kategorileri Development dışında `Warning`'e çekilir: `Microsoft.AspNetCore` (URL + query loglar), `Microsoft.EntityFrameworkCore` (SQL metni), `System.Net.Http.HttpClient` (giden URL).

### 1.1 Redaksiyon kuralı (data-policy §8) — `LogRedactor`
Development dışı her log satırının mesajı, istisna metni, state ve scope alanları formatter'da zorunlu olarak bu kuraldan geçer; yani hatalı bir log çağrısı bile kişisel veri/sır yazamaz.

| Ne | Davranış |
|---|---|
| Alan **adı** sır/kişisel veri belirtiyorsa: `Authorization`, `Cookie`, `*password*`, `*secret*`, `*Token` (sayılar hariç: `InputTokens` kalır), `*email*`, `*phone*`, `*address*`, `*body*`, `ApiKey`, `ConnectionString`, `Name`/`FullName`, `Iban`, `Tckn`, `TaxNumber` … | değer `[redacted]` |
| `RequestPath`, `QueryString`, `Path`, `Query` alanları ve düz metin scope'lar (`"HTTP GET /path"`) | hiç yazılmaz (route şablonu yeterli) |
| Değer içindeki `Bearer …`, JWT (`eyJ….….…`) | `Bearer [redacted]`, `[jwt]` |
| URL query string'i (signed URL token'ları) | `https://host/path?[redacted]` |
| `password=`, `pwd=`, `api_key=`, `token=`, `secret=`, `cookie:`, `sig=` … | `password=[redacted]` |
| E-posta | `[email]` |
| Telefon (`+90 5xx…`, `05xx…`, `5xxxxxxxxx`, `+<10–15 hane>`) — GUID/kimlik içindeki rakamlara dokunmaz | `[phone]` |

İzin verilenler (allowlist): method, route şablonu, durum kodu, süre, tenant/user id (UUID), correlation/trace/span id, job tipi, sayılar.
İstek/yanıt gövdesi ve başlıklar API tarafından hiç okunup loglanmaz.

Testler: `tests/BayiOS.UnitTests/Platform/LogRedactionTests.cs` (kural + formatter), `tests/BayiOS.IntegrationTests/Platform/ObservabilityTests.cs`
(gerçek istek: `Authorization`, `Cookie`, e-posta query'si gönderilir → logda yok; route şablonu, durum, süre, tenant/user var).

**Saklama:** teknik loglar 30 gün (data-policy §4) — log backend'i seçilince bu süre orada ayarlanır. Log backend'i seçilene kadar loglar yalnızca host'un (ör. Fly) kısa süreli log tamponundadır.

## 2. OpenTelemetry (trace + metrik)

**Varsayılan kapalı.** `OTEL_EXPORTER_OTLP_ENDPOINT` boşsa OpenTelemetry SDK hiç kaydedilmez; hiçbir veri süreç dışına çıkmaz.
Doluysa trace ve metrikler **yalnızca OTLP** ile o uç noktaya gider — sağlayıcıdan bağımsız (Grafana Cloud, Honeycomb, SigNoz, Jaeger, Sentry OTLP, kendi OpenTelemetry Collector'ünüz…).

| Değişken | Anlam |
|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Açma anahtarı + hedef (ör. `http://otel-collector:4317`) |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` (varsayılan) veya `http/protobuf` |
| `OTEL_EXPORTER_OTLP_HEADERS` | Sağlayıcı API anahtarı (`x-api-key=…`) — **sır**, host secret'ında tutulur |
| `OTEL_SERVICE_NAME` | Varsayılan `bayios-api` (worker ayrı container'da çalışırsa `bayios-worker`) |

Kaynak öznitelikleri: `service.name`, `service.version` (assembly InformationalVersion), `deployment.environment.name` (`ASPNETCORE_ENVIRONMENT`).

**Trace kaynakları:** ASP.NET Core (gelen istek; `/health*` filtrelenir), HttpClient (Anthropic, Supabase Storage çağrıları), Npgsql (SQL; parametre değerleri yok, metin `$1` yer tutuculu), `BayiOS` ActivitySource. İstek span'ine `bayios.tenant_id` ve `enduser.id` (UUID) eklenir. Query string'ler span'lere hiç yazılmaz (sunucu span'inde `url.query` silinir, istemci span'inde `url.full` yalnızca şema + host + path — signed URL token'ları dahil); istek/yanıt başlıkları yakalanmaz; istisnalar span'e ham olarak yazılmaz (`RecordException=false`) — yalnızca hata izleme kancası (§5) redakte edilmiş halini ekler.

**Metrikler:** `BayiOS` meter'ı (§3) + ASP.NET Core (`http.server.request.duration`, durum kodu ve route etiketli → 5xx oranı) + HttpClient (`http.client.request.duration`) + `Npgsql` (bağlantı havuzu, komut süreleri) + `System.Runtime` (GC, thread pool).

Yerel deneme (ücretsiz, dış gönderim yok): `infra/ops/observability/otel-collector.example.yaml` ile bir Collector çalıştırıp `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` verin; Collector metrikleri Prometheus formatında `:8889`'da yayınlar, trace'leri `debug` exporter'ı ile konsola basar.

## 3. İş ve platform metrikleri (`BayiOS` meter)

| Metrik (OTLP adı) | Prometheus adı (Collector varsayılanı) | Tür | Etiketler | Ne zaman |
|---|---|---|---|---|
| `bayios.orders.approved` | `bayios_orders_approved_total` | counter | — | `order.approved` audit'i **commit edildikten sonra** |
| `bayios.inventory.reservation_shortages` | `bayios_inventory_reservation_shortages_total` | counter | `decision` = `blocked` \| `confirmation_required` | Rezervasyon stok yetersizliğiyle reddedildi (409) |
| `bayios.inventory.negative_stock_confirmed` | `bayios_inventory_negative_stock_confirmed_total` | counter | — | Eski `warn` politikasında eksiye düşen onaylı değişiklik. OD-09 (2026-11-09) ile hiç yayınlanmaz; değer görülürse hatadır |
| `bayios.jobs.dead_lettered` | `bayios_jobs_dead_lettered_total` | counter | `job.type` | Job `dead` oldu (deneme bitti veya lease süpürücü) |
| `bayios.jobs.retried` | `bayios_jobs_retried_total` | counter | `job.type` | Başarısız deneme, geri çekilmeyle yeniden kuyrukta |
| `bayios.imports.failed` | `bayios_imports_failed_total` | counter | `outcome` = `failed` \| `rollback_failed` | İçe aktarma başarısız bitti (commit sonrası) |
| `bayios.ai.fallbacks` | `bayios_ai_fallbacks_total` | counter | `reason` (`quota_exceeded`, `provider_error`, `timeout`, `refused`, `truncated`, `invalid_output`) | AI ayrıştırma kural tabanlı ayrıştırıcıya düştü |
| `bayios.http.server_errors` | `bayios_http_server_errors_total` | counter | `http.route` (şablon), `http.response.status_code` | 5xx yanıt |
| `bayios.errors.unhandled` | `bayios_errors_unhandled_total` | counter | `source` = `http` \| `job` | İşlenmemiş istisna (HTTP 500 / dead job) |
| `bayios.audit.access_denials` | `bayios_audit_access_denials_total` | counter | `action` (`auth.*`), `outcome` = `written` \| `suppressed` \| `dropped` \| `unknown_tenant` \| `failed` | 403 erişim reddi audit yazıcısının sonucu (#89; threat-model §7) |
| `bayios.jobs.queue_lag` | `bayios_jobs_queue_lag_seconds` | gauge | — | Vadesi gelmiş en eski `queued` job'ın yaşı (son readiness ölçümü) |
| `bayios.jobs.queue_depth` | `bayios_jobs_queue_depth` | gauge | — | Vadesi gelmiş `queued` job sayısı (son readiness ölçümü) |

Kurallar: etiketler yalnızca düşük kardinaliteli kodlar — **asla** tenant id, kullanıcı id, varlık id veya serbest metin (kardinalite + KVKK). Tenant bazlı analiz audit log'dan yapılır. Commit'e bağlı sayaçlar `UnitOfWork.OnCommitted` ile yalnızca commit başarılıysa artar (geri alınan iş sayılmaz). Kayıt testi: `tests/BayiOS.UnitTests/Platform/ObservabilityTests.cs`.

Bilinen sınır: bir komutun audit'i yazıldıktan sonra aynı komutun savepoint'i geri alınır ama dış transaction commit edilirse sayaç bir fazla sayabilir (stok motorunda audit komutun son adımıdır; pratikte oluşmaz).

## 4. Health uç noktaları ve eşikler

| Uç nokta | Amaç | Yanıt |
|---|---|---|
| `GET /health` | Liveness — bağımlılık yok. Host yeniden başlatma kararı buna bağlanır | `200 {"status":"ok"}` |
| `GET /health/ready` | Readiness — trafik alma kararı ve izleme | `200 {"status":"ready"}` · `200 {"status":"degraded"}` · `503 {"status":"unavailable"}` |

Anonim yanıtta yalnızca durum kelimesi vardır (kontrol adı, host, hata metni yok; ayrıntı sunucu logunda).

| Kontrol (`ready`) | Sağlıklı | Degraded (200) | Unavailable (503) |
|---|---|---|---|
| `database` | `SELECT 1` 5 sn içinde | — | Bağlantı yok / zaman aşımı |
| `configuration` | `ConnectionStrings:Default`, `Auth:*` geçerli | — | Eksik/geçersiz |
| `job_queue` | Vadesi gelmiş en eski job < `Health:QueueLagDegradedAfter` (varsayılan **5 dk**) | ≥ 5 dk | ≥ `Health:QueueLagUnhealthyAfter` (varsayılan **30 dk**) **ve** bu instance worker çalıştırıyorsa (`Jobs:Enabled=true`) |

Gerekçe: yalnızca API çalıştıran bir instance worker sorunu yüzünden trafikten çekilmez (bu, iş kuyruğu sorununu tam kesintiye çevirirdi) — degraded kalır ve alarm üretir. Worker'ın kendisi 30 dk boyunca kuyruğu boşaltamıyorsa onu yenilemek işe yarayabileceği için 503 verir. `Health:QueueLagUnhealthyAfter=00:00:00` 503'ü kapatır. Yalnızca handler'ı kayıtlı job tipleri sayılır (bilinmeyen tip kuyruğu "takılı" göstermez). Sorgu kısmi index `ix_jobs_queued`'u kullanır.

Host bağlama önerisi: liveness check = `/health` (Fly `[[http_service.checks]]`), readiness/izleme = `/health/ready` (dış uptime izleyicisi; `degraded` gövdesi alarm, 503 kritik alarm).

## 5. Hata izleme kancası (varsayılan kapalı)

`IErrorReporter` (`Platform/Observability/ErrorReporting.cs`) işlenmemiş her HTTP istisnasında (`IExceptionHandler`) ve her dead-letter job'da çağrılır. Bağlam yalnızca allowlist alanlardır: kaynak (`http`/`job`), route şablonu veya job tipi, tenant/user id, correlation id.

| `ErrorTracking__Provider` | Davranış |
|---|---|
| `none` (varsayılan) | Yalnızca `bayios.errors.unhandled` sayacı; dışarı bir şey gitmez |
| `otel` | Ek olarak geçerli span'e redakte edilmiş `exception` olayı (tip, mesaj, stack) + span durumu `Error`. OTLP açıksa (§2) hata izleme destekleyen her backend (Sentry'nin OTLP girişi dahil) bunu gruplar |

Sentry SDK'sı (veya başka bir satıcı SDK'sı) seçilirse: `IErrorReporter`'ın bir uygulaması olarak eklenir, `ErrorTracking__Dsn` adlı değişkenle (sır, host secret'ı) yapılandırılır, **`SendDefaultPii = false`, istek gövdesi yakalama kapalı, `BeforeSend` içinde `LogRedactor.Redact`** zorunludur (data-policy §8.4) ve data-policy §6 alt işleyen tablosuna eklenir (AB bölgesi, DPA). Bu PR satıcı paketi eklemez.

## 6. Ortam değişkenleri (ad listesi; değerler host secret'ında)
```
OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_PROTOCOL, OTEL_EXPORTER_OTLP_HEADERS (sır), OTEL_SERVICE_NAME
ErrorTracking__Provider (none|otel), ErrorTracking__Dsn (yalnızca satıcı SDK'sı eklenirse; sır)
Health__QueueLagDegradedAfter (varsayılan 00:05:00), Health__QueueLagUnhealthyAfter (varsayılan 00:30:00; 00:00:00 = kapalı)
```

## 7. Ürün sahibi kararları
| Karar | Seçenekler | Not |
|---|---|---|
| Log + trace + metrik backend'i | Grafana Cloud free tier, Honeycomb free, self-host SigNoz/Grafana (Fly'da ek makine = ücret), Sentry (hata) | Hepsi OTLP; kod değişikliği yok. AB bölgesi + DPA şart; data-policy §6'ya eklenir |
| Hata izleme | `otel` (backend'in hata görünümü) veya satıcı SDK'sı | Varsayılan kapalı |
| Uptime izleyici | Ücretsiz bir dış izleyici `/health/ready`'yi 1 dk'da bir yoklar | Alarm kanalı (e-posta/Telegram) seçimi |
