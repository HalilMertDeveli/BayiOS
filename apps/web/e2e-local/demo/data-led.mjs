import { createRng, seedOf, syntheticEan13 } from "./rng.mjs";

/**
 * "Demo LED Elektronik" (#57) — fully synthetic LED screen / electronics wholesaler (sector-scenarios.md §2).
 * Every business, person, phone, address, code, barcode (GS1 in-store range 201) and price is invented and
 * deterministic (seeded RNG). Control cards and video processors carry the template flag "Seri no takibi = evet", but
 * serial-number tracking itself is NOT implemented (billing module serial_warranty is unavailable) — see
 * docs/operations/demo.md "Bilinen boşluklar".
 */

const PITCHES = [
  { key: "P186", value: 1.86, led: "SMD1515", scan: "1/32" },
  { key: "P25", value: 2.5, led: "SMD2121", scan: "1/32" },
  { key: "P391", value: 3.91, led: "SMD2121", scan: "1/16" },
  { key: "P4", value: 4, led: "SMD2121", scan: "1/16" },
  { key: "P10", value: 10, led: "DIP", scan: "1/8" },
];
const USES = [
  { key: "IC", label: "İç mekân", nit: 800 },
  { key: "DS", label: "Dış mekân", nit: 5500 },
];
const SIZES = ["320x160", "250x250", "192x192"];

const SHELVES = [
  { code: "R-01-1", category: "LED Modül", pickSequence: 10 },
  { code: "R-02-1", category: "Kabinet", pickSequence: 20 },
  { code: "R-03-1", category: "Kontrol Kartı", pickSequence: 30 },
  { code: "R-04-1", category: "Video İşlemci", pickSequence: 40 },
  { code: "R-05-1", category: "Güç Kaynağı", pickSequence: 50 },
  { code: "R-06-1", category: "Montaj Aparatı", pickSequence: 60 },
  { code: "R-07-1", category: "Kablo & Konnektör", pickSequence: 70 },
  { code: "R-08-1", category: "Yedek LED/Lamba", pickSequence: 80 },
];

const BRANDS = ["Parlak Örnek", "Kurgu Display", "Demo Power"];

const CUSTOMERS = [
  ["Mavi Sahne Entegrasyon", "Sistem entegratörü"],
  ["Işık Reklam Ajansı", "Reklam ajansı"],
  ["Pixel Kurulum Hizmetleri", "Kurulum firması"],
  ["Ekran Sistem Mühendislik", "Sistem entegratörü"],
  ["Renkli Tabela Atölyesi", "Kurulum firması"],
  ["Sahne Görsel Prodüksiyon", "Reklam ajansı"],
  ["Dijital Pano Teknik", "Kurulum firması"],
  ["Kuzey Ekran Entegrasyon", "Sistem entegratörü"],
  ["Ege Led Kurulum", "Kurulum firması"],
  ["Anadolu Görsel Çözümler", "Reklam ajansı"],
];
const CITIES = [
  ["İstanbul", "Kağıthane", "34410"],
  ["Ankara", "Çankaya", "06690"],
  ["İzmir", "Konak", "35250"],
  ["Bursa", "Osmangazi", "16200"],
  ["Kocaeli", "İzmit", "41000"],
];

/** @param {"full" | "small"} size */
export function ledDemo(size = "full") {
  const rng = createRng(seedOf(`led-${size}`));
  const small = size === "small";
  let serial = 1;
  const barcode = () => syntheticEan13(201, 570000 + serial++);
  const products = [];

  const pitches = small ? PITCHES.filter((p) => p.key === "P25" || p.key === "P391") : PITCHES;
  const sizes = small ? SIZES.slice(0, 1) : SIZES;
  for (const pitch of pitches) {
    for (const use of USES) {
      for (const dims of sizes) {
        const price = rng.price(pitch.value < 3 ? 1400 : 650, pitch.value < 3 ? 2600 : 1300);
        products.push({
          // Turkish lower case: "İç mekân" → "iç mekân" (toLowerCase() gives "i̇" = i + combining dot, #136).
          name: `LED modül P${pitch.value} ${use.label.toLocaleLowerCase("tr-TR")} ${dims.replace("x", "×")} mm (sentetik)`,
          category: "LED Modül",
          brand: rng.pick(BRANDS),
          unit: "adet",
          attributes: {
            pixel_pitch: pitch.value,
            kullanim: use.label,
            modul_olcusu: dims,
            parlaklik: use.nit,
            tarama: pitch.scan,
            led_tipi: pitch.led,
            guc: rng.int(18, 45),
          },
          variants: [{ sku: `MOD-${pitch.key}-${use.key}-${dims.split("x")[0]}`, barcode: barcode(), price }],
          tiers: [
            { minQuantity: 50, factor: 0.92 },
            { minQuantity: 200, factor: 0.85 },
          ],
        });
      }
    }
  }

  const cabinetPitches = small ? PITCHES.filter((p) => p.key === "P391") : PITCHES.filter((p) => p.key !== "P186");
  for (const pitch of cabinetPitches) {
    for (const use of small ? USES.slice(0, 1) : USES) {
      const price = rng.price(5200, 9800);
      products.push({
        name: `Kabinet P${pitch.value} ${use.label.toLocaleLowerCase("tr-TR")} 500×500 mm (sentetik)`,
        category: "Kabinet",
        brand: "Kurgu Display",
        unit: "adet",
        attributes: { pixel_pitch: pitch.value, kullanim: use.label, modul_olcusu: "500x500", parlaklik: use.nit, guc: rng.int(120, 220) },
        variants: [
          { sku: `KAB-${pitch.key}-${use.key}-500-OS`, barcode: barcode(), name: "Ön servis", optionValues: { Servis: "Ön" }, price },
          { sku: `KAB-${pitch.key}-${use.key}-500-AS`, barcode: barcode(), name: "Arka servis", optionValues: { Servis: "Arka" }, price: price - 350 },
        ],
      });
    }
  }

  // Control cards / processors: the template flag says serial numbers matter — tracking them is a documented gap.
  const families = small ? ["A"] : ["A", "B", "C"];
  for (const family of families) {
    products.push(
      {
        name: `Alıcı kart, kart ailesi ${family} (sentetik)`,
        category: "Kontrol Kartı",
        brand: "Parlak Örnek",
        unit: "adet",
        attributes: { kontrol_karti_uyumu: [`Kart ailesi ${family}`], seri_no_takibi: true, voltaj: 5 },
        variants: [{ sku: `KRT-ALC-${family}`, barcode: barcode(), price: rng.price(380, 620) }],
      },
      {
        name: `Gönderici kart, kart ailesi ${family} (sentetik)`,
        category: "Kontrol Kartı",
        brand: "Parlak Örnek",
        unit: "adet",
        attributes: { kontrol_karti_uyumu: [`Kart ailesi ${family}`], seri_no_takibi: true, voltaj: 5 },
        variants: [{ sku: `KRT-GON-${family}`, barcode: barcode(), price: rng.price(1900, 3400) }],
      },
    );
  }
  for (const [key, label] of small ? [["2K", "2K"]] : [["2K", "2K"], ["4K", "4K"], ["SPL", "4 çıkışlı bölücü"]]) {
    products.push({
      name: `Video işlemci ${label} (sentetik)`,
      category: "Video İşlemci",
      brand: "Kurgu Display",
      unit: "adet",
      attributes: { seri_no_takibi: true, guc: rng.int(25, 60), voltaj: 220 },
      variants: [{ sku: `VID-ISL-${key}`, barcode: barcode(), price: rng.price(9500, 24000) }],
    });
  }

  // The supplier's price list arrives as Excel: imported with the Imports API (products + default list price).
  const imports = [
    ["PSU-5V40A", "Güç kaynağı 5V 40A 200W", "Güç Kaynağı", "adet", 640, { guc: 200, voltaj: 5, ip_sinifi: "IP20" }],
    ["PSU-5V60A", "Güç kaynağı 5V 60A 300W", "Güç Kaynağı", "adet", 820, { guc: 300, voltaj: 5, ip_sinifi: "IP20" }],
    ["PSU-5V40A-SLIM", "İnce güç kaynağı 5V 40A", "Güç Kaynağı", "adet", 760, { guc: 200, voltaj: 5, ip_sinifi: "IP20" }],
    ["PSU-5V40A-IP67", "Dış mekân güç kaynağı 5V 40A", "Güç Kaynağı", "adet", 1180, { guc: 200, voltaj: 5, ip_sinifi: "IP67" }],
    ["KBL-FLAT-16P", "Flat kablo 16 pin", "Kablo & Konnektör", "metre", 14, {}],
    ["KBL-GUC-3X1", "Güç kablosu 3×1 mm²", "Kablo & Konnektör", "metre", 38, {}],
    ["KBL-CAT6", "Cat6 ağ kablosu", "Kablo & Konnektör", "metre", 22, {}],
    ["KBL-HDMI-2M", "HDMI kablo 2 m", "Kablo & Konnektör", "adet", 160, {}],
    ["MNT-MIKNATIS-10", "Modül mıknatısı (10'lu)", "Montaj Aparatı", "adet", 95, {}],
    ["MNT-PROFIL-AL", "Alüminyum taşıyıcı profil", "Montaj Aparatı", "metre", 210, {}],
    ["MNT-KOSEBENT", "Kabinet köşebendi", "Montaj Aparatı", "adet", 65, {}],
    ["MNT-ASKI-SET", "Asma montaj seti", "Montaj Aparatı", "adet", 540, {}],
    ["YDK-SMD2121-1K", "Yedek SMD2121 LED (1000'li makara)", "Yedek LED/Lamba", "kutu", 450, { led_tipi: "SMD2121" }],
    ["YDK-SMD1515-1K", "Yedek SMD1515 LED (1000'li makara)", "Yedek LED/Lamba", "kutu", 520, { led_tipi: "SMD1515" }],
    ["YDK-DIP-546", "Yedek DIP546 LED (1000'li paket)", "Yedek LED/Lamba", "kutu", 380, { led_tipi: "DIP" }],
  ];
  const importProducts = imports.map(([sku, name, category, unit, price, attributes]) => ({
    sku,
    barcode: barcode(),
    name: `${name} (sentetik)`,
    unit,
    taxRate: 20,
    category,
    brand: "Demo Power",
    price,
    attributes,
  }));

  const customers = (small ? CUSTOMERS.slice(0, 3) : CUSTOMERS).map(([name, kind], i) => {
    const [city, district, postalCode] = CITIES[i % CITIES.length];
    const n = String(i + 1).padStart(2, "0");
    return {
      code: `LED-${n}`,
      name: `${name} (sentetik)`,
      contactName: `Örnek Yetkili ${n}`,
      phone: `+90 000 000 02 ${n}`,
      email: `musteri${n}@demo-led.example.com`,
      group: kind === "Sistem entegratörü" ? "Entegratör" : kind === "Reklam ajansı" ? "Ajans" : "Kurulum",
      address: {
        title: "Merkez",
        line1: `Örnek Teknopark ${i + 1}. Bina Kat ${1 + (i % 4)}`,
        district,
        city,
        postalCode,
        country: "TR",
        contactName: `Örnek Yetkili ${n}`,
        contactPhone: `+90 000 000 02 ${n}`,
      },
      // Invoices go to the accounting office (#136: orders carry a billing address snapshot too).
      billingAddress: {
        title: "Muhasebe",
        line1: `Örnek Teknopark ${i + 1}. Bina Muhasebe Ofisi`,
        district,
        city,
        postalCode,
        country: "TR",
        contactName: `Örnek Muhasebe ${n}`,
        contactPhone: `+90 000 000 12 ${n}`,
      },
    };
  });

  const mod = (pitch, use, dims = "320") => `MOD-${pitch}-${use}-${dims}`;

  return {
    code: "led",
    sector: "led_electronics",
    tenant: { name: "Demo LED Elektronik", slug: "demo-led-elektronik" },
    owner: { email: "sahip@demo-led.example.test", fullName: "Demo LED Sahibi" },
    taxRate: 20,
    warehouses: [
      { code: "IST", name: "İstanbul Merkez", usesLocations: true, locations: SHELVES },
      { code: "ANK", name: "Ankara Şube", usesLocations: false, locations: [] },
      { code: "HSR", name: "Hasarlı / İade", usesLocations: false, locations: [] },
    ],
    brands: BRANDS,
    products,
    importProducts,
    priceLists: {
      defaultName: "Liste fiyatı (TRY)",
      dealer: [
        // Integrators buy 8 % below list; the list's own quantity tiers (50+/200+) still apply through the base list.
        { name: "Entegratör listesi", group: "Entegratör", discountPercent: 8, tiers: [] },
      ],
    },
    dealerGroups: ["Entegratör", "Ajans", "Kurulum"],
    customers,
    stock: (variant, category) => ({
      main: category === "LED Modül" ? rng.int(120, 400) : category === "Kablo & Konnektör" ? rng.int(150, 500) : rng.int(10, 60),
      shop: category === "LED Modül" || category === "Kabinet" ? rng.int(5, 30) : 0,
    }),
    lowStock: ["VID-ISL-2K"],
    orders: small
      ? [
          { target: "approved", customer: 1, lines: [[mod("P25", "IC"), 20]] },
          { target: "shipped", customer: 2, lines: [["KRT-ALC-A", 4]] },
        ]
      : [
          { target: "draft", customer: 1, lines: [[mod("P10", "DS"), 40]], notes: "Fiyat sordu (sentetik); fiyat listeden." },
          { target: "draft", customer: 5, lines: [["KAB-P4-IC-500-OS", 6], ["KRT-ALC-B", 6]] },
          { target: "pending", customer: 0, lines: [["KAB-P391-IC-500-OS", 24], ["KRT-ALC-A", 24], ["KRT-GON-A", 1], ["PSU-5V40A", 30], ["KBL-FLAT-16P", 50]] },
          { target: "pending", customer: 3, lines: [[mod("P186", "IC"), 60]] },
          { target: "approved", customer: 7, lines: [[mod("P391", "DS", "250"), 80], ["PSU-5V60A", 20]] },
          { target: "approved", customer: 2, lines: [["MNT-ASKI-SET", 4], ["KBL-CAT6", 30]] },
          { target: "picking", customer: 4, lines: [[mod("P4", "IC"), 50], ["KBL-GUC-3X1", 25]] },
          { target: "packed", customer: 6, lines: [["VID-ISL-4K", 1], ["KBL-HDMI-2M", 2]] },
          { target: "partial_closed", customer: 8, lines: [[mod("P25", "IC"), 210], ["PSU-5V40A", 12]], shortage: { line: 1, quantity: 2, reason: "insufficient" } },
          { target: "shipped", customer: 0, lines: [[mod("P391", "IC"), 220], ["KRT-ALC-A", 12], ["KBL-FLAT-16P", 12.5]] },
          { target: "shipped", customer: 9, lines: [["YDK-SMD2121-1K", 3]] },
          { target: "shipped", customer: 3, lines: [["VID-ISL-SPL", 1], ["KRT-GON-C", 2]] },
          { target: "completed", customer: 1, lines: [[mod("P10", "DS", "250"), 48], ["MNT-MIKNATIS-10", 10]] },
          { target: "completed", customer: 7, lines: [["KAB-P10-DS-500-AS", 12], ["PSU-5V40A-IP67", 12]] },
          { target: "cancelled", customer: 5, lines: [[mod("P25", "DS"), 30]], reason: "Proje ertelendi (sentetik)" },
          { target: "cancelled_draft", customer: 2, lines: [["KBL-CAT6", 100]], reason: "Yanlış müşteri (sentetik)" },
        ],
    // Sector-scenarios §2.3 (project order), with codes for the lines a reviewer would confirm.
    whatsapp: {
      customer: 0,
      text: [
        "Merhaba,",
        "AVM projesi için:",
        "- 24 adet KAB-P391-IC-500-OS",
        "- 24 adet KRT-ALC-A",
        "- 1 adet KRT-GON-A",
        "- 30 adet PSU-5V40A",
        "Teslim cuma olursa iyi olur. Teşekkürler",
      ].join("\n"),
      lines: [
        { raw: "- 24 adet KAB-P391-IC-500-OS", match: "kab-p391-ic-500-os", sku: "KAB-P391-IC-500-OS", quantity: 24 },
        { raw: "- 24 adet KRT-ALC-A", match: "krt-alc-a", sku: "KRT-ALC-A", quantity: 24 },
        { raw: "- 1 adet KRT-GON-A", match: "krt-gon-a", sku: "KRT-GON-A", quantity: 1 },
        { raw: "- 30 adet PSU-5V40A", match: "psu-5v40a", sku: "PSU-5V40A", quantity: 30 },
      ],
    },
  };
}
