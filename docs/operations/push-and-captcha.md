# Preview: push (Firebase FCM) ve CAPTCHA (Cloudflare Turnstile) kurulumu

> Issue #219 · Tarih: 2026-11-11 · Yalnızca ücretsiz planlar (Firebase Spark, Turnstile Free) · Production'ı kapsamaz.
> Push'un ayrıntısı: [push-notifications.md](push-notifications.md). CAPTCHA'nın ayrıntısı: [environments.md §2.1](environments.md).
> Hosting (Render API, Vercel web) ayrı belgelerde; bu belge yalnızca hangi değerin nereye girdiğini söyler.

Bu dosyada **değer yok**, yalnızca adlar ve kimlikler var. Sırlar repoya, issue'ya, PR'a veya sohbete yazılmaz.

## 1. Oluşturulanlar

| Kaynak | Kimlik | Not |
|---|---|---|
| Firebase projesi | `bayios-preview` (görünen ad "BayiOS Preview") | Spark (ücretsiz). Yalnızca FCM için; Analytics, Firestore, Hosting vb. **kullanılmaz** |
| Android uygulaması | paket `com.bayios.bayios_mobile` | `apps/mobile/android/app/build.gradle.kts` `applicationId` |
| iOS uygulaması | bundle `com.bayios.bayiosMobile` | APNs anahtarı yok: iOS push, Apple Developer hesabı gelince (#45) |
| Turnstile widget | `BayiOS preview` (sahip oluşturur, §3) | Cloudflare connector'ı Turnstile'ı desteklemiyor |

Hesapta önceden var olan `bayi-os` Firebase projesine dokunulmadı.

## 2. Push: değer → yer

| Değer | Nereye | Sır mı |
|---|---|---|
| Android istemci API anahtarı, proje id, gönderici no, Android app id | `apps/mobile/env/preview.json` → `BAYIOS_FIREBASE_API_KEY`, `_PROJECT_ID`, `_MESSAGING_SENDER_ID`, `_ANDROID_APP_ID` | Hayır (istemci tanımlayıcısı); yine de dosya git-ignored |
| iOS istemci API anahtarı, iOS app id, bundle id | aynı dosya → `BAYIOS_FIREBASE_IOS_API_KEY`, `_IOS_APP_ID`, `_IOS_BUNDLE_ID` | Hayır |
| Servis hesabı JSON anahtarı | Render API servisi env → `Push__ServiceAccountJson` | **Evet** |
| Gönderimi aç | Render API servisi env → `Push__Enabled=true` | Hayır |

Firebase her uygulama için ayrı bir istemci API anahtarı üretir. Uygulama Android'de `BAYIOS_FIREBASE_API_KEY`'i,
iOS'ta (doluysa) `BAYIOS_FIREBASE_IOS_API_KEY`'i kullanır. `google-services.json` / `GoogleService-Info.plist`
**gerekmez** ve commit edilmez: Firebase Dart'tan `FirebaseOptions` ile başlatılır.

Derleme: `flutter run --dart-define-from-file=env/preview.json` (aynı dosyada preview API ve Supabase adresleri de olmalı).

### 2.1 Servis hesabı anahtarı (sahip adımı)

Anahtar sırdır; ajan üretmez ve indirmez.

1. <https://console.firebase.google.com> → **BayiOS Preview** → sol üst ⚙ → **Project settings** → **Service accounts**.
2. **Firebase Admin SDK** → **Generate new private key** → **Generate key**. Bir JSON dosyası iner.
   (Daha dar yetki isterseniz: Google Cloud Console → IAM & Admin → Service Accounts → yeni hesap, yalnızca
   *Firebase Cloud Messaging API Admin* rolü → Keys → Add key → JSON.)
3. Render → BayiOS API servisi → **Environment** → **Add Environment Variable**:
   - `Push__ServiceAccountJson` = JSON dosyasının **tüm içeriği** (Render'da *Secret* olarak).
   - `Push__Enabled` = `true`.
   Kaydedip servisi yeniden deploy edin. Anahtar geçersizse API **başlamaz**; loglarda `Push` hatasına bakın.
4. İndirilen dosyayı diskten silin. Sohbete, issue'ya, PR'a yapıştırmayın.
5. *Firebase Cloud Messaging API (V1)* yeni projelerde açıktır: Project settings → **Cloud Messaging** sekmesinde
   "Enabled" görünmelidir.

### 2.2 Doğrulama

Preview'da personel hesabıyla mobil girişi → işletme seç → bildirim izni ver → web'den bir toplama görevini o kişiye
atayın → bildirim gelmeli. Ayrıntı: push-notifications.md §3.6.

## 3. CAPTCHA: Turnstile

Web kodu hazırdır (`apps/web/src/lib/auth/captcha.ts`). Token'ı **Supabase Auth** doğrular; API'de veya web
sunucusunda gizli anahtar kullanan kod yoktur.

**Sahip kararı:** Supabase Auth'ta CAPTCHA **açılmaz**. Mobil uygulama henüz token göndermez; Supabase'te açılırsa
mobil giriş `captcha_failed` ile reddedilir. Mobil CAPTCHA pilottan sonra yapılacak. Bu yüzden şimdilik:

- **Site key** (herkese açık) → Vercel → BayiOS web projesi → Settings → Environment Variables →
  `NEXT_PUBLIC_TURNSTILE_SITE_KEY`, ortam **Preview** → kaydet → **yeniden deploy** (değer derlemede gömülür).
  Widget giriş/kayıt/şifre sıfırlama formlarında görünür; Supabase doğrulamadığı için henüz **koruma sağlamaz**.
  İsterseniz bu adımı da Supabase CAPTCHA'yı açacağınız güne bırakabilirsiniz.
- **Secret key** → şimdilik **hiçbir yere**. Bir parola yöneticisinde saklayın. Mobil CAPTCHA hazır olduğunda
  Supabase Dashboard → Authentication → Attack Protection → *Enable CAPTCHA protection* → Turnstile → Secret key.
  Sıra: önce Vercel'de site key + deploy, sonra Supabase.

### 3.1 Widget oluşturma (sahip adımı)

1. <https://dash.cloudflare.com> → sol menü **Turnstile** (bazı hesaplarda *Application security → Turnstile*) →
   **Add widget**.
2. **Widget name**: `BayiOS preview`.
3. **Hostnames** → *Add hostnames*: `localhost` ve Vercel preview alanı (ör. `<proje>-git-preview-<hesap>.vercel.app`;
   hosting kurulunca netleşir). Her Vercel dalının kendi alanı olduğundan yalnızca sabit preview alias'ını ekleyin.
4. **Widget mode**: **Managed**. **Pre-clearance**: **No**. → **Create**.
5. Açılan ekrandaki **Site Key** → Vercel (yukarıda). **Secret Key** → parola yöneticisi. Ücretsiz plandır; kart istenmez.

Yerel deneme: `apps/web/.env.local` içine `NEXT_PUBLIC_TURNSTILE_SITE_KEY` (git-ignored). Cloudflare'in test anahtarları
da kullanılabilir (her zaman geçen: `1x00000000000000000000AA`).

## 4. Kalanlar (sahip)

- [ ] Servis hesabı anahtarı → Render `Push__ServiceAccountJson` + `Push__Enabled=true` (§2.1).
- [ ] `apps/mobile/env/preview.json` içine preview API ve Supabase adreslerini yazın (Firebase değerleri hazır; dosya
      yalnızca sahibin makinesinde).
- [ ] Turnstile widget'ı + hostname'ler (§3.1); site key → Vercel Preview; secret key → parola yöneticisi.
- [ ] iOS push: Apple Developer hesabı + APNs anahtarı (push-notifications.md §3.3, #45).
- [ ] Production için ayrı Firebase projesi ve ayrı Turnstile widget'ı (production kararıyla birlikte).
