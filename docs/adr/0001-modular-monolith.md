# ADR 0001: Modüler monolith (ASP.NET Core .NET 10)

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-04
- **Karar verenler:** Chief Orchestrator, Principal Software Architect

## Bağlam
BayiOS küçük bir ekip (insan + AI ajanlar) tarafından hızlı geliştirilecek çok kiracılı bir B2B SaaS'tır. 11 bounded context vardır (Identity&Tenancy, Catalog, Pricing, Inventory, Warehouse, Orders, Customers, Imports, Integrations, Billing&Entitlements, Audit&Notifications). Sipariş onayı + stok rezervasyonu, sevkiyat + stok çıkışı gibi akışlar modüller arası **tek ACID transaction** ister. Operasyon bütçesi ve ekip kapasitesi dağıtık sistem işletmeye uygun değildir; ancak ileride bazı modüllerin (AI, import) bağımsız ölçeklenmesi gerekebilir.

## Karar
- `services/api` altında tek dağıtılabilir ASP.NET Core (.NET 10 LTS) uygulaması: **modüler monolith**.
- Yerleşim: `src/BayiOS.Api` (host, minimal API endpoint grupları), `src/BayiOS.SharedKernel`, `src/Modules/<Module>/BayiOS.Modules.<Module>` (Domain/Application/Infrastructure/Contracts klasörleri; modül yalnızca implement edildiğinde oluşturulur), `tests/BayiOS.UnitTests`, `tests/BayiOS.IntegrationTests` (Testcontainers PostgreSQL), `tests/BayiOS.ArchitectureTests`.
- Modüller birbirinin tablolarına yazmaz; yalnızca `Contracts` ad alanındaki public arayüzler ve in-process domain event'ler ile konuşur. Kalıcı olması gereken event'ler transactional outbox ile ([ADR 0006](0006-postgres-job-queue-and-outbox.md)).
- Modül başına PostgreSQL şeması ve DbContext; modüller arası DB FK yok. Modüller arası transaction, istek başına paylaşılan bağlantı/transaction (UnitOfWork) ile.
- İzin verilen modül bağımlılık grafiği [system-architecture.md §5.1](../architecture/system-architecture.md#51-bağımlılık-kuralları-kim-kimi-çağırabilir)'de tanımlıdır ve ArchitectureTests ile zorlanır.

## Değerlendirilen alternatifler
| Alternatif | Neden seçilmedi |
|---|---|
| Mikroservisler | Dağıtık transaction (saga), ağ hataları, ayrı deploy/izleme maliyeti; ekip ve MVP takvimi için aşırı karmaşık |
| Klasik katmanlı monolith (modül sınırı yok) | Kısa vadede hızlı, uzun vadede "big ball of mud"; ajanların paralel çalışmasında çakışma artar; modül çıkarma imkânsızlaşır |
| Supabase-merkezli (PostgREST + Edge Functions + RLS ile iş mantığı) | Karmaşık stok/sipariş kurallarının SQL/Edge'e dağılması, test edilebilirlik ve .NET ekosistemi kaybı; istemcilerin doğrudan DB'ye yazmasını yasaklayan kuralla çelişir |
| Modül başına ayrı proje katmanları (Domain/Application/Infrastructure ayrı csproj) | Başlangıçta proje sayısı patlaması; tek proje + `internal` + ArchitectureTests yeterli. Gerekirse sonradan bölünür |

## Sonuçlar
- (+) Tek deploy, tek transaction, basit hata ayıklama; ajanlar modül bazında paralel çalışabilir.
- (+) Modül sınırları (şema, Contracts, event) çıkarma yolunu açık tutar ([system-architecture §8](../architecture/system-architecture.md#8-ölçeklenme-yolu)).
- (−) Sınır disiplini otomatik testlere bağlıdır; ArchitectureTests CI'da zorunlu olmalı.
- (−) Bir modüldeki bellek/CPU sorunu tüm API'yi etkiler; worker'lar gerektiğinde ayrı container rolüne alınır.
- (−) Modüller arası DB FK olmadığından referans bütünlüğü uygulama + arşivleme kuralına dayanır.
