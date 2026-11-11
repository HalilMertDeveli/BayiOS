# BayiOS — Risk Kaydı ve Maliyet Kaydı

| Alan | Değer |
|---|---|
| Sahip | Chief Orchestrator (bakım: DevOps + Security) |
| Son güncelleme | 2026-11-04 |
| Ölçek | Olasılık (O) ve Etki (E): 1 düşük – 5 çok yüksek; Skor = O × E |

## 1. Risk kaydı

| ID | Kategori | Risk | O | E | Skor | Sahip | Önlem | Durum |
|---|---|---|---|---|---|---|---|---|
| R-01 | Güvenlik | Çapraz tenant veri sızıntısı (IDOR, eksik filtre, başlık sahteciliği, tenant'sız iş) | 3 | 5 | 15 | Backend Lead + Security | Üç katman (membership + EF filtre + RLS), izolasyon matris testleri, CI'da RLS/GRANT kontrolü | Açık (tasarımda) |
| R-02 | Teknik | Eşzamanlı siparişlerde stok tutarsızlığı / negatif stok / deadlock | 3 | 5 | 15 | Backend + Database | Ledger, `FOR UPDATE` deterministik sıra, DB kısıtları, concurrency testleri | Açık |
| R-03 | Güvenlik | Public repoya sır/müşteri verisi sızması | 2 | 5 | 10 | DevOps | Push protection, gitleaks, `.env.example`, sentetik veri kuralı | Açık |
| R-04 | Bağımlılık | Mobil geliştirme engelli (Flutter/Android SDK yok, iOS için macOS yok) | 5 | 3 | 15 | Mobile Lead | Ürün sahibi kurulumu (connectors.md §4); API önce, mobil sonra | **BLOCKED** |
| R-05 | Yasal | KVKK uyumsuzluğu (aydınlatma, DPA, yurt dışı aktarım: Supabase AB, Anthropic ABD) | 3 | 4 | 12 | Ürün sahibi + Security | data-policy.md; hukuk incelemesi; AI özelliği tenant opt-in | Açık |
| R-06 | Güvenlik | AI prompt injection / halüsinasyonla yanlış sipariş | 3 | 4 | 12 | AI Engineer | Yalnızca draft, insan onayı, deterministik eşleştirme, eval seti | Açık |
| R-07 | Maliyet | Onaysız ücretli kaynak (Supabase proje/branch, host, AI token) | 2 | 3 | 6 | Chief Orchestrator | AUTOPILOT onay sınırları, harcama limitleri, maliyet kaydı | Açık |
| R-08 | Yasal | Vercel Hobby planının ticari kullanımı yasak | 4 | 3 | 12 | Ürün sahibi | Ticari pilot öncesi Pro planı ($20/kullanıcı/ay) onayı | Açık |
| R-09 | Yasal | Marka/domain çakışması (`bayios.com` başkasına kayıtlı) | 3 | 3 | 9 | Ürün sahibi | TÜRKPATENT araştırması, alternatif domain | Açık |
| R-10 | Bağımlılık | Supabase free projelerin 1 hafta etkisizlikte duraklaması; org'da 2 aktif proje sınırı | 4 | 2 | 8 | DevOps | Dev için local Postgres; prod için Pro; gerekirse ilgisiz projeyi duraklatma (ürün sahibi kararı) | Açık |
| R-11 | Güvenlik | Kötü niyetli Excel (zip bomb, formül enjeksiyonu) | 3 | 3 | 9 | AI/Integration + Security | Limitler, sanitizer, testler | Açık |
| R-12 | Bağımlılık | Kütüphane lisans değişikliği (ör. FluentAssertions v8 ticari) | 3 | 2 | 6 | QA + Architect | Shouldly/Assert; dependency-review lisans kontrolü | Önlendi (karar) |
| R-13 | Teknik | Supabase Auth JWT doğrulama yanlış yapılandırması | 2 | 5 | 10 | Backend + Security | Katı doğrulama, `JwtValidationTests` | Açık |
| R-14 | Takvim | Tek ürün sahibi onay darboğazı (`main` merge, ücretli kaynak) | 4 | 3 | 12 | Chief Orchestrator | Toplu onay paketleri, önceden karar listesi | Açık |
| R-15 | Teknik | Ajan paralel çalışmasında çakışma / tutarsız sözleşme | 3 | 3 | 9 | Chief Orchestrator | Worktree izolasyonu, contract-first, dosya sahipliği | Açık |
| R-16 | Güvenlik | CI tedarik zinciri (ele geçirilmiş Action, fork PR) | 2 | 5 | 10 | DevOps | SHA pin, `pull_request_target` yok, en dar `permissions` | Açık |
| R-17 | Maliyet | AI token maliyetinin tenant kullanımıyla kontrolsüz artması | 3 | 3 | 9 | AI Engineer | Tenant kotası, girdi limiti, bütçe alarmı, maliyet metrikleri | Açık |
| R-18 | Bağımlılık | Hosting fiyatlarının değişmesi (ör. Hetzner 2026'da çoklu zam) | 3 | 2 | 6 | DevOps | Docker-taşınabilir dağıtım, sağlayıcıdan bağımsız yapılandırma | Açık |
| R-19 | Güvenlik | GitHub token'ının geniş kapsamı (`repo`, `workflow`) | 2 | 4 | 8 | DevOps | Fine-grained token / GitHub App'e geçiş | Açık |
| R-20 | Teknik | Ana onay mekanizmasının tek kişilik repoda çalışmaması (kendi PR'ını onaylayamama) | 4 | 2 | 8 | DevOps + Ürün sahibi | Bot hesabı/GitHub App veya environment reviewer modeli (ci-cd.md §6) | Karar bekliyor |

## 2. Maliyet kaydı

Tüm rakamlar 2026-11-04 itibarıyla resmi sayfalardan; doğrulanamayanlar belirtilmiştir. **Hiçbir ücretli kaynak açılmadı.**

| Hizmet | Ücretsiz katman / limit | Maliyeti tetikleyen | Tahmini maliyet | Onay |
|---|---|---|---|---|
| Supabase | Free: 2 aktif proje, 500 MB DB, 1 GB storage, 50k MAU, 5 GB egress, 1 hafta etkisizlikte duraklama | Pro plan; branching (yalnızca Pro+, $0,01344/branch-saat); ek compute; PITR | Pro $25/ay'dan (+compute) | Prod ve branching için **gerekli**; `bayios-preview` (free) için de proje oluşturma onayı |
| Vercel | Hobby ücretsiz ama **ticari kullanım yok** | Ticari kullanım → Pro; Password Protection $20/ay/proje | Pro $20/kullanıcı/ay | **Gerekli** (ticari pilot öncesi) |
| API host — production (Fly.io önerisi; preview Render free, #218) | Free tier yok (deneme: 2 saat makine süresi / 7 gün) | Hesap açılışı | ~$10–20/ay pilot (1 GB Frankfurt makine $7,73/ay) | **Gerekli** |
| Anthropic Claude API | Ücretsiz katman varsayılmaz | Token kullanımı | Kullanıma bağlı; tenant kotası + aylık harcama limiti ayarlanır — model fiyatları uygulama sırasında resmi sayfadan doğrulanacak | **Gerekli** (anahtar + limit) |
| Firebase (FCM) | FCM ücretsiz (Spark) | Blaze'e geçiş (başka Firebase ürünleri kullanılırsa) | 0 | Blaze için gerekli |
| GitHub | Public repo: Actions standart runner dakikaları ve güvenlik özellikleri (secret scanning, CodeQL) ücretsiz — güncel koşullar doğrulanmalı | macOS runner, büyük runner, private repo'ya geçiş | 0 (public) | macOS/ücretli runner için gerekli |
| Apple Developer Program | Yok | iOS dağıtımı | Yıllık ücret (developer.apple.com'dan doğrulanmalı) | **Gerekli** |
| Google Play Console | Yok | Android mağaza yayını | Tek seferlik kayıt ücreti (doğrulanmalı) | **Gerekli** |
| Domain | — | Satın alma | Uzantıya göre yıllık | **Gerekli** |
| Figma | Mevcut starter plan, Full seat | Plan yükseltme | Mevcut | Değişiklik için gerekli |
| Log/hata izleme (opsiyonel) | Çoğu sağlayıcıda free tier | Hacim | Seçimde belirlenir | Gerekli |
