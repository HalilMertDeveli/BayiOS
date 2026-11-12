"use client";

import { useId } from "react";
import { CircleAlert, CircleCheck, CircleHelp, CircleX, RotateCcw, Trash2 } from "@/components/ui/icons";
import { VariantScanner } from "@/components/inventory/variant-scanner";
import { Badge, type BadgeTone } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox-field";
import { Input } from "@/components/ui/input";
import type { OrderParseLine } from "@/lib/assistant/api";
import { confidenceText, matchMethodLabels, matchStatusLabels, unitLabel } from "@/lib/assistant/labels";
import { lineIssues, type ReviewAction, type ReviewLine } from "@/lib/assistant/review";
import { cn } from "@/lib/cn";

const STATUS_TONE: Record<string, { tone: BadgeTone; Icon: typeof CircleCheck }> = {
  matched: { tone: "success", Icon: CircleCheck },
  ambiguous: { tone: "warning", Icon: CircleHelp },
  unmatched: { tone: "danger", Icon: CircleX },
};

const SOURCE_TEXT = {
  proposed: "Önerilen eşleşme",
  candidate: "Adaylardan seçildi",
  manual: "Katalogdan seçildi",
} as const;

/**
 * One parsed line: what the message said (plain text), the catalog choice and the quantity. A matched line shows its
 * proposal to be confirmed; an ambiguous line lists its candidates with nothing selected; an unmatched line offers a
 * catalog search. No price or stock is shown anywhere here (they are resolved by the API once the draft exists).
 */
export function ReviewLineCard({
  line,
  review,
  dispatch,
  serverErrors,
  disabled,
  unitName = unitLabel,
}: {
  line: OrderParseLine;
  review: ReviewLine;
  dispatch: (action: ReviewAction) => void;
  serverErrors?: readonly string[];
  disabled?: boolean;
  /** Catalog unit code → display name ("takim" → "Takım", useUnitNames); defaults to the message-unit labels. */
  unitName?: (code: string | null | undefined) => string | null;
}) {
  const headingId = useId();
  const n = review.lineNo;
  const status = STATUS_TONE[line.matchStatus] ?? STATUS_TONE.unmatched;
  const issues = lineIssues(review);
  const quantityIssue = issues.find((i) => i.kind === "quantity");
  const choiceIssue = issues.find((i) => i.kind !== "quantity");
  const messageUnit = unitLabel(line.unit);
  const proposal = review.variant?.source === "proposed" ? line.candidates.find((c) => c.variantId === review.variant?.id) : undefined;
  const ready = review.include && issues.length === 0;

  return (
    <li>
      <section
        aria-labelledby={headingId}
        className={cn(
          "flex flex-col gap-3 rounded-lg border bg-card p-4 shadow-e1",
          !review.include ? "border-dashed border-line opacity-80" : ready ? "border-success-border" : "border-line-strong",
        )}
      >
        <div className="flex flex-wrap items-center gap-2">
          <h3 id={headingId} className="text-h3 text-fg">
            Satır {n}
          </h3>
          <Badge tone={status.tone} icon={<status.Icon className="size-3.5" aria-hidden="true" />}>
            {matchStatusLabels[line.matchStatus] ?? line.matchStatus}
          </Badge>
          {!review.include ? <Badge tone="neutral">Siparişe eklenmeyecek</Badge> : ready ? <Badge tone="success">Hazır</Badge> : null}
        </div>

        <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 text-body">
          <dt className="text-fg-muted">Mesajda</dt>
          <dd className="break-words whitespace-pre-wrap text-fg">“{line.rawText}”</dd>
          <dt className="text-fg-muted">Ürün ifadesi</dt>
          <dd className="break-words text-fg">{line.productText || "—"}</dd>
          {line.productCode ? (
            <>
              <dt className="text-fg-muted">Kod</dt>
              <dd className="font-mono text-fg">{line.productCode}</dd>
            </>
          ) : null}
          {line.brandModel ? (
            <>
              <dt className="text-fg-muted">Marka / model</dt>
              <dd className="text-fg">{line.brandModel}</dd>
            </>
          ) : null}
          {line.notes ? (
            <>
              <dt className="text-fg-muted">Not</dt>
              <dd className="break-words text-fg">{line.notes}</dd>
            </>
          ) : null}
          <dt className="text-fg-muted">Okuma güveni</dt>
          <dd className="text-fg">{confidenceText(line.parseConfidence)}</dd>
        </dl>

        {!review.include ? (
          <div className="flex flex-wrap items-center gap-2">
            <p className="text-body text-fg-muted">Bu satır taslağa eklenmeyecek.</p>
            <Button
              size="sm"
              variant="secondary"
              icon={<RotateCcw className="size-4" />}
              disabled={disabled}
              onClick={() => dispatch({ type: "include", lineNo: n, include: true })}
            >
              Satırı geri al
            </Button>
          </div>
        ) : (
          <>
            {review.variant ? (
              <div className="flex flex-col gap-2 rounded-md border border-line bg-sunken p-3">
                <p className="text-caption text-fg-muted">
                  {SOURCE_TEXT[review.variant.source]}
                  {proposal ? ` · ${matchMethodLabels[proposal.method] ?? proposal.method} · eşleşme güveni ${confidenceText(line.matchConfidence)}` : ""}
                </p>
                <p className="flex flex-wrap items-baseline gap-x-2">
                  <span className="font-mono text-body font-medium text-fg">{review.variant.sku}</span>
                  <span className="text-body text-fg">{review.variant.name ?? ""}</span>
                  {review.variant.unit ? <span className="text-caption text-fg-muted">Birim: {unitName(review.variant.unit)}</span> : null}
                </p>
                {review.variant.source === "proposed" ? (
                  <CheckboxField
                    label={`Satır ${n}: önerilen ürünü onaylıyorum`}
                    checked={review.confirmed}
                    disabled={disabled}
                    onChange={(confirmed) => dispatch({ type: "confirmProposal", lineNo: n, confirmed })}
                  />
                ) : null}
                <div>
                  <Button size="sm" variant="ghost" disabled={disabled} onClick={() => dispatch({ type: "clearVariant", lineNo: n })}>
                    Başka ürün seç
                  </Button>
                </div>
              </div>
            ) : (
              <div className="flex flex-col gap-3">
                {line.candidates.length > 0 ? (
                  <fieldset className="flex flex-col gap-2">
                    <legend className="mb-1 text-body font-medium text-fg">
                      {line.matchStatus === "ambiguous" ? `Satır ${n} için aday ürünler — birini seçin` : `Satır ${n} için diğer adaylar`}
                    </legend>
                    {line.candidates.map((c) => (
                      <label
                        key={c.variantId}
                        className="flex min-h-12 cursor-pointer items-center gap-3 rounded-md border border-line-strong bg-card px-3 py-2 hover:bg-sunken has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-(--bos-brand-primary)"
                      >
                        <input
                          type="radio"
                          name={`line-${n}-candidate`}
                          value={c.variantId}
                          // Never pre-selected: nothing is checked until the person picks one (ADR 0010).
                          checked={false}
                          disabled={disabled}
                          className="size-5 accent-(--bos-brand-primary)"
                          onChange={() =>
                            dispatch({ type: "choose", lineNo: n, source: "candidate", variant: { id: c.variantId, sku: c.sku, name: c.name, unit: c.unit } })
                          }
                        />
                        <span className="flex flex-col">
                          <span className="font-mono text-body text-fg">{c.sku}</span>
                          <span className="text-caption text-fg-muted">
                            {c.name} · {unitName(c.unit)} · {matchMethodLabels[c.method] ?? c.method} {confidenceText(c.score)}
                          </span>
                        </span>
                      </label>
                    ))}
                  </fieldset>
                ) : null}
                <VariantScanner
                  label={`Satır ${n}: katalogda ara`}
                  hint="SKU, barkod veya ürün adının bir kısmı."
                  disabled={disabled}
                  pickedText={(v) => `Satır ${n}: ${v.sku} seçildi.`}
                  onPick={(v) => dispatch({ type: "choose", lineNo: n, source: "manual", variant: { id: v.id, sku: v.sku, name: v.name, unit: null } })}
                />
              </div>
            )}

            <div className="flex flex-col gap-1 sm:max-w-xs">
              <Input
                label={`Satır ${n} miktarı`}
                inputMode="decimal"
                autoComplete="off"
                value={review.quantityText}
                disabled={disabled}
                suffix={messageUnit ? <span className="text-caption text-fg-muted">{messageUnit}</span> : undefined}
                error={review.quantityText.trim() && quantityIssue ? quantityIssue.message : undefined}
                hint={
                  line.quantity === null || line.quantity === undefined
                    ? "Mesajda miktar yok; siz girin."
                    : messageUnit
                      ? `Mesajdaki birim: ${messageUnit}. Siparişte ürünün kendi birimi kullanılır.`
                      : undefined
                }
                onChange={(e) => dispatch({ type: "quantity", lineNo: n, text: e.target.value })}
              />
              {line.unitMismatch ? (
                <p className="flex items-start gap-1 text-caption text-warning-fg">
                  <CircleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
                  Mesajdaki birim ({messageUnit ?? "?"}) önerilen ürünün birimiyle aynı değil; miktarı ürünün birimine göre girin.
                </p>
              ) : null}
            </div>

            {choiceIssue || (quantityIssue && !review.quantityText.trim()) ? (
              <p className="text-caption text-fg-muted">
                Bekleyen: {[choiceIssue?.message, !review.quantityText.trim() ? quantityIssue?.message : null].filter(Boolean).join(" ")}
              </p>
            ) : null}
            {serverErrors?.length ? (
              <ul role="alert" className="list-disc pl-5 text-caption text-danger-fg">
                {serverErrors.map((m, i) => (
                  <li key={i}>{m}</li>
                ))}
              </ul>
            ) : null}

            <div>
              <Button
                size="sm"
                variant="ghost"
                icon={<Trash2 className="size-4" />}
                disabled={disabled}
                onClick={() => dispatch({ type: "include", lineNo: n, include: false })}
              >
                Satır {n}: siparişten çıkar
              </Button>
            </div>
          </>
        )}
      </section>
    </li>
  );
}
