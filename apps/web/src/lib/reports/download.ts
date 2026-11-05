import type { ReportId } from "./definitions";

export type ExportFormat = "csv" | "xlsx";

/** File name stems the API uses (ReportTables in services/api Reports): ascii, Turkish words. */
const STEMS: Record<ReportId, string> = {
  "critical-stock": "kritik-stok",
  orders: "siparisler",
  "top-products": "en-cok-siparis-edilenler",
  "stock-movements": "stok-hareketleri",
  "picking-tasks": "toplama-gorevleri",
  "ai-usage": "ai-kullanimi",
};

/** Keeps a header-supplied name safe to save: no path parts, no control characters, bounded length. */
function sanitize(name: string): string | null {
  const base = name.split(/[\\/]/).pop()?.replace(/[\u0000-\u001f\u007f"<>:|?*]/g, "").trim() ?? "";
  if (!base || base === "." || base === "..") return null;
  return base.slice(0, 150);
}

/**
 * The file name of a Content-Disposition header (RFC 6266): `filename*` (RFC 8187, UTF-8) wins over `filename`.
 * Null when the header is missing — cross-origin, the browser only exposes it if the API lists it in
 * Access-Control-Expose-Headers — or carries no usable name.
 */
export function filenameFromContentDisposition(header: string | null | undefined): string | null {
  if (!header) return null;
  const extended = /filename\*\s*=\s*([^']*)'[^']*'([^;]+)/i.exec(header);
  if (extended) {
    try {
      const charset = extended[1].trim().toLowerCase();
      const value = extended[2].trim().replace(/^"(.*)"$/, "$1");
      // RFC 8187 allows UTF-8 and ISO-8859-1; Latin-1 percent bytes map 1:1 to code points.
      const decoded =
        charset === "iso-8859-1"
          ? value.replace(/%([0-9a-f]{2})/gi, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)))
          : decodeURIComponent(value);
      const safe = sanitize(decoded);
      if (safe) return safe;
    } catch {
      // Malformed percent-encoding: fall through to the plain parameter.
    }
  }
  const plain = /(?:^|;)\s*filename\s*=\s*(?:"((?:[^"\\]|\\.)*)"|([^;]+))/i.exec(header);
  if (plain) return sanitize((plain[1] ?? plain[2] ?? "").replace(/\\(.)/g, "$1"));
  return null;
}

/**
 * Fallback name, same pattern as the API: `kritik-stok-<today>.csv`, periodic reports `<stem>-<from>_<to>.xlsx`. A
 * periodic report without both bounds (the API then defaulted them) gets today's date.
 */
export function exportFilename(id: ReportId, format: ExportFormat, range: { from: string | null; to: string | null }, today: string): string {
  const stem = STEMS[id];
  const suffix = id !== "critical-stock" && range.from && range.to ? `${range.from}_${range.to}` : today;
  return `${stem}-${suffix}.${format}`;
}

/** Hands a Blob to the browser as a download (an object URL on a temporary link), then releases it. */
export function saveBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  link.rel = "noopener";
  link.style.display = "none";
  document.body.appendChild(link);
  link.click();
  link.remove();
  // Give the browser a moment to start the download before the URL is revoked.
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}
