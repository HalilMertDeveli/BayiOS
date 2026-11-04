# Yerel Supabase (yalnızca geliştirme)

Bu klasör, Supabase CLI ile **yerelde, ücretsiz** çalışan Supabase Auth yığınının yapılandırmasıdır
(`supabase/config.toml`). Barındırılan (hosted) bir projeye bağlanmaz; `supabase link` kullanılmaz.

Neden `infra/supabase/local/`? Yerel yığın altyapıdır ve repo kökünü kirletmemeli. Ayrıca şemanın tek
kaynağı EF Core migration'larıdır (ADR 0005): bu klasördeki `supabase/` dizininde **migration yoktur**
ve olmamalıdır. BayiOS API veritabanı yerelde ayrı bir Postgres'tir (`infra/docker/compose.yml`);
yerel Supabase yalnızca kimlik (Auth, e-posta testi) için kullanılır.

## Gereksinimler
Docker ve Node 24. CLI sürümü sabittir: `supabase@2.122.1` (komutlarda `npx -y supabase@2.122.1`).

## Asimetrik imza anahtarı (zorunlu)
API yalnızca JWKS ile doğrulanan ES256/RS256 token kabul eder; HS256 tasarım gereği reddedilir
(ADR 0003). Bu yüzden yerel Auth bir ES256 anahtarıyla imzalar. Anahtar dosyası git'e girmez
(`supabase/.gitignore`).

```bash
# repo kökünden, bir kez
echo "[]" > infra/supabase/local/supabase/signing_keys.json   # CLI 2.122.1 boş dosya bekliyor
npx -y supabase@2.122.1 gen signing-key --algorithm ES256 --workdir infra/supabase/local --yes
```

## Başlat / durdur
```bash
npx -y supabase@2.122.1 start  --workdir infra/supabase/local
npx -y supabase@2.122.1 status --workdir infra/supabase/local   # API URL, publishable key, Mailpit
npx -y supabase@2.122.1 stop   --workdir infra/supabase/local
```

| Servis | Adres |
|---|---|
| Auth/API gateway | `http://127.0.0.1:54321` (issuer `http://127.0.0.1:54321/auth/v1`) |
| JWKS | `http://127.0.0.1:54321/auth/v1/.well-known/jwks.json` |
| E-posta (Mailpit) | `http://127.0.0.1:54324` — doğrulama/sıfırlama e-postaları burada görünür |

E-posta doğrulaması açıktır (`enable_confirmations = true`; AF-01: doğrulanmamış e-posta ile tenant
oluşturulmaz). Yönlendirme izin listesi `http://localhost:3200/auth/callback`.

## API'yi yerel Auth ile çalıştırma
```bash
Auth__Authority=http://127.0.0.1:54321/auth/v1 \
Auth__Audience=authenticated \
Auth__RequireHttpsMetadata=false \
ConnectionStrings__Default="Host=127.0.0.1;Port=5432;Database=bayios;Username=bayios_app;Password=<yerel>" \
dotnet run --project services/api/src/BayiOS.Api --launch-profile http
```
`Auth__RequireHttpsMetadata=false` yalnızca bu yerel http yığını içindir; barındırılan ortamda https zorunludur.

Web için: `apps/web/README.md`.
