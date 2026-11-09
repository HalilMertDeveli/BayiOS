# Runbook — Kimlik sağlayıcısı kesintisi (Supabase Auth / JWKS)

**Tetik:** `BayiOSAuthFailuresSpike` (401 oranı > %30, 10 dk); kullanıcılar giriş yapamıyor veya sürekli oturumdan atılıyor; Supabase durum sayfası olayı.
**Önem:** giriş tamamen imkânsızsa SEV2 (tüm tenant'lar), kısmi ise SEV3. Veri riski yok: API her token'ı imza (JWKS), issuer, audience ve süreyle doğrular — doğrulanamayan istek **reddedilir** (kapalı-güvenli, ADR 0003). Doğrulamayı gevşetmek **asla** çözüm değildir.

## 1. Teşhis
| Belirti | Neden |
|---|---|
| Giriş sayfası hata veriyor, API sağlıklı | Supabase Auth (GoTrue) kesintisi — tarayıcı token alamıyor |
| Token alınıyor ama API 401 `auth.invalid_token` | JWKS alınamıyor (`Auth:JwksUrl` / `{Authority}/.well-known/jwks.json`), imza anahtarı döndürüldü (rotasyon) ve önbellekte yok, `Auth__Authority`/`Auth__Audience` yanlış ortam, saat kayması |
| Yalnızca bir ortamda | Preview/prod secret karışıklığı (environments.md: her ortam ayrı proje) |
| Tek kullanıcı | Hesap devre dışı / üyelik `disabled` (bu 403'tür, 401 değil) |

Kontroller: `curl -s https://<ref>.supabase.co/auth/v1/.well-known/jwks.json` (200 + anahtar listesi); API loglarında JWT doğrulama uyarıları (token içeriği loglanmaz); `/health/ready` = `unavailable` ise `Auth:*` yapılandırması eksik/geçersiz (configuration kontrolü).

## 2. Aksiyon
- **Supabase kesintisi:** bekle; Supabase durum sayfasını izle; tenant'lara bilgi (`incident-response.md` §5). Mevcut token'lar süreleri dolana kadar çalışır.
- **Anahtar rotasyonu sonrası 401:** API JWKS'i yeniden yükler; geçmezse API'yi yeniden başlat (önbellek temizlenir). Rotasyon planlıysa önce yeni anahtarı yayınlayıp eskiyi bir süre tutmak (Supabase "standby key") kesintiyi önler.
- **Yanlış yapılandırma:** host secret'larını düzelt (`Auth__Authority` = `https://<ref>.supabase.co/auth/v1`, `Auth__Audience` = `authenticated`), yeniden deploy; doğru ortamın projesi olduğundan emin ol.
- **Saldırı şüphesi** (401'ler tek kaynaktan, kaba kuvvet): host/WAF düzeyinde sınırlama; `tenant-data-leak.md` gerekli mi değerlendir.

## 3. Yapılmayacaklar
`Auth:RequireHttpsMetadata=false` (yalnızca yerel Supabase CLI için), imza doğrulamasını kapatmak, service-role anahtarını istemciye vermek, token'ları loga/issue'ya yapıştırmak.

## 4. Doğrulama
401 oranı normal; sentetik bir kullanıcıyla giriş → `/api/v1/me` 200.
