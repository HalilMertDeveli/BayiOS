# ADR 0008: Web — Next.js 16 App Router, React 19, TypeScript strict, Tailwind v4, Radix/shadcn, TanStack Query/Table

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-04

## Bağlam
Web uygulaması (`apps/web`) yönetici, satış ve depo masa başı kullanıcıları için yoğun veri tablosu, form ve panel ekranlarından oluşur. SEO önemsizdir; oturum açmış kullanıcı deneyimi, hızlı tablo/filtre etkileşimi, erişilebilirlik ve tutarlı tasarım sistemi önemlidir. Barındırma Vercel'dir. Tüm veri ASP.NET Core API'den gelir.

## Karar
1. **Next.js 16 (App Router) + React 19 + TypeScript `strict`**, Vercel'de barındırma.
2. **Stil/bileşen:** Tailwind CSS v4; Radix tabanlı erişilebilir primitive'ler, shadcn/ui kalıbı (bileşen kodu repoya kopyalanır, tasarım token'larıyla özelleştirilir).
3. **Veri:** TanStack Query (sunucu durumu, önbellek, invalidation, optimistic güncelleme yalnızca güvenli alanlarda), TanStack Table (sanallaştırılmış, sunucu tarafı sayfalama/sıralama/filtre — keyset cursor).
4. **Kimlik:** `@supabase/ssr` yalnızca oturum (cookie tabanlı) için; access token API'ye `Authorization: Bearer` ile iletilir. Supabase JS ile **veri tablosu sorgusu yapılmaz** (iş şemalarına zaten yetki yok).
5. **API erişimi:** `packages/contracts/openapi.json`'dan `openapi-typescript` ile üretilen tipler; tek bir API istemci katmanı `X-Tenant-Id`, `Idempotency-Key` (mutasyon başına UUID), `If-Match`, `X-Correlation-Id` başlıklarını merkezi olarak ekler; Problem Details `code` alanı ile yerelleştirilmiş hata gösterimi.
6. **Render stratejisi:** Uygulama ekranları ağırlıklı olarak Client Component + TanStack Query; Server Component'ler düzen, oturum kontrolü ve ilk yükleme için. Server Actions iş mutasyonu için **kullanılmaz** (tek yazma yolu API).
7. **Gerçek zamanlı:** Hafta 8+ SignalR JS istemcisi; olay geldiğinde ilgili query invalidation.
8. Test: birim (Vitest + Testing Library), uçtan uca (Playwright). Lint/typecheck CI'da zorunlu.

## Değerlendirilen alternatifler
| Alternatif | Neden seçilmedi |
|---|---|
| Vite + React SPA | Basit ve uygun bir seçenek; ancak Vercel entegrasyonu, oturum için sunucu tarafı middleware, gelecekteki halka açık sayfalar (pazarlama, bayi portalı) için Next.js tercih edildi |
| Blazor (WASM/Server) | Tek dil avantajı; ancak bileşen ekosistemi, yoğun tablo UX'i ve ekip/ajan verimliliği React'te daha güçlü |
| Angular | Daha ağır; ekip tercihi React |
| Next.js Server Actions ile DB'ye doğrudan yazma | "İstemciler yalnızca API üzerinden yazar" kuralını ihlal eder |
| MUI / Ant Design | Hazır görünüm ama özelleştirme ve paket boyutu; shadcn kalıbı kod sahipliği verir |

## Sonuçlar
- (+) Olgun ekosistem, güçlü tablo/form araçları, Vercel ile sıfır-yapılandırma preview ortamları.
- (−) App Router + RSC karmaşıklığı; kural: iş verisi istemci tarafında TanStack Query ile, RSC yalnızca kabuk.
- (−) Next.js major sürüm değişimleri sık; yükseltmeler ayrı chore issue'ları ile.
- (−) Tarayıcıda access token işleme: XSS'e karşı CSP ve bağımlılık denetimi zorunlu (security dokümanı).
