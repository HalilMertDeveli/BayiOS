"use client";

import { useId, type ComponentProps } from "react";
import { cn } from "@/lib/cn";
import { Field, controlClasses, fieldDescribedBy, type FieldBaseProps } from "./field";

export type TextareaProps = ComponentProps<"textarea"> & FieldBaseProps;

/** Multi-line text with the shared label / hint / error layout. */
export function Textarea({ label, hint, error, hideLabel, id: idProp, className, required, rows = 3, ...rest }: TextareaProps) {
  const autoId = useId();
  const id = idProp ?? autoId;
  return (
    <Field id={id} label={label} hint={hint} error={error} hideLabel={hideLabel} required={required} className={className}>
      <textarea
        id={id}
        rows={rows}
        required={required}
        aria-invalid={error ? true : undefined}
        aria-describedby={fieldDescribedBy(id, hint, error)}
        className={cn(controlClasses, "min-h-20 px-3 py-2 read-only:bg-sunken", error ? "border-danger-fg" : "border-line-strong")}
        {...rest}
      />
    </Field>
  );
}
