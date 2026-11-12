"use client";

import { useId, type ComponentProps } from "react";
import { ChevronDown } from "@/components/ui/icons";
import { cn } from "@/lib/cn";
import { Field, controlClasses, fieldDescribedBy, type FieldBaseProps } from "./field";

export type SelectOption = { value: string; label: string; disabled?: boolean };

export type SelectProps = Omit<ComponentProps<"select">, "children" | "multiple" | "size"> &
  FieldBaseProps & {
    /** md = 36 px (web default); lg = 48 px touch target for mobile/warehouse screens. */
    size?: "md" | "lg";
    options: readonly SelectOption[];
    /** Adds an empty first option (e.g. "Seçiniz"). */
    placeholder?: string;
  };

/**
 * Single-value select built on the native <select>: full keyboard, mobile picker and
 * screen-reader support for free. Searchable/async combobox is a separate, later component.
 */
export function Select({
  label,
  hint,
  error,
  hideLabel,
  options,
  placeholder,
  id: idProp,
  className,
  required,
  size = "md",
  ...rest
}: SelectProps) {
  const autoId = useId();
  const id = idProp ?? autoId;

  return (
    <Field id={id} label={label} hint={hint} error={error} hideLabel={hideLabel} required={required} className={className}>
      <div className="relative flex items-center">
        <select
          id={id}
          required={required}
          aria-invalid={error ? true : undefined}
          aria-describedby={fieldDescribedBy(id, hint, error)}
          className={cn(controlClasses, size === "lg" ? "h-12 pl-4" : "h-9 pl-3", "appearance-none pr-9", error ? "border-danger-fg" : "border-line-strong")}
          {...rest}
        >
          {placeholder !== undefined && <option value="">{placeholder}</option>}
          {options.map((o) => (
            <option key={o.value} value={o.value} disabled={o.disabled}>
              {o.label}
            </option>
          ))}
        </select>
        <ChevronDown className="pointer-events-none absolute right-3 size-4 text-fg-muted" />
      </div>
    </Field>
  );
}
