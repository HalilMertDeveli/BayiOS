# ADR 0003: Supabase Auth ile kimlik, API'de JWT doğrulama ve kodda tanımlı rol→izin yetkilendirmesi

- **Durum:** Kabul edildi
- **Tarih:** 2026-11-04

## Bağlam
Kimlik doğrulamayı (kayıt, giriş, şifre sıfırlama, e-posta doğrulama, ileride sosyal giriş/MFA) sıfırdan yazmak istemiyoruz. Supabase zaten veri platformumuz. Ancak yetkilendirme (tenant üyeliği, rol, izin, plan hakları) iş kuralıdır ve API'de olmalıdır. Supabase Auth asimetrik JWT imzalama anahtarlarını ve `https://<project-ref>.supabase.co/auth/v1/.well-known/jwks.json` JWKS uç noktasını destekler (resmî doküman: Supabase "JWT Signing Keys"; ES256 önerilir, HS256 production için önerilmez).

## Karar
1. **Kimlik:** Supabase Auth. Web `@supabase/ssr` ile oturumu yönetir, mobil Supabase Flutter istemcisiyle; access token API'ye `Authorization: Bearer` ile iletilir.
2. **Doğrulama (API):** `JwtBearer` handler; imza **JWKS** ile (asimetrik; ES256/RS256), `iss` = `https://<ref>.supabase.co/auth/v1`, `aud` = `authenticated` (ortam yapılandırması), `exp`/`nbf` (saat kayması ≤ 30 sn). HS256/paylaşılan sır kabul edilmez. JWKS önbelleği yenilenir; bilinmeyen `kid` görülünce bir kez anında yenileme denenir (anahtar rotasyonu). Kullanıcı kimliği = `sub`.
3. **Tenant ve rol:** JWT'de tenant/rol taşınmaz. Tenant `X-Tenant-Id`, rol `identity.memberships` ([ADR 0002](0002-multi-tenancy-shared-schema-rls.md)).
4. **Yetkilendirme:** Roller `owner, admin, manager, sales, warehouse_staff, viewer`; izinler ve rol→izin haritası **kodda** (SharedKernel). Endpoint'ler policy tabanlı `RequirePermission(...)` ile korunur. Ücretli modüller için ek `RequireEntitlement(...)` kontrolü (Billing modülü, önbellekli).
5. **Oturum iptali:** Üyelik devre dışı bırakılınca önbellek invalidation ile sonraki istekte 403; token süresi kısa tutulur (Supabase ayarı, ör. 1 saat).
6. Supabase `service_role` anahtarı yalnızca API'nin sunucu tarafı yönetim çağrılarında (ör. davet e-postası, kullanıcı arama) kullanılır; istemcilere asla verilmez.

## Değerlendirilen alternatifler
| Alternatif | Neden seçilmedi |
|---|---|
| ASP.NET Core Identity + kendi token servisi | Kimlik altyapısını işletme yükü; şifre sıfırlama/e-posta/MFA'yı yeniden yazmak |
| Harici IdP (Auth0, Entra External ID, Clerk) | Ek maliyet ve tedarikçi; Supabase zaten var |
| Supabase custom claims (Auth hook) ile rol/tenant JWT'de | Çok tenant'lı kullanıcı, rol değişikliğinin token yenilemeye bağlı kalması; yine de DB kontrolü gerekir |
| HS256 JWT secret ile doğrulama | Sırrın API'de saklanması; Supabase production için önermiyor; rotasyon zor |
| Rol/izinlerin tabloda (özelleştirilebilir) tutulması | MVP'de gereksiz; yanlış yapılandırma riski; ileride `tenant_roles` ile geriye uyumlu eklenebilir ([data-model §3](../architecture/data-model.md#3-roller-ve-izinler-karar)) |

## Sonuçlar
- (+) Kimlik altyapısı yönetilen; API durumsuz doğrulama yapar (JWKS önbelleği).
- (+) Yetki kuralları kodda, test edilebilir, sürümlenmiş.
- (−) Supabase Auth'a bağımlılık; JWKS erişilemezse önbellekteki anahtarlarla devam edilir, yeni anahtarlar doğrulanamaz.
- (−) JWKS istemci tarafı önbellek süresi, anahtar iptalinde gecikme yaratır; acil iptal için JWKS önbelleğini temizleyen yönetim ucu/yeniden başlatma prosedürü (ops runbook).
- (−) Her istekte üyelik kontrolü — kısa TTL önbellek ile maliyet düşürülür.
