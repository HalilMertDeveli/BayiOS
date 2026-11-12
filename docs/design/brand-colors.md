# BayiOS — Renkler ve tasarım token'ları (UI kit, #222)

| Alan | Değer |
|---|---|
| Karar | Ürün sahibi, 2026-11-12 (#222): arayüz Figma Community UI kit'ine göre yeniden kurulur. Önceki petrol + bakır paleti (#61, #178) emekliye ayrıldı. |
| Kaynak (UI kit) | "AI Inventory Management Web App UI Kit" — Lukáš Marek (Figma Community), **CC BY 4.0**, değiştirilerek kullanılır. Kredi satırı: Ayarlar › Hakkında (web) ve Profil › Hakkında (mobil). |
| Tek kaynak | `packages/design-tokens/renkler.json`, `tipografi.json` (kit dışa aktarımı) → `node packages/design-tokens/build.mjs` üretir: `apps/web/src/app/tokens.css`, `apps/mobile/lib/core/theme/app_palette.dart` |
| Anlamsal katman | web `apps/web/src/app/globals.css` (`--bos-*`, açık + koyu) · mobil `apps/mobile/lib/core/theme/app_colors.dart` (`AppColors` ThemeExtension, açık + koyu) |
| İkonlar | `packages/design-tokens/icons.mjs` → `packages/design-tokens/icons/` (kaynak), `apps/mobile/assets/icons/`, `apps/web/src/components/ui/icons/data.ts` |
| Koruma | `apps/web/src/app/tokens.test.ts` (WCAG kontrastı, iki koyu blok eşitliği, üretilmiş dosyalar güncel mi, Flutter açık tema eşleşmesi), `apps/mobile/test/core/ui/tokens_test.dart` |

## 1. Palet
Kit ölçekleri (50…900): **Primary** (yeşil, ana `#27A376`), **Grayscale** (0 `#F4F6F6` … 900 `#0C110F`), Success,
Red, Yellow, Blue, Purple. Bileşenler ham ölçeği değil yalnızca anlamsal token'ları kullanır.

| Rol | Açık | Koyu |
|---|---|---|
| Sayfa (`surface.page`) | `#F4F6F6` gray-0 | `#0C110F` gray-900 |
| Kart (`surface.card`) | `#FFFFFF` | `#181D1B` (kit: beyaz %5, düz renge çevrildi) |
| Metin (`text.primary`) | `#0C110F` | `#F4F6F6` |
| Kenarlık (`border.default`) | `#E6E8E5` gray-200 | `#2F3833` |
| Marka vurgusu (`brand.accent`) | `#27A376` primary-600 | `#79C7AB` primary-500 |
| Marka dolgusu (`brand.primary`, buton) | `#1F825E` primary-700 | `#79C7AB` primary-500 |
| Kenar çubuğu (`nav.bg`) | `#2C342F` gray-800 | `#141A17` |

## 2. Kit değerinden sapmalar (WCAG 2.2 AA)
Kit'in birkaç değeri AA'yı karşılamıyor; testler gevşetilmedi, bir sonraki kit adımı kullanıldı:

| Kit değeri | Sorun | Kullanılan |
|---|---|---|
| Buton `#27A376` + beyaz metin | 3.19:1 (< 4.5) | Dolgu `#1F825E` (primary-700, 4.76:1); `#27A376` vurgu/grafik/logo olarak kalır, butonda üstten alta geçişin üst rengi |
| İkincil metin `#919992` | beyaz üzerinde 2.92:1 | `text.secondary` `#2C342F`, `text.muted` `#4A5950` (kit "Text/tertiary") |
| Kenarlık `#E6E8E5` form kontrollerinde | < 3:1 (1.4.11) | Kontrol kenarı `border.strong` `#707371`; kartlar ve tablolar kit kenarlığını kullanır |
| Rozet metinleri (`#0CAF60`, `#FFD023`, …) | kendi açık zeminlerinde < 4.5 | Aynı ailenin 700–900 adımı (`success.fg` `#07693A`, `warning.fg` `#66530E`, …); zemin ve kenarlık kit değerleri |
| Koyu tema rozetleri | `#E65A5F` / `#5A1416` 3.9:1 | Koyu temada fg = ailenin 300 adımı |

Durum etiketleri yalnızca renge dayanmaz (renk + metin + ikon). Kit'in "Interoly" adı ve logosu kullanılmaz; logo yerine
geçici "B" işareti + "BayiOS" yazısı durur (sahibinin logosu gelince değişir).

## 3. Tipografi, boşluk, yarıçap, gölge
- Yazı ailesi **Inter Tight** (web `next/font/google`, mobil `GoogleFonts.interTight` — font dosyaları uygulamada paketli), kod/SKU için JetBrains Mono.
- Ölçek (kit): H4 32/42 · H5 24/34 · Large 18/24 · Medium 16/20 · Small 14/18 · X-Small 12/16. Web eşlemesi: `display` = H4, `h1` = H5, `h2` = Large SemiBold, `h3` = Medium SemiBold, `body` = Small (14), `caption` = X-Small.
- Boşluk 4/8/12/16/24/32 (kit Padding ölçeği; web'de Tailwind'in 4 px tabanı ile aynı).
- Yarıçap: kontroller 8, kartlar 16, rozetler 8, büyük paneller 16–20 (web `rounded-md` 8, `rounded-lg` 12, `rounded-xl` 16, `rounded-2xl` 20).
- Gölge: kit düzdür — `elevation-1` kıl çizgisi, `elevation-2` kart, birincil butonda iç parlama + yumuşak düşen gölge (`shadow-button`).
