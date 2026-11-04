# Güvenlik Politikası

BayiOS, çok kiracılı (multi-tenant) bir B2B stok ve sipariş yönetimi SaaS'ıdır. Güvenlik açıklarını sorumlu şekilde bildiren
herkese teşekkür ederiz.

## Açık bildirme

**Lütfen güvenlik açıklarını public issue, PR, tartışma veya sosyal medya üzerinden paylaşmayın.**

Bildirim kanalı: **GitHub Private Vulnerability Reporting**
1. Bu deponun **Security** sekmesine gidin.
2. **Report a vulnerability** düğmesine tıklayın.
3. Formu doldurun.

> Not (bakımcılar için): Bu özellik depo ayarlarından açılmalıdır — Settings → Security and quality → Advanced Security →
> "Private vulnerability reporting" → Enable.

Raporunuzda lütfen şunları belirtin:
- Etkilenen bileşen (API, web, mobil, altyapı, CI) ve sürüm/commit,
- Yeniden üretme adımları veya kavram kanıtı,
- Etki (ör. başka bir tenant'ın verisine erişim),
- Varsa önerilen düzeltme.

## Yanıt hedefleri
| Adım | Hedef süre |
|---|---|
| Alındı bildirimi | 3 iş günü |
| İlk değerlendirme ve ciddiyet | 7 iş günü |
| Critical/High düzeltme | Mümkün olan en kısa sürede; hedef 30 gün |
| Koordineli açıklama | Düzeltme yayınlandıktan sonra, raportörle birlikte |

Bu hedefler küçük bir ekip tarafından en iyi çaba esasıyla sağlanır.

## Desteklenen sürümler
| Sürüm | Destek |
|---|---|
| `main` dalındaki en son sürüm ve en son yayınlanmış etiket | ✅ |
| Eski etiketler | ❌ |

Proje henüz pilot öncesi aşamadadır; kararlı (1.0) sürüm yayınlandığında bu tablo güncellenecektir.

## Kapsam
**Kapsam içi:**
- Bu depodaki kod: `services/api`, `apps/web`, `apps/mobile`, `infra`, `.github/workflows`
- Tenant izolasyonu ihlalleri (bir tenant'ın başka tenant verisini okuması/değiştirmesi) — en yüksek öncelik
- Kimlik doğrulama/yetkilendirme atlatma, rol yükseltme
- Stok/sipariş bütünlüğünü bozan açıklar
- Dosya yükleme (CSV/Excel) kaynaklı açıklar, formül enjeksiyonu
- AI ayrıştırma özelliğinde prompt injection ile yetkisiz işlem
- Depoda yanlışlıkla bulunan sırlar

**Kapsam dışı:**
- Üçüncü taraf hizmetlerin kendisi (Supabase, Vercel, Firebase, Anthropic, GitHub) — lütfen ilgili sağlayıcıya bildirin
- Hizmet engelleme (DoS/DDoS) testleri, yük testleri
- Sosyal mühendislik, fiziksel saldırılar
- Otomatik tarayıcı çıktıları (kanıtlanmış etki olmadan)
- Eksik güvenlik başlıkları gibi istismar edilemeyen en iyi uygulama önerileri (yine de memnuniyetle karşılanır)

## Güvenli test kuralları
- Yalnızca kendi oluşturduğunuz test hesapları ve tenant'larla test edin.
- Gerçek kullanıcı verisine erişirseniz durun, veriyi saklamayın ve hemen bildirin.
- Production ortamında otomatik tarama yapmayın; mümkünse yerel ortamı (`docker compose`) kullanın.

Bu kurallara uyan iyi niyetli araştırmalara karşı hukuki işlem başlatılmayacaktır. Şu an için ödül (bug bounty) programı yoktur.
