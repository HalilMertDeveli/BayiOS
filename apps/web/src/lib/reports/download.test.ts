import { describe, expect, it } from "vitest";
import { exportFilename, filenameFromContentDisposition } from "./download";

describe("filenameFromContentDisposition", () => {
  it("prefers filename* (RFC 8187) over filename", () => {
    expect(
      filenameFromContentDisposition(`attachment; filename=siparisler-2026-10-07_2026-11-05.csv; filename*=UTF-8''siparisler-2026-10-07_2026-11-05.csv`),
    ).toBe("siparisler-2026-10-07_2026-11-05.csv");
    expect(filenameFromContentDisposition(`attachment; filename="fallback.csv"; filename*=UTF-8''%C3%BCr%C3%BCnler.xlsx`)).toBe("ürünler.xlsx");
    expect(filenameFromContentDisposition(`attachment; filename*=iso-8859-1''r%E9sum%E9.csv`)).toBe("résumé.csv");
  });

  it("reads a quoted or bare filename", () => {
    expect(filenameFromContentDisposition(`attachment; filename="kritik stok.csv"`)).toBe("kritik stok.csv");
    expect(filenameFromContentDisposition("attachment; filename=kritik-stok-2026-11-05.xlsx")).toBe("kritik-stok-2026-11-05.xlsx");
  });

  it("strips path parts and control characters; null without a usable name", () => {
    expect(filenameFromContentDisposition(`attachment; filename="../../etc/passwd"`)).toBe("passwd");
    expect(filenameFromContentDisposition(`attachment; filename="a\u0007b.csv"`)).toBe("ab.csv");
    expect(filenameFromContentDisposition("attachment")).toBeNull();
    expect(filenameFromContentDisposition(null)).toBeNull();
    expect(filenameFromContentDisposition(`attachment; filename=".."`)).toBeNull();
  });

  it("falls back to filename when filename* is malformed", () => {
    expect(filenameFromContentDisposition(`attachment; filename=ok.csv; filename*=UTF-8''%E0%A4%A`)).toBe("ok.csv");
  });
});

describe("exportFilename (fallback, same pattern as the API)", () => {
  const today = "2026-11-05";
  it("critical stock uses today", () => {
    expect(exportFilename("critical-stock", "csv", { from: null, to: null }, today)).toBe("kritik-stok-2026-11-05.csv");
  });
  it("periodic reports use the range", () => {
    expect(exportFilename("orders", "xlsx", { from: "2026-10-07", to: "2026-11-05" }, today)).toBe("siparisler-2026-10-07_2026-11-05.xlsx");
    expect(exportFilename("top-products", "csv", { from: "2026-11-01", to: "2026-11-05" }, today)).toBe(
      "en-cok-siparis-edilenler-2026-11-01_2026-11-05.csv",
    );
    expect(exportFilename("ai-usage", "csv", { from: null, to: null }, today)).toBe("ai-kullanimi-2026-11-05.csv");
  });
});
