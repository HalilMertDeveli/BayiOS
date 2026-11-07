# Runbook — Geri alma (API, web, migration) ve veri olayı

Ayrıntılı deploy akışı: `../deploy-and-rollback.md`. Bu runbook olay anındaki adımlardır. Production adımları ürün sahibi onayıyla.

**Karar ağacı:**
- Sorun son API deploy'undan sonra mı başladı? → §1 (önce geri al, sonra incele).
- Yalnızca web/arayüz mü? → §2.
- Migration başarısız mı / şema sorunu mu? → §3 (geri değil, **ileri** düzeltme).
- Veri yanlış/silinmiş mi? → §4.

## 1. API imajı geri alma (Fly.io — hosting onayından sonra geçerli)
1. Önceki sağlıklı release'i bul: `fly releases --app <app> --image` (imajlar değişmez `<git-sha>` etiketlidir; `latest` yok).
2. Geri al: `fly deploy --config infra/deploy/fly.preview.toml --image registry.fly.io/<app>:<önceki-sha>`
   (production: `deploy-production.yml`'i önceki etiketle `workflow_dispatch`, `production` environment onayı).
3. Fly yeni makineyi `/health` geçene kadar trafiğe almaz. Ardından doğrula:
   - `GET /health` = 200, `GET /health/ready` = `{"status":"ready"}` (DB + kuyruk), `GET /api/v1/meta` sürüm = önceki sürüm.
   - 5xx oranı alarmı temizlendi (`bayios_http_server_errors_total` artışı durdu).
4. **DB geri alınmaz:** expand → migrate → contract kuralı sayesinde önceki API güncel şemayla çalışır (§3). Önceki sürüm yeni şemayla çalışamıyorsa bu bir expand/contract ihlalidir → SEV1, §3.
5. Kök neden issue'su + olay notu; düzeltme `fix/` dalında testle gelir, yeniden deploy normal akışla.

Worker ayrı container'da çalışıyorsa (Jobs:Enabled=false API + ayrı worker) ikisi aynı imaj sürümüne geri alınır.

## 2. Web geri alma (Vercel)
1. Vercel Dashboard → Project → Deployments → son sağlıklı **Production** deployment → **Instant Rollback** (yeniden derleme yok, anlık).
2. Doğrula: giriş sayfası açılıyor, panodaki `/api/v1/meta` çağrısı başarılı, kritik akış (sipariş listesi) çalışıyor.
3. Instant Rollback sonrası otomatik production atamaları durur; düzeltme merge edilince "Promote" ile yeniden açılır.
4. `NEXT_PUBLIC_*` yanlışsa (ör. API URL'si) değer düzeltilip yeniden deploy edilir — değer pakete gömülüdür, rollback eski değeri geri getirir.

## 3. Migration: ileri-düzeltme politikası (expand / contract)
- Production'da **migration geri alınmaz** (`dotnet ef database update <önceki>` / `Down()` kullanılmaz); bozuk migration'ın üstüne **yeni, ileri yönlü** düzeltme migration'ı yazılır, normal PR + CI (`ef-migrations.sh`) + onaylı deploy ile uygulanır.
- Kural: her şema değişikliği iki sürüme bölünür.
  1. **Expand:** yeni sütun/tablo nullable veya varsayılanlı eklenir; eski kod etkilenmez. Yeni kod hem eskiyle hem yeniyle çalışır.
  2. **Migrate:** veri taşınır (idempotent, partiler halinde, `inventory_movements` gibi append-only tablolara UPDATE yok).
  3. **Contract:** eski sütun/kısıt ancak hiçbir çalışan sürüm ona dokunmuyorsa ayrı bir sürümde kaldırılır.
- **Migration adımı başarısızsa:** pipeline durur (migration → API → web sırası), API deploy edilmez. Script'ler idempotent ve `IF NOT EXISTS`'lidir; EF migration'ı kendi transaction'ındadır → yarım kalmış migration geri alınmış olur. Yapılacaklar: CI logundaki hatayı oku → düzeltme migration'ı veya script düzeltmesi PR'ı → preview'da uygula → production onayı.
- Migration'ı production'a elle/MCP ile uygulamak yasaktır (`../environments.md` §7).
- Yanlış ortamda migration (AUTOPILOT §5 durdurma koşulu): o ortamda yazmayı durdur, `__ef_migrations_history` ile neyin uygulandığını tespit et, ileri düzeltme planını ürün sahibiyle onayla.

## 4. Veri olayı (yanlış toplu değişiklik, silinmiş veri, bozulma)
1. **Durdur:** kaynak bir iş/job ise ilgili job tipini durdurmak için worker instance'ını `Jobs__Enabled=false` ile yeniden başlat (API çalışmaya devam eder); kaynak kullanıcı işlemiyse ilgili tenant'ı bilgilendir. Tam yazma durdurma gerekirse ürün sahibi kararıyla API ölçeği 0.
2. **Kapsamı belirle (salt okunur):** audit log (`audit.audit_events`, `correlation_id` ile), defter (`inventory.inventory_movements` append-only olduğu için stok geçmişi her zaman yeniden kurulabilir), `stock-inconsistency.md` mutabakat sorgusu.
3. **Düzeltme yolunu seç (tercih sırası):**
   1. Uygulama içi telafi: stok için **düzeltme hareketi** (sayım düzeltmesi/adjustment — defter asla UPDATE/DELETE edilmez), sipariş için iptal/yeniden oluşturma, içe aktarma için "geri al" özelliği.
   2. Hedefli veri düzeltmesi: bir yedekten **izole** veritabanına geri yükle (`../backup-and-restore.md` §3, yeni veritabanı adı), oradan etkilenen satırları oku, düzeltmeyi API/telafi hareketleriyle uygula. Doğrudan SQL ile production'a yazma yalnızca ürün sahibi onayıyla ve audit notuyla.
   3. Tam geri dönüş (son çare, veri kaybı: yedekten sonraki tüm yazmalar): Supabase panosundan günlük yedek / PITR restore → **rol parolaları sıfırlanır** (Supabase yedekleri özel rol parolalarını saklamaz) → API `ConnectionStrings__Default` ve CI `ConnectionStrings__Migrator` secret'ları güncellenir → `/health/ready` = `ready` → mutabakat sorgusu 0 satır → tenant'lara kayıp penceresi bildirilir.
4. Geri yüklenen veride kontrol: `restore.sh` doğrulamaları (satır sayısı, RLS bayrakları, migration geçmişi, `bayios_app` tenant izolasyonu) + `infra/ops/sql/ledger-reconciliation.sql` = 0 satır.
5. Gerçek müşteri verisinin silinmesi/aktarımı ürün sahibi onayı gerektirir (AUTOPILOT §4). Kişisel veri etkilendiyse `tenant-data-leak.md` §4 (KVKK) değerlendirilir.
