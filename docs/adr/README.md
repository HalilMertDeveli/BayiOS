# Mimari Karar Kayıtları (ADR)

BayiOS'taki önemli mimari kararlar burada kayıt altındadır. Sahip: Principal Software Architect.

## Format
Dosya adı: `NNNN-kisa-baslik.md`. Bölümler: **Durum** (Önerildi / Kabul edildi / Yerini aldı: NNNN / Reddedildi), **Tarih**, **Bağlam**, **Karar**, **Değerlendirilen alternatifler**, **Sonuçlar**.
Kabul edilmiş bir ADR değiştirilmez; yeni bir ADR yazılır ve eskisi "Yerini aldı" olarak işaretlenir.

## Dizin

| No | Başlık | Durum | Tarih |
|---|---|---|---|
| [0001](0001-modular-monolith.md) | Modüler monolith (ASP.NET Core .NET 10) | Kabul edildi | 2026-11-04 |
| [0002](0002-multi-tenancy-shared-schema-rls.md) | Çok kiracılık: paylaşılan şema, `tenant_id`, API zorlaması + RLS | Kabul edildi | 2026-11-04 |
| [0003](0003-supabase-auth-jwt-validation-in-api.md) | Supabase Auth, API'de JWKS ile JWT doğrulama, kodda rol→izin | Kabul edildi | 2026-11-04 |
| [0004](0004-ledger-based-inventory.md) | Ledger tabanlı stok, türetilmiş bakiyeler, sıralı kilitler | Kabul edildi | 2026-11-04 |
| [0005](0005-ef-core-migrations.md) | EF Core migration'ları tek doğruluk kaynağı, modül başına şema | Kabul edildi | 2026-11-04 |
| [0006](0006-postgres-job-queue-and-outbox.md) | PostgreSQL job kuyruğu ve transactional outbox | Kabul edildi | 2026-11-04 |
| [0007](0007-openapi-contract-first.md) | OpenAPI contract-first, commit'li sözleşme, CI drift kontrolü | Kabul edildi | 2026-11-04 |
| [0008](0008-web-stack-nextjs.md) | Web: Next.js 16 + React 19 + TS + Tailwind v4 + TanStack | Kabul edildi | 2026-11-04 |
| [0009](0009-mobile-stack-flutter.md) | Mobil: Flutter + Riverpod + dio + go_router | Kabul edildi | 2026-11-04 |
| [0010](0010-ai-order-parsing-human-in-the-loop.md) | AI sipariş ayrıştırma: yapılandırılmış çıktı, deterministik eşleştirme, insan onayı | Kabul edildi | 2026-11-04 |

## İlgili belgeler
- [Sistem mimarisi](../architecture/system-architecture.md)
- [Domain modeli](../architecture/domain-model.md)
- [Veri modeli](../architecture/data-model.md)
- [Stok ve sipariş algoritmaları](../architecture/inventory-and-orders.md)
- [API stratejisi](../architecture/api-strategy.md)
