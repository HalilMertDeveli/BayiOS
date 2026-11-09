# BayiOS — Release candidate raporu (RC-1) ve pilot onay sunumu

| Alan | Değer |
|---|---|
| Issue | #60 (yol haritası #148, Faz A'nın son maddesi) |
| Hazırlayan | QA Lead (Orkestra Şefi'ne raporlar) |
| Tarih | 2026-11-07 |
| Aday | `preview` @ `ba83e85be58a861dea1656baae4be9a33c6c3f0c` (2026-11-07 14:33 +03:00) |
| Hedef | Barındırma onayından sonra **preview ortamında kontrollü pilot**. Production değildir. |

Bu belgedeki her sayı 2026-11-07'de bu commit üzerinde **çalıştırılarak** ölçüldü veya bağlantısı verilen CI koşusundan
okundu. Ölçülmeyen şey "ölçülmedi" diye yazılmıştır.

---

## 1. Özet ve öneri

**Öneri: RC-1, barındırma onayı (#138) ve aşağıdaki §8(a) zorunlu sahip adımları tamamlandıktan sonra preview ortamında
1–2 işletmeyle kontrollü pilota UYGUNDUR. Production'a UYGUN DEĞİLDİR.**

Gerekçe:
- Çekirdek iş akışı (kayıt → katalog → içe aktarma → mal kabul → fiyat → müşteri → sipariş/rezervasyon → toplama →
  paket → sevkiyat → stok çıkışı → rapor) gerçek yığında (Supabase Auth + API + PostgreSQL 17 + web) uçtan uca geçiyor.
- Ölçülen tüm kalite kapıları yeşil: 874 API testi (1 canlı AI testi anahtar olmadığı için atlanır), 665 web birim testi,
  283 mock Playwright + axe testi, 10/10 gerçek yığın E2E, 0 bilinen bağımlılık açığı (NuGet, npm production, container).
- Tenant izolasyonu üç katmanda (üyelik + EF filtresi + FORCE RLS) ve OpenAPI güdümlü IDOR / rol / X-Tenant-Id
  süpürmeleriyle her PR'da test ediliyor; gerçek bulgu yok.
- Açık **kritik** ürün hatası yok. Bu ölçümde bulunan tek hata test altyapısında (orta, #153, §5).

Production'ı engelleyenler (özet; ayrıntı §7–§8): canlı ortam hesapları ve deploy hattı yok (#138, #70, #76, #10),
gözlemlenebilirlik/yedek/geri yükleme/runbook yok (#55), KVKK ve yasal metinler yok (#139, #83), bağımsız güvenlik denetimi
ve yük testi yapılmadı (#54, #58), mobil uygulama yok (Faz C, Flutter SDK).

---

## 2. Aday kimliği

- Commit: `ba83e85` — "fix(web): demo verisi, kararsız test kök nedenleri, birim adı ve kalanı kapatılmış etiket (#136, #133) (#150)".
- `preview`'e birleşen PR'lar (2026-11-04 → 2026-11-07, `gh pr list --state merged --base preview`): **54**.

| Tür | Adet | PR'lar |
|---|---:|---|
| feat | 34 | #65 #67 #74 #75 #77 #80 #81 #84 #85 #88 #90 #91 #93 #95 #97 #99 #103 #104 #109 #110 #113 #114 #116 #118 #120 #122 #124 #125 #126 #131 #132 #134 #149 #151 |
| fix | 4 | #106 #117 #127 #150 |
| test | 5 | #100 #101 #107 #108 #130 |
| chore | 3 | #62 #64 #73 |
| docs | 8 | #63 #72 #82 #112 #119 #123 #128 #135 |

Son dalga (STATUS 2026-11-06'dan sonra): #149 içe aktarma sihirbazı ve geçmişi (#94), #151 minimum stok eşikleri ve
eşiğe göre kritik stok (#137), #150 demo verisi + kararsız test kök nedenleri + UI gözlemleri (#136, #133).

Şema: 12 modül, 24 EF Core migration'ı, modül başına bir idempotent script (`infra/ci/ef-migrations.sh`).

---

## 3. Ölçülen kalite

### 3.1 Yerel ölçüm (Windows 11, Docker Desktop, 20 çekirdek; worktree `docs/60-release-candidate` = `ba83e85`)

| Kontrol | Komut | Sonuç | Süre |
|---|---|---|---|
| API tam paket (Testcontainers PostgreSQL 17) | `dotnet test --solution BayiOS.slnx` (services/api) | **875 toplam · 874 geçti · 0 kaldı · 1 atlandı** (`LiveClaudeEvaluationTests`, yalnızca Anthropic anahtarı varken) | 114 sn |
| — aynı paketin ilk koşusu | aynı komut, makine `npm ci` + vitest yükü altında | 874 test geçti, ancak assembly fixture kapanışı NRE verdi → 498 "Test Assembly Cleanup Failure", çıkış kodu 2. Kök neden: §5 B-1 (#153) | 166 sn |
| Performans (10k ürün) | `dotnet test ... -c Release --filter-trait "Category=Performance" --output detailed` | 6/6 geçti (bütçe p95 < 500 ms); tablo §3.3 | 38 sn |
| Web lint | `npm --prefix apps/web run lint` | geçti | 11 sn |
| Web typecheck | `npm --prefix apps/web run typecheck` | geçti | 5 sn |
| API tipleri ↔ sözleşme | `npm run gen:api` + `git status` | fark yok | – |
| Web birim (Vitest) | `npm --prefix apps/web test` | **665/665 geçti** (91 dosya) | 7 sn |
| Web build | `next build` (CI ile aynı placeholder ortam değişkenleri) | geçti | 10 sn |
| Mock Playwright + axe | `CI=true E2E_PORT=3310 npx playwright test` | **283/283 geçti**, yeniden deneme yok | 46 sn |
| Gerçek yığın E2E | `bash infra/ci/e2e-stack.sh up` (izole: `E2E_PG_PORT=55532 E2E_API_PORT=5380 E2E_WEB_PORT=3300`, ayrı konteyner `bayios-e2e-postgres-rc`) + `npm run test:e2e:real` (`E2E_WEB_URL`/`E2E_API_URL` ile) | **10/10 geçti**, `retries: 0` | yığın ≈ 1,6 dk + test 1,4 dk |
| NuGet açıkları | `dotnet list BayiOS.slnx package --vulnerable --include-transitive` | 16 projenin hiçbirinde açık yok | – |
| npm production açıkları | `npm audit --omit=dev` | **0** | – |
| npm tüm bağımlılıklar | `npm audit` | 8 orta (yalnızca geliştirme bağımlılıkları), 0 yüksek/kritik | – |

Gerçek yığın E2E'de geçen 10 senaryo: LED demo (toplu katalog aktarma → fiyat kademesi → kademeli fiyatlı sipariş →
kısmi sevk + kalanı kapat), motor demo (WhatsApp → inceleme → onay → barkodla toplama → sevk), sipariş → rezervasyon →
toplama → paket → sevkiyat (stok defterden doğru düşer), ikinci işletme birincinin ürününü göremez (UI + API, gerçek RLS),
pano + marka/kategori/birim ekranları, içe aktarma sihirbazı (xlsx → eşle → önizle → aktar → geri al), davet (sahip davet
eder → davetli kaydolur, doğrular, kabul eder → Satış rolü), WhatsApp → satır onayı → taslak (kural tabanlı), kayıt →
e-posta doğrulama → onboarding → pano → çıkış → giriş, yanlış şifre Türkçe hata.

### 3.2 CI (aynı commit, `preview` push)

| Workflow | Koşu | Sonuç | Ölçülen |
|---|---|---|---|
| ci-api | [42368663720](https://github.com/HalilMertDeveli/BayiOS/actions/runs/42368663720) | success | 875 toplam · 874 geçti · 0 kaldı · 1 atlandı (2 dk 42 sn) |
| ci-web | [42368663721](https://github.com/HalilMertDeveli/BayiOS/actions/runs/42368663721) | success | Vitest 91 dosya; Playwright 283 geçti (4,1 dk); `npm audit --omit=dev` 0 açık |
| codeql | [42368663756](https://github.com/HalilMertDeveli/BayiOS/actions/runs/42368663756) | success | açık code-scanning uyarısı: 0 |
| ci-container | [42367352628](https://github.com/HalilMertDeveli/BayiOS/actions/runs/42367352628) (`7c42419`, #151; #150 yalnızca web/doküman) | success | Trivy: imaj (ubuntu 24.04), `BayiOS.Api.deps.json`, ASP.NET Core 10.0.14 — HIGH/CRITICAL **0** |
| ci-migrations | [42367352567](https://github.com/HalilMertDeveli/BayiOS/actions/runs/42367352567) (`7c42419`) | success | boş DB'ye uygulama, ikinci kez uygulama, drift kontrolü |
| e2e-real (PR'da) | [42368100346](https://github.com/HalilMertDeveli/BayiOS/actions/runs/42368100346) (#150), [42366820262](https://github.com/HalilMertDeveli/BayiOS/actions/runs/42366820262) (#151) | success | gerçek yığın + demo E2E |

`e2e-real` yalnızca PR'da koşar (push'ta değil); RC commit'inin gerçek yığın kanıtı §3.1'deki yerel koşudur.

### 3.3 Performans (10.000 sentetik ürün, Release, süreç içi HTTP → API → PostgreSQL 17; senaryo başına 5 ısınma + 40 ölçüm)

| Senaryo | p50 ms | p95 ms | max ms |
|---|---:|---:|---:|
| Katalog liste: varsayılan sayfa (50) | 4,3 | 5,3 | 5,3 |
| Katalog arama: ad / SKU parçası / barkod | 8,9 / 13,3 / 13,4 | 9,3 / 14,4 / 14,1 | 9,8 / 14,7 / 14,6 |
| Barkod okutma (`/variants/lookup`) | 2,9 | 3,2 | 3,3 |
| Filtre: varyant `attr.renk` + ad (en yavaş katalog filtresi) | 12,5 | 13,8 | 14,5 |
| Derin keyset sayfası (100–180) | 4,6 | 5,4 | 6,1 |
| Stok bakiyeleri: ad / SKU / barkod araması | 18,3 / 17,4 / 18,5 | 19,3 / 18,0 / 19,6 | 21,4 / 18,2 / 20,5 |
| Stok bakiyeleri: geniş terim (5k+ eşleşme) | 16,8 | 18,6 | 43,3 |
| Kritik stok: ilk sayfa (eşik 2, 100) | 23,4 | 25,9 | 77,9 |
| Kritik stok dışa aktarma: 4.162 satır CSV / XLSX (tek koşu) | 137,9 / 259,9 | – | – |

Bütçe her senaryoda p95 < 500 ms; en yüksek p95 25,9 ms. **Sınır:** bu, tek süreç içinde ve tek kullanıcıyla ölçülen gecikmedir;
eşzamanlı kullanıcı yükü, ağ ve gerçek barındırma ölçülmedi (yük testi #58, Faz B).

### 3.4 Kritik senaryolar (test-strategy §4) ve eşzamanlılık

| # | Senaryo | Durum |
|---|---|---|
| 1, 2, 4 | Mobil ayaklı senaryolar | Mobil ayak BLOCKED (Flutter SDK); API ve web ayağı yeşil |
| 3 | Onay → rezervasyon | API + gerçek yığın E2E yeşil |
| 5 | Sevkiyat → `on_hand` azalır | API + gerçek yığın E2E yeşil |
| 6 | İptal → rezervasyon serbest | API yeşil |
| 7 | Son 1 adet için iki eşzamanlı sipariş | `Parallel_requests_for_the_last_unit_let_exactly_one_succeed` yeşil (gerçek PostgreSQL, paralel istek). Belgedeki "200 iterasyonluk gece koşusu" **yok** — #58'e taşındı |
| 8 | Tenant A ↔ B | IDOR / liste / gövde referansı / X-Tenant-Id / kimliksiz süpürmeler + gerçek yığın E2E yeşil |
| 9 | Aynı Excel iki kez | API yeşil; sihirbaz önceki aktarmayı uyarır |
| 10 | Belirsiz WhatsApp satırı | API + gerçek yığın E2E yeşil (kural tabanlı ayrıştırıcı) |

Gerçek PostgreSQL üzerinde paralel isteklerle koşan 17 eşzamanlılık testi her koşuda yeşil; örnekler: rezervasyon asla fazla
satmaz, aynı siparişin paralel onayı tek rezervasyon, iptal ↔ sevkiyat yarışında tek kazanan, tek sevkiyatın paralel
onayı stoğu bir kez düşer, paralel okutma fazla toplamaz, eşzamanlı mal kabul/çıkış tutarlı, eşzamanlı davetler
`max_users`'ı aşamaz, eşzamanlı sahip düşürmede bir aktif sahip kalır, iş kuyruğunda aynı iş iki kez verilmez.

---

## 4. Güvenlik durumu

### 4.1 Korumalar (her PR'da CI)
- **Veritabanı guard'ları** (`DatabaseSecurityGuardTests`): her iş tablosu RLS ENABLED + FORCED; politikalar yalnızca
  `bayios_app`'e; `bayios_app` yetkileri tablo bazında beyan edilenle birebir; defter (ledger) ve audit için
  UPDATE/DELETE/TRUNCATE tetikleyicileri; SECURITY DEFINER fonksiyonlar sabit `search_path`'li ve PUBLIC'e kapalı;
  `bayios_app` süper kullanıcı/BYPASSRLS değil ve hiçbir nesnenin sahibi değil; `anon`/`authenticated`/`service_role` iş
  şemalarına erişemez. Yeni tablo beyan edilmeden test kırılır.
- **OpenAPI güdümlü süpürmeler** (`TenantIsolationSweepTests`, `RolePermissionSweepTests`): `{id}` içeren her uçta IDOR
  (A'nın satır görüntüsü önce/sonra birebir), liste/arama sızıntısı, gövdede çapraz referans, X-Tenant-Id sahteciliği
  (başka tenant, tenant'sız, davetli/askıda üyelik, bilinmeyen tenant → 403), kimliksiz → 401, 6 rol × her uç izin
  matrisi. #151'deki yeni eşik uçları süpürmelere eklendi.
- **Depo güvenliği** (GitHub API, 2026-11-07): secret scanning **açık**, push protection **açık**, Dependabot güvenlik
  güncellemeleri **açık**; açık secret-scanning uyarısı **0**, açık code-scanning (CodeQL) uyarısı **0**, açık Dependabot
  uyarısı **0**. `dependency review` zorunlu kontrol. Workflow'larda `pull_request_target` yok.
- Not: tehdit modeli R6 "gitleaks CI" der; **gitleaks workflow'u yok** — koruma GitHub secret scanning + push protection'a
  dayanır (§7, R-7).

### 4.2 Tehdit modeli maddeleri

| Madde | Durum |
|---|---|
| R1 çapraz tenant, R3 Supabase istemci rolleri, R5 JWT (yalnız asimetrik, JWKS), R9 fork PR sırları | Kapalı — CI testleriyle doğrulanıyor |
| R2 tenant bağlamsız arka plan işi | Kapalı — iş tenant bağlamında, `Handler_runs_in_the_job_tenant_context_and_sees_only_that_tenant` |
| R4 eşzamanlı negatif stok / çift rezervasyon | Kapalı — §3.4 eşzamanlılık testleri |
| R7 AI uydurma/onaysız sipariş | Kapalı (pilot): AI varsayılan kapalı (`Ai:Enabled=false`), her satır insan onayı, WhatsApp kaynaklı sipariş otomatik onaylanmaz |
| R8 kötü niyetli Excel | Kapalı — 20 MB (OD-04, 2026-11-09; önce 10 MB) / 10.000 satır, `.xls`/`.xlsm` red, içerik/uzantı kontrolü, CSV formül temizleme |
| Audit (§7) | Uygulanan kodlar koddan yeniden sayıldı; `inventory.threshold_set` / `inventory.threshold_removed` eklendi. **Açık:** erişim reddi olayları (`auth.*`, `rate_limit.exceeded`, #89), katalog/fiyat değişiklik olayları (`product.*`, `price_list.*`) |
| I2 davet kabulünde e-posta doğrulaması | **Açık bağımlılık:** güvence Supabase **Confirm email** ayarına dayanır. API varsayılanı `Invitations:TrustUserMetadataEmailVerified = true` (kullanıcının yazabildiği `user_metadata.email_verified` kabul edilir). Production için: Confirm email + Secure email change açık, `email_confirmed_at`'ten üst düzey `email_verified` üreten custom access-token hook, ardından `TrustUserMetadataEmailVerified=false` |
| I7 askıya alınan üyenin oturumu | **Kalan risk (düşük):** tenant erişimi bir sonraki istekte kesilir; Supabase oturumu sonlanmaz (service-role kullanılmıyor) |
| R10 signed URL | Uygulanmadı (Storage/logo yükleme yok) |
| §9 açık sorular | Açık: barındırma bölgesi (OD-07), platform yöneticisi aktörü, AI'ya giden metinde kişi adı maskeleme (#121) |

Bağımsız (dış) güvenlik denetimi ve WCAG 2.2 AA raporu yapılmadı (#54).

---

## 5. Bu ölçümde bulunan hatalar

| ID | Önem | Bulgu | Durum |
|---|---|---|---|
| B-1 | Orta (test altyapısı; ürün hatası değil) | API paketinin ilk yerel koşusunda 874 testin tamamı geçmesine rağmen `ApiFixture.DisposeAsync` → `WebApplicationFactory.DisposeAsync` içinde `NullReferenceException`; xUnit v3 bunu 498 "Test Assembly Cleanup Failure" olarak raporladı (çıkış 2). İkinci koşu ve CI temiz. **Kök neden:** `WithWebHostBuilder` türetilen fabrikayı üst fabrikanın thread-safe olmayan `List<T>`'sine (`_derivedFactories`) ekler; 7 sınıftaki 16 çağrı paralel koleksiyonlarda eşzamanlı koşunca liste bozulabiliyor (null yuva), kapanışta NRE. Çok çekirdek + CPU yükü pencereyi büyütür. **Tekrar üretme:** makine meşgulken tam paketi birkaç kez koşun. **Önerilen düzeltme:** fixture'da kilitli `Derive(...)` yardımcısı | [#153](https://github.com/HalilMertDeveli/BayiOS/issues/153) açıldı (kod bu PR'ın kapsamı dışında) |
| B-2 | Düşük (doküman) | Kullanıcı kılavuzu ve UAT listesi yayımlanmış özellikleri "henüz yok" diye anlatıyordu (ekip/davet, iş kuralları, içe aktarma sihirbazı, pano, marka/kategori/birim, Tamamla/Kalanı kapat, eşik bazlı kritik stok) | Bu PR'da düzeltildi (§9) |

Zaten bilinen kararsız test: `AssistantApiTests` saatlik limit testi zamanlamaya bağlı (#152, Faz B). Bu ölçümdeki iki tam
koşuda geçti.

---

## 6. Özellik tamlığı — PRD §4.1 MVP tablosu (koda göre doğrulandı)

Kaynak: `packages/contracts/openapi.json` (uçlar), `apps/web/src/app` (ekranlar), modül kodu. **Hazır** = API + web +
test; **Kısmi** = bir kısmı var; **Yok** = kodda yok.

| Modül | Durum | Hazır | Eksik / kısmi |
|---|---|---|---|
| 01 Tenant & Onboarding | Kısmi | Kayıt + e-posta doğrulama, 4 adımlı sihirbaz, sektör şablonu (motor/LED/genel), para birimi, KDV, birimler, sabit 6 rol, ekip daveti/rol/askıya alma/sahiplik devri, iş kuralları, çoklu tenant seçici | İşletme profili ekranı ve logo yükleme yok (API'de `logoObjectPath` alanı var); ekrandan demo verisi yükleme/temizleme yok (yalnızca operatör betiği `demo:seed`); onboarding kontrol listesi, KVKK onayı (#83) |
| 02 Katalog | Kısmi | SKU, çoklu barkod, marka, kategori (ağaç), varyant, birim, alış/satış fiyatı, KDV, durum (aktif/pasif/arşiv), özel alanlar + filtre, çoklu fiyat listesi + kademe, bayi/grup fiyatı, minimum stok | Ürün görseli yok; ürün kartında tedarikçi yok (mal kabulde yalnızca referans); motor uyumluluğu yapılandırılmış değil, şablon özel alanlarıyla (uyumlu markalar çoklu seçim, model metin, model yılı aralığı) |
| 03 Toplu içe aktarma | Kısmi | Excel/CSV, eşleme sihirbazı + şablon, doğrulama, önizleme, satır hataları + CSV raporu, tekrar yükleme uyarısı, ürün ve fiyat listesi ayrı aktarma, arka plan işi, iptal/sürdürme, 7 gün geri alma, 10.000 satır | AI kolon önerisi yok (başlık eş anlamlıları + şablon; #22, Faz E); stok ve müşteri aktarma yok |
| 04 Stok & Depo | Kısmi | Fiziksel/rezerve/satılabilir, defter (ledger) tek doğruluk kaynağı, çoklu depo, raf/göz, mal kabul, düzeltme (iade girişi, hasar çıkışı türleri dahil), hareket geçmişi, negatif stok politikası, minimum stok/kritik | Sayım ve depolar arası transfer yok (hareket tipleri tanımlı, uç/ekran yok; #29, Faz E) |
| 05 Siparişler | Hazır | Manuel, WhatsApp metninden, web; durum makinesi; ya hep ya hiç rezervasyon; iptal; kısmi sevk + kalanı kapat; tamamla; idempotent komutlar (`Idempotency-Key`), If-Match | Onayda fiyatın yeniden çözümü ürün kararı (#61) |
| 06 WhatsApp asistanı v1 | Hazır (AI kapalı) | Yapıştırılan mesaj, yalnızca tenant kataloğuyla eşleşme, belirsizlik işaretleme, satır satır zorunlu insan onayı, PII (telefon/e-posta) gizleme, kural tabanlı ayrıştırıcı | Canlı AI ayrıştırma anahtar + model kararına bağlı; 202 + arka plan işi, tenant opt-in, kişi adı maskeleme (#121) |
| 07 Depo görevleri | Kısmi | Görev atama/üstlenme, toplama listesi (telefon tarayıcısı), raf sırası, barkod okutma, eksik bildirimi, paket kontrolü, sevkiyat onayı/iptali | Flutter mobil uygulama yok (Faz C); raf değişiminde yeniden tahsis, görev öncelik/müşteri adı (#98) |
| 08 Müşteriler & bayiler | Hazır | Müşteri/adres, bayi grupları, özel fiyat listesi, sipariş geçmişi, KVKK anonimleştirme | – |
| 09 Raporlar | Hazır | Sipariş durumları (günlük aralık), stok hareketleri, kritik stok (eşik bazlı), toplama görevleri, en çok sipariş edilenler, AI kullanımı, CSV/XLSX | Ayrı bir "stok seviyeleri" raporu yok — **Stok** ekranı ve kritik stok raporu karşılıyor |
| 10 SaaS & modüller | Hazır | Paket/ek modül entitlement (backend'de zorunlu), deneme, modül mağazası (ödeme yok) | Ödeme/tahsilat kapsam dışı (#141) |
| Platform | Kısmi | Çok kiracılılık (RLS), kimlik (Supabase JWT/JWKS), audit (append-only, 2 yıl saklama), iş kuyruğu + outbox, idempotency, işletme başına saatlik sınırlar (dosya yükleme, dışa aktarma, AI ayrıştırma) | Bildirim (FCM) yok (#44, Faz C); outbox'ın abonesi yok; erişim reddi audit'i (#89) |

---

## 7. Bilinen sorunlar ve kalan riskler

Açık issue'lar (`gh issue list --state open`, 2026-11-07): 37. Kritik etiketli açık ürün hatası **yok**.

| # | Risk | Önem | Kaynak | Pilotu engeller mi? | Production'ı engeller mi? |
|---|---|---|---|---|---|
| R-1 | Canlı ortam yok: Supabase projesi, API hosting, Vercel, alan adı, GitHub Environments | Kritik (çıkış için) | #138, #61 | **Evet** (preview ortamı gerekir) | Evet |
| R-2 | Deploy migration adımı, Supabase bootstrap (pg_trgm, roller), preview doğrulaması | Yüksek | #70, #76, #10 | **Evet** | Evet |
| R-3 | Davet güvencesi Supabase "Confirm email" ayarına bağlı; API varsayılanı `user_metadata.email_verified`'a güvenir | Yüksek | threat-model I2, #60 yorumu | Ayar açılırsa hayır | Evet (hook + `TrustUserMetadataEmailVerified=false`) |
| R-4 | Gözlemlenebilirlik, yedek/geri yükleme provası, rollback runbook'u yok | Yüksek | #55 | Hayır (sentetik/pilot veri, kısa süre) — ama pilot verisi için yedek kararı (OD-06) gerekir | Evet |
| R-5 | KVKK aydınlatma metni, kullanım koşulları, gizlilik politikası yok; onboarding'de onay adımı yok | Yüksek | #139, #83 | **Evet** (gerçek işletmenin verisi girilecekse) | Evet |
| R-6 | Bağımsız güvenlik denetimi, WCAG 2.2 AA raporu, yük testi, 200 iterasyonluk eşzamanlılık gece koşusu yapılmadı | Orta | #54, #58, #152 | Hayır | Evet |
| R-7 | gitleaks CI yok (tehdit modeli R6 varsayıyor); koruma GitHub secret scanning + push protection | Düşük | threat-model R6 | Hayır | Hayır (karar: gitleaks ekle ya da belgeyi düzelt) |
| R-8 | Erişim reddi ve katalog/fiyat değişiklikleri audit'e yazılmıyor | Orta | #89, threat-model §7 | Hayır | Değerlendirilmeli |
| R-9 | Askıya alınan üyenin Supabase oturumu sürer | Düşük | threat-model I7 | Hayır | Hayır |
| R-10 | AI ayrıştırma istek içinde eşzamanlı (bağlantı ~40 sn açık); canlı AI öncesi 202 + iş gerekli | Orta | #121 | Hayır (AI kapalı) | AI açılacaksa evet |
| R-11 | Mobil uygulama ve FCM yok; depo telefon tarayıcısıyla çalışır | Orta | Faz C | Hayır (tarayıcı akışı E2E'de) | Hayır (ürün kararı) |
| R-12 | Test altyapısı yarışı (B-1) — kırmızı yerel koşu üretebilir | Orta | #153 | Hayır | Hayır (CI kararlılığı için düzeltilmeli) |
| R-13 | Supabase yerel `minimum_password_length = 6`; web 8+ harf+rakam ister — canlı projede sunucu tarafı en az 8 olmalı | Düşük | `infra/supabase/local/supabase/config.toml` | Hayır | Ayar |
| R-14 | Ürün kararı bekleyen davranışlar: raf değişiminde yeniden tahsis, onayda fiyat yeniden çözümü, seri no kapsamı, paket limitleri | Orta | #98, #57/#147, #61 | Hayır (bilinen sınırlama olarak pilotta not edilir) | Kısmen |

---

## 8. Canlıya çıkış kontrol listesi

### (a) Ürün sahibi aksiyonları
- [ ] **#138 canlı ortam:** Supabase projesi (bölge kararı OD-07; plan, PITR ve yedek saklama OD-06), API hosting (öneri
      Fly.io fra, ~10–20 $/ay, harcama limiti ile), Vercel (ticari kullanım: Pro), alan adı, GitHub Environments
      `preview`/`production`. Maliyet onayı önce.
- [ ] Supabase bootstrap (proje başına bir kez, kendi makinenizde): `SUPABASE_DB_URL=… bash
      infra/supabase/bootstrap/bootstrap.sh --set-passwords` → `verify: OK` (`infra/supabase/bootstrap/README.md` §1).
- [ ] Supabase Auth ayarları: **Confirm email açık**, **Secure email change açık** (davet kabulü buna dayanır), minimum
      şifre uzunluğu ≥ 8, asimetrik (ES256) JWT imzalama; site/redirect URL'leri (README §3).
- [ ] Custom access-token hook'unu etkinleştir (`bayios_auth_hooks.custom_access_token`, bootstrap kurar), token'da üst
      düzey `email_verified` doğrula → sonra API'de `Invitations__TrustUserMetadataEmailVerified=false`.
- [ ] Fly uygulaması + uygulama secret'ları (adlar): `ConnectionStrings__Default` (`bayios_app`), `Auth__Authority`,
      `Auth__Audience`, `Cors__AllowedOrigins__0`, `Audit__IpHashKey`; deploy token'ı `fly tokens create deploy`.
- [ ] GitHub Environment **`preview`**: secrets `PREVIEW_MIGRATOR_CONNECTION`, `FLY_API_TOKEN`, (ops.)
      `VERCEL_DEPLOY_HOOK_URL`; variables (ops.) `FLY_APP`, `API_BASE_URL`; deployment branch yalnızca `preview`.
- [ ] GitHub Environment **`production`**: required reviewer = ürün sahibi; secrets `PRODUCTION_MIGRATOR_CONNECTION`,
      `FLY_API_TOKEN`, (ops.) `VERCEL_DEPLOY_HOOK_URL`; variables `FLY_APP`, `API_BASE_URL`; en son repo değişkeni
      `PRODUCTION_DEPLOY_ENABLED=true` (production deploy onayıyla). Ayrıntı: `deploy-and-rollback.md` §8.
- [ ] **#139 hukuki:** KVKK aydınlatma metni ve açık rıza, kullanım koşulları, gizlilik politikası, abonelik sözleşmesi,
      şirket/fatura; müşteri anonimleştirme yetkisinin kimde olacağı.
- [ ] **#61 kararlar:** OD-01…OD-12 (özellikle OD-02 paket limitleri, OD-04 import limiti, OD-06 yedek, OD-07 bölge,
      OD-08 AI sağlayıcı/bütçe, OD-09 sipariş varsayılanları, OD-10 seri no, OD-11 lisans, OD-12 ücretli kaynaklar),
      marka rengi; #98 (raf değişimi, otomatik görev, öncelik/müşteri adı); #57'nin seri no kısmı (#147).
- [ ] Anthropic API anahtarı + model kararı (yalnızca canlı AI isteniyorsa; pilot AI'sız yürüyebilir).
- [ ] Pilot işletme(ler)in seçimi ve pilot takvimi (#140).

### (b) Mühendislik aksiyonları — production öncesi (Faz B)
- [x] #70 deploy hattında idempotent migration adımı (`deploy-preview.yml` / `deploy-production.yml` iskeleti,
      `infra/deploy/apply-migrations.sh`; sırsız güvenli no-op) · #76 Supabase bootstrap (`infra/supabase/bootstrap/`:
      `pg_trgm`, roller, access-token hook, `verify.sql`; CI'da düz Postgres 17 + Supabase imajında prova).
- [ ] İlk gerçek preview deploy'u (hesaplar açıldıktan sonra) · #10 preview uçtan uca smoke (web → API → Supabase).
- [ ] #55 yapılandırılmış log + correlation id, hata izleme, yedekten geri yükleme provası, rollback ve olay runbook'u.
- [ ] `Audit__IpHashKey` (≥ 32 bayt) ortam sırrı olarak; 32 bayttan kısa anahtar başlatmayı durdurur (test edildi), boş
      bırakılırsa audit'e IP özeti yazılmaz.
- [ ] #54 güvenlik denetimi (tehdit modeli her risk için kanıt), erişilebilirlik raporu · #58 tam regresyon, yük testi,
      eşzamanlılık gece koşusu · #152 odak/URL yarışları ve kararsız limit testi · #153 fixture yarışı.
- [ ] #89 erişim reddi audit'i · #83 onboarding devamı (KVKK onayı, adres/vergi, ilk depo).
- [ ] #121 AI'yı canlıya hazırlama (yalnızca AI açılacaksa).
- [x] Gitleaks (R-7): `secret-scan.yml` (PR commit'leri + haftalık/push tam geçmiş, checksum doğrulamalı binary,
      `.gitleaks.toml` yalnızca belgeli sentetik değerler). Kalan: tehdit modeli R6 metninin güncellenmesi (Security).
- [ ] Production deploy ve production migration: **ürün sahibi onayı zorunlu** (AUTOPILOT §4).

### (c) Pilot giriş / çıkış ölçütleri
**Giriş** (hepsi gerekli):
- [ ] Preview ortamı ayakta; `/health/ready` 200; web `/giris` açılıyor; RC commit'i (veya sonrası) deploy edilmiş ve
      bu belgedeki kapılar o commit'te yeşil.
- [ ] (a)'daki Supabase Auth ayarları uygulanmış; KVKK/kullanım koşulları metni pilot işletmeye sunulmuş (#139).
- [ ] Pilot verisi için yedek kararı (OD-06) verilmiş; gerçek veri girilmeden önce geri yükleme yolu belli.
- [ ] Kullanıcılar `docs/user-guide/` ve `bilinen-sinirlamalar.md` ile bilgilendirilmiş.
- [ ] **Açık kritik hata yok.**

**Çıkış** (`docs/testing/uat-scenarios.md` §3):
- [ ] Tüm UAT senaryoları G veya kabul edilmiş gerekçeyle E.
- [ ] **Açık kritik hata yok** — kritik: çapraz işletme veri erişimi (UAT-05, UAT-17), stok çift ayrılması/çift düşmesi
      (UAT-80, UAT-117), insan onayı olmadan sipariş kesinleşmesi (UAT-95), veri kaybı.
- [ ] Kalan (K) senaryoların her birinin GitHub issue'su var; pilot geri bildirimi #140'ta toplanmış.

---

## 9. Bu PR'daki doküman düzeltmeleri
- `docs/user-guide/bilinen-sinirlamalar.md`: artık var olan özellikler listeden çıkarıldı (ekip/davet, iş kuralları, içe
  aktarma ekranı, pano, marka/kategori/birim, Tamamla, Kalanı kapat); gerçek eksikler eklendi (işletme profili/logo, davet
  e-postası, stok/müşteri aktarma, AI kolon önerisi, eşiği dosyadan aktarma, kritik stok bildirimi, raf değişimi, 2FA,
  askıya alınan oturum, görsel/tedarikçi, ekrandan demo verisi).
- `docs/user-guide/11-raporlar-ve-disa-aktarma.md`: kritik stok artık ürün/depo bazında minimum stoğa göre (#151); sabit
  eşik isteğe bağlı; yeni mesajlar.
- `docs/user-guide/README.md`, `01`, `02`, `03`, `08`, `12`, `13`: aynı "henüz yok" ifadeleri güncellendi.
- `docs/testing/uat-scenarios.md`: §1.2 kullanıcı hazırlığı, §2.4 web sihirbazı senaryoları; UAT-18/19 (ekip), 55/56
  (minimum stok), 82/83 (Kalanı kapat/Tamamla), 131 (eşik bazlı kritik stok), 154 (iş kuralları) eklendi/güncellendi.
- `docs/security/threat-model.md` §7: `inventory.threshold_set`/`inventory.threshold_removed` eklendi; uygulanmayan kod
  listesi koddan doğrulanarak tamamlandı.
- `STATUS.md`: güncel durum.

## 10. Yeniden üretme
```bash
cd services/api && dotnet test --solution BayiOS.slnx
dotnet test --solution BayiOS.slnx -c Release --filter-trait "Category=Performance" --output detailed
dotnet list BayiOS.slnx package --vulnerable --include-transitive
cd ../../apps/web && npm ci && npm run lint && npm run typecheck && npm test && npm audit --omit=dev
NEXT_PUBLIC_API_BASE_URL=http://localhost:5280 NEXT_PUBLIC_SUPABASE_URL=http://127.0.0.1:54321 \
  NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY=sb_publishable_ci-placeholder-not-a-key npm run build
CI=true E2E_PORT=3310 npx playwright test
# Gerçek yığın, sahibin yığınına dokunmadan (repo kökünden):
E2E_STATE_DIR=<geçici> E2E_PG_CONTAINER=bayios-e2e-postgres-rc E2E_PG_PORT=55532 E2E_API_PORT=5380 E2E_WEB_PORT=3300 \
  bash infra/ci/e2e-stack.sh up
E2E_WEB_URL=http://localhost:3300 E2E_API_URL=http://localhost:5380 npm --prefix apps/web run test:e2e:real
# Kapatma: API/web PID'lerini (E2E_STATE_DIR/*.pid) durdurun ve konteyneri silin. `e2e-stack.sh down` paylaşılan
# Supabase Auth yığınını da durdurur — sahibin yerel yığını çalışıyorsa kullanmayın.
```
