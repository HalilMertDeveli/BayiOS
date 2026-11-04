# ADR 0006: PostgreSQL tabanlı job kuyruğu (`FOR UPDATE SKIP LOCKED`) ve transactional outbox

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-04

## Bağlam
Arka plan işleri: Excel/CSV import doğrulama ve içe aktarma, AI ayrıştırma çağrıları, push bildirimleri (FCM), SignalR yayınları, mutabakat, temizlik (idempotency anahtarları, eski AI metinleri). Bazı yan etkiler (bildirim) iş transaction'ı commit olduysa **kesinlikle** gerçekleşmeli, commit olmadıysa **asla** gerçekleşmemelidir. MVP'de ek altyapı (Redis, RabbitMQ, Service Bus) işletmek istenmez.

## Karar
1. **Job tablosu** `platform.jobs`: `type, payload, status, priority, run_after, attempts, max_attempts, locked_by, locked_until, dedupe_key, tenant_id`.
   - Alma:
     ```sql
     UPDATE platform.jobs SET status='running', locked_by=:worker, locked_until=now()+:lease, attempts=attempts+1
     WHERE id IN (SELECT id FROM platform.jobs
                  WHERE status='queued' AND run_after <= now()
                  ORDER BY priority DESC, run_after
                  FOR UPDATE SKIP LOCKED LIMIT :n)
     RETURNING *;
     ```
   - Başarısızlıkta üstel geri çekilme ile `run_after`; `max_attempts` sonrası `dead` + alarm. Süresi dolan lease'ler (çöken worker) bir süpürücü ile `queued`'a döner.
   - `dedupe_key` ile aynı işin çift kuyruğa girmesi engellenir.
   - Job handler'ları idempotenttir; tenant'lı işler çalışmadan önce `app.tenant_id` ayarlar.
2. **Transactional outbox** `platform.outbox_messages`: iş transaction'ı içinde yazılır. `OutboxDispatcher` hosted service'i `SKIP LOCKED` ile toplu okur, tüketicilere (FCM gönderici, SignalR yayıncısı, notification oluşturucu) iletir, `processed_at` işaretler. **En az bir kez** teslim; tüketiciler `message_id` ile idempotent.
3. Uyandırma: polling (ör. 1–2 sn, boşta geri çekilme) + isteğe bağlı `LISTEN/NOTIFY` ile gecikme düşürme (yalnızca ipucu; doğruluk polling'e dayanır).
4. Worker'lar MVP'de API sürecinde `BackgroundService` olarak çalışır; yapılandırmayla kapatılıp aynı image ayrı "worker" container'ında çalıştırılabilir.
5. Tek seferlik zamanlanmış işler (mutabakat, temizlik) job tablosuna `dedupe_key` (ör. `reconcile:2026-11-04`) ile yazan basit bir zamanlayıcı.

## Değerlendirilen alternatifler
| Alternatif | Neden seçilmedi |
|---|---|
| Hangfire (PostgreSQL storage) | Olgun, ancak kendi şeması/dashboard'u, ek bağımlılık; outbox'ı yine ayrıca çözmek gerekir. İhtiyaç büyürse yeniden değerlendirilebilir |
| Redis/RabbitMQ/Azure Service Bus | Ek altyapı ve maliyet; dual-write (DB + broker) sorunu outbox gerektirir |
| Quartz.NET | Zamanlama odaklı; kuyruk semantiği ve outbox için fazladan kod |
| Commit sonrası doğrudan yan etki (outbox yok) | Çökme anında kayıp veya commit edilmemiş veri için bildirim |
| pgmq vb. PostgreSQL uzantıları | Supabase'de uzantı mevcudiyeti/sürümü ve EF entegrasyonu belirsiz; basit tablo yeterli |

## Sonuçlar
- (+) Tek altyapı (PostgreSQL); işlem ve kuyruk aynı transaction'da.
- (+) Çoklu instance'a güvenli ölçeklenir (`SKIP LOCKED`).
- (−) Yüksek hacimde tablo şişmesi/vacuum; tamamlanan kayıtlar periyodik silinir/arşivlenir, index'ler kısmi.
- (−) Polling gecikmesi (saniyeler) — bildirim için kabul edilebilir.
- (−) Sıralı teslim garantisi yok; tüketiciler sıraya bağımlı olmamalı (ör. durum bildirimi en güncel durumu yeniden okur).
