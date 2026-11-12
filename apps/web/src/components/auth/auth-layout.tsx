import type { ReactNode } from "react";
import Link from "next/link";
import { CircleAlert, CircleCheck } from "@/components/ui/icons";
import { BrandMark } from "@/components/ui/brand-mark";
import { cn } from "@/lib/cn";

const HIGHLIGHTS = [
  "Sipariş onaylandığında stok otomatik ayrılır; satılabilir miktar her an doğru.",
  "Barkodla toplama, sevkiyat, transfer ve sayım tek akışta.",
  "WhatsApp mesajından sipariş taslağı; son onay her zaman sizde.",
];

/**
 * Sign-in, sign-up, password and onboarding screens. From lg up a petrol brand panel sits beside the form (decorative
 * copper accent only); below lg it is a centered single column with the brand mark on top.
 */
export function AuthLayout({ children, wide = false }: { children: ReactNode; wide?: boolean }) {
  return (
    <div className="flex min-h-dvh bg-page">
      <aside
        aria-label="BayiOS hakkında"
        className="relative hidden w-[26rem] shrink-0 flex-col justify-between overflow-hidden bg-brand-panel p-10 text-on-brand-panel lg:flex xl:w-[30rem]"
      >
        <Link href="/" className="flex w-fit items-center gap-2 rounded-md text-h3 text-on-brand-panel">
          <BrandMark className="ring-1 ring-on-brand-panel/30" />
          BayiOS
        </Link>
        <div className="flex flex-col gap-6">
          <span aria-hidden="true" className="h-1 w-12 rounded-full bg-accent" />
          <p className="text-display tracking-tight">Stok, depo ve siparişleriniz tek yerde.</p>
          <ul className="flex flex-col gap-3">
            {HIGHLIGHTS.map((text) => (
              <li key={text} className="flex gap-3 text-body-lg text-on-brand-panel-muted">
                <CircleCheck className="mt-1 size-5 shrink-0 text-accent" aria-hidden="true" />
                {text}
              </li>
            ))}
          </ul>
        </div>
        <p className="text-caption text-on-brand-panel-muted">Toptancılar, distribütörler ve bayi ağları için B2B stok ve sipariş yönetimi.</p>
        {/* Decorative rings in the corner — the brand's "warehouse grid" motif, purely visual. */}
        <span aria-hidden="true" className="pointer-events-none absolute -right-24 -bottom-24 size-72 rounded-full border-[28px] border-on-brand-panel/5" />
        <span aria-hidden="true" className="pointer-events-none absolute -top-16 -right-16 size-40 rounded-full border-[16px] border-accent/15" />
      </aside>
      <div className="flex min-w-0 flex-1 flex-col items-center px-4 py-10 sm:py-16">
        <header className="mb-8 lg:hidden">
          <Link href="/" className="flex items-center gap-2 rounded-md text-h3 text-fg">
            <BrandMark />
            BayiOS
          </Link>
        </header>
        <main id="main" className={cn("w-full lg:my-auto", wide ? "max-w-2xl" : "max-w-md")}>
          {children}
        </main>
      </div>
    </div>
  );
}

export function AuthCard({ title, description, children }: { title: string; description?: ReactNode; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-6 rounded-xl border border-line bg-card p-6 shadow-e2 sm:p-8">
      <div className="flex flex-col gap-1">
        <h1 className="text-h1 text-fg">{title}</h1>
        {description ? <p className="text-body text-fg-muted">{description}</p> : null}
      </div>
      {children}
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
