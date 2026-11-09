# BayiOS — Test Stratejisi

| Alan | Değer |
|---|---|
| Sahip | QA Lead (`.claude/agents/qa-lead.md`) |
| Durum | Taslak v0.1 |
| Son güncelleme | 2026-11-04 |

İlke: Coverage tek başına kalite kanıtı değildir. Kritik iş kuralları (tenant izolasyonu, stok ledger, rezervasyon,
eşzamanlılık, idempotency, AI onayı) **adıyla** test edilir. Başarısız test atlanmaz, `Skip` eklenmez, assertion gevşetilmez.

## 1. Test piramidi (uygulama bazında)

| Katman | API (.NET 10) | Web (Next.js 16) | Mobil (Flutter) |
|---|---|---|---|
| Unit (çok, hızlı) | Domain kuralları (rezervasyon hesabı, durum makinesi, fiyat), validator'lar, sanitizer'lar | Saf fonksiyonlar, hook'lar, formatlayıcılar | Model/servis, state yönetimi |
| Bileşen / widget | — | Vitest + Testing Library (bileşen, form, tablo durumları: loading/empty/error/success) | `flutter_test` widget testleri |
| Integration (gerçek DB) | `WebApplicationFactory` + Testcontainers PostgreSQL 17 + Respawn; RLS, tenant, RBAC, ledger, concurrency, job worker | API mock'lu (MSW) sayfa testleri | `integration_test` (emülatörde, mock API veya local API) |
| Contract | OpenAPI şeması ↔ uygulama (drift kontrolü), istemci tipleri üretimi | Üretilen TS tipleri derlenir | Üretilen Dart modelleri |
| E2E (az, kritik) | Playwright, local API + local DB üzerinde | Playwright + axe-core | `integration_test` + API ile çapraz senaryolar (Flutter SDK kurulunca) |

Hedef oran (yaklaşık): unit %60, integration %30, E2E %10. Integration testler ağırlığı stok/sipariş modüllerindedir.

## 2. Araçlar

| Alan | Araç | Not |
|---|---|---|
| .NET test framework | xUnit | |
| .NET assertion | **Shouldly** veya xUnit `Assert` | **FluentAssertions kullanılmaz.** v8 ile Apache 2.0'dan Xceed lisansına geçti; ticari kullanım ücretli lisans gerektirir (kaynaklar aşağıda, 2026-11-04 kontrol edildi). v7 Apache 2.0 kalsa da yeni projede bağımlılık riski alınmaz. |
| Gerçek DB | Testcontainers for .NET (`postgres:17` imajı) | Docker gerekir (yerelde mevcut: Docker 29.9.2) |
| DB sıfırlama | Respawn | Testler arası tabloları temizler; şema/migration bir kez uygulanır |
| Sentetik veri | Bogus (.NET), `@faker-js/faker` (web) | Sabit seed ile deterministik |
| Web unit/bileşen | Vitest + @testing-library/react + MSW | |
| E2E web | Playwright + `@axe-core/playwright` | WCAG 2.2 AA ihlali = test hatası (kritik/serious) |
| Mobil | `flutter_test`, `integration_test`, `mocktail` | **BLOCKED:** Flutter SDK kurulu değil |
| Yük/eşzamanlılık | xUnit + `Task.WhenAll` + `Barrier`; ileride k6 (opsiyonel) | |
| Güvenlik | Özel tenant matris testleri, gitleaks, CodeQL, `dotnet list package --vulnerable`, `npm audit` | |

Sürüm sabitleme: tüm paket sürümleri eklenirken resmi kaynaktan doğrulanır (`Directory.Packages.props` ile merkezi).

Kaynaklar (FluentAssertions lisansı): https://www.infoq.com/news/2025/01/fluent-assertions-v8-license ,
https://packages.nuget.org/packages/FluentAssertions/8.3.0/License (2026-11-04 kontrol edildi).

## 3. Test verisi stratejisi
- **Yalnızca sentetik veri.** Gerçek müşteri/firma/fiyat listesi yok (public repo).
- **Builder/Mother pattern:** `TenantBuilder`, `ProductBuilder`, `OrderBuilder` — testler yalnızca ihtiyaç duyduğu alanları belirtir.
- **Senaryo seed'leri:** `tests/fixtures/` altında iki sektör şablonu: "Demo Motor Yedek Parça" ve "Demo LED Elektronik"; her biri 2 depo, 6 rol kullanıcısı, 50 müşteri.
- **10k katalog fixture:** sabit seed ile üretilen 10.000 ürün (SKU, barkod, varyant, fiyat listesi) — arama, sayfalama, import ve performans testleri için. CSV/XLSX olarak `tests/fixtures/catalog-10k.*` (üretici script ile yeniden oluşturulabilir; büyük dosya commit edilmezse CI'da üretilir).
- **İki tenant varsayılanı:** Her integration test sınıfı en az iki tenant (A ve B) ile başlar; izolasyon kontrolleri ucuzlar.
- **Kötü niyetli fixture'lar:** formül enjeksiyonlu CSV, zip bomb xlsx, injection içeren WhatsApp metinleri (`tests/fixtures/malicious/`).
- **AI eval seti:** sentetik WhatsApp mesajları + beklenen yapılandırılmış çıktı; CI'da kayıtlı (recorded) yanıtlarla, gerçek API çağrısı yalnızca manuel/gece işinde ve bütçe limitli.

## 4. Kritik 10 E2E senaryosu

| # | Senaryo | Seviye | Sahip | Kabul ölçütü |
|---|---|---|---|---|
| 1 | Web'de ürün oluştur → mobilde görünür | API integration + Playwright (web) + Flutter integration (API üzerinden doğrulama) | Frontend Lead + Mobile Lead, QA koordine | Ürün oluşturulduktan sonra mobil katalog sorgusunda ≤ 5 sn içinde görünür |
| 2 | Mobilde mal kabul → web'de stok artar | API integration + Flutter integration + Playwright doğrulama | Mobile Lead + Backend Lead | `receipt` hareketi oluşur, `on_hand` artar, web stok ekranı yeni değeri gösterir |
| 3 | Sipariş onayı → rezervasyon | API integration (zorunlu) + Playwright | Backend Lead | `reserved` artar, `available = on_hand - reserved` azalır, `on_hand` değişmez |
| 4 | Mobilde toplama → web'de ilerleme | API integration + Flutter + Playwright | Mobile Lead + Frontend Lead | Toplanan satır sayısı web'de güncellenir |
| 5 | Sevkiyat → `on_hand` azalır | API integration (zorunlu) | Backend Lead | `shipment_out` hareketi; `on_hand` ve `reserved` aynı tx'te azalır |
| 6 | İptal → rezervasyon serbest | API integration (zorunlu) | Backend Lead | `reserved` önceki değere döner; `stock_reservations` `released`; ledger'da hareket yok (rezervasyon hareket değildir) |
| 7 | Son 1 adet için iki eşzamanlı sipariş → biri başarısız | API integration, gerçek PostgreSQL, paralel istek | Backend Lead + QA | Tam olarak bir onay başarılı, diğeri `409`/iş kuralı hatası; `reserved ≤ on_hand`; 100 tekrar koşusunda tutarlı |
| 8 | Tenant A, tenant B verisini okuyamaz/yazamaz | API integration matris (tüm endpoint'ler) + Playwright smoke | Security Engineer + Backend Lead | Her endpoint için B kaynağı → 404/403; RLS testi ayrıca geçer |
| 9 | Aynı Excel iki kez → mükerrer yok | API integration (job worker dahil) | AI & Integration Engineer | İkinci import 0 yeni kayıt (upsert by SKU) veya dosya hash'i ile "zaten içe aktarıldı"; idempotent |
| 10 | Belirsiz WhatsApp satırı → işaretlenir, otomatik seçilmez | API integration (kayıtlı AI yanıtı) + Playwright onay ekranı | AI & Integration Engineer | Satır `ambiguous`, `variant_id = null`, onay ekranında kullanıcı seçimi zorunlu |

Mobil ayağı olan senaryolardan 2 ve 4 gerçek Android emülatöründe koşar (§4.2, #45). Senaryo 1'in mobil ayağı (ürün
web'de oluşturulur → mobil stok sorgusunda görünür) aynı akışta kapsanır: ürünler API'den oluşturulur, mobil stok
sorgusu onları ve web onayının rezervasyonunu gösterir (≤ 5 sn ölçütü ayrıca ölçülmez). iOS ayağı BLOCKED (§4.2).

### 4.1 Gerçek yığın web E2E (`e2e-real`, issue #40)
`apps/web/e2e/` Playwright testleri ağ katmanında mock'ludur. Ayrıca **hiçbir şeyin mock'lanmadığı** tek bir iş
vardır: `.github/workflows/e2e-real.yml` → iş adı `e2e real stack` (ci-gate otomatik toplar). Yığın:
`infra/ci/e2e-stack.sh up` — Supabase CLI yerel Auth (ES256, JWKS; e-posta doğrulaması Mailpit'ten), boş PostgreSQL 17
(`infra/docker/initdb` rolleri, EF idempotent betikleri `bayios_migrator` ile), API (Release, `Production` ortamı,
`bayios_app` ile bağlanır), web (`next build` + `next start`). Testler: `apps/web/e2e-local/` (`npm run test:e2e:real`).

| Senaryo | Kapsam |
|---|---|
| Kayıt → doğrulama e-postası → onboarding (motosiklet şablonu) → pano → çıkış → giriş | `real-stack.spec.ts` |
| Ürün + varyant + barkod → mal kabul (barkod okutma) → fiyat listesi + fiyat → müşteri → taslak sipariş (fiyat API'den) → onaya gönder → onay (rezervasyon; /stok'ta ayrılmış) → /depo/gorevler'de "Bana ata" + barkodla toplama + tamamla → /depo/sevkiyatlar'da paket kontrolü + sevk onayı (sevkiyatta tek stok hareketi) → sipariş "Sevk Edildi", /stok'ta rafta azaldı, ledger'da tek `shipment_out`; ikinci onay (API) 409, stok oynamaz | `order-to-shipment.spec.ts` (#3, #5) |
| İkinci işletme (ayrı kayıt) A'nın ürününü UI'da (liste, detay, stok) ve API'de (404, boş arama, sahte `X-Tenant-Id` → 403) göremez; A pozitif kontrol | `order-to-shipment.spec.ts` (#8 smoke) |
| Pilot demo motor (#56): küçük sentetik demo API'den kurulur → bayi WhatsApp mesajı → inceleme (belirsiz satır elle seçilir, ekranda fiyat yok) → taslak (Bayi A fiyatı = API teklifi) → onay → raf + barkodla toplama (yanlış ürün reddi) → paket → sevk → /stok | `demo-motor.spec.ts` (#10) |
| Pilot demo LED (#57): XLSX katalog içe aktarma (Imports API) → arayüzde fiyat kademesi → raflı mal kabul → kademe fiyatlı sipariş → toplamada hasarlı eksik → kısmi sevk → kalanı kapatma → /stok → rapor CSV/XLSX indirme | `demo-led.spec.ts` (#9 yolu) |

Tam boy demo verisi ayrıca `.github/workflows/demo-seed.yml` (`demo seed (twice)`) ile iki kez kurulur; ikinci
çalıştırmanın hiçbir şeyi değiştirmemesi zorunludur. Ayrıntı: `docs/operations/demo.md`.

Yeniden deneme yok (`retries: 0`); veriler çalıştırma başına benzersiz ve sentetik. Yerelde çalıştırma: `apps/web/README.md`.

### 4.2 Web ↔ mobil gerçek akış + Android smoke (`e2e-mobile`, issue #45)
`.github/workflows/e2e-mobile.yml` → iş adı `e2e web-mobile flow (android emulator)` (path filtreli: mobil, API,
sözleşme, `apps/web/e2e-local`, yığın betikleri; ci-gate otomatik toplar). Aynı `e2e-stack.sh` yığını + KVM'li Android
emülatörü (`ReactiveCircus/android-emulator-runner`, SHA sabit; API 34 x86_64, AVD + açılış snapshot'ı önbellekli).
Tek komut, yerelde de aynısı: `apps/mobile/tool/e2e.sh` (`apps/mobile/README.md`). Mock yok; her çalıştırma kendi
işletmesini, kullanıcılarını ve ürünlerini kurar.

| Faz | Kapsam | Dosya |
|---|---|---|
| 1 web | API: işletme + şablon + raflı depo (3 raf) + A/B/C ürünleri (barkodlu) + A/B mal kabul + varsayılan fiyat listesi + müşteri; **warehouse_staff** davet edilir ve kendi Supabase hesabıyla kabul eder. Web: sahip siparişi barkodla oluşturur (A×3, B×2), onaya gönderir, onaylar → /stok'ta ayrılmış; havuzda tek açık görev | `apps/web/e2e-local/web-mobile-1-prepare.spec.ts` |
| 2 mobil | Personel girişi/çıkışı (smoke). Akış: pano rolü "Depo personeli"; A'nın stokunda web onayının rezervasyonu, **"Düzelt" yok**; mal kabulde bilinmeyen kod reddi → C rafına 4 adet → bakiye; görevler → havuz → "Üstlen ve başla"; A rafı, bilinmeyen kod ("Barkod tanınmadı"), **yanlış ürün** C ("Yanlış ürün", kalan değişmez), A iki okutmada; B için **yanlış raf** ("Yanlış raf"), sonra doğru raf + B; tamamla → taslak sevkiyat | `apps/mobile/integration_test/smoke_test.dart`, `web_flow_test.dart` |
| 3 web | API: görev `completed`, atanan = personel, satırlar tam toplandı, sevkiyat var; ledger: C'de tek `receipt` (raf C, `createdBy` = personel), A'da toplamadan hareket yok. **Yetki ayrımı** personelin kendi token'ıyla: `POST /inventory/adjustments` → 403 `auth.forbidden` (hareket oluşmaz), sipariş fiyatsız (`totals`/`unitPrice` null). Web: görev "Tamamlandı" + atanan, sipariş "Paketlendi", /stok A/B rafta aynı + ayrılmış, C 4 (raf C). Sahip paket kontrolü + sevk → "Sevk Edildi", rafta azaldı, ayrılmış 0, iki `shipment_out` | `apps/web/e2e-local/web-mobile-2-verify.spec.ts` |

iOS: Windows'ta derlenemez; macOS runner `docs/operations/ci-cd.md` §1 gereği ürün sahibi onayı ister → **BLOCKED**
(macOS runner onayı; dağıtım için Apple Developer hesabı). Ayrıntı: `apps/mobile/README.md` "iOS".

## 5. Eşzamanlılık test tasarımı
- **Ortam:** Testcontainers PostgreSQL 17 (in-memory/SQLite **yasak** — kilit davranışı farklı).
- **Kurulum:** ürün P, depo W, `on_hand = 1`, `reserved = 0`; iki onaylanmaya hazır sipariş (her biri 1 adet).
- **Yürütme:** iki ayrı `HttpClient`/DI scope, `Barrier(2)` ile aynı anda `POST /orders/{id}/approve`.
- **Doğrulama:** tam olarak 1 başarı; `reserved = 1`; 1 aktif `stock_reservations` satırı, ledger'da hareket yok; deadlock yok.
- **Tekrar:** test 50–100 iterasyon döngüde koşar (CI'da 20, gece işinde 200).
- **Deadlock senaryosu:** iki sipariş aynı iki ürünü ters sırada içerir → deterministik kilit sırası (`variant_id, warehouse_id, location_id` — inventory-and-orders.md §2.1) sayesinde deadlock yok; `lock_timeout` aşımı yok.
- **Idempotency:** aynı `Idempotency-Key` ile 10 paralel istek → 1 etki, diğerleri aynı yanıtı döner.
- **Worker:** 2 worker örneği, 100 iş → her iş tam bir kez işlenir (`SKIP LOCKED`).
- **Ledger tutarlılık invariant'ı:** her concurrency testinin sonunda `SUM(movements) = balances` kontrolü.
- **Mimari invariant'lar:** `docs/architecture/inventory-and-orders.md` §5'teki I1–I13 ve C1–C8 her biri adlandırılmış bir test olarak yazılır; bu bölüm onların asgari uygulamasıdır.

## 6. PR başına CI kapıları
| Kapı | Koşul |
|---|---|
| Build | API, web (ve Flutter eklendiğinde mobil) derlenir |
| Format/lint | `dotnet format --verify-no-changes`, ESLint, `flutter analyze` |
| Typecheck | `tsc --noEmit` |
| Unit + integration | Tamamı geçer; tenant izolasyon ve concurrency paketleri zorunlu |
| Contract drift | OpenAPI üretilen dosya ile commit'lenen aynı |
| Güvenlik | CodeQL, dependency review (High+ bloklar), gitleaks, vulnerable package check |
| E2E smoke | Playwright kritik akış alt kümesi (#3, #5, #7, #8 API; web smoke) |
| Erişilebilirlik | axe-core: critical/serious ihlal yok |
| Migration | Boş DB'ye tüm migration'lar uygulanır; RLS/GRANT kontrol SQL'i geçer |

Ayrıntı: `docs/operations/ci-cd.md`.

## 7. Flaky test politikası
1. Flaky test tespit edilince **aynı gün** issue açılır (`flaky` etiketi), sahibi atanır.
2. Test **atlanmaz, silinmez, gevşetilmez.** Kök neden bulunur (zamanlama, paylaşılan durum, sıra bağımlılığı, saat).
3. CI'da otomatik "retry until pass" yok. (Playwright `retries` yalnızca rapor amaçlı 1 olabilir; yeniden denemede geçen test "flaky" olarak raporlanır ve issue zorunlu.)
4. Kök neden bulunana kadar ilgili PR'lar merge edilmez; acil durumda ürün sahibi kararı gerekir ve STATUS.md'ye yazılır.
5. Zamana bağlı testlerde `TimeProvider` (fake clock) kullanılır; `Task.Delay` ile senkronizasyon yasak.
6. **Yeniden üretme:** CI koşucusu yerel makineden yavaştır. Web E2E flake'i yerelde
   `E2E_PORT=<boş port> CI=1 npx playwright test <spec> --repeat-each 20 --retries 0` ile, gerekirse CDP
   `Emulation.setCPUThrottlingRate` (ör. 6×) ya da çekirdek sayısından fazla `--workers` ile yük bindirerek üretilir;
   düzeltme aynı koşulda tekrar koşturularak kanıtlanır.

### 7.1 Bilinen web E2E flake kök nedenleri (#136)
| Belirti | Kök neden | Doğru düzeltme |
|---|---|---|
| `İçeriğe geç` / `main#main` odak testi (shell) | `/pano` gibi rotaların önceden üretilen HTML'i yalnızca yükleniyor iskeletini içerir; kabuk (atlama bağlantısı dahil) istemcide çizilir ve `goto` döndükten sonra gelebilir. Erken `Tab` odaklanacak öğe bulamaz. | Klavyeden önce gerçek kabuğu bekle (ör. `h1` görünür). |
| Hata uyarısı odak testi (rapor dışa aktarma 429) | **Uygulama hatası:** `await` sonrası `setState` + `requestAnimationFrame(() => ref.focus())`. React güncellemeyi sonraki görevde işler; yük altında kare önce çalışır, uyarı henüz DOM'da yoktur ve odak `<body>`'de kalır (klavye/ekran okuyucu kullanıcısı da etkilenir). | Odağı commit sonrası `useEffect`'te ver (`export-buttons.tsx`). Aynı kalıp (`rAF` + `await` sonrası state) başka bileşenlerde de var; yeni kodda kullanılmaz. |
| Sipariş listesi filtreleri: "temizle" sonrası tarih süzmede satır sayısı yanlış | **Uygulama hatası:** `router.replace` bir geçiş (transition); tamamlanana kadar `useSearchParams` eski URL'yi verir. Hemen ardından gelen filtre değişikliği yeni URL'yi eski sorgudan kurdu ve temizlenen `kaynak` filtresini geri getirdi (`?kaynak=whatsapp_ai&baslangic=…`). Sunucu/makine yükü pencereyi açar (yerelde RSC gecikmesiyle üretildi). | `order-list.tsx`: bekleyen gezinme varken değişiklik son istenen sorgunun üstüne kurulur; URL yetişince URL esas (geri/ileri dahil). Birim testi `order-list.test.tsx` dondurulmuş `useSearchParams` ile. Aynı kalıp diğer listelerde de olabilir. |
| Askıya alınan işletme testi: "Stok" bağlantısı bulunamadı (sayfa erken uyarıyla değişti) | `waitForLoadState("networkidle")` durum daha önce bir kez oluştuysa hemen döner; yük altında hidrasyon sırasında 500 ms ağ sessizliği olur, liste istekleri sonra başlar ve test işletmeyi askıya aldıktan sonra 403 alır. | `networkidle` ile senkronizasyon yapılmaz; isteklerin görünür sonucu beklenir ("N ürün gösteriliyor", filtreler etkin). |
| Mal kabul: hızlı iki okutmada 1 satır | **Uygulama hatası:** `VariantScanner` önceki arama sürerken gelen okutmayı sessizce atıyordu (`busy` ise `return`); ayrıca arama bitince alanı koşulsuz boşaltıp arada yazılan sonraki kodu siliyordu — el tarayıcısıyla eksik satır. | Okutma kuyruğa alınır (alan boşaltılır), sırayla çözülür; ilk sorun (bulunamadı/çok sonuç/hata) gösterilir; alan yalnızca hâlâ aranan kodu içeriyorsa boşaltılır. Birim testi `variant-scanner.test.tsx`. Aynı testin son adımı ayrıca ekleme duyurusu beklenmeden kaydediyordu (test yarışı). |
| Fiyat sorgusu "En az bir ürün ekleyin" | Ürün ekleme (barkod/SKU araması) asenkron; test eklenme duyurusunu beklemeden gönderdi. | `"<SKU> eklendi."` durum mesajını bekle (kullanıcının gördüğü sinyal). |

## 8. Release readiness kontrol listesi
- [ ] Tüm CI kapıları `main` adayı commit'te yeşil
- [ ] 10 kritik E2E senaryosu son koşuda geçti (kanıt: CI run linki)
- [ ] Concurrency gece koşusu (200 iterasyon) yeşil
- [ ] Tenant izolasyon matrisi tüm yeni endpoint'leri kapsıyor
- [ ] Açık Critical/High güvenlik bulgusu yok
- [ ] Migration'lar preview'da uygulandı, geri dönüş planı yazılı
- [ ] Preview ortamında manuel duman testi (web masaüstü + mobil genişlik ekran görüntüleri)
- [ ] Erişilebilirlik taraması temiz
- [ ] Release notes (Technical Writer) hazır
- [ ] Rollback prosedürü test edildi (`docs/operations/ci-cd.md`)
- [ ] Ürün sahibi onayı (production deploy için zorunlu)

## 9. Definition of Done (12 madde)
Bir iş ancak şu 12 madde tamamlandığında "Done"dur:
1. **Issue:** GitHub issue'su var ve işe bağlı.
2. **Kabul kriterleri:** Issue'da ölçülebilir Given/When/Then kriterleri yazılı.
3. **Branch:** `feature|fix|chore/<issue>-<ad>` adlı dal, hedef `preview`.
4. **Kod:** Katman kurallarına uygun, sır içermeyen kod.
5. **Review:** En az bir rol review'u (mimari/güvenlik etkiliyse ilgili rol); bulgular kapatıldı.
6. **Testler:** Unit + integration (+ gerekiyorsa E2E); tenant/yetki negatif testleri dahil.
7. **Güvenlik kontrolü:** threat-model kontrol listesi uygulandı; yeni endpoint izolasyon matrisine eklendi.
8. **CI yeşil:** Tüm zorunlu kontroller geçti (atlanan/gevşetilen test yok).
9. **PR:** Açıklama, issue bağlantısı, test kanıtı ile PR açıldı.
10. **Preview doğrulandı:** Preview ortamında çalıştığı gözle doğrulandı (UI ise ekran görüntüsü).
11. **Dokümantasyon:** İlgili belgeler/OpenAPI/ADR güncellendi.
12. **Raporlandı:** STATUS.md ve Orkestra Şefi raporuna gerçek kanıtla (PR/commit/CI linki) işlendi.
