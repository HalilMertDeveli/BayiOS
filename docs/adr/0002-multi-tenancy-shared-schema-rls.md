# ADR 0002: Çok kiracılık — paylaşılan veritabanı/şema, `tenant_id` + API zorlaması + PostgreSQL RLS

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-04

## Bağlam
Çok sayıda küçük/orta işletme (tenant) beklenmektedir. Çapraz tenant veri erişimi kritik güvenlik hatasıdır. Maliyet ve operasyon basitliği için tenant başına DB/şema istenmez. İstemciler DB'ye doğrudan erişmez; tüm erişim API üzerindendir ve API tek DB rolüyle bağlanır. Tek bir hatalı sorgu (unutulan `WHERE tenant_id`) veri sızıntısına yol açmamalıdır.

## Karar
1. **Paylaşılan DB, paylaşılan şema** (modül şemaları tenant'lar arasında ortaktır); tenant'a ait her satırda `tenant_id uuid NOT NULL`.
2. **Birincil zorlama API'de:**
   - Tenant `X-Tenant-Id` başlığından alınır ve kimliği doğrulanmış kullanıcının `identity.memberships` kaydına (aktif) karşı doğrulanır (kısa TTL önbellekli).
   - EF Core global query filter (`TenantId == current`) ve `SaveChanges` interceptor'ı (yeni kayıtlara damgalama, farklı tenant'a yazmayı reddetme).
3. **Derinlemesine savunma: PostgreSQL RLS** tüm tenant tablolarında `ENABLE` + `FORCE`; politika `tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid`. API her transaction başında `set_config('app.tenant_id', :id, true)` (transaction-local, `SET LOCAL` eşdeğeri, parametreli) çağırır. Ayarlanmamışsa hiçbir satır görünmez (fail-closed).
4. Çalışma zamanı rolü `bayios_app`: `NOBYPASSRLS`, tabloların sahibi değil. Migration rolü `bayios_migrator` sahiptir.
5. Supabase `anon` / `authenticated` rollerine iş şemalarında **hiçbir yetki verilmez** (açık `REVOKE`); iş şemaları Data API'de expose edilmez. Supabase yalnızca Auth ve Storage için kullanılır.
6. Her veri erişimi açık bir transaction içinde yapılır (okumalar dahil, `READ ONLY`) ki `app.tenant_id` bağlantı havuzunda sızmasın.
7. İstisnalar (memberships, tenants, platform tabloları) [data-model.md §2.3](../architecture/data-model.md#23-rls-istisnaları)'te tanımlıdır.
8. Bağlantı: Npgsql havuzu ile doğrudan bağlantı veya Supavisor **session** modu; transaction-mode pooler'da prepared statement kısıtları nedeniyle tercih edilmez. `set_config(..., true)` her iki modda da transaction ile sınırlıdır.

## Değerlendirilen alternatifler
| Alternatif | Neden seçilmedi |
|---|---|
| Tenant başına veritabanı | Yüksek maliyet, migration'ın N kez çalışması, Supabase plan limitleri |
| Tenant başına şema | Şema sayısı patlaması, EF Core model önbelleği/migration karmaşıklığı |
| Yalnızca uygulama filtresi (RLS yok) | Tek hata = sızıntı; raw SQL / `IgnoreQueryFilters` hataları yakalanmaz |
| Yalnızca RLS (uygulama filtresi yok) | Hata mesajları belirsiz, test edilebilirlik düşük; EF sorgularında tenant yüklemi index kullanımına da yardım eder |
| Tenant'ı JWT custom claim'den almak | Kullanıcı birden çok tenant'a üye olabilir; tenant değişiminde token yenileme gerekir; üyelik iptalinin anında etkili olması için DB kontrolü zaten gerekli |

## Sonuçlar
- (+) İki bağımsız katman; integration testleri hem EF filtresini hem (filtre kapatılarak) RLS'i doğrular.
- (+) Tenant kimliği istemci gövdesinden asla okunmaz.
- (−) Her istek bir transaction ve `set_config` çağrısı gerektirir (ihmal edilebilir maliyet).
- (−) Çok tenant'lı arka plan işleri (outbox, job dispatcher) özel ele alınır: platform tabloları RLS'siz, iş yürütülürken tenant bağlamı ayarlanır.
- (−) RLS politikaları raw SQL migration'larında; her yeni tablo için politika eklemeyi unutmamak adına ArchitectureTests/integration testi "RLS'siz tenant tablosu" kontrolü yapar (`pg_class.relrowsecurity` sorgusu).
- (−) Gürültülü komşu riski (büyük tenant); izleme + rate limit; gerekirse büyük tenant'ı ayrı projeye taşıma (ileride ADR).
