# ADR 0005: EF Core 10 + Npgsql; EF Core migration'ları şemanın tek doğruluk kaynağı; modül başına PostgreSQL şeması

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-04

## Bağlam
Veri erişimi .NET API'dedir. Şema değişikliklerinin kod ile birlikte sürümlenmesi, review edilebilmesi ve preview/production'a güvenli uygulanması gerekir. RLS politikaları, trigger'lar, kısmi index'ler ve `NULLS NOT DISTINCT` gibi PostgreSQL'e özgü yapılar kullanılacaktır. Repo kökündeki `CLAUDE.md` migration yeri olarak `infra/supabase/migrations`'ı anar; Supabase CLI'nın SQL migration'larıyla EF migration'larının iki kaynak olarak yaşaması drift riski taşır.

## Karar
1. ORM: **EF Core 10 + Npgsql EF Core sağlayıcısı**. Karmaşık/performans kritik stok sorguları (kilitleme, keyset) gerektiğinde raw SQL (Dapper veya `FromSql`) — yine aynı bağlantı/transaction'da.
2. **Modül başına şema ve DbContext:** `catalog`, `inventory`, `orders`, … (`HasDefaultSchema`). Migration geçmiş tablosu modül şemasında (`<schema>.__ef_migrations_history`). `public` şeması iş verisi için kullanılmaz.
3. **Tek doğruluk kaynağı EF Core migration'larıdır** (`services/api/src/Modules/<M>/.../Infrastructure/Persistence/Migrations`). RLS politikaları, `FORCE ROW LEVEL SECURITY`, grant/revoke, trigger'lar, uzantılar migration içinde `migrationBuilder.Sql(...)` ile.
4. **CI:** her modül için `dotnet ef migrations script --idempotent` ile SQL script üretilir ve PR artefaktı olarak review'a sunulur; ayrıca boş bir PostgreSQL 17 (Testcontainers) üzerinde tüm migration'lar uygulanır, ardından "model ile migration arasında bekleyen değişiklik yok" kontrolü yapılır.
5. `infra/supabase/migrations` yalnızca Supabase'e özgü, EF dışı bootstrap için kullanılır (DB rollerinin oluşturulması `bayios_migrator`/`bayios_app`, uzantı etkinleştirme, Storage bucket politikaları). İş şemaları orada **tanımlanmaz**. (CLAUDE.md bu ayrımı yansıtacak şekilde güncellendi — 2026-11-04.)
6. Uygulama: deploy öncesi ayrı adım, `bayios_migrator` rolüyle, üretilen idempotent script veya EF bundle ile. API başlangıçta **migration çalıştırmaz**. Production migration'ı ürün sahibi onayı gerektirir.
7. Geriye uyumlu (expand/contract) migration kuralı: kolon silme/yeniden adlandırma iki sürüme yayılır.
8. Adlandırma: snake_case konvansiyonu (Npgsql ekosistemindeki adlandırma konvansiyonu paketi veya model yapılandırmasıyla).

## Değerlendirilen alternatifler
| Alternatif | Neden seçilmedi |
|---|---|
| Supabase CLI SQL migration'ları kaynak, EF scaffold/elle eşleme | İki model (SQL + C#) elle senkron; drift |
| DbUp/Flyway ile saf SQL + EF yalnızca ORM | Model değişikliğinin SQL'e elle çevrilmesi; ekip hızı düşer |
| Tek DbContext, tek şema | Modül sınırları DB'de görünmez; modül çıkarma zorlaşır; ArchitectureTests ile şema sahipliği doğrulanamaz |
| Uygulama başlangıcında otomatik `Migrate()` | Çoklu instance yarışı, rol yetkisi (app rolü DDL yapmamalı), production onayı kuralıyla çelişir |

## Sonuçlar
- (+) Şema kodla aynı PR'da; idempotent script ile insan review'u.
- (+) RLS ve özel yapılar da sürümlü.
- (−) Birden çok DbContext → birden çok migration zinciri; sıralama bağımlılığı olmamalı (modüller arası FK yok kuralı bunu sağlar). Ortak bootstrap (roller, uzantılar) EF migration'larından önce uygulanır.
- (−) Raw SQL parçaları için SQL review disiplini gerekir.
- (✓) CLAUDE.md'deki migration yolu ifadesi güncellendi (Phase 0 kapısında doğrulandı).
