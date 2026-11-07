# Runbook — İş kuyruğu takıldı / dead job / içe aktarma hataları

**Tetik:** `/health/ready` = `degraded` (vadesi gelmiş en eski job ≥ 5 dk) veya worker instance'ında 503 (≥ 30 dk); `BayiOSJobQueueLagging` / `BayiOSJobQueueStuck`; `BayiOSJobDeadLettered`; `BayiOSImportFailures`; kullanıcı "içe aktarma bitmiyor / bildirim gelmiyor" diyor.
Kuyruk: `platform.jobs` + `platform.outbox_messages` (ADR 0006). Worker API sürecinde `BackgroundService` (veya `Jobs:Enabled=false` API + ayrı worker container'ı).

## 1. Hızlı teşhis (admin, `BEGIN READ ONLY`)
```sql
-- Durum özeti (tip başına):
SELECT type, status, count(*), min(run_after) AS oldest_run_after, max(attempts) AS max_attempts
FROM platform.jobs WHERE status IN ('queued', 'running', 'dead') GROUP BY type, status ORDER BY type, status;

-- Vadesi gelmiş bekleyenler (readiness'in ölçtüğü gecikme):
SELECT type, count(*), now() - min(run_after) AS lag FROM platform.jobs
WHERE status = 'queued' AND run_after <= now() GROUP BY type ORDER BY lag DESC;

-- Kilit sahipleri ve süresi dolmuş lease'ler (çöken worker):
SELECT locked_by, count(*), min(locked_until) FROM platform.jobs WHERE status = 'running' GROUP BY locked_by;

-- Son dead job'lar (last_error temizlenmiştir: tip + maskelenmiş mesaj, kişisel veri yok):
SELECT id, type, tenant_id, attempts, finished_at, last_error FROM platform.jobs
WHERE status = 'dead' ORDER BY finished_at DESC LIMIT 20;

-- İşlenmemiş outbox:
SELECT type, count(*), min(occurred_at) FROM platform.outbox_messages WHERE processed_at IS NULL GROUP BY type;
```
Loglar: `Background worker … started` (worker ayakta mı?), `Background {Loop} loop failed; retrying` (DB kesintisi), `Job … is dead after … attempts` (Error, istisna tipiyle), `lost its lease`.

## 2. Durumlar
| Belirti | Neden | Yapılacak |
|---|---|---|
| Kuyruk büyüyor, `running` yok, worker logu yok | Worker çalışmıyor: `Jobs__Enabled=false` olan instance tek başına; worker container'ı durmuş; yapılandırma geçersiz (`Background worker not started: configuration is invalid`) | Worker'ı başlat/ölçekle; yapılandırmayı düzelt. Readiness worker instance'ında 30 dk sonra 503 verir → host makineyi yeniler |
| `running` var, `locked_until` geçmiş | Worker çöktü | Süpürücü lease'i bir sonraki turda `queued`'a döndürür (deneme bitmişse `dead`). Kendiliğinden düzelmezse worker'ı yeniden başlat |
| Tek tip birikiyor, diğerleri akıyor | O tipin handler'ı yavaş/hatalı veya o tipin handler'ı bu dağıtımda kayıtlı değil | Loglarda o tipin hataları; son deploy (`rollback.md` §1). Kayıtsız tip readiness gecikmesine sayılmaz |
| `Background jobs loop failed` tekrar ediyor | DB erişilemiyor / bağlantı havuzu dolu | `/health/ready` database; Supabase durum sayfası; bağlantı limiti |
| Dead job (tek) | Kalıcı hata (geçersiz yük, silinmiş kayıt) | `last_error`'ı oku; job handler'ları idempotenttir. Düzeltmeden sonra yeniden kuyruğa alma: aynı job'ı `queued` yapmak yerine **uygulama akışını tekrarla** (ör. içe aktarmayı yeniden başlat). İstisnai durumda (onaylı) `UPDATE platform.jobs SET status='queued', attempts=0, run_after=now(), last_error=NULL, finished_at=NULL WHERE id=:id AND status='dead'` |
| Dead job (çok, aynı tip) | Kod hatası veya bağımlılık kesintisi (AI, Storage) | Kök neden; `ai-provider-outage.md`; düzeltme deploy'u sonrası etkilenenleri tek tek tekrarla |
| İçe aktarma `failed` | Dosya biçimi, doğrulama, Storage erişimi | İçe aktarma geçmişindeki `failure_code`; tenant'a dosyayı düzeltip yeniden yüklemesini söyle; `rollback_failed` → SEV2 (kısmi değişiklik kalmış olabilir, `stock-inconsistency.md` mutabakatı) |

## 3. Yapılmayacaklar
- `platform.jobs` satırlarını toplu silme (temizlik `platform.queue_cleanup` job'ının işidir: başarılı 7 gün, dead 30 gün).
- `tenant_id`'yi değiştirmek veya tenant'lı job'ı tenant'sız çalıştırmak (job handler'ı job'ın tenant bağlamıyla çalışır; `platform.enforce_queue_tenant` trigger'ı korur).
- Worker'ı `postgres` rolüyle çalıştırmak.

## 4. Doğrulama
Gecikme < 5 dk (`/health/ready` = `ready`, `bayios_jobs_queue_lag_seconds` düşüyor), yeni dead job yok, etkilenen tenant'ın işlemi tamamlandı.
