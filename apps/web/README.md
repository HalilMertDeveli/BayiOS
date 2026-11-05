# BayiOS Web (`apps/web`)

Next.js 16 + React 19 işletme paneli. Veri yalnızca BayiOS API üzerinden okunur/yazılır; Supabase
yalnızca kimlik (oturum çerezleri) içindir.

## Komutlar
```bash
npm ci
npm run lint && npm run typecheck && npm test && npm run build
npm run test:e2e          # Playwright — Supabase ve API page.route ile MOCK (CI'da çalışan)
npm run test:e2e:real     # Playwright — gerçek yığın, mock yok (CI: e2e-real); yığın aşağıda
npm run test:e2e:local    # aynı klasörün tamamı + docs/screenshots yenileme (yalnızca yerel)
npm run gen:api           # packages/contracts/openapi.json → src/lib/api/schema.d.ts
```

## Ortam değişkenleri
İsimler `.env.example`'da. Yerelde `.env.local` (git'e girmez):
```
NEXT_PUBLIC_API_BASE_URL=http://localhost:5280
NEXT_PUBLIC_SUPABASE_URL=http://127.0.0.1:54321
NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY=<supabase status çıktısındaki publishable key>
```
`NEXT_PUBLIC_*` değerleri derleme sırasında koda gömülür ve tarayıcıya gider: service-role key gibi
sırlar asla buraya konmaz.

## Kimlik ve oturum
- `src/proxy.ts`: Supabase oturumunu çerezden yeniler; oturum yoksa korumalı sayfalardan
  `/giris?next=…`'e, oturum varsa `/giris` ve `/kayit`'ten `/pano`'ya yönlendirir. Bu bir **yetki
  kontrolü değildir**: sunucu tarafında tenant verisi render edilmez; API her istekte JWT imzasını
  (JWKS), üyeliği ve rolü doğrular.
- `src/lib/session/session-context.tsx`: `GET /api/v1/me` → üyelikler; aktif tenant tarayıcıda
  kullanıcı başına saklanır ve `X-Tenant-Id` olarak gönderilir. Üyeliği yoksa `/isletme-olustur`,
  birden fazla ve seçim yoksa `/isletme-sec`.
- `src/lib/api/client.ts`: Bearer + `X-Tenant-Id` + `Idempotency-Key`/`If-Match`; Problem Details
  → Türkçe mesaj. 401 → oturum kapatılır, girişe dönülür; 403 `tenant.access_denied` → işletme seçimi.
- Onboarding (`/isletme-olustur`): `POST /tenants` → yeni tenant ile sektör şablonu
  (`POST /sector-templates/{code}/apply`), ticari ayarlar (`PATCH /tenant/settings`) ve para birimi
  (`PATCH /tenant`). Her adım için deneme başına sabit `Idempotency-Key`; başarısız alt adımlar tenant
  yeniden oluşturulmadan tekrar denenir.

## Gerçek yığın E2E (Supabase Auth + API + PostgreSQL 17 + web, mock yok)
CI'da `.github/workflows/e2e-real.yml` (`e2e real stack`) ile her ilgili PR'da koşar. Yerelde aynı betik, repo kökünden
(bash, Docker, .NET SDK, Node 24):
```bash
bash infra/ci/e2e-stack.sh up            # Supabase Auth (CLI, ES256 anahtarı yoksa üretir) + boş postgres:17 (55432)
                                         # + EF migration'ları + API (Release, :5280) + web (next build, :3200)
npm --prefix apps/web run test:e2e:real  # real-stack + order-to-shipment
bash infra/ci/e2e-stack.sh down          # API/web süreçleri, Postgres konteyneri, Supabase (yerel Auth verisi korunur)
```
- Şifreler her çalıştırmada üretilir; hiçbir sır gerekmez. Supabase anahtarları CLI'ın herkese açık yerel anahtarlarıdır.
- Kimlik doğrulama gerçektir: kayıt → Mailpit'teki doğrulama bağlantısı → oturum; API token'ı JWKS ile doğrular
  (yalnız yapılandırma farkı: http yerel issuer için `Auth__RequireHttpsMetadata=false`). Test anahtarı/bypass yoktur.
- Web'de ekranı olmayan adımlar (toplama, paket, sevkiyat) testte, oturum açmış kullanıcının web'in API isteklerinden
  alınan kendi Bearer token'ı ve `X-Tenant-Id`'si ile API'den sürülür.
- Süreç günlükleri: `$E2E_STATE_DIR` (varsayılan `${TMPDIR:-/tmp}/bayios-e2e`) altında `api.log`, `web.log`.
- Portlar: `E2E_PG_PORT`, `E2E_API_PORT`, `E2E_WEB_PORT`. Web'i yeniden derlemeden başlatmak: `E2E_SKIP_WEB_BUILD=1`;
  migration betiklerini yeniden üretmemek: `E2E_REUSE_MIGRATIONS=1`. Port 3200 Supabase yönlendirme izin listesindedir.
- Test verisi sentetik ve çalıştırma başına benzersizdir; yeniden deneme yoktur (`retries: 0`). Kapsam:
  `docs/testing/test-strategy.md` §4.1.
