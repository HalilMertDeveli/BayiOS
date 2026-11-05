# STATUS — Gerçek proje durumu

> Yalnızca doğrulanmış durum. Son güncelleme: 2026-11-05 (çekirdek akış API + web'de tamam).
> İş emirleri, bağımlılık grafiği ve aktif atamalar: `docs/operations/work-orders.md`.

## Mevcut faz
Çekirdek iş akışı `preview`'de API + web olarak tamam: kayıt/onboarding → katalog + özel alanlar → toplu içe aktarma →
stok defteri + depo/raf → müşteri + fiyat listeleri → sipariş + rezervasyon → toplama/paketleme/sevkiyat → stok çıkışı.
Tüm web testleri şimdilik ağ mock'una karşı; gerçek yığın E2E (#40) yapılıyor. Flutter BLOCKED (SDK).

## GitHub
- Repo: https://github.com/HalilMertDeveli/BayiOS (public) · dallar: `main`, `preview` (ikisi de ruleset korumalı)
- Rulesets: PR zorunlu, force-push/silme yasak, review thread çözümü zorunlu. Bypass yok.
- Güvenlik: secret scanning + push protection, CodeQL, Dependabot (hedef `preview`).
- Birleşen PR'lar (`preview`, squash, tüm kontroller yeşil): #62–#65, #67, #72–#75, #77, #80–#82, #84, #85, #88, #90, #91, #93, #95, #97, #99, #100, #104.
- Zorunlu kontroller: `api build & test`, `web lint, typecheck, test & build`, `dependency review`, `ci gate`.
- Zorunlu kontroller: `api build & test`, `web lint, typecheck, test & build`, `dependency review`, `ci gate` (path-filtreli işleri toplar).
- Kapalı issue'lar (Phase 0 dışı): #6, #8, #9, #11, #12, #14, #16, #17, #19, #26, #27, #30, #68, #71.

## Durum panosu (aktif)
| İş | Issue | Branch | Durum |
|---|---|---|---|
| Koşullu/kendi kayıtları izinleri + API boşlukları + askıdaki tenant erişimi | #69, #86 | `feature/69-conditional-permissions` | IN_PROGRESS |
| Gerçek yığın E2E (web + API + Postgres, CI) | #40 | `test/40-real-stack-e2e` | IN_PROGRESS |
| Flutter temeli ve depo uygulaması | #7, #41–#45 | — | BLOCKED (SDK) |

Tamamlananlar ve iş emri durumu: `docs/operations/work-orders.md`.

## Test durumu (`preview`, son CI)
| Alan | Sonuç |
|---|---|
| API (`dotnet test`, Testcontainers Postgres 17) | 447 geçti (stok/sipariş/toplama eşzamanlılığı, idempotency, import güvenliği dahil) |
| Güvenlik korumaları | Katalog tabanlı DB guard'ları (RLS/policy/grant/trigger/SECURITY DEFINER), OpenAPI tabanlı IDOR (74 rota), X-Tenant-Id sahteciliği, rol taraması — gerçek hata yok |
| Katalog performansı (10k ürün) | yerel p95 ≤ 15,4 ms; CI'da p95 < 500 ms assert |
| Web lint / typecheck / birim / Playwright + axe / build | geçti (295 birim, 152 e2e — mock) |
| Migration (9 modül) | idempotent, iki kez uygulama, drift yok |
| API container (Trivy) | 0 zafiyet |
| Flutter | BLOCKED (SDK yok) |

## Bilinen kısıtlar
- `main` onayı "0 zorunlu onay + PR" ile korunuyor (tek kişilik hesap); sahip onayı süreç kuralı, ajan `main`'e merge etmez.
- Web dev bağımlılıklarında 16 orta seviye npm audit bulgusu (production: 0).
- Mevcut local Postgres volume'u olanlar initdb rolleri için bir kez `docker compose -f infra/docker/compose.yml down -v` çalıştırmalı.
- Koşullu (K) ve kendi kayıtları (O) izinleri şimdilik reddediliyor (#69 sürüyor): satış personeli taslak düzenleyemiyor, depo personeli sipariş detayı göremiyor.
- Askıya alınmış/kapalı tenant hâlâ erişilebilir — #69 PR'ında düzeltiliyor.
- Yeni tablo/uç ekleyen her PR güvenlik korumalarına (DeclaredGrants, ApiRoutes) kayıt eklemek zorunda (bilinçli).

## Blokerler (ürün sahibi aksiyonu) — ayrıntı #61
| Konu | Etki |
|---|---|
| Flutter SDK + Android SDK kurulumu | JOB 06 / Hat C tamamen bekliyor |
| Supabase preview projesi onayı (ücretsiz slot) | Preview DB; dev local Postgres ile sürer |
| API hosting seçimi (öneri Fly.io fra, ~$10–20/ay) | API preview URL'si |
| Vercel projesi bağlama / Pro (ticari kullanım) | Web preview URL'si |
| Lisans | Repo lisansı |
| OD-01…OD-12 + marka rengi, KVKK metni, anonimleştirme yetkisi, import limitleri | İlgili işler |

## Sonraki adım
#69/#86 ve #40 birleşince: web'i /me izinlerine geçir, açık takip işleri (#83, #89, #92, #94, #96, #98) önceliğe göre; AI WhatsApp (#46–#50) ve Flutter sahip kararlarına bağlı.
