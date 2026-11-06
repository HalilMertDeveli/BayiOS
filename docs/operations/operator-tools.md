# BayiOS — Operatör araçları

| Alan | Değer |
|---|---|
| Sahip | Backend Lead |
| Durum | Uygulandı (#129) |
| Son güncelleme | 2026-11-06 |

Operatör araçları **HTTP uç noktası değildir**; uygulama bunları çağırmaz. Her biri depoda sürümlenir, gözden geçirilir ve
entegrasyon testiyle (gerçek `psql`, Testcontainers PostgreSQL) doğrulanır. Production'da çalıştırmak, verinin
sahibinin (tenant) talebi veya ürün sahibinin onayı ile olur ve audit kaydı bırakır.

## 1. Deneme süresini kısaltma / bitirme (UAT) — `infra/ops/shorten-trial.sql`

Amaç: UAT'de "deneme bitti → Temel paket" yolunu (salt okunur özellikler, `entitlement.*` hataları, deneme bandı) 30 gün
beklemeden denemek.

```bash
psql "host=<db host> port=5432 dbname=<db> user=bayios_app sslmode=require" \
     -v ON_ERROR_STOP=1 \
     -v tenant_id=<tenant uuid> \
     [-v ends_at='2026-11-07T09:00:00Z'] \
     -f infra/ops/shorten-trial.sql
```

- **Rol: `bayios_app`** (NOBYPASSRLS). Değişiklik bir istek gibi RLS'den geçer ve yalnızca verilen tenant'ın bağlamında
  (`app.tenant_id`) çalışır; başka tenant'a dokunamaz. `bayios_migrator` tablonun sahibidir ama FORCE RLS altında bu
  tabloda UPDATE politikası yoktur (tasarım gereği) — script başka rolle çalıştırılırsa hiçbir şey değiştirmeden durur.
  `postgres`/service-role kullanılmaz.
- Yalnızca `status = 'trialing'` abonelik ve yalnızca **kısaltma**: `ends_at` (varsayılan: bir dakika önce — sunucular
  arası saat farkı denemeyi açık bırakmasın) mevcut bitişten
  önceyse uygulanır; `trial_started_at + 1 sn`'den önceye gidemez (tablo CHECK'i). Uzatma bu araçla yapılmaz (pilot
  süresiz erişim = `billing.tenant_entitlements` override'ı, roles-permissions §5).
- Tek transaction: abonelik güncellenir + `billing.trial_shortened` audit olayı (`actor_role = 'operator'`, kişisel veri
  yok) yazılır.
- Etki anında: entitlement'lar her istekte zamanla türetilir, bitiş geçtiyse tenant Temel'dedir (`GET /billing/subscription`
  → `plan = basic`, `status = expired`). Saatlik `billing.subscription_expiry` işi ardından `status = 'expired'` ve
  `billing.trial_expired` kaydeder. **Veri silinmez.** Tekrar çalıştırmak zararsızdır (kısaltacak bir şey yoksa 0 satır).
- Geri alma: yok (deneme yeniden başlatılmaz). UAT tenant'ı için yeni bir tenant oluşturun veya override ile özellik açın.
- Doğrulama: `services/api/tests/BayiOS.IntegrationTests/Billing/OperatorToolsTests.cs`.

## 2. Üye daveti (operatör aracı değildir)

Pilot ve UAT'de kullanıcılar artık API ile davet edilir (`POST /api/v1/members/invitations`, #129); veritabanına elle
üyelik eklemek gerekmez. E-posta gönderimi yoktur: davet eden, yanıttaki token'ı (bir kez gösterilir) davetliye kendisi
iletir; davetli aynı, doğrulanmış e-posta ile Supabase'de oturum açıp `POST /api/v1/invitations/accept` ile kabul eder.
Ayrıntı: [api-strategy §12.1](../architecture/api-strategy.md), [threat-model §4.1](../security/threat-model.md).
