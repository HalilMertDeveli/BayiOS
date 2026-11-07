"use client";

import { useEffect, useRef, useState } from "react";
import { FileDown, FileSpreadsheet } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Button } from "@/components/ui/button";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { exportReport, type ExportFilters } from "@/lib/reports/api";
import type { ReportId } from "@/lib/reports/definitions";
import { saveBlob, type ExportFormat } from "@/lib/reports/download";
import { exportErrorMessage } from "@/lib/reports/errors";
import { useSession } from "@/lib/session/session-context";

const FORMAT_LABELS: Record<ExportFormat, string> = { csv: "CSV", xlsx: "Excel (XLSX)" };

/**
 * CSV / XLSX download of a report (POST …/export, Idempotency-Key). A retry of the same file (network error) reuses the
 * key, so the API replays it instead of counting a second export; after a success the next click is a new export.
 * Errors (429 hourly limit with Retry-After, 422 too many rows) stay on the page.
 */
export function ExportButtons({ report, filters, today }: { report: ReportId; filters: ExportFilters | null; today: string }) {
  const { api } = useSession();
  const [busy, setBusy] = useState<ExportFormat | null>(null);
  const [result, setResult] = useState<{ tone: "success" | "error"; text: string } | null>(null);
  const [keys] = useState(() => new IdempotencyKeys());
  const alertRef = useRef<HTMLDivElement>(null);

  // Focus the error once it is on screen. Not requestAnimationFrame in the catch: that update comes after an await, so
  // React commits it on a later task and a frame can run first (alert not mounted yet; focus stays on <body>).
  useEffect(() => {
    if (result?.tone === "error") alertRef.current?.focus();
  }, [result]);

  const run = async (format: ExportFormat) => {
    if (!filters || busy) return;
    const body = { format, ...filters };
    setBusy(format);
    setResult(null);
    try {
      const file = await exportReport(api, report, format, filters, keys.keyFor(body), today);
      keys.reset();
      saveBlob(file.blob, file.filename);
      setResult({ tone: "success", text: `${FORMAT_LABELS[format]} dosyası indirildi: ${file.filename}` });
    } catch (error) {
      setResult({ tone: "error", text: exportErrorMessage(error) });
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="flex flex-col gap-2 sm:items-end">
      <div className="flex flex-wrap gap-2" role="group" aria-label="Dışa aktar">
        <Button
          variant="secondary"
          icon={<FileDown className="size-4" />}
          loading={busy === "csv"}
          disabled={!filters || (busy !== null && busy !== "csv")}
          onClick={() => void run("csv")}
        >
          CSV indir
        </Button>
        <Button
          variant="secondary"
          icon={<FileSpreadsheet className="size-4" />}
          loading={busy === "xlsx"}
          disabled={!filters || (busy !== null && busy !== "xlsx")}
          onClick={() => void run("xlsx")}
        >
          Excel indir
        </Button>
      </div>
      {result?.tone === "error" ? (
        <div className="sm:max-w-md">
          <FormAlert ref={alertRef} tone="error" title="Dosya indirilemedi">
            {result.text}
          </FormAlert>
        </div>
      ) : null}
      <p role="status" className={result?.tone === "success" ? "text-caption text-success-fg" : "sr-only"}>
        {result?.tone === "success" ? result.text : busy ? "Dosya hazırlanıyor…" : ""}
      </p>
    </div>
  );
}
