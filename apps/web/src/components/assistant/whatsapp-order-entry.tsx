"use client";

import { useEffect, useRef, useState, type FormEvent, useId } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ArrowLeft, CircleAlert, Clock, Lock, MessageSquareText, ScanText, ShieldCheck } from "@/components/ui/icons";
import { LockedFeature } from "@/components/billing/locked-feature";
import { FormAlert } from "@/components/catalog/form-alert";
import { CustomerPicker } from "@/components/customers/customer-picker";
import { Badge } from "@/components/ui/badge";
import { Button, buttonClasses } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { Textarea } from "@/components/ui/textarea";
import { ApiError } from "@/lib/api/client";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import {
  DEFAULT_MESSAGE_LIMITS,
  ParseStillPendingError,
  getAiSettings,
  listOrderParses,
  messageLimits,
  orderParseHref,
  submitOrderParse,
  updateAiSettings,
  waitForOrderParse,
  type AiSettings,
  type MessageLimits,
  type OrderParseListItem,
} from "@/lib/assistant/api";
import {
  assistantErrorMessage,
  isOptInRequired,
  isRateLimited,
  parseFailureMessage,
  rateLimitMessage,
  translateTextError,
} from "@/lib/assistant/errors";
import { parserNotice } from "@/lib/assistant/labels";
import { FEATURES, featureGate } from "@/lib/billing/entitlements";
import { assistantPermissions } from "@/lib/assistant/permissions";
import { NEW_ORDER_HREF, ORDERS_HREF } from "@/lib/orders/api";
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
export function messageError(text: string, limits: MessageLimits = DEFAULT_MESSAGE_LIMITS): string | null {
  const trimmed = text.trim();
  if (!trimmed) return "WhatsApp mesajını yapıştırın.";
  if (trimmed.length > limits.maxChars) return `Mesaj en fazla ${limits.maxChars.toLocaleString("tr-TR")} karakter olabilir; mesajı bölerek gönderin.`;
  if (trimmed.split("\n").length > limits.maxLines) return `Mesaj en fazla ${limits.maxLines} satır olabilir; mesajı bölerek gönderin.`;
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
            <AssistantEntry canManage={permissions.manageAi} />
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

type SettingsState = { status: "loading" } | { status: "ready"; settings: AiSettings | null };

/**
 * The tenant's AI opt-in (data-policy §7, default off) decides what is shown: the message form, or — while off — an
 * explanation with the switch for owner/admin. If the settings cannot be read the form is shown; the API refuses
 * (409 `ai.opt_in_required`) when needed and the panel follows.
 */
function AssistantEntry({ canManage }: { canManage: boolean }) {
  const { api } = useSession();
  const [state, setState] = useState<SettingsState>({ status: "loading" });

  useEffect(() => {
    const controller = new AbortController();
    getAiSettings(api, controller.signal)
      .then((settings) => {
        if (!controller.signal.aborted) setState({ status: "ready", settings });
      })
      .catch(() => {
        if (!controller.signal.aborted) setState({ status: "ready", settings: null });
      });
    return () => controller.abort();
  }, [api]);

  if (state.status === "loading") {
    return (
      <section aria-label="WhatsApp mesajı" className="rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
        <p role="status" className="text-body text-fg-muted">
          Yükleniyor…
        </p>
      </section>
    );
  }
  const { settings } = state;
  if (settings && !settings.aiParsingEnabled) {
    return <OptInPanel canManage={canManage} onChanged={(next) => setState({ status: "ready", settings: next })} />;
  }
  return (
    <SubmitForm
      limits={messageLimits(settings)}
      onOptInRequired={() =>
        setState((current) => ({
          status: "ready",
          settings:
            current.status === "ready" && current.settings
              ? { ...current.settings, aiParsingEnabled: false }
              : { aiParsingEnabled: false, providerConfigured: false, limits: { maxInputChars: DEFAULT_MESSAGE_LIMITS.maxChars, maxInputLines: DEFAULT_MESSAGE_LIMITS.maxLines, hourlyParseLimit: 0 }, updatedAt: null, updatedBy: null, version: 0 },
        }))
      }
    />
  );
}

/** AI parsing is off for the tenant: what switching it on means, and the switch (owner/admin only). */
function OptInPanel({ canManage, onChanged }: { canManage: boolean; onChanged: (settings: AiSettings) => void }) {
  const uid = useId();
  const { api } = useSession();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const errorRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (error) errorRef.current?.focus();
  }, [error]);

  const enable = async () => {
    setBusy(true);
    setError(null);
    try {
      onChanged(await updateAiSettings(api, true));
    } catch (e) {
      setError(assistantErrorMessage(e));
      setBusy(false);
    }
  };

  return (
    <section aria-labelledby={`${uid}-opt-in`} className="flex flex-col gap-3 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
      <h2 id={`${uid}-opt-in`} className="inline-flex items-center gap-2 text-h3 text-fg">
        <ShieldCheck className="size-5 text-fg-muted" aria-hidden="true" />
        AI ile sipariş ayrıştırma kapalı
      </h2>
      {error ? (
        <FormAlert ref={errorRef} tone="error" title="Ayar değiştirilemedi">
          {error}
        </FormAlert>
      ) : null}
      <p className="text-body text-fg">
        Açıldığında yapıştırdığınız mesaj metni — telefon, e-posta ve tanınan kişi adları gizlenerek — ayrıştırma için yurt dışındaki AI
        hizmetine gönderilir. Kataloğunuz, fiyatlarınız ve müşteri bilgileriniz gönderilmez; satırlar yalnızca kataloğunuzla, sunucuda
        eşleştirilir ve sipariş sizin onayınız olmadan oluşmaz.
      </p>
      {canManage ? (
        <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
          <p className="text-caption text-fg-muted">Bu tercih işletmenin tamamı için geçerlidir, kayıt altına alınır ve her zaman kapatılabilir.</p>
          <Button icon={<ShieldCheck className="size-4" />} loading={busy} onClick={() => void enable()}>
            AI ayrıştırmayı aç
          </Button>
        </div>
      ) : (
        <p role="note" className="text-body text-fg-muted">
          Bu ayarı işletme sahibi veya yöneticisi açabilir. O zamana kadar siparişi elle oluşturabilirsiniz.{" "}
          <Link href={NEW_ORDER_HREF} className="text-brand-fg underline underline-offset-2">
            Yeni sipariş
          </Link>
        </p>
      )}
    </section>
  );
}

type SubmitAlert = { tone: "error" | "warning"; title: string; message: string; parseId?: string; manual?: boolean };

function SubmitForm({ limits, onOptInRequired }: { limits: MessageLimits; onOptInRequired: () => void }) {
  const uid = useId();
  const { api } = useSession();
  const router = useRouter();
  const [customer, setCustomer] = useState<{ id: string; name: string; code: string } | null>(null);
  const [text, setText] = useState("");
  const [textError, setTextError] = useState<string | undefined>();
  const [alert, setAlert] = useState<SubmitAlert | null>(null);
  const [phase, setPhase] = useState<"idle" | "sending" | "parsing">("idle");
  const [keys] = useState(() => new IdempotencyKeys());
  const alertRef = useRef<HTMLDivElement>(null);
  const polling = useRef<AbortController | null>(null);
  const submitting = phase !== "idle";

  useEffect(() => {
    if (alert) alertRef.current?.focus();
  }, [alert]);

  // Leaving the page stops polling (the parse continues on the server and stays listed).
  useEffect(() => () => polling.current?.abort(), []);

  const length = text.trim().length;
  const over = length > limits.maxChars;

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (submitting) return;
    const invalid = messageError(text, limits);
    setTextError(invalid ?? undefined);
    if (invalid) return;
    const body = { text: text.trim(), customerId: customer?.id ?? null };
    setPhase("sending");
    setAlert(null);
    let parseId: string | null = null;
    try {
      const accepted = await submitOrderParse(api, body, keys.keyFor(body));
      keys.reset();
      parseId = accepted.id;
      setPhase("parsing");
      const controller = new AbortController();
      polling.current = controller;
      const status = await waitForOrderParse(api, accepted.id, { signal: controller.signal, initialDelayMs: Number(accepted.pollAfterMs) });
      if (status.state === "succeeded") {
        router.push(orderParseHref(accepted.id));
        return;
      }
      setAlert({ tone: "error", title: "Mesaj ayrıştırılamadı", message: parseFailureMessage(status.failureCode), manual: true });
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") return;
      if (error instanceof ParseStillPendingError) {
        setAlert({
          tone: "warning",
          title: "Ayrıştırma sürüyor",
          message: "Mesaj hâlâ işleniyor. Birazdan aşağıdaki listeden veya bu bağlantıdan incelemeye devam edebilirsiniz.",
          parseId: error.parseId,
        });
      } else if (parseId) {
        // Accepted, but polling failed (network): the parse continues on the server.
        setAlert({ tone: "warning", title: "Durum alınamadı", message: assistantErrorMessage(error), parseId });
      } else if (isOptInRequired(error)) {
        onOptInRequired();
      } else if (isRateLimited(error)) {
        setAlert({ tone: "warning", title: "Saatlik ayrıştırma sınırı doldu", message: rateLimitMessage(error) });
      } else if (error instanceof ApiError && error.status === 422 && error.fieldErrors.text?.length) {
        setTextError(translateTextError(error.fieldErrors.text[0]));
      } else {
        setAlert({ tone: "error", title: "Mesaj ayrıştırılamadı", message: assistantErrorMessage(error) });
      }
    }
    setPhase("idle");
  };

  return (
    <form noValidate onSubmit={submit} aria-label="WhatsApp mesajı" className="flex flex-col gap-4 rounded-lg border border-line bg-card p-4 shadow-e1 sm:p-6">
      {alert ? (
        <FormAlert
          ref={alertRef}
          tone={alert.tone}
          title={alert.title}
          actions={
            alert.parseId ? (
              <Link href={orderParseHref(alert.parseId)} className={buttonClasses("secondary", "sm")}>
                İncelemeyi aç
              </Link>
            ) : alert.manual ? (
              <Link href={NEW_ORDER_HREF} className={buttonClasses("secondary", "sm")}>
                Siparişi elle oluştur
              </Link>
            ) : undefined
          }
        >
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
          hint="Mesajı olduğu gibi yapıştırın. Telefon, e-posta ve tanınan kişi adları ayrıştırmadan önce gizlenir; mesajdaki fiyatlar kullanılmaz."
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
          {length.toLocaleString("tr-TR")} / {limits.maxChars.toLocaleString("tr-TR")} karakter
        </p>
      </div>
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
        <p className="text-caption text-fg-muted">AI yalnızca öneri yapar: fiyat, stok ve ürün kodu uydurmaz; sipariş sizin onayınız olmadan oluşmaz.</p>
        <Button type="submit" icon={<ScanText className="size-4" />} loading={submitting}>
          Mesajı ayrıştır
        </Button>
      </div>
      <p role="status" className={phase === "parsing" ? "text-body text-fg-muted" : "sr-only"}>
        {phase === "parsing" ? "Mesaj ayrıştırılıyor ve kataloğunuzla eşleştiriliyor…" : ""}
      </p>
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
