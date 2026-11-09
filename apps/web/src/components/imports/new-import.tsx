"use client";

import { useEffect, useId, useRef, useState, type DragEvent, type FormEvent } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ArrowLeft, FileSpreadsheet, Upload, X } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { Button } from "@/components/ui/button";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { IMPORTS_HREF, importHref, uploadImportFile, type ImportType } from "@/lib/imports/api";
import { importErrorMessage } from "@/lib/imports/errors";
import { FILE_ACCEPT, preCheckFile } from "@/lib/imports/file-check";
import { formatFileSize, importTypeDescriptions, importTypeLabels } from "@/lib/imports/labels";
import { canRunType, importPermissions } from "@/lib/imports/permissions";
import { listPriceLists, type PriceList } from "@/lib/pricing/api";
import { useSession } from "@/lib/session/session-context";
import { cn } from "@/lib/cn";
import { useFocusAfterCommit } from "@/lib/use-focus-after-commit";
import { ImportStepper } from "./import-stepper";
import { ImportsLocked } from "./import-history";

/** Query parameter that carries the price list chosen in step 1 to the mapping step. */
export const PRICE_LIST_PARAM = "liste";

export function BackToHistory() {
  return (
    <Link href={IMPORTS_HREF} className="inline-flex w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      İçe aktarma geçmişi
    </Link>
  );
}

/** Active (not archived) price lists the caller may write to. */
export function usePriceListOptions(enabled: boolean) {
  const { api } = useSession();
  const [state, setState] = useState<{ status: "idle" | "loading" | "error" | "ready"; lists: PriceList[]; message?: string }>({ status: "idle", lists: [] });
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    listPriceLists(api, { signal: controller.signal })
      .then((lists) => {
        if (!controller.signal.aborted) setState({ status: "ready", lists: lists.filter((l) => l.status !== "archived") });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ status: "error", lists: [], message: importErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, enabled, attempt]);
  return { ...state, status: enabled && state.status === "idle" ? ("loading" as const) : state.status, retry: () => setAttempt((n) => n + 1) };
}

export const priceListLabel = (l: PriceList) => `${l.name} (${l.currency}${l.isDefault ? ", varsayılan" : ""})`;

/** /ice-aktarma/yeni: steps 1 (type, target price list) and 2 (file) — the upload creates the job, then its own page continues. */
export function NewImport() {
  const { api, access } = useSession();
  const router = useRouter();
  const permissions = importPermissions(access);
  const [step, setStep] = useState<"type" | "upload">("type");
  const [type, setType] = useState<ImportType | null>(() => (permissions.products ? "products" : permissions.prices ? "prices" : null));
  const [priceListId, setPriceListId] = useState("");
  const [typeError, setTypeError] = useState<string | null>(null);
  const priceLists = usePriceListOptions(type === "prices" && permissions.prices);
  const headingRef = useRef<HTMLHeadingElement>(null);

  const [file, setFile] = useState<File | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [retryAt, setRetryAt] = useState<number | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [uploading, setUploading] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const inputRef = useRef<HTMLInputElement>(null);
  const alertRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();
  const inputId = useId();

  useEffect(() => {
    headingRef.current?.focus();
  }, [step]);

  // 429: the upload button stays disabled until Retry-After has passed.
  useEffect(() => {
    if (retryAt === null) return;
    const timer = window.setInterval(() => {
      const t = Date.now();
      setNow(t);
      if (t >= retryAt) {
        setRetryAt(null);
        setUploadError(null);
      }
    }, 1000);
    return () => window.clearInterval(timer);
  }, [retryAt]);
  const rateLimited = retryAt !== null && now < retryAt;

  if (!permissions.run || (!permissions.products && !permissions.prices)) {
    return (
      <>
        <BackToHistory />
        <PageHeader title="Yeni içe aktarma" />
        <ImportsLocked permissions={permissions} />
      </>
    );
  }

  const goUpload = (event: FormEvent) => {
    event.preventDefault();
    if (!type) return setTypeError("Bir aktarma türü seçin.");
    if (type === "prices" && !priceListId) return setTypeError("Fiyatların yazılacağı fiyat listesini seçin.");
    setTypeError(null);
    setStep("upload");
  };

  const choose = (candidate: File | null | undefined) => {
    setUploadError(null);
    if (!candidate) return;
    const problem = preCheckFile(candidate);
    setFileError(problem);
    setFile(problem ? null : candidate);
    if (problem && inputRef.current) inputRef.current.value = "";
  };

  const onDrop = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    setDragging(false);
    const files = event.dataTransfer.files;
    if (files.length > 1) {
      setFileError("Tek seferde bir dosya yükleyebilirsiniz.");
      return;
    }
    choose(files[0]);
  };

  const upload = async (event: FormEvent) => {
    event.preventDefault();
    if (!type || !file || uploading || rateLimited) {
      if (!file) setFileError("Yüklenecek bir dosya seçin.");
      return;
    }
    setUploading(true);
    setUploadError(null);
    try {
      // Same file + type → same key: a retry after a network error returns the first job instead of a duplicate.
      const key = keys.keyFor({ type, name: file.name, size: file.size, modified: file.lastModified });
      const job = await uploadImportFile(api, type, file, key);
      keys.reset();
      const query = type === "prices" && priceListId ? `?${PRICE_LIST_PARAM}=${encodeURIComponent(priceListId)}` : "";
      router.push(`${importHref(job.id)}${query}`);
    } catch (error) {
      if (error instanceof ApiError && error.status === 429) {
        const seconds = error.retryAfterSeconds ?? 60;
        setRetryAt(Date.now() + seconds * 1000);
        setNow(Date.now());
      }
      // A rejected file (413/415/422) is final for this file: the next attempt is a new key.
      if (error instanceof ApiError && [413, 415, 422].includes(error.status)) keys.reset();
      setUploadError(importErrorMessage(error));
      setUploading(false);
      focusAfterCommit(() => alertRef.current);
    }
  };

  return (
    <>
      <BackToHistory />
      <PageHeader title="Yeni içe aktarma" description="Dosyanızı yükleyin, kolonları eşleyin, önizlemeyi kontrol edin; kayıtlar yalnızca siz başlattığınızda yazılır." />
      <ImportStepper current={step} />

      {step === "type" ? (
        <form noValidate onSubmit={goUpload} className="flex flex-col gap-5 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
          <h2 ref={headingRef} tabIndex={-1} className="text-h2 text-fg focus:outline-none">
            Ne aktaracaksınız?
          </h2>
          {typeError ? (
            <FormAlert tone="error" title="Devam edilemedi">
              {typeError}
            </FormAlert>
          ) : null}
          <fieldset className="flex flex-col gap-3">
            <legend className="sr-only">Aktarma türü</legend>
            {(["products", "prices"] as const).map((t) => {
              const allowed = canRunType(permissions, t);
              return (
                <label
                  key={t}
                  className={cn(
                    "flex cursor-pointer gap-3 rounded-md border p-4",
                    type === t ? "border-brand bg-brand-bg" : "border-line-strong",
                    !allowed && "cursor-not-allowed opacity-70",
                  )}
                >
                  <input
                    type="radio"
                    name="import-type"
                    value={t}
                    checked={type === t}
                    disabled={!allowed}
                    onChange={() => setType(t)}
                    className="mt-1 size-5 accent-(--bos-brand-primary)"
                  />
                  <span className="flex flex-col gap-1">
                    <span className="font-medium text-fg">{importTypeLabels[t]}</span>
                    <span className="text-caption text-fg-muted">{importTypeDescriptions[t]}</span>
                    {!allowed ? (
                      <span className="text-caption text-fg-secondary">
                        {t === "products" ? "Ürün aktarmak için katalog yazma yetkisi gerekir." : "Fiyat aktarmak için fiyat yazma yetkisi gerekir."}
                      </span>
                    ) : null}
                  </span>
                </label>
              );
            })}
          </fieldset>
          <p className="text-caption text-fg-muted">Stok ve müşteri aktarma henüz kullanıma açılmadı.</p>

          {type === "prices" ? (
            priceLists.status === "error" ? (
              <FormAlert tone="error" title="Fiyat listeleri yüklenemedi" actions={<Button variant="secondary" onClick={priceLists.retry}>Tekrar dene</Button>}>
                {priceLists.message}
              </FormAlert>
            ) : (
              <Select
                label="Hedef fiyat listesi"
                required
                placeholder={priceLists.status === "loading" ? "Yükleniyor…" : "Liste seçin"}
                disabled={priceLists.status === "loading"}
                value={priceListId}
                onChange={(e) => setPriceListId(e.target.value)}
                options={priceLists.lists.map((l) => ({ value: l.id, label: priceListLabel(l) }))}
                hint={priceLists.status === "ready" && priceLists.lists.length === 0 ? "Önce Fiyat listeleri sayfasında bir liste oluşturun." : "Dosyadaki fiyatlar bu listeye yazılır."}
              />
            )
          ) : null}

          <div className="flex justify-end">
            <Button type="submit">Devam</Button>
          </div>
        </form>
      ) : (
        <form noValidate onSubmit={upload} className="flex flex-col gap-5 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
          <h2 ref={headingRef} tabIndex={-1} className="text-h2 text-fg focus:outline-none">
            Dosyayı yükleyin
          </h2>
          <p className="text-body text-fg-secondary">
            Tür: <span className="font-medium text-fg">{type ? importTypeLabels[type] : "—"}</span>
            {type === "prices" ? (
              <>
                {" "}
                · Liste: <span className="font-medium text-fg">{priceLists.lists.find((l) => l.id === priceListId)?.name ?? "—"}</span>
              </>
            ) : null}
          </p>
          {uploadError ? (
            <FormAlert ref={alertRef} tone="error" title={rateLimited ? "Yükleme sınırına ulaşıldı" : "Dosya yüklenemedi"}>
              {uploadError}
            </FormAlert>
          ) : null}

          <div
            onDragOver={(e) => {
              e.preventDefault();
              setDragging(true);
            }}
            onDragLeave={() => setDragging(false)}
            onDrop={onDrop}
            data-testid="import-dropzone"
            className={cn(
              "flex flex-col items-center gap-3 rounded-lg border-2 border-dashed px-4 py-8 text-center",
              dragging ? "border-brand bg-brand-bg" : fileError ? "border-danger-fg" : "border-line-strong",
            )}
          >
            <Upload className="size-8 text-fg-muted" aria-hidden="true" />
            <p className="text-body text-fg">
              Dosyayı buraya sürükleyip bırakın veya{" "}
              <label htmlFor={inputId} className="cursor-pointer font-medium text-brand-fg underline underline-offset-2">
                bilgisayarınızdan seçin
              </label>
            </p>
            <input
              ref={inputRef}
              id={inputId}
              type="file"
              accept={FILE_ACCEPT}
              aria-describedby={`${inputId}-hint${fileError ? ` ${inputId}-error` : ""}`}
              aria-invalid={fileError ? true : undefined}
              onChange={(e) => choose(e.target.files?.[0])}
              className="max-w-full text-body text-fg file:mr-3 file:h-9 file:rounded-md file:border file:border-line-strong file:bg-card file:px-3 file:text-body file:text-fg"
            />
            <p id={`${inputId}-hint`} className="text-caption text-fg-muted">
              .xlsx veya .csv · en fazla 20 MB ve 10.000 satır · ilk satır kolon başlıkları. Makrolu (.xlsm) ve eski (.xls) dosyalar kabul edilmez.
            </p>
            {fileError ? (
              <p id={`${inputId}-error`} role="alert" className="text-caption text-danger-fg">
                {fileError}
              </p>
            ) : null}
          </div>

          {file ? (
            <div className="flex items-center gap-3 rounded-md border border-line bg-sunken px-3 py-2">
              <FileSpreadsheet className="size-5 shrink-0 text-fg-muted" aria-hidden="true" />
              <span className="min-w-0 flex-1 text-body break-all text-fg">
                {file.name} <span className="text-caption text-fg-muted">({formatFileSize(file.size)})</span>
              </span>
              <button
                type="button"
                aria-label={`${file.name} dosyasını kaldır`}
                onClick={() => {
                  setFile(null);
                  if (inputRef.current) inputRef.current.value = "";
                }}
                className="inline-flex size-9 items-center justify-center rounded-md text-fg-muted hover:bg-card hover:text-fg"
              >
                <X className="size-4" aria-hidden="true" />
              </button>
            </div>
          ) : null}

          <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-between">
            <Button variant="secondary" onClick={() => setStep("type")} disabled={uploading}>
              Geri
            </Button>
            <Button type="submit" icon={<Upload className="size-4" />} loading={uploading} disabled={!file || rateLimited}>
              Yükle ve devam et
            </Button>
          </div>
        </form>
      )}
    </>
  );
}
