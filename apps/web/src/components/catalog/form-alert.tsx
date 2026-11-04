import type { ReactNode, Ref } from "react";
import { CircleAlert, CircleCheck, Info } from "lucide-react";
import { cn } from "@/lib/cn";

const tones = {
  error: { Icon: CircleAlert, className: "border-danger-border bg-danger-bg text-danger-fg", role: "alert" },
  warning: { Icon: CircleAlert, className: "border-warning-border bg-warning-bg text-warning-fg", role: "alert" },
  info: { Icon: Info, className: "border-info-border bg-info-bg text-info-fg", role: "status" },
  success: { Icon: CircleCheck, className: "border-success-border bg-success-bg text-success-fg", role: "status" },
} as const;

/** Inline page/form message. Critical errors stay on the page (not in a toast). */
export function FormAlert({
  tone,
  title,
  children,
  actions,
  ref,
}: {
  tone: keyof typeof tones;
  title: string;
  children?: ReactNode;
  actions?: ReactNode;
  ref?: Ref<HTMLDivElement>;
}) {
  const { Icon, className, role } = tones[tone];
  return (
    <div ref={ref} role={role} tabIndex={-1} className={cn("flex gap-3 rounded-md border px-4 py-3 text-body", className)}>
      <Icon className="mt-0.5 size-5 shrink-0" aria-hidden="true" />
      <div className="flex flex-1 flex-col gap-1">
        <p className="font-semibold">{title}</p>
        {children ? <div>{children}</div> : null}
        {actions ? <div className="mt-2 flex flex-wrap gap-2">{actions}</div> : null}
      </div>
    </div>
  );
}
