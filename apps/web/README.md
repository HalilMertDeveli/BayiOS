# BayiOS Web (`apps/web`)

Next.js 16 + React 19 işletme paneli. Veri yalnızca BayiOS API üzerinden okunur/yazılır; Supabase
yalnızca kimlik (oturum çerezleri) içindir.

## Komutlar
```bash
npm ci
npm run lint && npm run typecheck && npm test && npm run build
npm run test:e2e          # Playwright — Supabase ve API page.route ile MOCK (CI'da çalışan)
npm run test:e2e:local    # Playwright — gerçek yerel yığın, mock yok (CI'da yok)
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

## Yerel uçtan uca (gerçek Supabase + API + web, mock yok)
Repo kökünden:
```bash
# 1) Yerel Supabase (ES256 imza anahtarı) — ayrıntı: infra/supabase/local/README.md
npx -y supabase@2.122.1 start --workdir infra/supabase/local

# 2) API veritabanı + migration'lar
docker compose -f infra/docker/compose.yml up -d
OUT_DIR=/tmp/bayios-mig bash infra/ci/ef-migrations.sh      # idempotent SQL betiklerini üretir
for f in /tmp/bayios-mig/*Platform*.sql /tmp/bayios-mig/*Identity*.sql /tmp/bayios-mig/*Catalog*.sql; do
  docker exec -i -e PGPASSWORD=<migrator şifresi> bayios-postgres-1 \
    psql -h 127.0.0.1 -U bayios_migrator -d bayios -v ON_ERROR_STOP=1 -q -f - < "$f"
done

# 3) API — yerel Supabase JWKS ile
Auth__Authority=http://127.0.0.1:54321/auth/v1 Auth__Audience=authenticated Auth__RequireHttpsMetadata=false \
ConnectionStrings__Default="Host=127.0.0.1;Port=5432;Database=bayios;Username=bayios_app;Password=<app şifresi>" \
dotnet run --project services/api/src/BayiOS.Api --launch-profile http

# 4) Web (apps/web/.env.local yukarıdaki gibi)
npm --prefix apps/web run build && npm --prefix apps/web run start -- -p 3200

# 5) Gerçek E2E: kayıt → Mailpit'ten doğrulama → onboarding → pano → çıkış → giriş
npm --prefix apps/web run test:e2e:local
```

CI'ya eklemek için gerekenler (bu PR'da eklenmedi): Docker'lı runner'da Supabase CLI yığını (~1–2 dk
imaj çekme), imza anahtarı üretimi, Postgres + EF migration'ları, .NET API süreci ve web build'i;
`test:e2e:local` ayrı, isteğe bağlı/gece çalışan bir iş olarak.
