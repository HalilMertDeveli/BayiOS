import { createRng, seedOf, syntheticEan13 } from "./rng.mjs";

/**
 * "Demo Motor Yedek Parça" (#56) — fully synthetic motorcycle spare-parts dealer (docs/product/sector-scenarios.md §1).
 * Motorcycle makes/models appear only as compatibility attributes (the sector template's own options); every business,
 * person, phone, address, code, barcode (GS1 in-store range 200) and price is invented and deterministic (seeded RNG).
 */

const MODELS = [
  { key: "CG125", label: "CG 125", make: "Honda", years: [2015, 2021] },
  { key: "NMAX155", label: "NMAX 155", make: "Yamaha", years: [2017, 2024] },
  { key: "NS200", label: "Pulsar NS200", make: "Bajaj", years: [2016, 2023] },
  { key: "MON100", label: "Mondial 100", make: "Mondial", years: [2012, 2019] },
  { key: "YBR125", label: "YBR 125", make: "Yamaha", years: [2014, 2020] },
  { key: "ACT125", label: "Activa 125", make: "Honda", years: [2018, 2024] },
  { key: "KUB50", label: "Kuba Trendy 50", make: "Kuba", years: [2015, 2022] },
  { key: "PCX125", label: "PCX 125", make: "Honda", years: [2019, 2025] },
];

// Part types per model. kinds: OR = Orijinal, MU = Muadil, YS = Yan sanayi (variant attribute parca_tipi).
const PARTS = [
  { code: (m) => `FRN-${m}-ON`, name: (l) => `${l} ön fren balatası`, category: "Fren", unit: "takim", side: "Ön", kinds: ["OR", "MU"], price: [180, 320], warranty: 6 },
  { code: (m) => `FRN-${m}-AR`, name: (l) => `${l} arka fren balatası`, category: "Fren", unit: "takim", side: "Arka", kinds: ["OR", "MU"], price: [150, 280], warranty: 6 },
  { code: (m) => `AKT-${m}-ZDS`, name: (l) => `${l} zincir dişli seti`, category: "Aktarma (zincir-dişli)", unit: "takim", kinds: [], price: [650, 1400], warranty: 12 },
  { code: (m) => `ELK-BUJI-${m}`, name: (l) => `${l} buji (10'lu kutu)`, category: "Elektrik", unit: "kutu", kinds: [], price: [420, 780], warranty: 0 },
  { code: (m) => `FLT-${m}-HAVA`, name: (l) => `${l} hava filtresi`, category: "Filtre & Yağ", unit: "adet", kinds: ["OR", "MU"], price: [90, 210], warranty: 0 },
  { code: (m) => `DBR-${m}-BAL`, name: (l) => `${l} debriyaj balata seti`, category: "Debriyaj", unit: "takim", kinds: ["OR", "MU", "YS"], price: [380, 900], warranty: 6 },
  { code: (m) => `SUS-${m}-AMR`, name: (l) => `${l} arka amortisör`, category: "Süspansiyon", unit: "adet", side: "Arka", kinds: [], price: [900, 2400], warranty: 12 },
  { code: (m) => `KPR-${m}-AYN`, name: (l) => `${l} ayna takımı`, category: "Kaporta", unit: "takim", kinds: [], price: [160, 420], warranty: 0 },
];

const KIND = {
  OR: { label: "Orijinal", factor: 1 },
  MU: { label: "Muadil", factor: 0.72 },
  YS: { label: "Yan sanayi", factor: 0.55 },
};

// Shelf per category in the main warehouse (pick sequence = walking order). Codes follow sector-scenarios §1.3.
const SHELVES = [
  { code: "A-02-1", category: "Fren", pickSequence: 10 },
  { code: "B-01-1", category: "Filtre & Yağ", pickSequence: 20 },
  { code: "B-03-2", category: "Debriyaj", pickSequence: 30 },
  { code: "C-05-3", category: "Aktarma (zincir-dişli)", pickSequence: 40 },
  { code: "D-01-1", category: "Süspansiyon", pickSequence: 50 },
  { code: "D-04-2", category: "Kaporta", pickSequence: 60 },
  { code: "E-01-2", category: "Elektrik", pickSequence: 70 },
  { code: "F-01-1", category: "Aksesuar", pickSequence: 80 },
  { code: "G-01-1", category: "Lastik & Jant", pickSequence: 90 },
];

const BRANDS = ["Kurgu Parça", "Örnek Moto Sanayi", "Demo OEM"];

const CUSTOMER_NAMES = [
  "Usta Ali Motor",
  "Yıldırım Moto Servis",
  "Kartal Motor Tamir",
  "Anadolu Moto Yedek",
  "Rüzgar Motosiklet",
  "Ege Moto Atölye",
  "Marmara Motor Market",
  "Toros Moto Servis",
  "Kuzey Motor Garaj",
  "Liman Moto Yedek",
  "Bozkır Motor Tamir",
  "Güney Moto Center",
];
const CITIES = [
  ["İstanbul", "Ümraniye", "34760"],
  ["Ankara", "Ostim", "06370"],
  ["İzmir", "Bornova", "35040"],
  ["Bursa", "Nilüfer", "16110"],
  ["Konya", "Selçuklu", "42050"],
  ["Antalya", "Kepez", "07090"],
];

/** @param {"full" | "small"} size */
export function motorDemo(size = "full") {
  const rng = createRng(seedOf(`motor-${size}`));
  const small = size === "small";
  const models = small ? MODELS.slice(0, 2) : MODELS;
  let serial = 1;
  const barcode = () => syntheticEan13(200, 560000 + serial++);
  let oem = 1;

  const products = [];
  for (const model of models) {
    for (const part of PARTS) {
      const base = part.code(model.key);
      const price = rng.price(part.price[0], part.price[1]);
      const attributes = {
        uyumlu_markalar: [model.make],
        uyumlu_model: model.label,
        model_yili: { min: model.years[0], max: model.years[1] },
        oem_no: `90001-SNT-${String(oem++).padStart(3, "0")}`,
        ...(part.side ? { taraf: part.side } : {}),
        ...(part.warranty ? { garanti_ay: part.warranty } : {}),
      };
      const variants = (part.kinds.length ? part.kinds : [null]).map((kind) => ({
        sku: kind ? `${base}-${kind}` : base,
        barcode: barcode(),
        name: kind ? KIND[kind].label : null,
        optionValues: kind ? { "Parça tipi": KIND[kind].label } : null,
        attributes: kind ? { parca_tipi: KIND[kind].label } : null,
        price: Math.round(price * (kind ? KIND[kind].factor : 1) * 2) / 2,
      }));
      products.push({
        name: `${part.name(model.label)} (sentetik)`,
        category: part.category,
        brand: rng.pick(BRANDS),
        unit: part.unit,
        attributes,
        variants,
      });
    }
  }

  if (!small) {
    products.push(
      {
        name: "Motor yağı 10W40 1 lt (sentetik)",
        category: "Filtre & Yağ",
        brand: "Demo OEM",
        unit: "adet",
        attributes: { uyumlu_markalar: ["Honda", "Yamaha", "Bajaj", "Mondial", "Kuba"] },
        variants: [{ sku: "YAG-10W40-1L", barcode: barcode(), price: 260 }],
      },
      {
        name: "Gaz teli kablosu, metre (sentetik)",
        category: "Elektrik",
        brand: "Kurgu Parça",
        unit: "metre",
        attributes: {},
        variants: [{ sku: "KBL-GAZ-MT", barcode: barcode(), price: 45 }],
      },
      {
        name: "Açık yüz kask (sentetik model)",
        category: "Aksesuar",
        brand: "Örnek Moto Sanayi",
        unit: "adet",
        attributes: { garanti_ay: 24 },
        variants: ["M", "L", "XL"].map((s) => ({
          sku: `AKS-KASK-${s}`,
          barcode: barcode(),
          name: `Beden ${s}`,
          optionValues: { Beden: s },
          price: 1850,
        })),
      },
      {
        name: "Motosiklet eldiveni (sentetik model)",
        category: "Aksesuar",
        brand: "Örnek Moto Sanayi",
        unit: "adet",
        attributes: {},
        variants: ["M", "L"].map((s) => ({ sku: `AKS-ELD-${s}`, barcode: barcode(), name: `Beden ${s}`, optionValues: { Beden: s }, price: 540 })),
      },
      {
        name: "İç lastik 17 x 2.75 (sentetik)",
        category: "Lastik & Jant",
        brand: "Kurgu Parça",
        unit: "adet",
        attributes: {},
        variants: [{ sku: "LST-IC-1727", barcode: barcode(), price: 210 }],
      },
    );
  }

  // Supplier catalog imported with the Imports API (generated XLSX) — accessories and consumables.
  const importProducts = small
    ? []
    : [
        ["AKS-TEL-TUT", "Telefon tutucu gidon tipi", "Aksesuar", 320],
        ["AKS-ZNC-KLT", "Zincir kilit 120 cm", "Aksesuar", 480],
        ["AKS-YGMR-L", "Yağmurluk takımı L", "Aksesuar", 690],
        ["FLT-YAG-UNI", "Yağ filtresi universal", "Filtre & Yağ", 95],
        ["ELK-AMP-H4", "Far ampulü H4 12V", "Elektrik", 85],
        ["ELK-AMP-H7", "Far ampulü H7 12V", "Elektrik", 90],
        ["ELK-AKU-12V5", "Akü 12V 5Ah", "Elektrik", 1150],
        ["AKS-BGJ-30L", "Arka bagaj çantası 30 lt", "Aksesuar", 1290],
      ].map(([sku, name, category, price]) => ({
        sku,
        barcode: barcode(),
        name: `${name} (sentetik)`,
        unit: "adet",
        taxRate: 20,
        category,
        brand: "Demo OEM",
        price,
        attributes: { garanti_ay: category === "Elektrik" ? 6 : "" },
      }));

  const customers = (small ? CUSTOMER_NAMES.slice(0, 3) : CUSTOMER_NAMES).map((name, i) => {
    const [city, district, postalCode] = CITIES[i % CITIES.length];
    const n = String(i + 1).padStart(2, "0");
    return {
      code: `MTR-${n}`,
      name: `${name} (sentetik)`,
      contactName: `Örnek Yetkili ${n}`,
      phone: `+90 000 000 01 ${n}`,
      email: `musteri${n}@demo-motor.example.com`,
      group: i === 0 || i % 3 === 1 ? "Bayi A" : i % 3 === 2 ? "Bayi B" : "Perakende",
      address: {
        title: "Dükkân",
        line1: `Sentetik Sanayi Sitesi ${i + 1}. Blok No: ${10 + i}`,
        district,
        city,
        postalCode,
        country: "TR",
        contactName: `Örnek Yetkili ${n}`,
        contactPhone: `+90 000 000 01 ${n}`,
      },
      // Billing address (#136: orders carry a billing address snapshot too).
      billingAddress: {
        title: "Fatura",
        line1: `Sentetik Sanayi Sitesi ${i + 1}. Blok No: ${10 + i} Muhasebe`,
        district,
        city,
        postalCode,
        country: "TR",
        contactName: `Örnek Yetkili ${n}`,
        contactPhone: `+90 000 000 01 ${n}`,
      },
    };
  });

  const sku = (model, part, kind) => {
    const code = PARTS[part].code(MODELS[model].key);
    return kind ? `${code}-${kind}` : code;
  };

  return {
    code: "motor",
    sector: "motorcycle_parts",
    tenant: { name: "Demo Motor Yedek Parça", slug: "demo-motor-yedek-parca" },
    owner: { email: "sahip@demo-motor.example.test", fullName: "Demo Motor Sahibi" },
    taxRate: 20,
    warehouses: [
      { code: "MERKEZ", name: "Merkez Depo", usesLocations: true, locations: SHELVES },
      { code: "DUKKAN", name: "Dükkân", usesLocations: false, locations: [] },
    ],
    brands: BRANDS,
    products,
    importProducts,
    priceLists: {
      defaultName: "Perakende liste (TRY)",
      defaultTiers: [],
      dealer: [
        // Bayi A: 12 % below the retail list; brake pads have an explicit volume tier (10+ sets).
        {
          name: "Bayi A listesi",
          group: "Bayi A",
          discountPercent: 12,
          tiers: models.flatMap((_, m) => ["OR", "MU"].map((k) => ({ sku: sku(m, 0, k), minQuantity: 10, factor: 0.8 }))),
        },
        { name: "Bayi B listesi", group: "Bayi B", discountPercent: 6, tiers: [] },
      ],
    },
    dealerGroups: ["Bayi A", "Bayi B", "Perakende"],
    customers,
    // Received into the main warehouse shelves (category shelf); fast movers also in the shop.
    stock: (variant, category) => ({ main: rng.int(12, 45), shop: category === "Elektrik" || category === "Fren" ? rng.int(2, 6) : 0 }),
    lowStock: [sku(1, 4, "OR"), sku(0, 6)],
    orders: small
      ? [
          { target: "approved", customer: 1, lines: [[sku(1, 2), 1]] },
          { target: "shipped", customer: 2, lines: [[sku(0, 4, "MU"), 2]] },
        ]
      : [
          { target: "draft", customer: 3, lines: [[sku(2, 3), 2], [sku(2, 4, "MU"), 3]] },
          { target: "draft", customer: 9, lines: [["AKS-KASK-L", 1]] },
          { target: "pending", customer: 1, lines: [[sku(0, 0, "MU"), 4], [sku(0, 3), 2]] },
          { target: "pending", customer: 4, lines: [[sku(3, 5, "YS"), 1]] },
          { target: "approved", customer: 2, lines: [[sku(1, 2), 1], [sku(1, 1, "OR"), 2]] },
          { target: "approved", customer: 5, lines: [[sku(4, 6), 1]] },
          { target: "picking", customer: 0, lines: [[sku(5, 0, "OR"), 3], [sku(5, 7), 1]] },
          { target: "packed", customer: 6, lines: [[sku(6, 3), 2], ["YAG-10W40-1L", 6]] },
          { target: "partial_closed", customer: 7, lines: [[sku(7, 1, "MU"), 4], [sku(7, 4, "OR"), 3]], shortage: { line: 1, quantity: 1, reason: "damaged" } },
          { target: "shipped", customer: 0, lines: [[sku(0, 0, "MU"), 3], [sku(1, 2), 1], [sku(2, 3), 2]] },
          { target: "shipped", customer: 8, lines: [["KBL-GAZ-MT", 5]] },
          { target: "shipped", customer: 10, lines: [[sku(3, 0, "OR"), 2], [sku(3, 1, "OR"), 2]] },
          { target: "completed", customer: 1, lines: [[sku(0, 2), 1], [sku(0, 3), 1]] },
          { target: "completed", customer: 11, lines: [["AKS-ELD-M", 2], ["LST-IC-1727", 4]] },
          { target: "cancelled", customer: 4, lines: [[sku(4, 5, "MU"), 1]], reason: "Müşteri vazgeçti (sentetik)" },
          { target: "cancelled_draft", customer: 5, lines: [[sku(5, 4, "MU"), 2]], reason: "Mükerrer taslak (sentetik)" },
        ],
    // Sector-scenarios §1.3, codes written the way a dealer would paste them (deterministic parser, AI off).
    whatsapp: {
      customer: 0,
      text: [
        "Selamun aleyküm abi",
        `3 takım ${sku(0, 0, "MU")}`,
        "cg 125 arka balata 2 takım",
        `1 takım ${sku(1, 2)}`,
        `2 kutu ${sku(0, 3)}`,
        "bir de geçen seferki fiyattan olsun",
      ].join("\n"),
      lines: [
        { raw: `3 takım ${sku(0, 0, "MU")}`, match: sku(0, 0, "MU").toLowerCase(), sku: sku(0, 0, "MU"), quantity: 3 },
        { raw: "cg 125 arka balata 2 takım", match: "arka balata", sku: sku(0, 1, "MU"), quantity: 2 },
        { raw: `1 takım ${sku(1, 2)}`, match: sku(1, 2).toLowerCase(), sku: sku(1, 2), quantity: 1 },
        { raw: `2 kutu ${sku(0, 3)}`, match: sku(0, 3).toLowerCase(), sku: sku(0, 3), quantity: 2 },
      ],
    },
  };
}
