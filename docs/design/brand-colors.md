# BayiOS — Marka renkleri (petrol + bakır)

| Alan | Değer |
|---|---|
| Karar | Ürün sahibi, 2026-11-09 (#61): "güven veren ve farklı" bir ana renk; jenerik SaaS mavisi değil |
| Kaynak (web) | `apps/web/src/app/globals.css` (`--bos-petrol-*`, `--bos-copper-*`, `--bos-brand-*`) |
| Kaynak (mobil) | `apps/mobile/lib/core/ui/tokens.dart` (`BosPetrol`, `BosCopper`, `BosColors.brand*`) |
| Koruma | `apps/web/src/app/tokens.test.ts` (kontrast + iki koyu blok eşitliği + Flutter eşleşmesi), `apps/mobile/test/core/ui/tokens_test.dart` |

## 1. Gerekçe
- **Petrol (derin mavi-yeşil, ton ~193°)** — lacivertin kurumsal güvenini taşır ama B2B SaaS'ta yaygın mavi (`#2563eb`
  ailesi) ve önceki lacivert (`#0f4c75`) ile karışmaz. Depo/lojistik dünyasında "sakin, sağlam, endüstriyel" okunur.
- **Bakır (sıcak turuncu-kahve, ton ~22°)** — petrolün tamamlayıcısı; markaya sıcaklık verir. **Yalnızca dekoratif**:
  logo işaretindeki alt çizgi, illüstrasyon, pazarlama yüzeyi. Durum (uyarı/hata), rozet veya gövde metni olarak
  **kullanılmaz** — sıcak tonlar uyarı (amber) ve hata (kırmızı) ile aynı bölgededir.
- Durum renkleri (success yeşil, warning amber, danger kırmızı, info mavi, mor, teal, AI çivit) **değişmedi**. Tek
  istisna: koyu temada `danger-solid-hover` `#e04a3f` → `#b3302a` (beyaz metin 4.02:1 → 6.23:1; önceden AA altındaydı).
- Durum etiketleri yalnızca renge dayanmaz (renk + metin + ikon, tasarım planı §1.3); renk farkı ek ipucudur.

## 2. Ölçekler (temadan bağımsız)
| Adım | Petrol | Bakır |
|---|---|---|
| 50 | `#EDF6F8` | `#FCF3ED` |
| 100 | `#D4EAEF` | `#F8E1D2` |
| 200 | `#93CDD9` | `#EDB48D` |
| 300 | `#7FC4D3` | `#E39A68` |
| 400 | `#6CB8C8` | `#DD8650` |
| 500 | `#3D9AAE` | `#D4733A` |
| 600 | `#1F7A90` | `#A4501F` |
| 700 | `#136579` | `#843F18` |
| 800 | `#0C5468` | `#66311A` |
| 900 | `#0A4354` | `#4F2815` |
| 950 | `#04191E` | `#1F0F06` |

Bileşenler ölçeği doğrudan kullanmaz; yalnızca aşağıdaki anlamsal token'ları (Tailwind: `bg-brand`, `text-brand-fg`,
`text-link`, `outline-focus`, `bg-accent` …) kullanır.

## 3. Anlamsal eşleme
| Token | Açık tema | Koyu tema |
|---|---|---|
| `brand.primary` (birincil buton) | petrol-800 `#0C5468` | petrol-400 `#6CB8C8` |
| `brand.primary-hover` | petrol-900 `#0A4354` | petrol-200 `#93CDD9` |
| `brand.primary-subtle` | petrol-50 `#EDF6F8` | `#0D262D` |
| `brand.on-primary` | `#FFFFFF` | petrol-950 `#04191E` |
| `brand.accent` (dekoratif) | copper-500 `#D4733A` | copper-300 `#E39A68` |
| `brand.accent-hover` | copper-400 `#DD8650` | copper-200 `#EDB48D` |
| `brand.on-accent` | copper-950 `#1F0F06` | copper-950 `#1F0F06` |
| `text.link` | petrol-700 `#136579` | petrol-300 `#7FC4D3` |
| `border.focus` (odak halkası) | petrol-600 `#1F7A90` | petrol-300 `#7FC4D3` |
| `brand.fg / bg / border` (rozet, bağlantılar) | `#0C5468` / `#EDF6F8` / `#93CDD9` | `#93CDD9` / `#0D262D` / `#2B5D68` |
| `brand.panel` (giriş/kayıt yan paneli; temadan bağımsız, iki temada da koyu) | petrol-900 `#0A4354` | petrol-900 `#0A4354` |
| `brand.on-panel` / `brand.on-panel-muted` | `#FFFFFF` (10.8:1) / petrol-100 `#D4EAEF` (8.4:1) | aynı |

Panel üzerinde bakır vurgu (çizgi, ikon) yalnızca dekoratiftir (≥ 3:1, `tokens.test.ts`).

Mobil (Flutter) şu an yalnızca açık temayı kullanır (koyu tema P2, D-03); değerleri açık sütunla birebir aynıdır.

## 4. Kontrast (WCAG 2.2 AA, hesaplanmış)
Zeminler: açık `page #F6F7F9`, `card #FFFFFF`, `sunken #EEF0F3`; koyu `page #0D1117`, `card #151B23`, `sunken #0A0E13`.

| Çift | Eşik | Açık | Koyu |
|---|---|---|---|
| on-primary / primary (buton metni) | 4.5 | **8.46** | **8.02** |
| on-primary / primary-hover | 4.5 | **10.80** | **10.30** |
| primary dolgu / page · card · sunken (1.4.11) | 3 | 7.89 · 8.46 · 7.41 | 8.41 · 7.69 · 8.60 |
| text.link / page · card · sunken | 4.5 | 6.19 · 6.63 · 5.81 | 9.68 · 8.85 · 9.90 |
| brand.fg / page · card · sunken | 4.5 | 7.89 · 8.46 · 7.41 | 10.79 · 9.87 · 11.04 |
| brand.fg / brand.bg (rozet) | 4.5 | 7.71 | 8.99 |
| primary / primary-subtle | 4.5 | 7.71 | 7.00 |
| focus halkası / page · card · sunken | 3 | 4.62 · 4.95 · 4.33 | 9.68 · 8.85 · 9.90 |
| on-accent / accent | 4.5 | 5.59 | 8.05 |
| on-accent / accent-hover | 4.5 | 6.73 | 10.20 |

Odak halkası `outline-offset: 2px` ile zemine karşı çizilir; bu yüzden ölçüt halka/zemin kontrastıdır.

## 5. Durum renklerinden ayrışma (CIEDE2000 ΔE, metin renkleri)
| Çift | Açık | Koyu |
|---|---|---|
| primary ↔ success | 29.0 | 26.5 |
| primary ↔ info | 17.5 (eski lacivert: 12.5) | 18.7 |
| brand.fg ↔ teal.fg ("Sevk Edildi" ↔ "Paketlendi") | 12.7 | 10.3 |
| focus ↔ danger | 56.1 | 48.8 |
| accent ↔ warning.fg · danger.fg | 21.0 · 22.3 | 14.0 · 15.2 |

Petrol, eski laciverte göre bilgi mavisinden belirgin biçimde ayrıştı. "Paketlendi" (teal) ile "Sevk Edildi" (brand)
rozetleri yakın tonlardadır (önceki paletteki en yakın çift olan brand ↔ info ile aynı düzeyde); ikisi farklı ikon
ve etiket taşır. Teal tonu bilerek değiştirilmedi (yeşile yaklaşıp success ile karışırdı).
