/**
 * MOCK IMPORTS API FOR PLAYWRIGHT — SYNTHETIC FILES AND ROWS, NOT THE REAL API.
 *
 * In-memory stand-in for /api/v1/import-jobs/* and /api/v1/import-mapping-templates following the contract shapes in
 * packages/contracts/openapi.json and the rules of services/api Imports (ImportService): imports.run + catalog.write
 * (products) / prices.write (prices or a mapped price column) → 403; multipart upload with Idempotency-Key, per-tenant
 * upload limit → 429 + Retry-After, scripted file rejections (413/415/422 by file name); mapping suggestions (never
 * applied by the API), PUT mapping with the ValidateColumns rules (422 per `columns.<field>`), templates; validate /
 * start / cancel / rollback (If-Match → 412, 409 already_rolled_back / rollback_window_expired / file_purged);
 * rows with status/action/rollbackStatus filters and keyset cursors; CSV error report with Content-Disposition.
 * Background jobs advance one step per GET of the job (validating → validated, running in `chunk`-row steps,
 * rolling_back → rolled_back | partially_rolled_back), so tests are deterministic. Every file, SKU and name is invented.
 */
import type { Route } from "@playwright/test";
import { grantsForRoleName, type FixtureRules } from "../../src/test/me-fixtures";

export type MockImportFile = { headers: string[]; rows: string[][] };

export type MockImportRow = {
  rowNumber: number;
  action: string;
  status: string;
  outcome: string | null;
  raw: string[];
  values: Record<string, string>;
  errors: { field: string | null; column: string | null; code: string; message: string }[];
  matchedVariantId: string | null;
  targetId: string | null;
  rollbackStatus: string | null;
};

export type MockImportJob = {
  id: string;
  type: string;
  status: string;
  fileName: string;
  fileSizeBytes: number;
  fileSha256: string;
  fileFormat: string;
  fileEncoding: string | null;
  delimiter: string | null;
  sheetName: string | null;
  headers: string[];
  sampleRows: string[][];
  totalRows: number;
  mapping: Record<string, string> | null;
  matchKey: string;
  mode: string;
  priceListId: string | null;
  preview: { create: number; update: number; skip: number; error: number };
  progress: { plannedRows: number; processedRows: number; percent: number; created: number; updated: number; skipped: number; failed: number };
  failure: { code: string; message: string } | null;
  duplicateOfImportId: string | null;
  version: number;
  createdAt: string;
  createdBy: string | null;
  validatedAt: string | null;
  startedAt: string | null;
  startedBy: string | null;
  finishedAt: string | null;
  rollbackDeadline: string | null;
  rollback: { requestedAt: string; requestedBy: string | null; finishedAt: string | null; planned: number; rolledBack: number; conflicts: number; failed: number; skipped: number } | null;
};

export type MockTemplate = { id: string; name: string; type: string; columns: Record<string, string>; matchKey: string; mode: string; version: number; createdAt: string; updatedAt: string | null };

export type MockImports = {
  jobs: MockImportJob[];
  rows: Map<string, MockImportRow[]>;
  /** Parsed content per uploaded file name; unknown names get DEFAULT_PRODUCT_FILE. */
  files: Record<string, MockImportFile>;
  /** SKUs that already exist in the (mock) catalog: product rows update them, price rows need them. */
  existingSkus: Set<string>;
  /** Custom fields offered as `attr.<key>` targets for product imports. */
  attributes: { key: string; label: string; required: boolean }[];
  templates: MockTemplate[];
  /** Uploads allowed before 429 (the real limit is 10 per hour per tenant). */
  uploadLimit: number;
  uploadsDone: number;
  retryAfterSeconds: number;
  /** Upload answers by file name, e.g. { "makro.xlsx": { status: 415, code: "imports.file_macro_not_allowed" } }. */
  rejectFiles: Record<string, { status: number; code: string }>;
  /** Rows processed per job GET while running. */
  chunk: number;
  /** SKUs whose rows end as rollback conflicts (changed after the import). */
  conflictSkus: Set<string>;
  /** Jobs whose raw file was purged (validate → 409 imports.file_purged). */
  purged: Set<string>;
  userId: string;
  requests: { method: string; path: string; search: string; headers: Record<string, string> }[];
};

export const DEFAULT_PRODUCT_FILE: MockImportFile = {
  headers: ["Stok Kodu", "Ürün Adı", "Birim", "KDV %", "Satış Fiyatı", "Renk", "Not"],
  rows: [
    ["MCK-001", "Mock Fren Balatası (mock)", "adet", "20", "125,50", "Siyah", ""],
    ["MCK-002", "Mock Zincir Seti (mock)", "adet", "20", "890", "Gri", "yeni"],
    ["MCK-003", "", "adet", "20", "45", "", "adı eksik"],
    ["MCK-004", "Mock Ayna (mock)", "adet", "yirmi", "60", "", "KDV hatalı"],
    ["MCK-EXIST", "Mock Mevcut Ürün (mock)", "adet", "20", "300", "", ""],
  ],
};

let seq = 0;
const uuid = (prefix: string) => `00000000-0000-4000-8000-${prefix}${String(++seq).padStart(12 - prefix.length, "0")}`;
const nowIso = () => new Date().toISOString();
const DAY_MS = 86_400_000;

/** A job in any state, for seeding the history (e.g. a completed import inside or outside the rollback window). */
export function mockJob(patch: Partial<MockImportJob> = {}): MockImportJob {
  const file = DEFAULT_PRODUCT_FILE;
  return {
    id: uuid("1a"),
    type: "products",
    status: "uploaded",
    fileName: "urunler-mock.xlsx",
    fileSizeBytes: 18_432,
    fileSha256: "0".repeat(64),
    fileFormat: "xlsx",
    fileEncoding: null,
    delimiter: null,
    sheetName: "Sayfa1",
    headers: file.headers,
    sampleRows: file.rows.slice(0, 5),
    totalRows: file.rows.length,
    mapping: null,
    matchKey: "sku",
    mode: "upsert",
    priceListId: null,
    preview: { create: 0, update: 0, skip: 0, error: 0 },
    progress: { plannedRows: 0, processedRows: 0, percent: 0, created: 0, updated: 0, skipped: 0, failed: 0 },
    failure: null,
    duplicateOfImportId: null,
    version: 1,
    createdAt: nowIso(),
    createdBy: null,
    validatedAt: null,
    startedAt: null,
    startedBy: null,
    finishedAt: null,
    rollbackDeadline: null,
    rollback: null,
    ...patch,
  };
}

export function createMockImports(options: Partial<Omit<MockImports, "requests" | "uploadsDone">> = {}): MockImports {
  return {
    jobs: options.jobs ?? [],
    rows: options.rows ?? new Map(),
    files: options.files ?? {},
    existingSkus: options.existingSkus ?? new Set(["MCK-EXIST"]),
    attributes: options.attributes ?? [{ key: "renk", label: "Renk", required: false }],
    templates: options.templates ?? [],
    uploadLimit: options.uploadLimit ?? 10,
    uploadsDone: 0,
    retryAfterSeconds: options.retryAfterSeconds ?? 1500,
    rejectFiles: options.rejectFiles ?? {},
    chunk: options.chunk ?? 2,
    conflictSkus: options.conflictSkus ?? new Set(),
    purged: options.purged ?? new Set(),
    userId: options.userId ?? "00000000-0000-4000-8000-000000000001",
    requests: [],
  };
}

/** Rows of a seeded job: one per file row, already imported (for result/rollback tests). */
export function importedRows(file: MockImportFile, mapping: Record<string, string>, actions: string[]): MockImportRow[] {
  return file.rows.map((cells, i) => ({
    rowNumber: i + 2,
    action: actions[i] ?? "create",
    status: actions[i] === "error" ? "invalid" : "imported",
    outcome: actions[i] === "error" ? null : actions[i] === "update" ? "updated" : "created",
    raw: cells,
    values: valuesOf(file.headers, cells, mapping),
    errors: actions[i] === "error" ? [{ field: "name", column: mapping.name ?? null, code: "value.required", message: "Ürün adı zorunlu." }] : [],
    matchedVariantId: null,
    targetId: null,
    rollbackStatus: null,
  }));
}

const CORS = { "Access-Control-Allow-Origin": "*", "Access-Control-Expose-Headers": "ETag, Retry-After, Content-Disposition, Location" };
const json = (route: Route, status: number, body: unknown, headers: Record<string, string> = {}) =>
  route
    .fulfill({ status, contentType: status >= 400 ? "application/problem+json" : "application/json", headers: { ...CORS, ...headers }, body: JSON.stringify(body) })
    .then(() => true);
const problem = (status: number, code: string, extra: Record<string, unknown> = {}) => ({ type: `https://bayios.app/problems/${code}`, title: code, status, code, ...extra });

type Field = { key: string; label: string; requiredForCreate: boolean; required: boolean; synonyms: string[] };
const SKU: Field = { key: "sku", label: "Stok kodu (SKU)", requiredForCreate: true, required: false, synonyms: ["stok kodu", "sku", "urun kodu"] };
const BARCODE: Field = { key: "barcode", label: "Barkod", requiredForCreate: false, required: false, synonyms: ["barkod", "ean"] };
const PRICE: Field = { key: "price", label: "Fiyat", requiredForCreate: false, required: false, synonyms: ["fiyat", "satis fiyati", "birim fiyat"] };
const PRODUCT_FIELDS: Field[] = [
  SKU,
  BARCODE,
  { key: "name", label: "Ürün adı", requiredForCreate: true, required: false, synonyms: ["urun adi", "ad", "urun"] },
  { key: "description", label: "Açıklama", requiredForCreate: false, required: false, synonyms: ["aciklama"] },
  { key: "unit", label: "Birim", requiredForCreate: true, required: false, synonyms: ["birim"] },
  { key: "taxRate", label: "KDV oranı (%)", requiredForCreate: true, required: false, synonyms: ["kdv", "kdv %", "kdv orani"] },
  { key: "brand", label: "Marka", requiredForCreate: false, required: false, synonyms: ["marka"] },
  { key: "category", label: "Kategori", requiredForCreate: false, required: false, synonyms: ["kategori"] },
  { key: "status", label: "Durum", requiredForCreate: false, required: false, synonyms: ["durum"] },
  PRICE,
];
const PRICE_FIELDS: Field[] = [
  { ...SKU, requiredForCreate: false },
  BARCODE,
  { ...PRICE, required: true },
  { key: "minQuantity", label: "Minimum miktar", requiredForCreate: false, required: false, synonyms: ["min miktar", "kademe"] },
];
const fieldsFor = (type: string) => (type === "prices" ? PRICE_FIELDS : PRODUCT_FIELDS);

const fold = (s: string) =>
  s
    .toLocaleLowerCase("tr-TR")
    .replace(/[ıi̇]/g, "i")
    .replace(/ş/g, "s")
    .replace(/ğ/g, "g")
    .replace(/ü/g, "u")
    .replace(/ö/g, "o")
    .replace(/ç/g, "c")
    .trim();

function valuesOf(headers: string[], cells: string[], mapping: Record<string, string>): Record<string, string> {
  const values: Record<string, string> = {};
  for (const [field, header] of Object.entries(mapping)) {
    const v = cells[headers.indexOf(header)] ?? "";
    if (v !== "") values[field] = v;
  }
  return values;
}

const isNumber = (v: string | undefined) => v !== undefined && /^\d+([.,]\d+)?$/.test(v.trim());

function bump(job: MockImportJob) {
  job.version += 1;
}

function percent(job: MockImportJob) {
  const p = job.progress;
  p.percent = job.status === "completed" ? 100 : p.plannedRows === 0 ? 0 : Math.min(100, Math.floor((p.processedRows * 100) / p.plannedRows));
}

/** Dry run (ImportRowValidator, simplified): match by key, duplicates in the file, required values, numbers. */
function validate(imports: MockImports, job: MockImportJob) {
  const file = imports.files[job.fileName] ?? DEFAULT_PRODUCT_FILE;
  const mapping = job.mapping ?? {};
  const keyField = job.matchKey === "barcode" ? "barcode" : "sku";
  const seen = new Set<string>();
  const rows: MockImportRow[] = file.rows.map((cells, i) => {
    const values = valuesOf(file.headers, cells, mapping);
    const errors: MockImportRow["errors"] = [];
    const key = values[keyField];
    const err = (field: string, message: string, code = "value.invalid") => errors.push({ field, column: mapping[field] ?? null, code, message });
    if (!key) err(keyField, "Eşleşme anahtarı boş.", "value.required");
    else if (seen.has(key)) err(keyField, "Bu değer dosyada daha önce geçiyor (tekrar eden satır).", "row.duplicate");
    if (key) seen.add(key);
    const exists = key !== undefined && imports.existingSkus.has(key);
    let action: string;
    if (job.type === "prices") {
      if (!exists) err(keyField, "Ürün bulunamadı.", "product.not_found");
      if (!isNumber(values.price)) err("price", "Fiyat sayı olmalı.");
      action = errors.length ? "error" : job.mode === "create_only" ? "create" : "update";
    } else {
      const creating = !exists;
      if (creating && job.mode !== "update_only") {
        if (!values.name) err("name", "Ürün adı zorunlu.", "value.required");
        if (!values.unit) err("unit", "Birim zorunlu.", "value.required");
      }
      if (values.taxRate !== undefined && !isNumber(values.taxRate)) err("taxRate", "KDV oranı sayı olmalı (ör. 20).");
      if (values.price !== undefined && !isNumber(values.price)) err("price", "Fiyat sayı olmalı.");
      action = errors.length
        ? "error"
        : exists
          ? job.mode === "create_only"
            ? "skip"
            : "update"
          : job.mode === "update_only"
            ? "skip"
            : "create";
    }
    return {
      rowNumber: i + 2,
      action,
      status: action === "error" ? "invalid" : action === "skip" ? "skipped" : "valid",
      outcome: null,
      raw: cells,
      values,
      errors,
      matchedVariantId: exists ? uuid("7b") : null,
      targetId: null,
      rollbackStatus: null,
    };
  });
  imports.rows.set(job.id, rows);
  job.preview = {
    create: rows.filter((r) => r.action === "create").length,
    update: rows.filter((r) => r.action === "update").length,
    skip: rows.filter((r) => r.action === "skip").length,
    error: rows.filter((r) => r.action === "error").length,
  };
  job.status = "validated";
  job.validatedAt = nowIso();
  bump(job);
}

/** One background step per status read. */
function advance(imports: MockImports, job: MockImportJob) {
  if (job.status === "validating") return validate(imports, job);
  if (job.status === "running") {
    const rows = (imports.rows.get(job.id) ?? []).filter((r) => r.status === "valid").slice(0, imports.chunk);
    for (const r of rows) {
      r.status = "imported";
      r.outcome = r.action === "create" ? "created" : "updated";
      if (r.action === "create") job.progress.created += 1;
      else job.progress.updated += 1;
      job.progress.processedRows += 1;
    }
    if (job.progress.processedRows >= job.progress.plannedRows) {
      job.status = "completed";
      job.finishedAt = nowIso();
      job.rollbackDeadline = new Date(Date.now() + 7 * DAY_MS).toISOString();
    }
    percent(job);
    bump(job);
    return;
  }
  if (job.status === "rolling_back" && job.rollback) {
    const rows = (imports.rows.get(job.id) ?? []).filter((r) => r.status === "imported" && r.rollbackStatus === null);
    for (const r of rows) {
      r.rollbackStatus = imports.conflictSkus.has(r.values.sku ?? "") ? "conflict" : "rolled_back";
      if (r.rollbackStatus === "conflict") {
        r.errors = [{ field: null, column: null, code: "rollback.changed_since_import", message: "Kayıt aktarmadan sonra değiştirildi; dokunulmadı." }];
        job.rollback.conflicts += 1;
      } else job.rollback.rolledBack += 1;
    }
    job.rollback.finishedAt = nowIso();
    job.status = job.rollback.conflicts > 0 ? "partially_rolled_back" : "rolled_back";
    job.rollbackDeadline = null;
    bump(job);
  }
}

const respond = (job: MockImportJob) => ({ ...job, preview: { ...job.preview }, progress: { ...job.progress }, rollback: job.rollback ? { ...job.rollback } : null });

function parseMultipart(body: Buffer | null): { type: string | null; fileName: string | null; size: number } {
  const text = body?.toString("latin1") ?? "";
  const type = /name="type"\r\n\r\n([^\r\n]*)/.exec(text)?.[1] ?? null;
  const rawName = /filename="([^"]*)"/.exec(text)?.[1] ?? null;
  const fileName = rawName === null ? null : Buffer.from(rawName, "latin1").toString("utf8");
  return { type, fileName, size: body?.length ?? 0 };
}

const encodeCursor = (n: number) => Buffer.from(JSON.stringify({ R: n })).toString("base64url");
const decodeCursor = (c: string | null) => {
  if (!c) return 0;
  try {
    return Number((JSON.parse(Buffer.from(c, "base64url").toString()) as { R?: number }).R ?? 0);
  } catch {
    return 0;
  }
};

export async function handleImports(route: Route, imports: MockImports, role: string, rules: Partial<FixtureRules>): Promise<boolean> {
  const request = route.request();
  const url = new URL(request.url());
  const path = url.pathname;
  if (!path.startsWith("/api/v1/import-jobs") && !path.startsWith("/api/v1/import-mapping-templates")) return false;
  const method = request.method();
  imports.requests.push({ method, path, search: url.search, headers: request.headers() });

  const grants = grantsForRoleName(role, rules);
  const can = (permission: string) => grants.some((g) => g.permission === permission && g.scope === "all");
  if (!can("imports.run")) return json(route, 403, problem(403, "auth.forbidden", { title: "Missing permission: imports.run" }));
  const runDenied = (type: string, mapsPrice: boolean) =>
    (type === "products" && !can("catalog.write")) || ((type === "prices" || mapsPrice) && !can("prices.write"));
  const needsKey = () => !request.headers()["idempotency-key"];

  // ---- Templates ----
  if (path === "/api/v1/import-mapping-templates" && method === "GET") {
    const type = url.searchParams.get("type");
    return json(route, 200, { items: imports.templates.filter((t) => !type || t.type === type) });
  }
  if (path === "/api/v1/import-mapping-templates" && method === "POST") {
    if (needsKey()) return json(route, 428, problem(428, "precondition.required"));
    const body = request.postDataJSON() as { name: string; type: string; columns: Record<string, string>; matchKey?: string; mode?: string };
    const t: MockTemplate = { id: uuid("2b"), name: body.name, type: body.type, columns: body.columns, matchKey: body.matchKey ?? "sku", mode: body.mode ?? "upsert", version: 1, createdAt: nowIso(), updatedAt: null };
    imports.templates.push(t);
    return json(route, 201, t);
  }

  // ---- Jobs ----
  if (path === "/api/v1/import-jobs" && method === "GET") {
    const limit = Number(url.searchParams.get("limit") ?? 50);
    const cursor = url.searchParams.get("cursor");
    const start = cursor ? imports.jobs.findIndex((j) => j.id === cursor) + 1 : 0;
    const items = imports.jobs.slice(start, start + limit);
    const hasMore = start + limit < imports.jobs.length;
    return json(route, 200, { items: items.map(respond), page: { nextCursor: hasMore ? items.at(-1)!.id : null, hasMore, limit } });
  }

  if (path === "/api/v1/import-jobs" && method === "POST") {
    if (needsKey()) return json(route, 428, problem(428, "precondition.required"));
    const { type, fileName, size } = parseMultipart(request.postDataBuffer());
    if (type !== "products" && type !== "prices") return json(route, 422, problem(422, "validation.failed", { errors: { type: ["Must be one of: products, prices."] } }));
    if (runDenied(type, type === "prices")) return json(route, 403, problem(403, "auth.forbidden"));
    if (imports.uploadsDone >= imports.uploadLimit) {
      return json(route, 429, problem(429, "rate_limit.exceeded"), { "Retry-After": String(imports.retryAfterSeconds) });
    }
    const name = fileName ?? "upload";
    const rejected = imports.rejectFiles[name];
    if (rejected) return json(route, rejected.status, problem(rejected.status, rejected.code, { title: "The file was rejected" }));
    imports.uploadsDone += 1;
    const file = imports.files[name] ?? DEFAULT_PRODUCT_FILE;
    const duplicateOf = imports.jobs.find((j) => j.fileName === name && j.type === type && j.status === "completed")?.id ?? null;
    const job = mockJob({
      type,
      fileName: name,
      fileSizeBytes: size,
      fileFormat: name.toLowerCase().endsWith(".csv") ? "csv" : "xlsx",
      headers: file.headers,
      sampleRows: file.rows.slice(0, 5),
      totalRows: file.rows.length,
      createdBy: imports.userId,
      duplicateOfImportId: duplicateOf,
    });
    imports.jobs.unshift(job);
    return json(route, 201, respond(job), { Location: `/api/v1/import-jobs/${job.id}` });
  }

  const match = /^\/api\/v1\/import-jobs\/([^/]+)(?:\/([a-z-]+))?$/.exec(path);
  const job = match ? imports.jobs.find((j) => j.id === match[1]) : undefined;
  if (!match || !job) return json(route, 404, problem(404, "resource.not_found"));
  const action = match[2] ?? null;
  const invalid = (verb: string) => json(route, 409, problem(409, "imports.invalid_state", { title: `The import is '${job.status}' and cannot be ${verb} now` }));
  const canRemap = job.startedAt === null && job.progress.processedRows === 0 && ["uploaded", "mapped", "validated", "failed", "cancelled"].includes(job.status);

  if (action === null && method === "GET") {
    advance(imports, job);
    return json(route, 200, respond(job), { ETag: `W/"${job.version}"` });
  }

  if (action === "mapping-suggestions" && method === "GET") {
    const fields = fieldsFor(job.type);
    const suggestions = fields.flatMap((f) => {
      const header = job.headers.find((h) => f.synonyms.includes(fold(h)));
      return header ? [{ field: f.key, header, confidence: fold(header) === f.synonyms[0] ? 0.95 : 0.8, source: "synonym" }] : [];
    });
    const attrs = job.type === "products" ? imports.attributes : [];
    for (const a of attrs) {
      const header = job.headers.find((h) => fold(h) === fold(a.label) && !suggestions.some((s) => s.header === h));
      if (header) suggestions.push({ field: `attr.${a.key}`, header, confidence: 0.9, source: "synonym" });
    }
    const templates = imports.templates.filter((t) => t.type === job.type && Object.values(t.columns).every((h) => job.headers.includes(h)));
    return json(route, 200, {
      type: job.type,
      headers: job.headers,
      fields: [
        ...fields.map(({ key, label, requiredForCreate, required }) => ({ key, label, requiredForCreate, required })),
        ...attrs.map((a) => ({ key: `attr.${a.key}`, label: a.label, requiredForCreate: a.required, required: false })),
      ],
      suggestions,
      templates,
    });
  }

  if (action === "mapping" && method === "PUT") {
    if (!canRemap) return invalid("re-mapped");
    const body = request.postDataJSON() as { columns: Record<string, string> | null; matchKey: string | null; mode: string | null; priceListId: string | null; saveAsTemplate: string | null };
    const columns = body.columns ?? {};
    const matchKey = body.matchKey ?? "sku";
    const mode = body.mode ?? "upsert";
    const errors: Record<string, string[]> = {};
    const fields = fieldsFor(job.type);
    const used = new Set<string>();
    for (const [field, header] of Object.entries(columns)) {
      if (field.startsWith("attr.") ? !imports.attributes.some((a) => `attr.${a.key}` === field) || job.type !== "products" : !fields.some((f) => f.key === field))
        errors[`columns.${field}`] = ["Unknown field."];
      else if (!job.headers.includes(header)) errors[`columns.${field}`] = ["Must be one of the file's headers."];
      else if (used.has(header)) errors[`columns.${field}`] = ["A header can be mapped to one field only."];
      used.add(header);
    }
    if (Object.keys(errors).length === 0) {
      const required = fields.filter((f) => f.required || (f.requiredForCreate && mode !== "update_only")).map((f) => f.key);
      required.push(matchKey === "barcode" ? "barcode" : "sku");
      for (const k of new Set(required)) if (!(k in columns)) errors[`columns.${k}`] = ["Required for this import type and mode."];
    }
    const mapsPrice = "price" in columns;
    if (job.type === "prices" && !body.priceListId) errors.priceListId = ["Required for price imports."];
    else if (job.type === "products" && mapsPrice && !body.priceListId) errors.priceListId = ["Required when the price column is mapped."];
    else if (job.type === "products" && !mapsPrice && body.priceListId) errors.priceListId = ["Only allowed when the price column is mapped."];
    if (Object.keys(errors).length) return json(route, 422, problem(422, "validation.failed", { errors }));
    if (runDenied(job.type, mapsPrice)) return json(route, 403, problem(403, "auth.forbidden"));
    job.mapping = columns;
    job.matchKey = matchKey;
    job.mode = mode;
    job.priceListId = body.priceListId;
    job.status = "mapped";
    job.preview = { create: 0, update: 0, skip: 0, error: 0 };
    job.validatedAt = null;
    imports.rows.delete(job.id);
    bump(job);
    const name = body.saveAsTemplate?.trim();
    if (name) {
      const existing = imports.templates.find((t) => t.type === job.type && t.name === name);
      if (existing) Object.assign(existing, { columns, matchKey, mode, version: existing.version + 1, updatedAt: nowIso() });
      else imports.templates.push({ id: uuid("2b"), name, type: job.type, columns, matchKey, mode, version: 1, createdAt: nowIso(), updatedAt: null });
    }
    return json(route, 200, respond(job));
  }

  if (action === "validate" && method === "POST") {
    if (needsKey()) return json(route, 428, problem(428, "precondition.required"));
    if (job.mapping === null || !canRemap || job.status === "uploaded") return invalid("validated");
    if (imports.purged.has(job.id)) return json(route, 409, problem(409, "imports.file_purged"));
    job.status = "validating";
    bump(job);
    return json(route, 202, respond(job));
  }

  if (action === "start" && method === "POST") {
    if (needsKey()) return json(route, 428, problem(428, "precondition.required"));
    const resumable = (job.status === "cancelled" || job.status === "failed") && job.validatedAt !== null;
    if (job.status !== "validated" && !resumable) return invalid("started");
    job.status = "running";
    job.startedAt ??= nowIso();
    job.startedBy = imports.userId;
    job.finishedAt = null;
    job.failure = null;
    job.progress.plannedRows = job.preview.create + job.preview.update;
    percent(job);
    bump(job);
    return json(route, 202, respond(job));
  }

  if (action === "cancel" && method === "POST") {
    if (needsKey()) return json(route, 428, problem(428, "precondition.required"));
    if (!["uploaded", "mapped", "validating", "validated", "running"].includes(job.status)) return invalid("cancelled");
    job.status = "cancelled";
    job.finishedAt = nowIso();
    bump(job);
    return json(route, 200, respond(job));
  }

  if (action === "rollback" && method === "POST") {
    if (needsKey()) return json(route, 428, problem(428, "precondition.required"));
    if (!["completed", "rollback_failed"].includes(job.status)) {
      return ["rolling_back", "rolled_back", "partially_rolled_back"].includes(job.status)
        ? json(route, 409, problem(409, "imports.already_rolled_back"))
        : invalid("rolled back");
    }
    if (!can("imports.rollback") || runDenied(job.type, job.priceListId !== null)) return json(route, 403, problem(403, "auth.forbidden"));
    const ifMatch = request.headers()["if-match"];
    if (!ifMatch) return json(route, 428, problem(428, "precondition.required"));
    if (ifMatch !== `W/"${job.version}"`) return json(route, 412, problem(412, "concurrency.version_mismatch"));
    if (job.status === "completed" && job.rollbackDeadline && Date.parse(job.rollbackDeadline) < Date.now()) {
      return json(route, 409, problem(409, "imports.rollback_window_expired"));
    }
    job.status = "rolling_back";
    job.rollback ??= { requestedAt: nowIso(), requestedBy: imports.userId, finishedAt: null, planned: job.progress.created + job.progress.updated, rolledBack: 0, conflicts: 0, failed: 0, skipped: 0 };
    bump(job);
    return json(route, 202, respond(job));
  }

  if (action === "rows" && method === "GET") {
    const status = url.searchParams.get("status");
    const rowAction = url.searchParams.get("action");
    const rollbackStatus = url.searchParams.get("rollbackStatus");
    const limit = Number(url.searchParams.get("limit") ?? 50);
    const after = decodeCursor(url.searchParams.get("cursor"));
    const all = (imports.rows.get(job.id) ?? []).filter(
      (r) =>
        r.rowNumber > after &&
        (status === null || (status === "error" ? r.status === "invalid" || r.status === "failed" : r.status === status)) &&
        (rowAction === null || r.action === rowAction) &&
        (rollbackStatus === null || r.rollbackStatus === rollbackStatus),
    );
    const items = all.slice(0, limit);
    const hasMore = all.length > limit;
    return json(route, 200, { items, page: { nextCursor: hasMore ? encodeCursor(items.at(-1)!.rowNumber) : null, hasMore, limit } });
  }

  if (action === "error-report" && method === "GET") {
    const bad = (imports.rows.get(job.id) ?? []).filter((r) => r.status === "invalid" || r.status === "failed");
    const lines = [["Satır", ...job.headers, "Hatalar"].join(";"), ...bad.map((r) => [String(r.rowNumber), ...r.raw, r.errors.map((e) => e.message).join(" | ")].join(";"))];
    return route
      .fulfill({
        status: 200,
        headers: { ...CORS, "Content-Type": "text/csv; charset=utf-8", "Content-Disposition": `attachment; filename="ice-aktarma-hatalari-${job.id.slice(-6)}.csv"` },
        body: `﻿${lines.join("\r\n")}`,
      })
      .then(() => true);
  }

  return json(route, 404, problem(404, "resource.not_found"));
}
