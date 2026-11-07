# BayiOS — Yedekleme ve geri yükleme

| Alan | Değer |
|---|---|
| Sahip | DevOps / SRE |
| Durum | v1 (#55) — betikler + yerel geri yükleme provası **geçti** (2026-11-07). Barındırılan ortamda yedek planı ürün sahibi kararı bekliyor |
| Betikler | `infra/ops/backup.sh`, `infra/ops/restore.sh`, `infra/ops/restore-drill.sh`, `infra/ops/pg-tools.sh`, `infra/ops/sql/*.sql` |
| İlgili | `runbooks/rollback.md` (veri olayı), `environments.md` §3 (veri kuralları), data-policy §4 (yedek saklama) |
| Son güncelleme | 2026-11-07 |

## 1. Neyin yedeği, kimin sorumluluğu

| Veri | Nerede | Birincil yedek | İkincil (bizim) |
|---|---|---|---|
| İş şemaları (`identity`, `catalog`, `warehouse`, `inventory`, `pricing`, `customers`, `imports`, `orders`, `billing`, `integrations`, `reports`, `platform`, `audit`) | Supabase PostgreSQL | Supabase günlük yedek / PITR (plan) | `infra/ops/backup.sh` mantıksal dump (`pg_dump -Fc`) |
| Supabase Auth kullanıcıları (`auth` şeması) | Supabase | Supabase platform yedeği | Kapsam dışı (Supabase yönetir; şifre hash'leri dışa alınmaz) |
| Storage nesneleri (içe aktarma dosyaları, logolar) | Supabase Storage | **Supabase DB yedeği nesneleri kapsamaz** — yalnızca meta veriyi | İçe aktarma dosyaları 30 günlük geçici veridir; logolar tenant tarafından yeniden yüklenebilir. Gerekirse ayrı kopya (karar) |
| Roller (`bayios_app`, `bayios_migrator`) | Küme düzeyi | Bootstrap SQL (repo) | `restore.sh` yeniden oluşturur; **parolalar yedekte yok** → geri yüklemede yeniden verilir |
| Uzantılar (`pg_trgm`) | `extensions` şeması | Supabase'de hazır | Manifest'te kayıtlı; `restore.sh` oluşturur |

Kurallar: production dump'ı yalnızca production'a veya izole bir geri yükleme veritabanına yüklenir — preview/dev'e **asla** (environments.md §3). Dump dosyası tenant verisi içerir: şifreli, erişimi kısıtlı bir yerde (ürün sahibinin kasası) tutulur, repoya girmez (`backup.sh` repo içine yazmayı reddeder), saklama süresi data-policy §4 ile aynı (silinen veri rotasyonla düşer).

## 2. `backup.sh` — mantıksal yedek
```bash
BACKUP_DATABASE_URL='postgresql://<admin>@<host>:5432/<db>?sslmode=require' PGPASSWORD='…' \
BACKUP_DIR=/guvenli/yedekler bash infra/ops/backup.sh [etiket]
```
- Bağlanan rol **superuser veya BYPASSRLS** olmalı (FORCE RLS tablolarında `pg_dump` aksi halde hata verir). `bayios_app` ile yedek alınamaz (betik reddeder). Supabase'de `postgres` rolü kullanılır (oturum havuzlayıcı değil, doğrudan bağlantı: port 5432).
- Kapsam: `bayios_migrator`'a ait tüm şemalar (`extensions` hariç) — her şemanın `__ef_migrations_history` tablosu dahil.
- Çıktı: `<etiket>.dump` (custom format, sıkıştırılmış), `<etiket>.manifest` (şemalar, uzantılar, tablo başına satır sayısı ve RLS bayrakları, şema başına son migration), `<etiket>.sha256`.
- İstemci araçları: PATH'te `pg_dump` ≥ 17 yoksa `docker run postgres:17` kullanılır (`PG_TOOLS=local|docker|exec:<container>`). Parola komut satırına yazılmaz (`PGPASSWORD`/`.pgpass`).
- Satır sayıları dump'tan hemen önce alınır; yoğun yazma altında dump'tan biraz farklı olabilir → tam eşleşme için yazma durdurulmuş pencerede alın, aksi halde `RESTORE_COUNTS=report`.

## 3. `restore.sh` — yeni veritabanına geri yükleme
```bash
RESTORE_ADMIN_URL='postgresql://postgres@<host>:5432/postgres' PGPASSWORD='…' \
RESTORE_DATABASE=bayios_restore_20261107 RESTORE_CONFIRM=bayios_restore_20261107 \
BAYIOS_APP_PASSWORD='…' BAYIOS_MIGRATOR_PASSWORD='…' \
bash infra/ops/restore.sh /guvenli/yedekler/<etiket>.dump
```
Sıra: checksum → `CREATE DATABASE` (**var olan veritabanının üstüne asla**; `RESTORE_CONFIRM` adı tekrar ister) → roller (`infra/docker/initdb/01-roles.sql`) + parolalar (stdin üzerinden) → manifest'teki uzantılar → `pg_restore --single-transaction --exit-on-error` (sahiplik ve GRANT'lar dump'taki gibi) → doğrulama:

| Doğrulama | Ölçüt |
|---|---|
| Satır sayısı | Her tablo manifest ile aynı (`RESTORE_COUNTS=exact`, varsayılan) |
| RLS | Tablo başına `ENABLE`/`FORCE ROW LEVEL SECURITY` bayrakları kaynakla aynı |
| Migration geçmişi | Her şemanın son EF migration'ı manifest ile aynı |
| Uygulama rolü | `bayios_app` NOSUPERUSER, NOBYPASSRLS |
| Tenant izolasyonu | `bayios_app` olarak, `app.tenant_id` yokken hiçbir RLS'li tenant tablosunda tenant satırı görünmez; bir tenant seçiliyken başka tenant'ın satırı görünmez |

Herhangi biri başarısızsa betik hata verir ve veritabanı inceleme için bırakılır.

**Barındırılan hedef (yeni Supabase projesi):** önce `infra/supabase/bootstrap/bootstrap.sh --set-passwords` (roller, `extensions`/`pg_trgm`, auth hook; parolalar `\password` ile — düz metin DDL loguna düşmez), sonra
`RESTORE_TARGET=bootstrapped RESTORE_ADMIN_URL='postgresql://postgres.<ref>@…:5432/postgres?sslmode=require' RESTORE_DATABASE=postgres RESTORE_CONFIRM=postgres bash infra/ops/restore.sh <dump>` — betik veritabanı oluşturmaz, iş şemalarından biri zaten varsa reddeder. `BAYIOS_*_PASSWORD` yalnızca yerel/atılabilir sunucularda kullanılır (`ALTER ROLE … PASSWORD` `log_statement = ddl` olan sunucuda loga düşer). Supabase'in `postgres` rolü superuser değildir; `pg_restore`'un sahipliği `bayios_migrator`'a vermesi ve doğrulamanın `SET ROLE bayios_app` yapabilmesi rol üyeliği gerektirir — **bu yol henüz barındırılan bir projede prova edilmedi** (preview projesi açılınca ilk iş). Geri yüklenen veritabanı trafiğe ancak bu doğrulamalar ve `runbooks/rollback.md` §4 kontrolleri geçtikten sonra alınır.

## 4. Geri yükleme provası (RESTORE DRILL) — yerel, sentetik veri
```bash
bash infra/ops/restore-drill.sh      # bash + Docker + .NET SDK; ~3,5 dk
```
İki atılabilir container kullanır (`bayios-drill-source` 127.0.0.1:55641, `bayios-drill-target` 127.0.0.1:55642); sahibin yerel yığınına (3200, 5280, 54321, `bayios-e2e-postgres` 55532) dokunmaz.
1. `infra/ci/ef-migrations.sh` (bekleyen model değişikliği kontrolü + idempotent script'ler)
2. Kaynak: `postgres:17` + `infra/docker/initdb` + 12 modül migration'ı (`bayios_migrator`)
3. Tohum: `infra/ops/sql/drill-seed.sql` — 2 sentetik tenant; katalog, depo, defter (giriş + hasar çıkışı) ve türetilmiş bakiyeler, onaylı siparişler + rezervasyonlar, müşteriler, audit, job'lar
4. `backup.sh` → 5. **boş** `postgres:17` (rol yok) → `restore.sh`
6. Gerçek `bayios_app` parola girişiyle: tenant görünürlüğü, çapraz tenant INSERT'in RLS ile reddi, kontrol testi (RLS kapatılınca aynı sorgu diğer tenant'ları görür → kontrol boş değil), defter mutabakatı, kaynak = hedef içerik parmak izi

### Son prova sonucu (2026-11-07, Windows 11 + Docker Desktop, `postgres:17` = 17.11, worktree `feature/55-observability-backup`)

| Ölçüm | Değer |
|---|---|
| Migration script'i | 12 (idempotent, 2 kez uygulandı — `ef-migrations.sh` OK) |
| Yedeklenen şema | 13 (`audit, billing, catalog, customers, identity, imports, integrations, inventory, orders, platform, pricing, reports, warehouse`) |
| Tohum | 2 tenant, 65 ürün, 78 defter hareketi, 13 onaylı sipariş, 26 rezervasyon, 13 audit olayı |
| Dump | 274.645 bayt, `pg_dump` < 1 sn; sha256 doğrulandı |
| Tablo / satır | **60 tablo, 430 satır — manifest ile birebir** |
| RLS | 46 tabloda FORCE RLS; bayraklar kaynakla aynı |
| Migration geçmişi | 12/12 şema aynı son migration |
| `bayios_app` | NOSUPERUSER NOBYPASSRLS; 44 tenant tablosunda tenant'sız 0 satır; tenant A seçiliyken başka tenant satırı 0 (A'nın kendi 241 satırı görünür) |
| Drill kontrolleri | tenant'sız ürün 0 · A'nın ürünü 40/40 · A'dan B'nin ürünü 0 · B'nin defter hareketi 30/30 · A→B çapraz INSERT **RLS ile reddedildi** · kontrol (RLS kapalıyken) 65 · defter mutabakatı 0 uyumsuzluk |
| Parmak izi (kaynak = hedef) | `drill-a products=40 movements=48 ledger_sum=3976 on_hand=3976 reserved=80 orders=8 …` · `drill-b products=25 movements=30 ledger_sum=1235 on_hand=1235 reserved=50 orders=5 …` |
| Süre | `pg_restore` < 1 sn; restore + doğrulama 52 sn; prova toplamı 197 sn (migration üretimi dahil) |

Prova her şema değişikliğinde (yeni modül/migration) ve en az ayda bir tekrarlanır; sonuç bu tabloya yazılır. CI'a eklenmesi (`ci-migrations` sonrası) ayrı bir iş — workflow'lar bu PR'ın kapsamı dışında.

## 5. Barındırılan ortam: Supabase yedekleri ve PITR (ürün sahibi kararı)
Kaynak: https://supabase.com/docs/guides/platform/backups (2026-11-07 okundu). Fiyatlar için güncel https://supabase.com/pricing kontrol edilmelidir.

| Plan | Supabase yedeği | Not |
|---|---|---|
| Free (`bayios-preview`) | Belgede günlük yedek erişimi yalnızca Pro/Team/Enterprise için tanımlı | Preview yalnızca sentetik veri → kayıp kabul edilebilir; gerekirse `backup.sh` ile manuel dump |
| Pro | Son **7 gün** günlük yedek, panodan geri yükleme | Günlük yedekte en kötü durumda **1 günlük veri kaybı** |
| Team / Enterprise | 14 / 30 gün | — |
| PITR eklentisi (Pro ve üstü, ücretli) | Saniye hassasiyetinde herhangi bir ana dönüş; en kötü durum **RPO ≈ 2 dk** | PITR açılınca günlük yedek alınmaz; maliyet ayrıca onaylanır |

Önemli: Supabase günlük yedekleri **özel rollerin parolalarını saklamaz** → geri yüklemeden sonra `bayios_app` ve `bayios_migrator` parolaları sıfırlanıp API/CI secret'ları güncellenmelidir (`runbooks/rollback.md` §4).

**Öneri (pilot production):** Pro plan (günlük yedek, 7 gün) + haftalık `backup.sh` dump'ı ürün sahibinin şifreli depolamasına (ikinci, sağlayıcıdan bağımsız kopya) + aylık prova. Pilot sipariş hacmi büyüdüğünde (1 günlük kaybın kabul edilemediği noktada) PITR. Hedefler (öneri): **RPO 24 saat** (Pro) / **≤ 5 dk** (PITR); **RTO 4 saat**.

## 6. Ürün sahibi kararları
1. Production Supabase planı (Pro önerilir) ve PITR'in açılıp açılmayacağı (maliyet).
2. İkinci kopyanın yeri (şifreli; AB bölgesi; erişim kimde) ve saklama süresi.
3. Storage nesneleri için ayrı yedek gerekip gerekmediği.
4. RPO/RTO hedeflerinin onayı.
