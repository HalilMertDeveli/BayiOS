"use client";

import { useCallback, useEffect, useId, useReducer, useRef, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ArrowLeft, Bot, CircleAlert, FileCheck2, Lock, MessageSquareText, RotateCcw, ShieldAlert, XCircle } from "lucide-react";
import { FormAlert } from "@/components/catalog/form-alert";
import { CustomerPicker } from "@/components/customers/customer-picker";
import { useCustomerNames, useWarehouses } from "@/components/orders/use-order-reference";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { WHATSAPP_ORDER_HREF, approveOrderParse, getOrderParse, rejectOrderParse, type LoadedParse, type OrderParse } from "@/lib/assistant/api";
import { approveFieldErrors, assistantErrorMessage, isNotReviewable, isParseVersionConflict } from "@/lib/assistant/errors";
import { parseStatusLabels, parserNotice, warningTexts } from "@/lib/assistant/labels";
import { assistantPermissions } from "@/lib/assistant/permissions";
import { buildApproveRequest, initialReview, lineErrorsFromFields, reviewReducer, reviewSummary } from "@/lib/assistant/review";
import { orderHref } from "@/lib/orders/api";
import { useSession } from "@/lib/session/session-context";
import { MessageText } from "./message-text";
import { ReviewLineCard } from "./review-line-card";

type LoadState = { status: "loading" } | { status: "error"; message: string; notFound: boolean } | { status: "ready"; loaded: LoadedParse };

const TITLE = "WhatsApp siparişini incele";
const dateTime = new Intl.DateTimeFormat("tr-TR", { dateStyle: "medium", timeStyle: "short", timeZone: "Europe/Istanbul" });

function BackLink() {
  return (
    <Link href={WHATSAPP_ORDER_HREF} className="inline-flex w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      WhatsApp siparişi
    </Link>
  );
}

/** /siparisler/whatsapp/[id]: human review of a parse; creates the whatsapp_ai draft order only from confirmed lines. */
export function OrderParseReviewPage({ parseId }: { parseId: string }) {
  const { api, access } = useSession();
  const permissions = assistantPermissions(access);
  const [load, setLoad] = useState<LoadState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!permissions.parse) return;
    const controller = new AbortController();
    getOrderParse(api, parseId, controller.signal)
      .then((loaded) => {
        if (!controller.signal.aborted) setLoad({ status: "ready", loaded });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return;
        setLoad({ status: "error", message: assistantErrorMessage(error), notFound: error instanceof ApiError && error.status === 404 });
      });
    return () => controller.abort();
  }, [api, parseId, attempt, permissions.parse]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);

  if (!permissions.parse) {
    return (
      <>
        <BackLink />
        <PageHeader title={TITLE} />
        <section aria-label="İnceleme durumu" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<Lock className="size-6" />}
            title="WhatsApp siparişi yetkiniz yok"
            description="Rolünüz WhatsApp mesajından sipariş hazırlamaya izin vermiyor. Gerekirse işletme yöneticinizle görüşün."
          />
        </section>
      </>
    );
  }
  if (load.status === "loading") {
    return (
      <>
        <BackLink />
        <PageHeader title={TITLE} />
        <p role="status" className="text-body text-fg-muted">
          Ayrıştırma yükleniyor…
        </p>
      </>
    );
  }
  if (load.status === "error") {
    return (
      <>
        <BackLink />
        <PageHeader title={TITLE} />
        <div role="alert" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<CircleAlert className="size-6" />}
            title={load.notFound ? "Ayrıştırma bulunamadı" : "Ayrıştırma yüklenemedi"}
            description={load.notFound ? "Kayıt bu işletmeye ait değil veya bağlantı hatalı." : load.message}
            action={
              load.notFound ? undefined : (
                <Button
                  onClick={() => {
                    setLoad({ status: "loading" });
                    reload();
                  }}
                >
                  Tekrar dene
                </Button>
              )
            }
          />
        </div>
      </>
    );
  }

  const { parse } = load.loaded;
  return (
    <>
      <BackLink />
      <PageHeader
        title={TITLE}
        description={`${dateTime.format(new Date(parse.createdAt))} · ${parseStatusLabels[parse.status] ?? parse.status}`}
      />
      {parse.status === "needs_review" ? (
        // Keyed by version: a reload after a conflict starts the review from the server's state.
        <ReviewForm
          key={String(parse.version)}
          loaded={load.loaded}
          canCreateDraft={permissions.createDraft}
          onLoaded={(next) => setLoad({ status: "ready", loaded: next })}
          onReload={() => {
            setLoad({ status: "loading" });
            reload();
          }}
        />
      ) : (
        <ClosedParse parse={parse} />
      )}
    </>
  );
}

/** Approved (link to the draft) or rejected parse: nothing left to review. */
function ClosedParse({ parse }: { parse: OrderParse }) {
  const approved = parse.status === "approved";
  return (
    <section aria-label="İnceleme durumu" className="rounded-lg border border-line bg-card shadow-e1">
      <EmptyState
        icon={approved ? <FileCheck2 className="size-6" /> : <XCircle className="size-6" />}
        title={approved ? "Bu mesajdan taslak sipariş oluşturuldu" : "Bu ayrıştırma reddedildi"}
        description={
          approved
            ? "Taslak sipariş normal sipariş akışında; fiyatlar fiyat listesinden geldi ve onay yine bir kişi tarafından verilir."
            : "Bu mesajdan sipariş oluşturulmadı. Gerekirse mesajı yeniden yapıştırıp ayrıştırın."
        }
        action={
          approved && parse.orderId ? (
            <Link href={orderHref(parse.orderId)} className={buttonClasses("primary")}>
              Taslak siparişi aç
            </Link>
          ) : (
            <Link href={WHATSAPP_ORDER_HREF} className={buttonClasses("secondary")}>
              Yeni mesaj ayrıştır
            </Link>
          )
        }
      />
    </section>
  );
}

function ParseNotices({ parse }: { parse: OrderParse }) {
  const notice = parserNotice(parse.parser, parse.fallbackReason, parse.warnings);
  const warnings = warningTexts(parse.warnings);
  const injection = warnings.find((w) => w.code === "instruction_ignored");
  const others = warnings.filter((w) => w.code !== "instruction_ignored");
  return (
    <div className="flex flex-col gap-3">
      <div
        role="status"
        className={
          notice.tone === "warning"
            ? "flex flex-wrap items-start gap-3 rounded-md border border-warning-border bg-warning-bg px-4 py-3 text-body text-warning-fg"
            : "flex flex-wrap items-start gap-3 rounded-md border border-info-border bg-info-bg px-4 py-3 text-body text-info-fg"
        }
      >
        <Badge tone={parse.parser === "ai" ? "ai" : "neutral"} icon={<Bot className="size-3.5" aria-hidden="true" />}>
          Ayrıştırıcı: {notice.label}
        </Badge>
        <p className="flex-1">{notice.text}</p>
      </div>
      {injection ? (
        <FormAlert tone="warning" title="Talimat benzeri ifade">
          <span className="inline-flex items-start gap-2">
            <ShieldAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
            {injection.text} Mesajdaki talimatlar hiçbir işlem yaptırmaz; yalnızca aşağıdaki satırları siz onaylarsınız.
          </span>
        </FormAlert>
      ) : null}
      {others.length > 0 ? (
        <section aria-label="Ayrıştırma uyarıları" className="rounded-md border border-line bg-card px-4 py-3">
          <h2 className="mb-1 text-body font-semibold text-fg">Uyarılar</h2>
          <ul className="flex list-disc flex-col gap-1 pl-5 text-body text-fg">
            {others.map((w) => (
              <li key={w.code} className={w.tone === "warning" ? "text-warning-fg" : undefined}>
                {w.text}
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </div>
  );
}

function ReviewForm({
  loaded,
  canCreateDraft,
  onLoaded,
  onReload,
}: {
  loaded: LoadedParse;
  canCreateDraft: boolean;
  onLoaded: (next: LoadedParse) => void;
  onReload: () => void;
}) {
  const { api } = useSession();
  const router = useRouter();
  const { toast } = useToast();
  const { parse, etag } = loaded;
  const [state, dispatch] = useReducer(reviewReducer, parse, initialReview);
  const warehouses = useWarehouses();
  const names = useCustomerNames(parse.customerId ? [parse.customerId] : []);
  const [customer, setCustomer] = useState<{ id: string; name: string | null; code: string | null } | null>(
    parse.customerId ? { id: parse.customerId, name: null, code: null } : null,
  );
  const [warehouseId, setWarehouseId] = useState("");
  const [notes, setNotes] = useState("");
  const [busy, setBusy] = useState<"approve" | "reject" | null>(null);
  const [formError, setFormError] = useState<{ title: string; message: string; summary: string[]; reload: boolean } | null>(null);
  const [customerError, setCustomerError] = useState<string | undefined>();
  const [lineErrors, setLineErrors] = useState<Map<number, string[]>>(new Map());
  const [rejectOpen, setRejectOpen] = useState(false);
  const [approveKeys] = useState(() => new IdempotencyKeys());
  const [rejectKeys] = useState(() => new IdempotencyKeys());
  const alertRef = useRef<HTMLDivElement>(null);
  const blockerId = useId();

  const defaultWarehouse = warehouses.status === "ready" ? (warehouses.items.find((w) => w.isDefault && w.status === "active") ?? null) : null;
  if (!warehouseId && defaultWarehouse) setWarehouseId(defaultWarehouse.id);

  useEffect(() => {
    if (formError) alertRef.current?.focus();
  }, [formError]);

  const summary = reviewSummary(state);
  const blocker = !canCreateDraft
    ? "Taslak sipariş oluşturma yetkiniz yok (sipariş oluşturma izni gerekir)."
    : !customer
      ? "Müşteri seçin."
      : summary.blocker;
  const disabled = busy !== null;
  const customerShown = customer
    ? { id: customer.id, name: customer.name ?? names.get(customer.id)?.name ?? null, code: customer.code ?? names.get(customer.id)?.code ?? null }
    : null;
  const warehouseOptions =
    warehouses.status === "ready"
      ? warehouses.items.filter((w) => w.status === "active").map((w) => ({ value: w.id, label: `${w.name} (${w.code})` }))
      : [];

  const fail = (error: unknown, title: string) => {
    if (isNotReviewable(error) || isParseVersionConflict(error)) {
      setFormError({ title, message: assistantErrorMessage(error), summary: [], reload: true });
      return;
    }
    if (error instanceof ApiError && error.status === 422) {
      const fields = approveFieldErrors(error);
      setLineErrors(lineErrorsFromFields(state, fields));
      setCustomerError(fields.customerId);
      const general = Object.entries(fields)
        .filter(([key]) => !/^lines\[\d+\]/.test(key) && key !== "customerId")
        .map(([, message]) => message);
      setFormError({ title, message: "Bazı satırlar veya alanlar geçersiz. İşaretli yerleri düzeltip tekrar deneyin.", summary: general, reload: false });
      return;
    }
    setFormError({ title, message: assistantErrorMessage(error), summary: [], reload: false });
  };

  const approve = async () => {
    if (blocker || !customer) return;
    const body = buildApproveRequest(state, { customerId: customer.id, warehouseId: warehouseId || null, notes });
    setBusy("approve");
    setFormError(null);
    setCustomerError(undefined);
    setLineErrors(new Map());
    try {
      const next = await approveOrderParse(api, parse.id, body, { etag, idempotencyKey: approveKeys.keyFor(body) });
      approveKeys.reset();
      toast({ tone: "success", title: "Taslak sipariş oluşturuldu", description: "Fiyatlar fiyat listesinden geldi. Siparişi kontrol edip onaya gönderin." });
      if (next.parse.orderId) router.push(orderHref(next.parse.orderId));
      else onLoaded(next);
    } catch (error) {
      fail(error, "Taslak sipariş oluşturulamadı");
    } finally {
      setBusy(null);
    }
  };

  const reject = async () => {
    setBusy("reject");
    setFormError(null);
    try {
      const next = await rejectOrderParse(api, parse.id, { etag, idempotencyKey: rejectKeys.keyFor({ id: parse.id, action: "reject" }) });
      setRejectOpen(false);
      toast({ tone: "info", title: "Ayrıştırma reddedildi", description: "Sipariş oluşturulmadı." });
      onLoaded(next);
    } catch (error) {
      setRejectOpen(false);
      fail(error, "Ayrıştırma reddedilemedi");
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="flex flex-col gap-4">
      {formError ? (
        <FormAlert
          ref={alertRef}
          tone="error"
          title={formError.title}
          actions={
            formError.reload ? (
              <Button size="sm" icon={<RotateCcw className="size-4" />} onClick={onReload}>
                Yeniden yükle
              </Button>
            ) : undefined
          }
        >
          {formError.message}
          {formError.summary.length > 0 ? (
            <ul className="mt-1 list-disc pl-5">
              {formError.summary.map((m, i) => (
                <li key={i}>{m}</li>
              ))}
            </ul>
          ) : null}
        </FormAlert>
      ) : null}
      {!canCreateDraft ? (
        <FormAlert tone="warning" title="Taslak oluşturma yetkiniz yok">
          Satırları inceleyebilir veya ayrıştırmayı reddedebilirsiniz; taslak siparişi sipariş oluşturma izni olan bir kullanıcı oluşturur.
        </FormAlert>
      ) : null}

      <ParseNotices parse={parse} />

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[minmax(0,2fr)_minmax(0,3fr)] lg:items-start">
        <section aria-labelledby="original-message" className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1 lg:sticky lg:top-4">
          <h2 id="original-message" className="inline-flex items-center gap-2 text-h3 text-fg">
            <MessageSquareText className="size-5 text-fg-muted" aria-hidden="true" />
            Orijinal mesaj
          </h2>
          {parse.messageText ? (
            <MessageText message={parse.messageText} lines={parse.lines} />
          ) : (
            <p className="text-body text-fg-muted">Mesaj metni saklama süresi dolduğu için silindi.</p>
          )}
          <p className="text-caption text-fg-muted">Mesaj düz metin olarak gösterilir; içindeki bağlantılar ve kodlar çalıştırılmaz.</p>
          {parse.unparsedFragments.length > 0 ? (
            <div className="flex flex-col gap-1 border-t border-line pt-3">
              <h3 className="text-body font-semibold text-fg">Sipariş satırı olarak okunmayan kısımlar</h3>
              <ul className="flex list-disc flex-col gap-1 pl-5 text-body break-words whitespace-pre-wrap text-fg-secondary">
                {parse.unparsedFragments.map((f, i) => (
                  <li key={i}>{f}</li>
                ))}
              </ul>
            </div>
          ) : null}
        </section>

        <div className="flex flex-col gap-4">
          <section aria-labelledby="draft-header" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1">
            <h2 id="draft-header" className="text-h3 text-fg">
              Müşteri ve depo
            </h2>
            <CustomerPicker
              id="f-customerId"
              label="Müşteri"
              pickedLabel="Müşteri"
              hint="Ad veya kodun bir kısmı; tam vergi no / telefon / WhatsApp. Yalnızca aktif müşteriler."
              status="active"
              value={customerShown}
              error={customerError}
              disabled={disabled}
              onChange={(c) => setCustomer(c ? { id: c.id, name: c.name, code: c.code } : null)}
            />
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <Select
                label="Depo"
                options={warehouseOptions}
                placeholder={warehouses.status === "loading" ? "Depolar yükleniyor…" : "Varsayılan depo"}
                value={warehouseId}
                disabled={disabled}
                error={warehouses.status === "error" ? "Depolar yüklenemedi; varsayılan depo kullanılır." : undefined}
                onChange={(e) => setWarehouseId(e.target.value)}
              />
              <Textarea
                label="Sipariş notu"
                hint="Mesaj metni siparişe kopyalanmaz."
                rows={2}
                maxLength={2000}
                value={notes}
                disabled={disabled}
                onChange={(e) => setNotes(e.target.value)}
              />
            </div>
          </section>

          <section aria-labelledby="review-lines" className="flex flex-col gap-3">
            <div className="flex flex-col gap-1">
              <h2 id="review-lines" className="text-h3 text-fg">
                Satırlar ({state.lines.length})
              </h2>
              <p className="text-body text-fg-muted">
                Her satırda ürünü onaylayın veya seçin ve miktarı kontrol edin. Fiyat ve stok burada gösterilmez; fiyatlar taslak oluşunca fiyat
                listesinden gelir.
              </p>
            </div>
            {parse.lines.length === 0 ? (
              <div className="rounded-lg border border-line bg-card shadow-e1">
                <EmptyState
                  headingLevel="h3"
                  icon={<MessageSquareText className="size-6" />}
                  title="Mesajda sipariş satırı bulunamadı"
                  description="Mesajda ürün ve miktar okunamadı. Ayrıştırmayı reddedin veya siparişi elle oluşturun."
                />
              </div>
            ) : (
              <ol className="flex flex-col gap-3">
                {parse.lines.map((line, i) => (
                  <ReviewLineCard
                    key={String(line.lineNo)}
                    line={line}
                    review={state.lines[i]}
                    dispatch={dispatch}
                    serverErrors={lineErrors.get(state.lines[i].lineNo)}
                    disabled={disabled}
                  />
                ))}
              </ol>
            )}
          </section>

          <section aria-label="Taslak oluştur" className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1">
            <p className="text-body text-fg">
              {summary.kept} satır taslağa eklenecek{summary.skipped > 0 ? `, ${summary.skipped} satır çıkarıldı` : ""}.
            </p>
            <p id={blockerId} aria-live="polite" className={blocker ? "text-body text-warning-fg" : "text-body text-success-fg"}>
              {blocker ?? "Tüm satırlar hazır. Taslak oluşturulunca siparişi kontrol edip onaya gönderebilirsiniz; sipariş kendiliğinden onaylanmaz."}
            </p>
            <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
              <Button variant="secondary" disabled={disabled} onClick={() => setRejectOpen(true)}>
                Ayrıştırmayı reddet
              </Button>
              <Button
                icon={<FileCheck2 className="size-4" />}
                loading={busy === "approve"}
                disabled={disabled || blocker !== null}
                aria-describedby={blockerId}
                onClick={() => void approve()}
              >
                Taslak siparişi oluştur
              </Button>
            </div>
          </section>
        </div>
      </div>

      <Dialog
        open={rejectOpen}
        onClose={() => setRejectOpen(false)}
        title="Ayrıştırma reddedilsin mi?"
        description="Bu mesajdan sipariş oluşturulmaz. Gerekirse mesajı yeniden yapıştırabilirsiniz."
        role="alertdialog"
        dismissOnBackdrop={false}
        footer={
          <>
            <Button variant="secondary" disabled={busy === "reject"} onClick={() => setRejectOpen(false)}>
              Vazgeç
            </Button>
            <Button variant="danger" loading={busy === "reject"} onClick={() => void reject()}>
              Reddet
            </Button>
          </>
        }
      />
    </div>
  );
}
