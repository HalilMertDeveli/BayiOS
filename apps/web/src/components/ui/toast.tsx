"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { CircleAlert, CircleCheck, Info, X } from "lucide-react";
import { cn } from "@/lib/cn";

export type ToastTone = "info" | "success" | "error";

export type ToastOptions = {
  title: string;
  description?: string;
  tone?: ToastTone;
  /** Optional inline action, typically "Geri al". Extends the duration to 8 s. */
  action?: { label: string; onAction: () => void };
  /** Override the auto-dismiss duration in ms. Defaults: 5000, or 8000 with an action. */
  duration?: number;
};

type ToastItem = ToastOptions & { id: number };

type ToastContextValue = {
  toast: (options: ToastOptions) => number;
  dismiss: (id: number) => void;
};

const ToastContext = createContext<ToastContextValue | null>(null);

export function useToast(): ToastContextValue {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error("useToast must be used inside <ToastProvider>.");
  return ctx;
}

const toneMeta = {
  info: { Icon: Info, className: "text-info-fg", accent: "border-l-info-fg" },
  success: { Icon: CircleCheck, className: "text-success-fg", accent: "border-l-success-fg" },
  error: { Icon: CircleAlert, className: "text-danger-fg", accent: "border-l-danger-fg" },
} as const;

/**
 * Toasts are for non-critical feedback only; critical errors belong inline on the page.
 * The live region is always mounted so screen readers announce new toasts. Timers pause
 * while the toast is hovered or focused (WCAG 2.2.1).
 */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastItem[]>([]);
  const nextId = useRef(1);

  const dismiss = useCallback((id: number) => {
    setToasts((list) => list.filter((t) => t.id !== id));
  }, []);

  const toast = useCallback((options: ToastOptions) => {
    const id = nextId.current++;
    setToasts((list) => [...list.slice(-4), { ...options, id }]);
    return id;
  }, []);

  const value = useMemo(() => ({ toast, dismiss }), [toast, dismiss]);

  return (
    <ToastContext.Provider value={value}>
      {children}
      <section
        aria-label="Bildirimler"
        className="pointer-events-none fixed right-0 bottom-0 z-(--bos-z-toast) flex w-full flex-col items-end gap-2 p-4 sm:max-w-sm"
      >
        <ol aria-live="polite" aria-relevant="additions" className="flex w-full flex-col gap-2">
          {toasts.map((t) => (
            <ToastView key={t.id} item={t} dismiss={dismiss} />
          ))}
        </ol>
      </section>
    </ToastContext.Provider>
  );
}

function ToastView({ item, dismiss }: { item: ToastItem; dismiss: (id: number) => void }) {
  const { id, title, description, tone = "info", action } = item;
  const onDismiss = useCallback(() => dismiss(id), [dismiss, id]);
  const duration = item.duration ?? (action ? 8000 : 5000);
  const [paused, setPaused] = useState(false);
  const remaining = useRef(duration);
  const { Icon, className: iconClass, accent } = toneMeta[tone];

  useEffect(() => {
    if (paused) return;
    const startedAt = Date.now();
    const timer = window.setTimeout(onDismiss, remaining.current);
    return () => {
      window.clearTimeout(timer);
      remaining.current -= Date.now() - startedAt;
    };
  }, [paused, onDismiss]);

  return (
    <li
      role={tone === "error" ? "alert" : undefined}
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
      onFocus={() => setPaused(true)}
      onBlur={() => setPaused(false)}
      className={cn("pointer-events-auto flex w-full items-start gap-3 rounded-lg border border-l-4 border-line bg-raised p-4 text-fg shadow-e4", accent)}
    >
      <Icon className={cn("mt-0.5 size-5 shrink-0", iconClass)} />
      <div className="flex min-w-0 flex-1 flex-col gap-1">
        <p className="text-body font-semibold">{title}</p>
        {description ? <p className="text-body text-fg-muted">{description}</p> : null}
        {action ? (
          <button
            type="button"
            className="self-start rounded-sm text-body font-semibold text-link underline-offset-2 hover:underline"
            onClick={() => {
              action.onAction();
              onDismiss();
            }}
          >
            {action.label}
          </button>
        ) : null}
      </div>
      <button
        type="button"
        onClick={onDismiss}
        aria-label="Bildirimi kapat"
        className="-mt-1 -mr-1 inline-flex size-7 shrink-0 items-center justify-center rounded-md text-fg-muted hover:bg-sunken hover:text-fg"
      >
        <X className="size-4" />
      </button>
    </li>
  );
}
