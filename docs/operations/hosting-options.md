# BayiOS — API Hosting Seçenekleri ve Domain/Marka

| Alan | Değer |
|---|---|
| Sahip | DevOps Engineer |
| Durum | **Preview kararı verildi (2026-11-11, OD-12 — yalnızca ücretsiz katmanlar):** API ve web Render (free, Frankfurt; Vercel Hobby ekibi 2026-12-05'e kadar bloklu) — §4. Production hostu hâlâ ürün sahibi kararında |
| Araştırma tarihi | 2026-11-04 |

## 1. Gereksinimler
- ASP.NET Core .NET 10 API, Docker imajı olarak.
- Aynı süreçte veya ayrı süreçte **arka plan worker** (Postgres kuyruğu + outbox, import, AI işleri).
- Supabase `eu-central-1` (Frankfurt) yakınında → düşük DB gecikmesi; AB veri yerleşimi.
- WebSocket/SignalR (canlı ilerleme için; polling'e düşülebilir).
- Preview ortamı (en az `preview` dalı için tek uygulama; PR başına opsiyonel).
- Pilot: 1–3 tenant, düşük trafik; **always-on** tercih (worker ve SignalR için scale-to-zero uygun değil).

## 2. Karşılaştırma

Fiyatlar USD/EUR, vergi hariç; pilot varsayımı: 1 örnek, ~1 vCPU paylaşımlı / 1 GB RAM, 7/24 açık. Tahminler kaba hesaptır ve
egress, IPv4, log, depolama gibi kalemleri eksik bırakabilir — **"tahmini"** olarak okunmalı.

| Sağlayıcı | Fiyat modeli | Pilot tahmini/ay | AB bölgesi (Frankfurt'a yakınlık) | Always-on / scale-to-zero (cold start) | WebSocket/SignalR | Worker | Preview/PR | Ops yükü |
|---|---|---|---|---|---|---|---|---|
| **Fly.io** | Makine başına saniyelik; free tier yok (yalnızca deneme: 2 saat makine süresi veya 7 gün) | shared-cpu-1x 1 GB Frankfurt **$7,73/ay** (resmi); 2 makine + IPv4/volume ile ~$10–20 | `fra` Frankfurt — Supabase ile aynı şehir | İkisi de; auto-stop ile scale-to-zero (.NET cold start birkaç sn) | Evet | Ayrı process group / makine | Yerleşik yok; GitHub Actions ile `pr-<n>` uygulaması oluşturulabilir (resmi "review apps" örnekleri var — doğrulanacak) | Düşük–orta (CLI, fly.toml) |
| **Render** | Plan + instance; preview için Pro workspace | Starter instance fiyatı resmi sayfadan **çekilemedi (doğrulanamadı)**; free web servis 15 dk sonra uyur, ~1 dk uyanma | Frankfurt bölgesi — sayfadan **doğrulanamadı** (bilinen: var; teyit gerekir) | Free: uyur; ücretli: always-on | Evet (free'de WS mesajları da trafik sayılır) | Background worker ayrı servis tipi, **free yok** | Preview Environments **Pro workspace ve üstü** | Düşük |
| **Railway** | Plan ücreti + kullanım; Hobby $5 (5$ kullanım dahil), Pro $20/workspace | vCPU ≈ $20/ay, RAM ≈ $10/GB/ay → 0,5 vCPU + 1 GB ≈ $20 + plan | **EU West = Amsterdam** (Frankfurt değil; ~7–10 ms ek gecikme tahmini) | Always-on; "serverless/sleep" opsiyonu — sayfadan doğrulanamadı | Evet (genel bilgi, doğrulanacak) | Ayrı servis | PR environments var (genel bilgi; resmi sayfada doğrulanamadı) | Düşük |
| **Azure Container Apps** | Consumption: saniyelik vCPU/GiB; aylık ücretsiz hibe 180.000 vCPU-sn, 360.000 GiB-sn, 2M istek | Always-on 0,5 vCPU/1 GiB ≈ 1,3M vCPU-sn → hibeyi aşar; birim fiyatlar resmi sayfada görüntülenemedi → **~$20–40 (doğrulanamadı)** | Germany West Central (Frankfurt) | Scale-to-zero (ücret yok) veya min 1 replika | Evet | Ayrı container app / job | Revisions + GitHub Actions ile PR başına revision/app | Orta (Azure kaynak modeli, abonelik) |
| **Google Cloud Run** | İstek veya instance bazlı; `europe-west3` Frankfurt **Tier 2** | Instance-based Tier 2 CPU $0,0000216/vCPU-sn (ikincil kaynak, resmi sayfa tam okunamadı) → 1 vCPU 7/24 ≈ **$50+** + bellek; scale-to-zero ile çok daha az | `europe-west3` Frankfurt | Scale-to-zero (cold start .NET ~1–3 sn) veya min-instances | Evet (istek zaman aşımı ile sınırlı) | Always-allocated CPU veya Cloud Run Jobs | Etiketli revision URL'leri ile PR önizleme | Orta |
| **Hetzner Cloud + Coolify/Dokploy** | Sabit aylık VPS | Paylaşımlı "Regular Performance" **€6,49/ay'dan** (resmi); "Cost-Optimized" €5,99'dan ama "şu an mevcut değil". 2026'da birden fazla zam (ikincil kaynaklar) | Almanya (Falkenstein/Nürnberg) + Finlandiya | Always-on | Evet | Evet (aynı VPS) | Coolify/Dokploy PR preview özelliği var (doğrulanacak) | **Yüksek** (OS yaması, yedek, TLS, izleme, güvenlik sertleştirme sizde) |

### Kaynaklar (hepsi 2026-11-04 kontrol edildi)
- Fly.io: https://docs.fly.io/about/pricing
- Render: https://render.com/pricing (fiyat tablosu içerik olarak okunamadı), https://render.com/docs/free, https://render.com/docs/preview-environments
- Railway: https://railway.com/pricing, https://docs.railway.com/reference/deployment-regions
- Azure Container Apps: https://azure.microsoft.com/en-us/pricing/details/container-apps/ (birim fiyatlar sayfada "$-" olarak göründü)
- Google Cloud Run: https://cloud.google.com/run/pricing (sayfa tam okunamadı; Tier 2 oranı arama sonucundaki resmi sayfa özetinden — **doğrulanmalı**)
- Hetzner: https://www.hetzner.com/cloud/ ; zam haberleri (ikincil): https://wz-it.com/en/blog/hetzner-price-increase-june-2026-cpx-ccx-alternatives/
- Supabase (karşılaştırma bağlamı): https://supabase.com/pricing

## 3. Değerlendirme
- **Scale-to-zero** bu mimariye uymaz: Postgres-tabanlı job worker ve SignalR sürekli çalışan süreç ister. Bu nedenle Cloud Run / ACA'nın ücretsiz hibesi pratikte avantaj sağlamaz (min 1 replika gerekir).
- **Frankfurt'a yakınlık:** Fly.io `fra`, ACA Germany West Central, Cloud Run `europe-west3`, Hetzner (Almanya) aynı ülkede; Railway Amsterdam.
- **Ops yükü:** Hetzner + Coolify en ucuz ama tek kişilik ekip için güvenlik/yedek yükü yüksek.

## 4. Karar — preview (2026-11-11, #218)
- **Preview = iki Render free web service (`frankfurt`): API + web.** İlk karar web için Vercel Hobby'ydi; ekip `hmd-team` diğer projelerin CPU kullanımıyla "fair use" blokunda (2026-11-05 → 2026-12-05), blok kaldırma ücretli → web de Render'da (ürün sahibi, 2026-11-11). **Ayda 750 ücretsiz instance saati workspace'teki tüm servislerce paylaşılır.** Gerekçe: OD-12 (yalnızca ücretsiz katman; kart,
  plan yükseltme veya ücret = dur, ürün sahibine sor). Fly.io free tier sunmadığı için preview'da kullanılmaz.
- Render free sınırları ve kabul edilen riskler (yalnızca preview için): ~15 dk etkisizlikte uyur, ilk istek **~30–60 sn**
  sürer (cold start); worker (outbox, işler) yalnızca örnek uyanıkken çalışır; SignalR bağlantıları uykuda kopar; aylık
  ücretsiz instance saati sınırı vardır (https://render.com/docs/free). PR başına Preview Environments Render'da ücretli → yok.
- Vercel Hobby (blok kalkınca / production): ticari olmayan kullanım şartı; ticari pilot öncesi yeniden değerlendirilir.
- Tanım: `render.yaml` (Blueprint), deploy akışı `deploy-and-rollback.md` §3 (API) ve §4 (web).

## 4.1 Öneri — production (⚠️ ürün sahibi onayı gerekir; değişmedi)
Free katman production için uygun değildir (uyku, worker kesintisi). Aşağıdaki öneri production içindir.
- **Öneri: Fly.io, `fra` bölgesi.** 1 API makinesi (always-on, shared-cpu-1x 1 GB) + aynı imajdan worker process group;
  tahmini ~$10–20/ay. Supabase ile aynı şehir, düşük gecikme, WebSocket desteği, Docker-native, GitHub Actions ile deploy.
  Free tier olmadığından hesap açılışında kart gerekir → **maliyet onayı**.
- **İkinci seçenek: Azure Container Apps (Germany West Central).** .NET ekosistemine yakın, yönetilen, min 1 replika ile
  always-on; fiyat birimleri doğrulanmalı, ops modeli daha karmaşık.
- Bütçe en kritik kriterse üçüncü yol: Hetzner + Coolify (bilinçli ops yükü kabulüyle).

Onaydan önce yapılacaklar: seçilen sağlayıcının güncel fiyatı ürün sahibi tarafından hesap panelinde teyit; harcama limiti/alarm.

## 5. Domain / marka

RDAP sorgusu: `https://rdap.org/domain/<ad>` (2026-11-04). Kontrol: `google.dev`, `google.net`, `google.app` → 200 (çalışıyor);
`google.io`, `google.co`, `turkcell.com.tr` → 404 → bu uzantılar için rdap.org sonuç **vermiyor**, yani 404 = doğrulanamadı.

| Aday | RDAP sonucu | Yorum |
|---|---|---|
| bayios.com | **kayıtlı** (kayıt 2026-03-15, bitiş 2027-03-15, `client transfer prohibited`) | Başkasına ait görünüyor |
| bayios.app | kayıtsız görünüyor (404, .app RDAP çalışıyor) | HTTPS zorunlu TLD (HSTS preload) |
| bayios.net | kayıtsız görünüyor | |
| bayios.dev | kayıtsız görünüyor | Daha çok geliştirici markası |
| getbayios.com | kayıtsız görünüyor | |
| bayi-os.com | kayıtsız görünüyor | Tireli ad, sözlü iletişimde zayıf |
| bayios.io | doğrulanamadı (rdap.org .io için sonuç vermiyor) | |
| bayios.co | doğrulanamadı | |
| bayios.com.tr | doğrulanamadı (TRABİS üzerinden kontrol gerekir) | .com.tr başvurusu belge gerektirebilir |

Notlar:
- "Kayıtsız görünüyor" satın alınabilir garantisi değildir (premium/rezerve olabilir); registrar'da kontrol gerekir.
- **TÜRKPATENT marka araştırması** (https://www.turkpatent.gov.tr) "BayiOS" ve benzerleri için yapılmalı; `bayios.com`'un başkasına ait olması marka çakışması riskine işaret edebilir.
- Satın alma yapılmadı; domain ve DNS ürün sahibi onayı gerektirir.
