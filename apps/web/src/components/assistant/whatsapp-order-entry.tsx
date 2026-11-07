"use client";

import { useEffect, useRef, useState, type FormEvent, useId } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ArrowLeft, CircleAlert, Clock, Lock, MessageSquareText, ScanText } from "lucide-react";
import { LockedFeature } from "@/components/billing/locked-feature";
import { FormAlert } from "@/components/catalog/form-alert";
import { CustomerPicker } from "@/components/customers/customer-picker";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { Textarea } from "@/components/ui/textarea";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import {
  MAX_MESSAGE_CHARS,
  MAX_MESSAGE_LINES,
  listOrderParses,
  orderParseHref,
  submitOrderParse,
  type OrderParseListItem,
} from "@/lib/assistant/api";
import { assistantErrorMessage, isRateLimited, rateLimitMessage, translateTextError } from "@/lib/assistant/errors";
import { parserNotice } from "@/lib/assistant/labels";
import { FEATURES, featureGate } from "@/lib/billing/entitlements";
import { assistantPermissions } from "@/lib/assistant/permissions";
import { ORDERS_HREF } from "@/lib/orders/api";
import { useSession } from "@/lib/session/session-context";

const dateTime = new Intl.DateTimeFormat("tr-TR", { dateStyle: "medium", timeStyle: "short", timeZone: "Europe/Istanbul" });

function BackLink() {
  return (
    <Link href={ORDERS_HREF} className="inline-flex w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Siparişler
    </Link>
  );
}

/** Client-side check before sending (the API checks the same limits and stays authoritative). */
export function messageError(text: string): string | null {
  const trimmed = text.trim();
  if (!trimmed) return "WhatsApp mesajını yapıştırın.";
  if (trimmed.length > MAX_MESSAGE_CHARS) return `Mesaj en fazla ${MAX_MESSAGE_CHARS.toLocaleString("tr-TR")} karakter olabilir; mesajı bölerek gönderin.`;
  if (trimmed.split("\n").length > MAX_MESSAGE_LINES) return `Mesaj en fazla ${MAX_MESSAGE_LINES} satır olabilir; mesajı bölerek gönderin.`;
  return null;
}

/**
 * /siparisler/whatsapp: paste a WhatsApp message (untrusted text), optionally pick the customer, and send it to the
 * assistant (whatsapp.parse). The API parses and matches it against the tenant catalog only; the review page follows.
 */
export function WhatsappOrderPage() {
  const { access, entitlements } = useSession();
  const permissions = assistantPermissions(access);
  // The AI assistant is an add-on (#51): without it new messages cannot be parsed, but started reviews stay reachable.
  const aiGate = featureGate(entitlements, FEATURES.aiOrderParsing);
  return (
    <>
      <BackLink />
      <PageHeader
        title="WhatsApp siparişi"
        description="Müşterinin mesajını yapıştırın. Satırlar yalnızca kataloğunuzla eşleştirilir; taslak, siz her satırı onaylayınca oluşur."
      />
      {permissions.parse ? (
        <>
          {aiGate.locked ? (
            <section aria-label="WhatsApp mesajı" className="rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
              <LockedFeature gate={aiGate} title="WhatsApp siparişi ayrıştırma kapalı" />
            </section>
          ) : (
            <SubmitForm />
          )}
          <PendingReviews />
        </>
      ) : (
        <section aria-label="WhatsApp siparişi durumu" className="rounded-lg border border-line bg-card shadow-e1">
          <EmptyState
            icon={<Lock className="size-6" />}
            title="WhatsApp siparişi yetkiniz yok"
            description="Rolünüz WhatsApp mesajından sipariş hazırlamaya izin vermiyor. Gerekirse işletme yöneticinizle görüşün."
          />
        </section>
      )}
    </>
  );
}

function SubmitForm() {
  const uid = useId();
  const { api } = useSession();
  const router = useRouter();
  const [customer, setCustomer] = useState<{ id: string; name: string; code: string } | null>(null);
  const [text, setText] = useState("");
  const [textError, setTextError] = useState<string | undefined>();
  const [alert, setAlert] = useState<{ tone: "error" | "warning"; title: string; message: string } | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [keys] = useState(() => new IdempotencyKeys());
  const alertRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (alert) alertRef.current?.focus();
  }, [alert]);

  const length = text.trim().length;
  const over = length > MAX_MESSAGE_CHARS;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (submitting) return;
    const invalid = messageError(text);
    setTextError(invalid ?? undefined);
    if (invalid) return;
    const body = { text: text.trim(), customerId: customer?.id ?? null };
    setSubmitting(true);
    setAlert(null);
    try {
      const { parse } = await submitOrderParse(api, body, keys.keyFor(body));
      keys.reset();
      router.push(orderParseHref(parse.id));
    } catch (error) {
      if (isRateLimited(error)) {
        setAlert({ tone: "warning", title: "Saatlik ayrıştırma sınırı doldu", message: rateLimitMessage(error) });
      } else if (error instanceof ApiError && error.status === 422 && error.fieldErrors.text?.length) {
        setTextError(translateTextError(error.fieldErrors.text[0]));
      } else {
        setAlert({ tone: "error", title: "Mesaj ayrıştırılamadı", message: assistantErrorMessage(error) });
      }
      setSubmitting(false);
    }
  };

  return (
    <form noValidate onSubmit={submit} aria-label="WhatsApp mesajı" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
      {alert ? (
        <FormAlert ref={alertRef} tone={alert.tone} title={alert.title}>
          {alert.message}
        </FormAlert>
      ) : null}
      <CustomerPicker
        id={`${uid}-f-customerId`}
        label="Müşteri (isteğe bağlı)"
        pickedLabel="Müşteri"
        hint="Şimdi veya inceleme ekranında seçebilirsiniz. Müşteri bilgisi AI'ya gönderilmez."
        status="active"
        value={customer}
        disabled={submitting}
        onChange={(c) => setCustomer(c ? { id: c.id, name: c.name, code: c.code } : null)}
      />
      <div className="flex flex-col gap-1">
        <Textarea
          label="WhatsApp mesajı"
          hint="Mesajı olduğu gibi yapıştırın. Telefon ve e-posta adresleri ayrıştırmadan önce gizlenir; mesajdaki fiyatlar kullanılmaz."
          rows={10}
          value={text}
          error={textError}
          required
          disabled={submitting}
          spellCheck={false}
          onChange={(e) => {
            setText(e.target.value);
            if (textError) setTextError(undefined);
          }}
        />
        <p className={over ? "text-caption text-danger-fg" : "text-caption text-fg-muted"}>
          {length.toLocaleString("tr-TR")} / {MAX_MESSAGE_CHARS.toLocaleString("tr-TR")} karakter
        </p>
      </div>
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
        <p className="text-caption text-fg-muted">AI yalnızca öneri yapar: fiyat, stok ve ürün kodu uydurmaz; sipariş sizin onayınız olmadan oluşmaz.</p>
        <Button type="submit" icon={<ScanText className="size-4" />} loading={submitting}>
          Mesajı ayrıştır
        </Button>
      </div>
    </form>
  );
}

type PendingState = { status: "loading" } | { status: "error"; message: string } | { status: "ready"; items: OrderParseListItem[] };

/** Parses still waiting for review (newest first), so a started review can be continued. */
function PendingReviews() {
  const uid = useId();
  const { api } = useSession();
  const [state, setState] = useState<PendingState>({ status: "loading" });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    listOrderParses(api, { status: "needs_review", limit: 10, signal: controller.signal })
      .then((items) => {
        if (!controller.signal.aborted) setState({ status: "ready", items });
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState({ status: "error", message: assistantErrorMessage(error) });
      });
    return () => controller.abort();
  }, [api, attempt]);

  return (
    <section aria-labelledby={`${uid}-pending-reviews`} className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
      <h2 id={`${uid}-pending-reviews`} className="text-h3 text-fg">
        İnceleme bekleyen mesajlar
      </h2>
      {state.status === "loading" ? (
        <p role="status" className="text-body text-fg-muted">
          Yükleniyor…
        </p>
      ) : state.status === "error" ? (
        <div role="alert" className="flex flex-wrap items-center gap-3 text-body text-danger-fg">
          <CircleAlert className="size-4" aria-hidden="true" />
          {state.message}
          <Button
            size="sm"
            variant="secondary"
            onClick={() => {
              setState({ status: "loading" });
              setAttempt((n) => n + 1);
            }}
          >
            Tekrar dene
          </Button>
        </div>
      ) : state.items.length === 0 ? (
        <EmptyState headingLevel="h3" icon={<MessageSquareText className="size-6" />} title="Bekleyen inceleme yok" description="Ayrıştırdığınız ve henüz onaylamadığınız mesajlar burada görünür." className="py-6" />
      ) : (
        <ul className="flex flex-col gap-2">
          {state.items.map((item) => {
            const s = item.summary;
            return (
              <li key={item.id}>
                <Link
                  href={orderParseHref(item.id)}
                  className="flex min-h-12 flex-col gap-1 rounded-md border border-line-strong px-4 py-2 hover:bg-sunken sm:flex-row sm:items-center sm:justify-between"
                >
                  <span className="inline-flex items-center gap-2 text-body text-fg">
                    <Clock className="size-4 text-fg-muted" aria-hidden="true" />
                    {dateTime.format(new Date(item.createdAt))}
                  </span>
                  <span className="flex flex-wrap items-center gap-2 text-caption text-fg-muted">
                    <span>
                      {String(s.lines)} satır · {String(s.matched)} eşleşti · {String(s.ambiguous)} belirsiz · {String(s.unmatched)} eşleşmedi
                    </span>
                    <Badge tone={item.parser === "ai" ? "ai" : "neutral"}>{parserNotice(item.parser, item.fallbackReason).label}</Badge>
                  </span>
                </Link>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
