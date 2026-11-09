import { createRng, seedOf, syntheticEan13 } from "./rng.mjs";

/**
 * "Demo Toptan Ticaret" — the DEFAULT demo: a fully synthetic, sector-agnostic B2B wholesaler / distributor of everyday
 * goods (cleaning supplies, paper & hygiene, packaging, stationery & office, small hardware, catering consumables).
 * The product is sector-agnostic (docs/operations/phase0-gate.md §5.2), so this demo starts from the "Sade başlangıç"
 * (general) template and builds its own units, categories and custom fields through the API.
 *
 * Every business, person, phone, address, code, barcode (GS1 in-store range 202, never a real item) and price is
 * invented and deterministic (seeded RNG): `genelDemo(size)` is a pure function — same size, same dataset.
 * It showcases every current feature: two warehouses (shelved main + branch), variants, price lists for two customer
 * groups, a team (owner, manager, sales, warehouse staff), orders in every state with prioritised picking tasks,
 * two-stage transfers (one on the road, one received in full, one received short and closed), a shelf move, an open
 * stock count and minimum-stock thresholds for the critical-stock report.
 */

/** Main-warehouse shelves: one per category (pick sequence = walking order) + an overflow shelf for the shelf move. */
const SHELVES = [
  { code: "A-01", category: "Temizlik Ürünleri", pickSequence: 10 },
  { code: "A-02", category: "Kâğıt & Hijyen", pickSequence: 20 },
  { code: "B-01", category: "Ambalaj & Paketleme", pickSequence: 30 },
  { code: "B-02", category: "Kırtasiye & Ofis", pickSequence: 40 },
  { code: "C-01", category: "Hırdavat", pickSequence: 50 },
  { code: "C-02", category: "Mutfak & İkram Sarf", pickSequence: 60 },
  { code: "R-01", category: null, key: "overflow", pickSequence: 90 },
];

export const GENEL_CATEGORIES = SHELVES.filter((s) => s.category).map((s) => s.category);

/** Units on top of the general template's adet / koli / kg. */
const EXTRA_UNITS = [
  { code: "paket", name: "Paket", allowsFraction: false },
  { code: "rulo", name: "Rulo", allowsFraction: false },
  { code: "kutu", name: "Kutu", allowsFraction: false },
  { code: "litre", name: "Litre", allowsFraction: true },
];

/** Tenant-defined custom fields (the general template has none). */
const CUSTOM_FIELDS = [
  { entityType: "product", key: "koli_ici", label: "Koli içi adet", dataType: "integer", unit: "adet", options: null, filterable: false },
  { entityType: "product", key: "mensei", label: "Menşe", dataType: "enum", unit: null, options: ["Yerli üretim", "İthal"], filterable: true },
];

const BRAND_OF = {
  "Temizlik Ürünleri": "Örnek Temizlik",
  "Kâğıt & Hijyen": "Örnek Kâğıt",
  "Ambalaj & Paketleme": "Kurgu Ambalaj",
  "Kırtasiye & Ofis": "Demo Ofis",
  Hırdavat: "Sentetik Hırdavat",
  "Mutfak & İkram Sarf": "Kurgu Mutfak",
};
const PRIVATE_LABEL = "Demo Toptan Özel Seri";
const BRANDS = [...Object.values(BRAND_OF), PRIVATE_LABEL];

/** Variant axis: [code, label, price factor]. */
const v = (option, values) => ({ option, values });
const HACIM = (a, b, f) => v("Hacim", [[a.replace(/[ ,]/g, "").toUpperCase(), a, 1], [b.replace(/[ ,]/g, "").toUpperCase(), b, f]]);
const RENK = {
  MAV: "Mavi",
  SIY: "Siyah",
  KIR: "Kırmızı",
  YES: "Yeşil",
  SAR: "Sarı",
  PEM: "Pembe",
  BEY: "Beyaz",
  SFF: "Şeffaf",
  KHV: "Kahverengi",
};
const COLORS = (...codes) => v("Renk", codes.map((c) => [c, RENK[c], 1]));
const SIZES = (option, list) => v(option, list);

/**
 * The catalog (API). [sku, name, category, unit, [min, max] price (TRY, excl. VAT), variant axis?, default-list tiers?]
 * Order matters: `--size small` keeps the first two products of every category.
 */
const CATALOG = [
  // ---- Temizlik Ürünleri
  ["TEM-YUZEY", "Çok amaçlı yüzey temizleyici", "Temizlik Ürünleri", "adet", [55, 75], HACIM("1 L", "5 L", 4.2)],
  ["TEM-BULASIK", "Sıvı bulaşık deterjanı", "Temizlik Ürünleri", "adet", [40, 60], HACIM("750 ml", "4 L", 4)],
  ["TEM-CAMASIRSUYU", "Çamaşır suyu", "Temizlik Ürünleri", "adet", [28, 40], HACIM("1 L", "4 L", 3.2)],
  ["TEM-CAM", "Cam temizleyici 500 ml", "Temizlik Ürünleri", "adet", [35, 50]],
  ["TEM-YER", "Yer temizleyici 2,5 L", "Temizlik Ürünleri", "adet", [70, 95], v("Koku", [["LIM", "Limon", 1], ["LAV", "Lavanta", 1], ["CAM", "Çam", 1]])],
  ["TEM-SABUN", "Sıvı el sabunu", "Temizlik Ürünleri", "adet", [38, 52], HACIM("500 ml", "5 L", 6)],
  ["TEM-MIKROFIBER", "Mikrofiber temizlik bezi (4'lü)", "Temizlik Ürünleri", "paket", [90, 130], COLORS("MAV", "SAR", "PEM", "YES")],
  [
    "TEM-COP",
    "Çöp torbası (10'lu rulo)",
    "Temizlik Ürünleri",
    "rulo",
    [18, 26],
    SIZES("Boyut", [["KUC", "Küçük 40x50", 1], ["ORT", "Orta 55x60", 1.3], ["BUY", "Büyük 65x80", 1.7], ["JMB", "Jumbo 80x110", 2.4]]),
    [{ minQuantity: 20, factor: 0.9 }],
  ],
  ["TEM-SUNGER", "Bulaşık süngeri (10'lu)", "Temizlik Ürünleri", "paket", [45, 65]],
  ["TEM-ELDIVEN", "Nitril eldiven (100'lü kutu)", "Temizlik Ürünleri", "kutu", [160, 220], SIZES("Beden", [["S", "S", 1], ["M", "M", 1], ["L", "L", 1]])],
  ["TEM-MOP", "Mop yedek başlığı", "Temizlik Ürünleri", "adet", [65, 95]],
  ["TEM-KIREC", "Kireç çözücü 1 L", "Temizlik Ürünleri", "adet", [48, 68]],
  ["TEM-DETERJAN-KG", "Toz çamaşır deterjanı (dökme, kg)", "Temizlik Ürünleri", "kg", [42, 58]],
  ["TEM-OVMA", "Krem ovma temizleyici 750 ml", "Temizlik Ürünleri", "adet", [40, 55]],
  ["TEM-KOVA", "Çift hazneli temizlik kovası", "Temizlik Ürünleri", "adet", [380, 520]],
  // ---- Kâğıt & Hijyen
  ["HIJ-TUVALET", "Tuvalet kâğıdı (32'li)", "Kâğıt & Hijyen", "paket", [210, 290], null, [{ minQuantity: 10, factor: 0.93 }]],
  ["HIJ-HAVLU", "Kâğıt havlu (12'li)", "Kâğıt & Hijyen", "paket", [180, 240]],
  ["HIJ-ZHAVLU", "Z katlı kâğıt havlu (12 paket)", "Kâğıt & Hijyen", "koli", [320, 420]],
  ["HIJ-PECETE", "Peçete (100'lü)", "Kâğıt & Hijyen", "paket", [22, 32], SIZES("Ölçü", [["24", "24x24", 1], ["33", "33x33", 1.35]])],
  ["HIJ-ICTEN", "İçten çekmeli havlu (6'lı)", "Kâğıt & Hijyen", "paket", [260, 340]],
  ["HIJ-DEZENFEKTAN", "El dezenfektanı", "Kâğıt & Hijyen", "adet", [75, 105], HACIM("1 L", "5 L", 4.4)],
  ["HIJ-ISLAK", "Islak mendil (100 yaprak)", "Kâğıt & Hijyen", "paket", [38, 55]],
  ["HIJ-DISPENSER", "Havlu dispenseri, duvar tipi", "Kâğıt & Hijyen", "adet", [420, 560], COLORS("BEY", "SIY")],
  // ---- Ambalaj & Paketleme
  [
    "AMB-KOLI",
    "Oluklu koli (25'li)",
    "Ambalaj & Paketleme",
    "paket",
    [190, 240],
    SIZES("Ölçü", [["20", "20x20x20 cm", 1], ["30", "30x30x30 cm", 1.5], ["40", "40x30x30 cm", 1.9], ["60", "60x40x40 cm", 3]]),
    [{ minQuantity: 10, factor: 0.9 }],
  ],
  ["AMB-BANT", "Koli bandı 45 mm x 100 m (6'lı)", "Ambalaj & Paketleme", "paket", [150, 190], COLORS("SFF", "KHV")],
  ["AMB-STREC", "Streç film 17 mikron 50 cm", "Ambalaj & Paketleme", "rulo", [240, 310]],
  ["AMB-BALON", "Balonlu naylon 1 m x 50 m", "Ambalaj & Paketleme", "rulo", [280, 360]],
  ["AMB-KRAFT", "Kraft ambalaj kâğıdı 1 m x 100 m", "Ambalaj & Paketleme", "rulo", [350, 450]],
  ["AMB-ZIP", "Kilitli poşet (100'lü)", "Ambalaj & Paketleme", "paket", [45, 60], SIZES("Ölçü", [["1015", "10x15 cm", 1], ["1520", "15x20 cm", 1.4], ["2030", "20x30 cm", 2]])],
  ["AMB-ETIKET", "Termal etiket 100x100 (500'lü rulo)", "Ambalaj & Paketleme", "rulo", [130, 170]],
  ["AMB-BANTMAK", "Koli bandı kesme makinesi", "Ambalaj & Paketleme", "adet", [190, 260]],
  ["AMB-KARGO", "Kargo poşeti (100'lü)", "Ambalaj & Paketleme", "paket", [110, 150], SIZES("Boyut", [["KUC", "Küçük", 1], ["ORT", "Orta", 1.4], ["BUY", "Büyük", 1.9]])],
  ["AMB-CEMBER", "PP çember 12 mm", "Ambalaj & Paketleme", "rulo", [420, 540]],
  ["AMB-KOSEBENT", "Karton köşebent (50'li)", "Ambalaj & Paketleme", "paket", [160, 210]],
  // ---- Kırtasiye & Ofis
  ["OFS-A4", "A4 fotokopi kâğıdı 80 g (5 top)", "Kırtasiye & Ofis", "koli", [520, 640], null, [{ minQuantity: 10, factor: 0.92 }]],
  ["OFS-KALEM", "Tükenmez kalem (50'li kutu)", "Kırtasiye & Ofis", "kutu", [140, 190], COLORS("MAV", "SIY", "KIR")],
  ["OFS-KLASOR", "Geniş klasör", "Kırtasiye & Ofis", "adet", [32, 45], COLORS("MAV", "KIR", "SIY", "YES")],
  ["OFS-ZIMBA", "Masa tipi zımba", "Kırtasiye & Ofis", "adet", [120, 170]],
  ["OFS-ZIMBATEL", "Zımba teli No:10 (20 kutu)", "Kırtasiye & Ofis", "paket", [70, 95]],
  ["OFS-YAPISKAN", "Yapışkanlı not kâğıdı 75x75 (12'li)", "Kırtasiye & Ofis", "paket", [85, 115], v("Renk", [["SAR", "Sarı", 1], ["NEO", "Neon karışık", 1.15]])],
  ["OFS-PIL", "Kalem pil (4'lü)", "Kırtasiye & Ofis", "paket", [55, 75], v("Tip", [["AA", "AA", 1], ["AAA", "AAA", 1]])],
  ["OFS-DEFTER", "Spiralli defter A5 (10'lu)", "Kırtasiye & Ofis", "paket", [150, 200]],
  ["OFS-TAHTA", "Beyaz tahta kalemi (12'li)", "Kırtasiye & Ofis", "kutu", [170, 230], COLORS("SIY", "MAV", "KIR")],
  ["OFS-MAKAS", "Ofis makası 21 cm", "Kırtasiye & Ofis", "adet", [45, 65]],
  ["OFS-SEFFAFBANT", "Şeffaf bant 18 mm (8'li)", "Kırtasiye & Ofis", "paket", [55, 75]],
  ["OFS-DOSYA", "Telli dosya (50'li)", "Kırtasiye & Ofis", "paket", [120, 160]],
  ["OFS-CETVEL", "Metal cetvel 30 cm", "Kırtasiye & Ofis", "adet", [25, 38]],
  // ---- Hırdavat
  ["HRD-VIDA", "Sunta vidası (200'lü kutu)", "Hırdavat", "kutu", [65, 85], SIZES("Ölçü", [["3530", "3,5x30", 1], ["440", "4x40", 1.25], ["550", "5x50", 1.6]])],
  ["HRD-DUBEL", "Plastik dübel (100'lü)", "Hırdavat", "paket", [35, 48], SIZES("Ölçü", [["06", "6 mm", 1], ["08", "8 mm", 1.3], ["10", "10 mm", 1.7]])],
  ["HRD-SILIKON", "Silikon 280 ml", "Hırdavat", "adet", [75, 100], COLORS("SFF", "BEY")],
  ["HRD-IZOLE", "İzole bant (10'lu)", "Hırdavat", "paket", [70, 95], COLORS("SIY", "KIR", "MAV")],
  ["HRD-KELEPCE", "Plastik kablo bağı (100'lü)", "Hırdavat", "paket", [30, 42], SIZES("Boy", [["150", "150 mm", 1], ["200", "200 mm", 1.3], ["300", "300 mm", 1.8]])],
  ["HRD-UZATMA", "Uzatma kablosu, 3'lü priz", "Hırdavat", "adet", [160, 210], SIZES("Uzunluk", [["3M", "3 m", 1], ["5M", "5 m", 1.3]])],
  ["HRD-ELDIVEN", "İş eldiveni (12 çift)", "Hırdavat", "paket", [140, 190], SIZES("Beden", [["M", "M", 1], ["L", "L", 1], ["XL", "XL", 1]])],
  ["HRD-METRE", "Şerit metre 5 m", "Hırdavat", "adet", [85, 120]],
  ["HRD-MAKET", "Maket bıçağı + 10 yedek uç", "Hırdavat", "adet", [55, 80]],
  ["HRD-SUTERAZI", "Su terazisi 60 cm", "Hırdavat", "adet", [190, 260]],
  ["HRD-YAGLAYICI", "Çok amaçlı yağlayıcı sprey 400 ml", "Hırdavat", "adet", [110, 150]],
  ["HRD-AMPUL", "LED ampul 9 W E27 (10'lu)", "Hırdavat", "paket", [280, 360], v("Işık", [["GUN", "Gün ışığı", 1], ["BEY", "Beyaz", 1]])],
  // ---- Mutfak & İkram Sarf
  ["MUT-BARDAK", "Karton bardak (1000'li)", "Mutfak & İkram Sarf", "koli", [650, 820], SIZES("Boy", [["7OZ", "7 oz", 1], ["8OZ", "8 oz", 1.12], ["12OZ", "12 oz", 1.45]])],
  ["MUT-CATAL", "Plastik çatal (100'lü)", "Mutfak & İkram Sarf", "paket", [38, 52]],
  ["MUT-KASIK", "Plastik tatlı kaşığı (100'lü)", "Mutfak & İkram Sarf", "paket", [36, 50]],
  ["MUT-KARISTIRICI", "Ahşap karıştırıcı çubuk (1000'li)", "Mutfak & İkram Sarf", "paket", [95, 130]],
  ["MUT-FOLYO", "Alüminyum folyo 30 cm x 100 m", "Mutfak & İkram Sarf", "rulo", [210, 280]],
  ["MUT-STREC", "Gıda streç filmi 30 cm x 300 m", "Mutfak & İkram Sarf", "rulo", [190, 250]],
  ["MUT-PISIRME", "Pişirme kâğıdı 37 cm x 50 m", "Mutfak & İkram Sarf", "rulo", [160, 210]],
  ["MUT-TABAK", "Karton tabak 23 cm (50'li)", "Mutfak & İkram Sarf", "paket", [80, 110]],
  ["MUT-KAP", "Sızdırmaz saklama kabı 1 L (25'li)", "Mutfak & İkram Sarf", "paket", [240, 310]],
];

/** Supplier catalog (Imports API, generated XLSX): [sku, name, category, unit, price, origin]. */
const IMPORT_CATALOG = [
  ["AMB-MAKINEFILM", "Makine streç filmi 23 mikron", "Ambalaj & Paketleme", "rulo", 690, "İthal"],
  ["OFS-ZARF-A4", "Torba zarf A4 (250'li)", "Kırtasiye & Ofis", "paket", 310, "Yerli üretim"],
  ["OFS-ETIKET-A4", "Lazer etiket A4 (100 yaprak)", "Kırtasiye & Ofis", "paket", 260, "Yerli üretim"],
  ["TEM-PARFUM", "Oda parfümü 400 ml", "Temizlik Ürünleri", "adet", 95, "Yerli üretim"],
  ["TEM-KLOR", "Klor tablet (1 kg)", "Temizlik Ürünleri", "kutu", 340, "İthal"],
  ["HIJ-KLOZET", "Klozet kapak örtüsü (250'li)", "Kâğıt & Hijyen", "paket", 120, "Yerli üretim"],
  ["HRD-YAPISTIRICI", "Hızlı yapıştırıcı 20 g (24'lü)", "Hırdavat", "kutu", 410, "İthal"],
  ["HRD-FIRCA", "Boya fırçası 2,5 inç (12'li)", "Hırdavat", "paket", 230, "Yerli üretim"],
  ["MUT-PIPET", "Kâğıt pipet (500'lü)", "Mutfak & İkram Sarf", "paket", 140, "Yerli üretim"],
  ["MUT-KAPAK", "Bardak kapağı 8 oz (1000'li)", "Mutfak & İkram Sarf", "koli", 520, "İthal"],
];

const CUSTOMERS = [
  // [name, group]  — Bayi: resellers (Bayi listesi, %10), Kurumsal: offices/hotels (Kurumsal liste, %5), Market: default list.
  ["Örnek Market Zinciri", "Bayi"],
  ["Kurgu Toptan Gıda", "Bayi"],
  ["Demo Kırtasiye Dünyası", "Bayi"],
  ["Sentetik Temizlik Bayii", "Bayi"],
  ["Örnek Nalbur Ticaret", "Bayi"],
  ["Kurgu Otel İşletmeleri", "Kurumsal"],
  ["Demo Ofis Hizmetleri", "Kurumsal"],
  ["Örnek Lojistik Depoculuk", "Kurumsal"],
  ["Sentetik Hastane Yönetimi", "Kurumsal"],
  ["Kurgu Okul Yönetimi", "Kurumsal"],
  ["Mahalle Bakkalı (örnek)", "Market"],
  ["Köşe Büfe (örnek)", "Market"],
  ["Demo Kafe", "Market"],
  ["Örnek Kuaför Salonu", "Market"],
  ["Kurgu Oto Yıkama", "Market"],
];
const CITIES = [
  ["İstanbul", "Başakşehir", "34490"],
  ["Ankara", "Yenimahalle", "06370"],
  ["İzmir", "Bayraklı", "35530"],
  ["Bursa", "Osmangazi", "16200"],
  ["Kocaeli", "Gebze", "41400"],
  ["Antalya", "Muratpaşa", "07100"],
];

/** Picking task priorities (WHT-03). */
export const PRIORITIES = ["low", "normal", "high", "urgent"];

/** @param {"full" | "small"} size */
export function genelDemo(size = "full") {
  const rng = createRng(seedOf(`genel-${size}`));
  const small = size === "small";
  let serial = 1;
  const barcode = () => syntheticEan13(202, 330000 + serial++);

  const perCategory = new Map();
  const rows = CATALOG.filter(([, , category]) => {
    const n = perCategory.get(category) ?? 0;
    perCategory.set(category, n + 1);
    return !small || n < 2;
  });

  const products = rows.map(([code, name, category, unit, [min, max], axis, tiers]) => {
    const price = rng.price(min, max);
    const brand = rng.next() < 0.2 ? PRIVATE_LABEL : BRAND_OF[category];
    const attributes = { koli_ici: rng.pick([6, 12, 24, 48]), mensei: rng.next() < 0.8 ? "Yerli üretim" : "İthal" };
    const variants = axis
      ? axis.values.map(([vc, label, factor]) => ({
          sku: `${code}-${vc}`,
          barcode: barcode(),
          name: label,
          optionValues: { [axis.option]: label },
          price: Math.round(price * factor * 2) / 2,
        }))
      : [{ sku: code, barcode: barcode(), price }];
    return { name: `${name} (sentetik)`, category, brand, unit, attributes, variants, ...(tiers ? { tiers } : {}) };
  });

  const importProducts = small
    ? []
    : IMPORT_CATALOG.map(([sku, name, category, unit, price, origin]) => ({
        sku,
        barcode: barcode(),
        name: `${name} (sentetik)`,
        unit,
        taxRate: 20,
        category,
        brand: PRIVATE_LABEL,
        price,
        attributes: { mensei: origin },
      }));

  const customers = (small ? [CUSTOMERS[0], CUSTOMERS[5], CUSTOMERS[10]] : CUSTOMERS).map(([name, group], i) => {
    const [city, district, postalCode] = CITIES[i % CITIES.length];
    const n = String(i + 1).padStart(2, "0");
    const contact = `Örnek Yetkili ${n}`;
    const phone = `+90 000 000 03 ${n}`;
    const base = { district, city, postalCode, country: "TR", contactName: contact, contactPhone: phone };
    return {
      code: `TPT-${n}`,
      name: `${name} (sentetik)`,
      contactName: contact,
      phone,
      email: `musteri${n}@demo-genel.example.com`,
      group,
      address: { title: "Teslimat", line1: `Sentetik Organize Sanayi ${i + 1}. Cadde No: ${20 + i}`, ...base },
      billingAddress: { title: "Fatura", line1: `Sentetik İş Merkezi Kat ${1 + (i % 5)} No: ${100 + i}`, ...base },
    };
  });

  // Customer indexes into `customers` (small keeps one customer per group: 0 Bayi, 1 Kurumsal, 2 Market).
  const C = small ? { bayi: [0, 0, 0], kurumsal: [1, 1, 1, 1, 1], market: [2, 2, 2, 2, 2] } : { bayi: [0, 1, 2, 3, 4], kurumsal: [5, 6, 7, 8, 9], market: [10, 11, 12, 13, 14] };

  // Orders in every state. priority: picking-task priority after approval; assignTo: team role whose member gets the task;
  // deliveryInDays: requested delivery date relative to the seeding day.
  const orders = small
    ? [
        { target: "draft", customer: C.market[0], lines: [["MUT-BARDAK-7OZ", 1]] },
        { target: "approved", customer: C.bayi[0], priority: "urgent", assignTo: "warehouse_staff", deliveryInDays: 1, lines: [["TEM-YUZEY-5L", 2], ["HIJ-TUVALET", 3]] },
        { target: "approved", customer: C.kurumsal[0], priority: "low", deliveryInDays: 7, lines: [["HRD-VIDA-440", 2]] },
        { target: "picking", customer: C.kurumsal[1], priority: "high", assignTo: "warehouse_staff", lines: [["AMB-KOLI-30", 4], ["OFS-KALEM-SIY", 2]] },
        { target: "shipped", customer: C.market[1], lines: [["TEM-BULASIK-750ML", 3]] },
      ]
    : [
        { target: "draft", customer: C.market[0], lines: [["TEM-YUZEY-5L", 4], ["HIJ-TUVALET", 10]] },
        { target: "draft", customer: C.kurumsal[3], lines: [["OFS-A4", 5]] },
        { target: "pending", customer: C.bayi[2], lines: [["AMB-KOLI-40", 6], ["AMB-BANT-KHV", 4]] },
        {
          target: "approved",
          customer: C.bayi[0],
          priority: "urgent",
          assignTo: "warehouse_staff",
          deliveryInDays: 1,
          notes: "Acil: müşteri yarın sabah teslim istiyor (sentetik).",
          lines: [["HIJ-HAVLU", 8], ["TEM-COP-BUY", 10], ["TEM-SABUN-5L", 3]],
        },
        { target: "approved", customer: C.kurumsal[1], priority: "high", deliveryInDays: 2, lines: [["OFS-KALEM-MAV", 3], ["OFS-KLASOR-SIY", 12]] },
        { target: "approved", customer: C.kurumsal[4], priority: "normal", deliveryInDays: 4, lines: [["HRD-VIDA-440", 4], ["HRD-DUBEL-08", 4]] },
        { target: "approved", customer: C.market[3], priority: "low", deliveryInDays: 7, lines: [["MUT-BARDAK-8OZ", 2]] },
        { target: "picking", customer: C.bayi[1], priority: "high", assignTo: "warehouse_staff", lines: [["TEM-BULASIK-4L", 6], ["TEM-ELDIVEN-M", 5], ["HIJ-PECETE-33", 10]] },
        { target: "packed", customer: C.kurumsal[2], lines: [["AMB-STREC", 6], ["AMB-ETIKET", 4]] },
        { target: "partial_closed", customer: C.bayi[2], lines: [["OFS-PIL-AA", 10], ["OFS-YAPISKAN-SAR", 6]], shortage: { line: 1, quantity: 2, reason: "damaged" } },
        { target: "shipped", customer: C.bayi[3], lines: [["TEM-YER-LIM", 6], ["TEM-MIKROFIBER-MAV", 5]] },
        { target: "shipped", customer: C.kurumsal[0], lines: [["MUT-FOLYO", 4], ["MUT-STREC", 4]] },
        { target: "shipped", customer: C.market[2], lines: [["HRD-SILIKON-SFF", 12]] },
        { target: "completed", customer: C.bayi[0], lines: [["HIJ-ZHAVLU", 3], ["HIJ-DEZENFEKTAN-1L", 6]] },
        { target: "completed", customer: C.bayi[4], lines: [["AMB-ZIP-1520", 5]] },
        { target: "cancelled", customer: C.market[4], lines: [["HRD-UZATMA-5M", 2]], reason: "Müşteri vazgeçti (sentetik)" },
        { target: "cancelled_draft", customer: C.kurumsal[1], lines: [["OFS-DEFTER", 3]], reason: "Mükerrer taslak (sentetik)" },
      ];

  // Two-stage transfers Merkez Depo → Şube Depo (from the category shelf). receive: what arrives (per line, in order);
  // close: the rest is written off with this resolution. Products here are never ordered, so the stock is free.
  const transfers = small
    ? [
        { note: "Şube haftalık ikmal (sentetik)", lines: [["HRD-DUBEL-06", 3], ["OFS-KALEM-KIR", 2]], target: "in_transit" },
        { note: "Şube ikmali, yolda hasar (sentetik)", lines: [["AMB-BANT-SFF", 4]], target: "closed", receive: [3], close: { resolution: "lost", reason: "1 paket yolda ezildi, kullanılamaz (sentetik)" } },
      ]
    : [
        { note: "Şube haftalık ikmal (sentetik)", lines: [["TEM-KIREC", 4], ["HIJ-ISLAK", 6], ["OFS-ZIMBATEL", 3]], target: "in_transit" },
        { note: "Şube kırtasiye ikmali (sentetik)", lines: [["TEM-SUNGER", 5], ["OFS-DOSYA", 4]], target: "received", receive: [5, 4] },
        {
          note: "Şube sarf ikmali (sentetik)",
          lines: [["AMB-KOSEBENT", 5], ["MUT-TABAK", 6]],
          target: "closed",
          receive: [5, 4],
          close: { resolution: "lost", reason: "2 paket karton tabak eksik geldi, kayıp olarak düşüldü (sentetik)" },
        },
      ];

  // Shelf moves inside Merkez Depo (#168): part of the stock goes to the overflow shelf R-01.
  const relocations = small ? [["HRD-DUBEL-10", 3]] : [["OFS-SEFFAFBANT", 6], ["HRD-KELEPCE-200", 5]];

  // One open stock count on a main-warehouse shelf: the first lines counted (one with a difference), the rest pending.
  const stockCount = {
    shelf: small ? "Ambalaj & Paketleme" : "Kırtasiye & Ofis",
    note: "Dönem sonu raf sayımı (sentetik)",
    counted: small ? [0, -1] : [0, 0, -1, 0, 2],
  };

  return {
    code: "genel",
    sector: "general",
    tenant: { name: "Demo Toptan Ticaret", slug: "demo-genel" },
    owner: { email: "sahip@demo-genel.example.test", fullName: "Demo Toptan Sahibi" },
    // Invited through the Members API (#129); each signs up and accepts the invitation.
    team: [
      { role: "manager", email: "mudur@demo-genel.example.test", fullName: "Örnek Müdür (sentetik)" },
      { role: "sales", email: "satis@demo-genel.example.test", fullName: "Örnek Satış Temsilcisi (sentetik)" },
      { role: "warehouse_staff", email: "depo@demo-genel.example.test", fullName: "Örnek Depo Görevlisi (sentetik)" },
    ],
    taxRate: 20,
    units: EXTRA_UNITS,
    categories: GENEL_CATEGORIES,
    customFields: CUSTOM_FIELDS,
    warehouses: [
      { code: "MERKEZ", name: "Merkez Depo", usesLocations: true, locations: SHELVES },
      { code: "SUBE", name: "Şube Depo", usesLocations: false, locations: [] },
    ],
    brands: BRANDS,
    products,
    importProducts,
    priceLists: {
      defaultName: "Liste fiyatı (TRY)",
      dealer: [
        // Bayi: 10 % below the list; corrugated boxes get an extra 25+ tier.
        {
          name: "Bayi listesi",
          group: "Bayi",
          discountPercent: 10,
          tiers: ["AMB-KOLI-20", "AMB-KOLI-30", "AMB-KOLI-40", "AMB-KOLI-60"].map((sku) => ({ sku, minQuantity: 25, factor: 0.82 })),
        },
        { name: "Kurumsal liste", group: "Kurumsal", discountPercent: 5, tiers: [] },
      ],
    },
    dealerGroups: ["Bayi", "Kurumsal", "Market"],
    customers,
    stock: (_variant, category) => ({
      main: rng.int(18, 60),
      shop: ["Temizlik Ürünleri", "Kâğıt & Hijyen", "Kırtasiye & Ofis"].includes(category) ? rng.int(4, 12) : 0,
    }),
    lowStock: small ? ["HIJ-HAVLU", "OFS-A4"] : ["TEM-CAM", "OFS-ZIMBA"],
    // Never received and no minimum stock threshold: shown only with "stokta olmayanlar dahil".
    outOfStock: small ? ["MUT-CATAL"] : ["HRD-SUTERAZI"],
    orders,
    transfers,
    relocations,
    stockCount,
    whatsapp: {
      customer: C.bayi[0],
      text: small
        ? ["Merhaba, sipariş geçmek istiyoruz", "2 paket AMB-KOLI-20", "1 koli OFS-A4", "teşekkürler"].join("\n")
        : ["Merhaba, haftalık siparişimiz:", "5 rulo TEM-COP-ORT", "3 paket AMB-KOLI-30", "2 koli OFS-A4", "mavi kalem 2 kutu", "iyi çalışmalar"].join("\n"),
      lines: small
        ? [
            { raw: "2 paket AMB-KOLI-20", match: "amb-koli-20", sku: "AMB-KOLI-20", quantity: 2 },
            { raw: "1 koli OFS-A4", match: "ofs-a4", sku: "OFS-A4", quantity: 1 },
          ]
        : [
            { raw: "5 rulo TEM-COP-ORT", match: "tem-cop-ort", sku: "TEM-COP-ORT", quantity: 5 },
            { raw: "3 paket AMB-KOLI-30", match: "amb-koli-30", sku: "AMB-KOLI-30", quantity: 3 },
            { raw: "2 koli OFS-A4", match: "ofs-a4", sku: "OFS-A4", quantity: 2 },
            { raw: "mavi kalem 2 kutu", match: "mavi kalem", sku: "OFS-KALEM-MAV", quantity: 2 },
          ],
    },
  };
}
