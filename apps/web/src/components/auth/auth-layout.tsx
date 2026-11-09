import type { ReactNode } from "react";
import Link from "next/link";
import { CircleAlert, CircleCheck } from "lucide-react";
import { BrandMark } from "@/components/ui/brand-mark";
import { cn } from "@/lib/cn";

/** Centered single-column layout for sign-in, sign-up, password and onboarding screens. */
export function AuthLayout({ children, wide = false }: { children: ReactNode; wide?: boolean }) {
  return (
    <div className="flex min-h-dvh flex-col items-center bg-sunken px-4 py-10">
      <Link href="/" className="mb-8 flex items-center gap-2 rounded-md text-h3 text-fg">
        <BrandMark />
        BayiOS
      </Link>
      <main id="main" className={cn("w-full", wide ? "max-w-2xl" : "max-w-md")}>
        {children}
      </main>
    </div>
  );
}

export function AuthCard({ title, description, children }: { title: string; description?: ReactNode; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-6 rounded-lg border border-line bg-card p-6 shadow-e1 sm:p-8">
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
