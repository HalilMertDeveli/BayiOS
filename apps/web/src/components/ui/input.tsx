"use client";

import { useId, type ComponentProps, type ReactNode } from "react";
import { cn } from "@/lib/cn";
import { Field, controlClasses, fieldDescribedBy, type FieldBaseProps } from "./field";

export type InputProps = Omit<ComponentProps<"input">, "prefix"> &
  FieldBaseProps & {
    /** Decorative content inside the control, before the text (e.g. a search icon). */
    prefix?: ReactNode;
    /** Content after the text (e.g. "₺" or a unit). */
    suffix?: ReactNode;
  };

export function Input({
  label,
  hint,
  error,
  hideLabel,
  prefix,
  suffix,
  id: idProp,
  className,
  required,
  ...rest
}: InputProps) {
  const autoId = useId();
  const id = idProp ?? autoId;

  return (
    <Field id={id} label={label} hint={hint} error={error} hideLabel={hideLabel} required={required} className={className}>
      <div className="relative flex items-center">
        {prefix ? (
          <span className="pointer-events-none absolute left-3 flex text-fg-muted" aria-hidden="true">
            {prefix}
          </span>
        ) : null}
        <input
          id={id}
          required={required}
          aria-invalid={error ? true : undefined}
          aria-describedby={fieldDescribedBy(id, hint, error)}
          className={cn(
            controlClasses,
            "h-9 px-3 read-only:bg-sunken",
            prefix ? "pl-9" : null,
            suffix ? "pr-9" : null,
            error ? "border-danger-fg" : "border-line-strong",
          )}
          {...rest}
        />
        {suffix ? <span className="absolute right-3 flex text-fg-muted">{suffix}</span> : null}
      </div>
    </Field>
  );
}
