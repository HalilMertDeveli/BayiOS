# Runbook — Tenant veri sızıntısı şüphesi (KVKK)

**Tetik:** bir kullanıcı başka işletmenin verisini gördüğünü bildirir; güvenlik testi (`TenantIsolationSweepTests`, IDOR süpürmesi) production sürümünde kırmızı; loglarda olağan dışı `Tenant access denied` yoğunluğu; sızdırılmış anahtar/token; yanlış ortama veri kopyalanması.
**Önem:** her zaman **SEV1** (AUTOPILOT §5 durdurma koşulu). Hukuki danışmanlık değildir — KVKK uzmanı/avukat sürece dahil edilir.

## 1. İlk saat — sınırla ve kanıtı koru
1. Olay sorumlusu atanır, saat (İstanbul) kaydedilir: **"farkına varma anı"** — KVKK süreleri buradan işler.
2. Kanıt: ilgili `correlationId`'ler, istek log satırları (route şablonu, durum, tenant/user id), audit olayları. Ekran görüntüsü/veri kopyası **alma**; yalnızca kimlikleri ve zamanları kaydet. Logların saklama süresi (30 gün) dolmadan dışa al.
3. Sınırlama (gerekirse birden çoğu):
   - Sorun son sürümle geldiyse → `rollback.md` §1 (imaj geri alma).
   - Belirli uç nokta → o route'u kapatan acil düzeltme (preview'da test, onayla production).
   - Sızmış sır (JWT imza anahtarı, Supabase secret key, DB parolası, `Audit__IpHashKey`) → **önce iptal/rotasyon** (data-policy §10), sonra her şey.
   - Kullanıcı hesabı ele geçirilmişse → Supabase Auth'ta oturumları iptal et, üyeliği `disabled`.
4. Yazmayı durdurmak gerekiyorsa ürün sahibi kararıyla API ölçeği 0 (tam kesinti) — kesinti, sızıntının sürmesinden iyidir.

## 2. Teşhis — gerçekten çapraz tenant erişimi var mı?
Katmanlar (ADR 0002): (1) API üyelik kontrolü (`X-Tenant-Id` yalnızca aktif üyelikle kabul), (2) EF tenant filtresi, (3) PostgreSQL FORCE RLS (`bayios_app`, `app.tenant_id`). Üçü birden aşılmadıkça veri görünmez.
- Hangi kullanıcı (UUID), hangi tenant seçimiyle (log `TenantId`), hangi route'a, hangi zaman aralığında?
- Audit: `SELECT action, entity_type, entity_id, actor_user_id, occurred_at, correlation_id FROM audit.audit_events WHERE correlation_id = ANY(:ids)` (admin, salt okunur).
- RLS sağlam mı? Admin oturumunda: `SELECT n.nspname, c.relname, c.relrowsecurity, c.relforcerowsecurity FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname IN (…iş şemaları…) AND c.relkind = 'r' AND NOT (c.relrowsecurity AND c.relforcerowsecurity);` — boş olmalı (tenant tabloları için). `bayios_app` rolü: `SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = 'bayios_app'` → `f, f`.
- API'nin `postgres`/service-role ile bağlanmadığını doğrula (yalnızca `bayios_app`).
- Paylaşılan tenant'sız tablolar (`platform.jobs`, `outbox`) yalnızca kimlik içerir; job handler'ları tenant bağlamıyla çalışır (ADR 0006).

## 3. Etki değerlendirmesi
- Etkilenen tenant'lar (veri sorumluları) ve veri kategorileri (data-policy §2: personel, müşteri iletişim, adres, WhatsApp metni, işlem verisi).
- Kişisel veri var mı? Varsa yaklaşık kişi sayısı. İşlem verisi (fiyat, stok) ticari sırdır — kişisel veri değildir ama tenant'a bildirilir.
- Süre: ilk ve son yetkisiz erişim zamanı.

## 4. KVKK bildirim zaman çizelgesi
Roller (data-policy §1): tenant = **veri sorumlusu**, BayiOS = **veri işleyen**. Kendi verisi için (owner iletişim/faturalama) BayiOS veri sorumlusudur.

| Zaman (farkına varmadan itibaren) | Aksiyon | Kim |
|---|---|---|
| 0–24 saat | Etkilenen tenant'lara (veri sorumlularına) **gecikmeksizin** yazılı bildirim: ne oldu, hangi veri kategorileri, ne zaman, alınan önlemler, irtibat kişisi. DPA'daki taahhüt süresi uygulanır (önerilen: ≤ 24 saat) | BayiOS (ürün sahibi) |
| ≤ 72 saat | Veri sorumlusu, Kişisel Verileri Koruma Kurulu'na bildirim yapar (Kurul'un 2019/10 sayılı kararıyla "en kısa sürede ve en geç 72 saat"; KVKK md. 12/5). BayiOS'un veri sorumlusu olduğu veride bildirimi BayiOS yapar. 72 saat aşılırsa gecikme gerekçesi bildirime eklenir | Tenant (veri sorumlusu) / BayiOS kendi verisi için |
| Makul en kısa süre | İlgili kişilere (müşteri, personel) bildirim — veri sorumlusunun yükümlülüğü; BayiOS içerik ve tespit ettiği kişi listesini (kimlikler, kişisel veri kopyalanmadan) sağlar | Tenant |
| Sürekli | Bilgiler parça parça netleştikçe ek bildirim; tüm kararlar ve gerekçeler olay kaydında (Kurul incelemesi için) | Olay sorumlusu |

Not: Kurul bildirim formu ve süreler hukukçu ile doğrulanmalıdır; bu tablo hukuki görüş değildir (data-policy başlığı). GDPR kapsamına giren bir tenant varsa ayrıca 72 saat kuralı (GDPR md. 33) değerlendirilir.

Bildirim içeriği (taslak, kişisel veri yok): olayın niteliği · zaman aralığı · etkilenen veri kategorileri ve yaklaşık kişi sayısı · olası sonuçlar · alınan/alınacak önlemler · irtibat.

## 5. Kapatma
- Kök neden + düzeltme testi: sızıntıyı yakalayacak bir **regresyon testi** zorunlu (güvenlik paketine: `tests/BayiOS.IntegrationTests/Security`).
- Gerekirse anahtar rotasyonu, etkilenen kullanıcıların oturumlarının iptali.
- Olay sonrası inceleme (`incident-response.md` §4) + `docs/security/threat-model.md` ve risk kaydı güncellemesi.
