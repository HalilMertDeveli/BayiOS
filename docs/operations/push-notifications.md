# Push bildirimleri (FCM) — yapılandırma ve açma adımları

> Sahip: Flutter Mobile Lead + Backend · Durum: kod hazır, **varsayılan kapalı** · Issue #44 · Tarih: 2026-11-08

BayiOS, bir toplama görevi bir personele atandığında o kişinin cihazlarına Firebase Cloud Messaging (FCM) ile bildirim
gönderir. Firebase **yalnızca bildirim** içindir (CLAUDE.md): veri her zaman BayiOS API'sinden gelir.

Henüz bir Firebase projesi **yok**. Proje oluşturmak ürün sahibi eylemidir (AUTOPILOT.md). O zamana kadar:

- API: `Push:Enabled=false` (varsayılan). Cihaz kayıtları (`/api/v1/devices`) çalışır, atama olayı ve gönderim işi
  kuyruğa girer, ama gönderici **no-op**'tur (`DisabledPushSender`): FCM'e hiçbir istek gitmez, token silinmez.
- Mobil: `BAYIOS_FIREBASE_*` tanımları boşsa Firebase **hiç başlatılmaz** (`NoopPushService`): bildirim izni istenmez,
  token kaydı yapılmaz. `google-services.json` / `GoogleService-Info.plist` gerekmez; Android derlemesi bu dosyalar
  olmadan çalışır (Firebase Dart'tan, `FirebaseOptions` ile başlatılır; Google Services Gradle eklentisi kullanılmaz).

## 1. Akış

```
Supervisor "Görevi ata" (POST /picking-tasks/{id}/assign)
  └─ aynı transaction: warehouse.picking_task_assigned.v1 → platform.outbox_messages
OutboxDispatcher → platform.outbox_delivery job → PickingTaskAssignedHandler (Notifications)
  └─ notifications.push_send job (tenant'lı; kendi üstlenmesinde — assignedBy = assignedTo — iş yok)
PushSendJob (IExternalCallJobHandler: FCM beklerken bağlantı/transaction tutulmaz)
  ├─ o tenant'taki atanan kişinin cihazları (RLS + sorgu)
  ├─ IPushSender.SendAsync → FCM HTTP v1 (token başına bir istek)
  ├─ UNREGISTERED / SENDER_ID_MISMATCH / "registration token" INVALID_ARGUMENT → kayıt silinir
  └─ geçici hata (429, 5xx, ağ, süresi dolmuş OAuth) → job hata verir → kuyruk geri çekilmeyle yeniden dener
     (yeniden denemede aynı collapse key / tag: cihaz bildirimi yığmaz, değiştirir)
```

Bildirim içeriği (PII ve fiyat **yok**): başlık "Yeni toplama görevi", gövde "Size bir toplama görevi atandı. Açmak için
dokunun.", `data`: `type=picking_task_assigned`, `taskId`, `tenantId`. Uygulama dokunuşta oturum ve işletme geri
yüklendikten sonra `/gorevler/:id`'yi açar (başka bir üyelik işletmesiyse önce ona geçer).

## 2. Değişken adları (değer yok, repoya hiçbir değer girmez)

### API (`Push:*`, ortam değişkeni olarak `Push__*`)
| Değişken | Zorunlu | Açıklama |
|---|---|---|
| `Push__Enabled` | — | `true` = FCM'e gönder. Varsayılan `false` |
| `Push__ServiceAccountJson` | `Enabled=true` ise (veya `ServiceAccountFile`) | **Sır.** Firebase servis hesabı JSON anahtarının tamamı. API host secret'ı; loglanmaz, repoda/issue'da/PR'da yer almaz |
| `Push__ServiceAccountFile` | — | Yalnızca yerel geliştirme: anahtar dosyasının repo **dışındaki** yolu |
| `Push__ProjectId` | — | Firebase proje id'si; boşsa anahtardaki `project_id` |
| `Push__StaleAfterDays` | — | Bu kadar gün yenilenmeyen kayıt silinir (30–270, varsayılan 90 — data-policy §4) |
| `Push__MaxDevicesPerUser` | — | Tenant başına kullanıcı başına en çok kayıt (varsayılan 10; en eski görülen düşer) |
| `Push__TimeoutSeconds` | — | Tek FCM / OAuth çağrısının zaman aşımı (varsayılan 10) |

`Push__Enabled=true` ama anahtar yoksa API **başlamaz** (`OptionsValidationException`): yarım yapılandırma deploy
hatasıdır. Anahtar yalnızca `type=service_account` ve `token_uri=https://oauth2.googleapis.com/token` ise kabul edilir.

### Mobil (`--dart-define-from-file=env/<ortam>.json`, git-ignored; adlar `env/dev.example.json`'da)
| Değişken | Açıklama |
|---|---|
| `BAYIOS_FIREBASE_API_KEY` | Firebase istemci API anahtarı (istemci tanımlayıcısıdır, sır değildir; yine de commit edilmez) |
| `BAYIOS_FIREBASE_PROJECT_ID` | Proje id'si |
| `BAYIOS_FIREBASE_MESSAGING_SENDER_ID` | Gönderici (proje numarası) |
| `BAYIOS_FIREBASE_ANDROID_APP_ID` | Android uygulama id'si (`1:…:android:…`) |
| `BAYIOS_FIREBASE_IOS_APP_ID` | iOS uygulama id'si (`1:…:ios:…`) |
| `BAYIOS_FIREBASE_IOS_BUNDLE_ID` | Opsiyonel, iOS bundle id |

Bir platformun dört değeri (api key, project id, sender id, o platformun app id'si) tam değilse o derlemede push yoktur.

`.gitignore`: `google-services.json`, `GoogleService-Info.plist`, `service-account*.json`, `*firebase-adminsdk*.json`,
`firebase-service-account*.json`, `.firebaserc`, `apps/mobile/lib/firebase_options.dart`, `apps/mobile/firebase.json`.

## 3. Ürün sahibinin yapacakları (push'u açmak için)

1. **Firebase projeleri** (ücretsiz Spark planı; Blaze'e geçiş onay ister — environments.md §6): `bayios-dev`
   (local + preview) ve `bayios-prod`. Analytics gerekmez (kapalı bırakın).
2. **Android uygulaması** ekleyin: paket adı bugün `com.bayios.bayios_mobile` (`apps/mobile/android/app/build.gradle.kts`
   `applicationId`; ortam flavor'ları eklenince `com.bayios.app.dev` / `com.bayios.app` gibi ayrı id'ler ayrıca
   eklenir). İndirilen `google-services.json` **commit edilmez**; içinden şu değerler alınır: `api_key.current_key` →
   `BAYIOS_FIREBASE_API_KEY`, `project_info.project_id`, `project_info.project_number` →
   `BAYIOS_FIREBASE_MESSAGING_SENDER_ID`, `client.client_info.mobilesdk_app_id` → `BAYIOS_FIREBASE_ANDROID_APP_ID`.
3. **iOS uygulaması** (macOS + Xcode gerekir): bundle id ile ekleyin; `GoogleService-Info.plist`'ten `GOOGLE_APP_ID` →
   `BAYIOS_FIREBASE_IOS_APP_ID`. Apple Developer hesabında **APNs Authentication Key (.p8)** oluşturup Firebase konsolu →
   Project settings → Cloud Messaging → Apple app configuration'a yükleyin (Key ID + Team ID). Xcode'da Runner hedefine
   *Push Notifications* yeteneğini ve *Background Modes → Remote notifications*'ı ekleyin (imzalama ekibi gerektirir;
   bu değişiklik iOS imzalama kurulduğunda yapılır).
4. **Servis hesabı anahtarı**: Firebase konsolu → Project settings → Service accounts → *Generate new private key*
   (veya Google Cloud IAM'de yalnızca *Firebase Cloud Messaging API Admin* rolüne sahip ayrı bir servis hesabı — en az
   yetki). JSON'u **yalnızca** API host secret'ı olarak saklayın: preview → `Push__ServiceAccountJson` (bayios-dev
   anahtarı), production → `Push__ServiceAccountJson` (bayios-prod anahtarı) ve `Push__Enabled=true`. Dosyayı diske
   bıraktıysanız silin; asla sohbet/issue/PR'a yapıştırmayın. *Firebase Cloud Messaging API (V1)* projede açık olmalı
   (yeni projelerde varsayılan).
5. **Mobil yapılandırma**: `env/preview.json` / `env/prod.json`'a (git-ignored) yukarıdaki `BAYIOS_FIREBASE_*`
   değerlerini yazın; CI'da derleme yapılacaksa bu dosya bir GitHub Environment secret'ından üretilir.
6. **Doğrulama** (preview'da): personel hesabıyla giriş → işletme seç → bildirim izni ver → web'den görevi o kişiye
   atayın → bildirim gelmeli, dokununca görev açılmalı. `GET /api/v1/devices` (aynı kullanıcı + `X-Tenant-Id`) kaydı
   gösterir; çıkışta silinir.
7. **Rotasyon / iptal**: anahtar yılda bir veya sızıntı şüphesinde hemen; önce Google Cloud'da eski anahtarı silin,
   sonra yeni anahtarı secret'a yazıp API'yi yeniden başlatın (data-policy §9).

## 4. Cihaz kaydı (API, `notifications.device_tokens`)

- `POST /api/v1/devices` (İ, `tenant.read` = her rol): `{token, platform: android|ios, appVersion?, locale?}` →
  `DeviceResponse` (token **dönmez**). Aynı token = aynı kayıt (yenilenir). Token global tektir: başka kullanıcı veya
  aynı kullanıcı başka işletmede kaydederse kayıt ona **taşınır** (`notifications.release_device_token`, SECURITY
  DEFINER, token başına advisory lock — eşzamanlı iki kayıttan biri kalır).
- `GET /api/v1/devices`: bu işletmede **kendi** kayıtlarım. `DELETE /api/v1/devices/{id}`: kendi kaydı (yoksa 404).
- RLS: `tenant_id = app.tenant_id` **ve** istek bağlamında `user_id = app.user_id` (bir istek başka kullanıcının
  kaydını göremez); kullanıcısız job bağlamı (gönderim) tenant'ın kayıtlarını görür.
- Token hassastır: loglanmaz (yalnızca sayılar ve FCM hata kodları), job payload'ında yoktur (yalnızca kullanıcı ve
  görev id'si), API yanıtında yoktur; veritabanında `token_sha256` tekillik anahtarıdır.
- Temizlik: saatlik tenant'sız bakım işi `notifications.device_cleanup` → `notifications.purge_stale_device_tokens`
  (`Push:StaleAfterDays`, taban 30 gün); FCM "unregistered" yanıtı; çıkışta mobil kaydı siler ve FCM token'ını iptal eder.

## 5. Mobil davranış

- İzin: Android 13+ `POST_NOTIFICATIONS` ve iOS izni, giriş ve işletme seçiminden sonra **bir kez** istenir (kullanıcı
  artık görev alacak bir işletmede). Reddedilirse kayıt yapılmaz.
- Kayıt: oturum hazır olunca; token yenilenince yeni token kaydedilir, eskisi silinir; işletme değişince yeni işletmede
  kaydedilir, eskisinden silinir (eski işletmenin `X-Tenant-Id`'siyle); çıkıştan **önce** silinir (en çok ~5 sn,
  bağlantı yoksa çıkış yine olur). Çevrimdışı kayıt, bağlantı gelince aynı `Idempotency-Key` ile yeniden denenir.
- Ön planda gelen bildirim: üstte "Aç" düğmeli bir bant.
- Bilinen sınırlar: oturum süresi kendiliğinden biterse (401) API kaydı silinemez — eski kayıt FCM "unregistered" veya
  90 gün sonra temizlenir; aynı cihazda başka kullanıcı giriş yapınca token ona taşınır.

## 6. Test

- API: `tests/BayiOS.IntegrationTests/Notifications/*` (kayıt/taşıma/eşzamanlılık/izolasyon/temizlik, atamada tek
  outbox olayı, yalnızca doğru tenant'taki doğru cihazlara gönderim, unregistered temizliği, geçici hata → yeniden deneme,
  kapalıyken no-op), `tests/BayiOS.UnitTests/Notifications/FcmPushSenderTests.cs` (sahte HTTP handler ile FCM v1 istek
  biçimi, hata sınıflandırması, OAuth JWT imzası ve önbellek; gerçek FCM CI'da **çağrılmaz**). Güvenlik taramaları
  (`TenantIsolationSweepTests`, `DatabaseSecurityGuardTests`) yeni tabloyu ve uçları otomatik kapsar.
- Mobil: `test/core/push/*` (kayıt yaşam döngüsü), `test/app/push_coordinator_test.dart` (dokunuş → `/gorevler/:id`).
