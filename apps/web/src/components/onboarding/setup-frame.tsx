"use client";

import { useId, type ReactNode, type RefObject } from "react";
import type { FactsState } from "./setup-checklist";

/** Card of one step: focusable heading (focus moves here on step change), body and the step's own footer. */
export function StepFrame({
  title,
  description,
  headingRef,
  children,
}: {
  title: string;
  description?: ReactNode;
  headingRef: RefObject<HTMLHeadingElement | null>;
  children: ReactNode;
}) {
  // Unique per instance: a hidden (kept-alive) previous route may still hold another step card.
  const headingId = `${useId()}-step`;
  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-5 rounded-xl border border-line bg-card p-4 shadow-e1 sm:p-6">
      <div className="flex flex-col gap-1">
        <h2 id={headingId} ref={headingRef} tabIndex={-1} className="text-h2 text-fg focus:outline-none">
          {title}
        </h2>
        {description ? <div className="text-body text-fg-secondary">{description}</div> : null}
      </div>
      {children}
    </section>
  );
}

export type StepProps = {
  headingRef: RefObject<HTMLHeadingElement | null>;
  /** Go to the next step (after saving, or when skipped). */
  onNext: () => void;
  onBack: (() => void) | null;
  /** Re-reads the progress facts (after a write). */
  onChanged: () => void;
  facts: FactsState;
};
