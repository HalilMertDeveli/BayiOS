import type { ReactNode } from "react";
import Link from "next/link";
import { CircleAlert, CircleCheck, Mail } from "@/components/ui/icons";
import { BrandMark } from "@/components/ui/brand-mark";
import { cn } from "@/lib/cn";

const HIGHLIGHTS = [
  "Sipariş onaylandığında stok otomatik ayrılır; satılabilir miktar her an doğru.",
  "Barkodla toplama, sevkiyat, transfer ve sayım tek akışta.",
  "WhatsApp mesajından sipariş taslağı; son onay her zaman sizde.",
];

/** Decorative bars for the panel's product preview (UI kit sign-in): relative heights, purely visual. */
const PREVIEW_BARS = [38, 56, 44, 72, 60, 84, 66, 90, 58, 76, 48, 68];

/**
 * Sign-in, sign-up, password and onboarding screens (UI kit layout, #222): the form on the left, and from lg up a
 * floating deep-green brand panel on the right with a decorative product preview; below lg a single centred column.
 */
export function AuthLayout({ children, wide = false }: { children: ReactNode; wide?: boolean }) {
  return (
    <div className="flex min-h-dvh bg-page">
      <div className="flex min-w-0 flex-1 flex-col items-center px-4 py-10 sm:py-16">
        <header className="mb-8 lg:hidden">
          <Link href="/" className="flex items-center gap-2.5 rounded-md text-h2 font-bold text-fg">
            <BrandMark />
            BayiOS
          </Link>
        </header>
        <main id="main" className={cn("w-full lg:my-auto", wide ? "max-w-2xl" : "max-w-md")}>
          {children}
        </main>
      </div>
      <aside
        aria-label="BayiOS hakkında"
        className="relative m-3 hidden w-[44%] max-w-3xl shrink-0 flex-col overflow-hidden rounded-2xl bg-brand-panel p-10 text-on-brand-panel lg:flex"
      >
        {/* Warehouse-grid motif (UI kit panel), purely visual. */}
        <span
          aria-hidden="true"
          className="pointer-events-none absolute inset-0 bg-[linear-gradient(to_right,var(--bos-brand-on-panel)_1px,transparent_1px),linear-gradient(to_bottom,var(--bos-brand-on-panel)_1px,transparent_1px)] bg-size-[12.5%_12.5%] opacity-[0.06]"
        />
        <Link href="/" className="relative flex w-fit items-center gap-2.5 rounded-md text-h2 font-bold text-on-brand-panel">
          <BrandMark className="ring-1 ring-on-brand-panel/30" />
          BayiOS
        </Link>
        <div className="relative mt-12 flex flex-col items-center gap-4 text-center">
          <p className="max-w-lg text-display tracking-tight">Stok, depo ve siparişleriniz tek yerde.</p>
          <p className="max-w-lg text-body-lg text-on-brand-panel-muted">Toptancılar, distribütörler ve bayi ağları için B2B stok ve sipariş yönetimi.</p>
        </div>
        {/* Product preview card — decorative (aria-hidden), no real data. */}
        <div aria-hidden="true" className="relative mx-auto mt-10 w-full max-w-md">
          <div className="absolute inset-x-6 -bottom-3 h-full rounded-2xl bg-on-brand-panel/10" />
          <div className="relative flex flex-col gap-4 rounded-2xl bg-card p-5 text-fg shadow-e3">
            <div className="flex items-center justify-between">
              <span className="text-h3">Satış ve stok</span>
              <span className="rounded-md border border-line px-2 py-1 text-caption text-fg-muted">Bu ay</span>
            </div>
            <div className="flex h-28 items-end gap-2">
              {PREVIEW_BARS.map((h, i) => (
                <span key={i} className={cn("flex-1 rounded-sm", i === 7 ? "bg-chart-1" : "bg-chart-track")} style={{ height: `${h}%` }} />
              ))}
            </div>
          </div>
        </div>
        <ul className="relative mt-auto flex flex-col gap-3 pt-10">
          {HIGHLIGHTS.map((text) => (
            <li key={text} className="flex gap-3 text-body-lg text-on-brand-panel-muted">
              <CircleCheck className="mt-0.5 size-5 shrink-0 text-accent" aria-hidden="true" />
              {text}
            </li>
          ))}
        </ul>
      </aside>
    </div>
  );
}

export function AuthCard({
  title,
  description,
  icon,
  children,
}: {
  title: string;
  description?: ReactNode;
  /** Decorative icon in the round badge (UI kit). Defaults to an envelope. */
  icon?: ReactNode;
  children: ReactNode;
}) {
  return (
    <section className="overflow-hidden rounded-2xl border border-line bg-card p-2 shadow-e2">
      <div className="flex flex-col gap-6 rounded-xl border border-line bg-linear-to-b from-brand-subtle via-card to-card px-5 pt-8 pb-6 sm:px-8">
        <div className="flex flex-col items-center gap-4 text-center">
          <span aria-hidden="true" className="flex size-16 items-center justify-center rounded-full bg-linear-to-b from-accent to-brand text-on-brand shadow-button ring-8 ring-brand-bg">
            {icon ?? <Mail className="size-7" />}
          </span>
          <div className="flex flex-col gap-1">
            <h1 className="text-h1 text-fg">{title}</h1>
            {description ? <p className="text-body-lg text-fg-muted">{description}</p> : null}
          </div>
        </div>
        {children}
      </div>
    </section>
  );
}

/** Inline form-level message. Errors use role="alert" so they are announced when they appear. */
export function FormMessage({ tone, children }: { tone: "error" | "success"; children: ReactNode }) {
  const Icon = tone === "error" ? CircleAlert : CircleCheck;
  return (
    <div
      role={tone === "error" ? "alert" : "status"}
      className={cn(
        "flex gap-2 rounded-md border px-3 py-2 text-body",
        tone === "error" ? "border-danger-border bg-danger-bg text-danger-fg" : "border-success-border bg-success-bg text-success-fg",
      )}
    >
      <Icon className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
      <div>{children}</div>
    </div>
  );
}
