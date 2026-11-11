# BayiOS — Bağlayıcılar, Araçlar ve Ortam Denetimi

| Alan | Değer |
|---|---|
| Sahip | DevOps Engineer (+ Security Engineer) |
| Denetim tarihi | 2026-11-04 (gerçek ortamdan toplandı) |

## 1. Claude Code ve delegasyon
- Claude Code **2.1.316**, model `claude-opus-5-5`.
- **Agent Teams: etkin değil** (`CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS` tanımlı değil). Resmi belgeye göre deneysel ve varsayılan
  kapalı; etkinleştirme: settings.json `{"env": {"CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS": "1"}}`
  (https://code.claude.com/docs/en/agent-teams, 2026-11-04 doğrulandı). Etkinleştirmek ürün sahibi kararıdır; şu an gerek yok.
- Kullanılan: Agent aracı ile alt ajanlar + `isolation: worktree` (ayrı git worktree).

## 2. Yerel araçlar

| Araç | Durum | Sürüm / not |
|---|---|---|
| git | ✅ | 2.55 |
| .NET SDK | ✅ | 8.0.427, 10.0.202, 10.0.403 |
| Node / npm | ✅ | Node 26.13 / npm 11.20 |
| Docker | ✅ | 29.9.2, daemon çalışıyor (Testcontainers için yeterli) |
| Vercel CLI | ✅ | 59.5.0, `halilmertdeveliii-2443` olarak giriş yapılmış |
| Java | ✅ | 21 |
| GitHub CLI | ✅ | gh 2.103.3, `HalilMertDeveli`, kapsamlar: `gist, read:org, repo, workflow` |
| Flutter / Dart SDK | ❌ | Kurulu değil → mobil işler BLOCKED |
| Android SDK | ❌ | `ANDROID_HOME` boş |
| Supabase CLI | ❌ | Kurulu değil |
| Firebase CLI | ❌ | Kurulu değil |
| pnpm | ❌ | Gerekli değil (npm kullanılıyor) |
| iOS derleme | ❌ | Windows 11 host → macOS gerekir (CI macOS runner / Codemagic vb.) |

## 3. Bağlayıcı tablosu

| Bağlayıcı / araç | Durum | Kapsam / yetki | Kullanım amacı | Limitler | Gerekli kimlik | Maliyet riski | Güvenlik riski | En az yetki notu |
|---|---|---|---|---|---|---|---|---|
| GitHub (`gh` CLI) | Bağlı | `repo, workflow, gist, read:org` | Repo, issue, PR, Actions, ruleset | API rate limit | Mevcut token | Düşük (public repo Actions dakikaları ücretsiz — plan koşullarına bağlı) | `repo`+`workflow` geniş: tüm repolara yazabilir | Ruleset ayarı için admin gerekir; uzun vadede repo'ya özel fine-grained token veya GitHub App; `gist` gereksiz |
| Supabase MCP | Bağlı | Org "HalilMertDeveli's Org" — proje oluşturma, SQL, migration, branch | Şema inceleme, dev/preview migration, advisors | Free: 2 aktif proje | Mevcut OAuth | **Yüksek**: `create_project`, `create_branch` maliyet doğurabilir | `execute_sql` ilgisiz `led-com-staging` dahil tüm projelere erişebilir | Yalnızca `bayios-*` projelerinde; prod'a yazma yok; MCP'yi mümkünse proje kapsamlı/read-only modda kullan |
| Vercel CLI + Vercel MCP | Bağlı (MCP'nin `hmd-team` yetkisi yok; CLI çalışır) | Hobby ekibi `hmd-team` — **2026-12-05'e kadar fair-use blokunda** | Şimdilik kullanılmıyor (web preview Render'da) | Hobby: ticari kullanım yok | Mevcut giriş / OAuth | Orta (Pro geçişi) | Token tüm projelere erişir | Yalnızca `bayios-web`; deploy Git entegrasyonuyla, CI'da token yok |
| Figma MCP | Bağlı | Kullanıcı "Halil Develi", starter takım, Full seat | Tasarım sistemi, ekranlar (UX Designer) | Starter plan limitleri (dosya/sayfa) | Mevcut | Düşük | Düşük (gerçek veri konmaz) | Yalnızca BayiOS dosyaları |
| context7 | Bağlı | Okuma | Kütüphane dokümantasyonu doğrulama | — | — | Yok | Düşük | — |
| Yerleşik tarayıcı / Claude in Chrome | Bağlı | Tarayıcı kontrolü | Preview ekran görüntüsü, E2E gözden geçirme | — | Chrome'da kullanıcının oturumları | Yok | **Orta**: Chrome uzantısı kullanıcının gerçek oturumlarıyla çalışır | Ekran görüntüsü için yerleşik tarayıcı tercih; Chrome'da üretim panellerine işlem yapılmaz |
| Computer use | Bağlı | Masaüstü kontrolü | Gerektiğinde yerel GUI (ör. Android Studio kurulum doğrulama) | — | Uygulama bazında izin | Yok | Orta | Yalnızca gerektiğinde, uygulama bazlı izin |
| Notion MCP | Bağlı | Çalışma alanı | Kullanılmıyor (kaynak gerçeklik GitHub + repo belgeleri) | — | — | Yok | Düşük | Kullanılmaz |
| Gmail MCP | Bağlı | Posta | Kullanılmıyor | — | — | Yok | **Orta** (gönderim yeteneği) | Kullanılmaz; mesaj gönderimi onaysız yapılmaz |
| Firebase MCP | **Eksik** | — | FCM proje yönetimi | — | — | — | — | CLI ile ve onayla |
| Domain registrar | **Eksik** | — | Domain satın alma/DNS | — | — | — | — | Ürün sahibi yapar |
| Anthropic API (uygulama içi) | Henüz yok | — | WhatsApp ayrıştırma | Hesap limiti | API anahtarı (ürün sahibi oluşturur) | **Orta**: token kullanımı | Anahtar sızıntısı | Ortam başına ayrı anahtar, harcama limiti |
| Render MCP | Bağlı | Workspace "Halil mert's workspace" | Preview servisleri `bayios-api-preview` + `bayios-web-preview` (free, Frankfurt), env, deploy/log | Free: uyku, aylık instance saati | Mevcut OAuth | **Orta**: plan yükseltme ücret doğurur — yalnızca `plan: free` (OD-12) | Env değerlerini okuyabilir/yazabilir | Sırlar ajan tarafından girilmez; yalnızca `bayios-*` servisleri |
| API host — production (Fly.io önerisi) | Henüz yok | — | API + worker | — | Hesap + kart | **Orta** | Deploy token | Uygulama kapsamlı deploy token |

## 4. BLOCKED — ürün sahibi kurulum adımları

### 4.1 Flutter SDK + Android Studio (mobil işler için)
1. Android Studio'yu indir/kur: https://developer.android.com/studio → ilk açılışta SDK, platform-tools, bir emülatör imajı.
2. SDK Manager → SDK Tools → **Android SDK Command-line Tools** işaretle.
3. Flutter SDK (resmi öneri: VS Code Flutter eklentisi ile kurulum, alternatif: https://docs.flutter.dev/install adresinden stable zip indir, örn. `C:\dev\flutter`'a çıkar, `C:\dev\flutter\bin`'i PATH'e ekle).
4. Yeni terminalde:
   ```
   flutter doctor
   flutter doctor --android-licenses
   flutter doctor
   ```
5. `ANDROID_HOME` ortam değişkenini SDK yoluna ayarla (genelde `%LOCALAPPDATA%\Android\Sdk`).
6. Doğrulama: `flutter --version` çıktısını Orkestra Şefi'ne ilet.

### 4.2 Supabase CLI (Windows)
- **`npm i -g supabase` desteklenmez** (resmi belge npm'i yalnızca proje bazlı kurulum olarak anlatır).
- Resmi Windows yöntemi Scoop (https://supabase.com/docs/guides/local-development/cli/getting-started, 2026-11-04):
  ```powershell
  # Scoop yoksa: https://scoop.sh adresindeki kurulum komutu
  scoop bucket add supabase https://github.com/supabase/scoop-bucket.git
  scoop install supabase
  supabase --version
  ```
- Alternatif (proje bazlı): `npm install supabase --save-dev` → `npx supabase ...` (Node 20+ gerekir).
- Local stack için Docker Desktop çalışıyor olmalı (mevcut).

### 4.3 Firebase CLI
- Resmi (https://firebase.google.com/docs/cli, 2026-11-04): `npm install -g firebase-tools` (Node ≥ 18) veya Windows standalone binary.
- Sonra **ürün sahibi** çalıştırır: `firebase login` (Google hesabı ile — ajan giriş yapmaz), `firebase projects:list`.
- Projeler: `bayios-dev`, `bayios-prod` (oluşturma ücretsiz Spark planında; Blaze'e geçme onayı gerekir).

### 4.4 Apple Developer hesabı (iOS)
- Apple Developer Program üyeliği (yıllık ücretli — güncel ücreti developer.apple.com'dan doğrulayın) → **maliyet onayı**.
- macOS derleme ortamı: GitHub Actions macOS runner (dakika çarpanı ücretli olabilir) veya Codemagic gibi bir CI → **onay**.
- APNs anahtarı → Firebase `bayios-prod`'a yüklenir (FCM iOS push için).

### 4.5 API host hesabı
- Preview: Render free (bağlı, #218). Sırlar (`bayios_app` bağlantısı) yalnızca ürün sahibi tarafından Render Dashboard'a girilir.
- Production: öneri Fly.io (bkz. `hosting-options.md` §4.1) — hesap, kart, harcama limiti ve deploy token'ı ürün sahibi onayıyla.

### 4.6 Diğer
- GitHub repo `HalilMertDeveli/BayiOS` oluşturma (public) — ajan `gh` ile yapabilir ama ilk kurulum ürün sahibi onayıyla.
- Anthropic API anahtarı (ortam başına) + harcama limiti.
- Supabase `bayios-preview` proje oluşturma onayı.
