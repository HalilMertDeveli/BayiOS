"use client";

import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import type { CustomField } from "@/lib/catalog/api";
import type { AttributeFormValue } from "@/lib/catalog/attributes";
import { fieldId } from "@/lib/catalog/errors";
import { cn } from "@/lib/cn";

export type AttributeInputProps = {
  field: CustomField;
  value: AttributeFormValue;
  onChange: (value: AttributeFormValue) => void;
  /** Error key (`attributes.guc`, `variants[0].attributes.renk`); also the DOM id source. */
  errorKey: string;
  error?: string;
  disabled?: boolean;
};

const BOOLEAN_OPTIONS = [
  { value: "true", label: "Evet" },
  { value: "false", label: "Hayır" },
];

/** One custom-field input, chosen by the definition's data type. */
export function AttributeInput({ field, value, onChange, errorKey, error, disabled }: AttributeInputProps) {
  const id = fieldId(errorKey);
  const common = { id, label: field.label, required: field.required, error, disabled };
  const unitSuffix = field.unit ? <span className="text-caption">{field.unit}</span> : undefined;
  const text = typeof value === "string" ? value : "";

  switch (field.dataType) {
    case "number":
    case "integer":
      return (
        <Input
          {...common}
          inputMode={field.dataType === "integer" ? "numeric" : "decimal"}
          value={text}
          onChange={(e) => onChange(e.target.value)}
          suffix={unitSuffix}
          hint={field.dataType === "integer" ? "Tam sayı" : "Ondalık için virgül kullanabilirsiniz"}
          autoComplete="off"
        />
      );
    case "boolean":
      return <Select {...common} options={BOOLEAN_OPTIONS} placeholder="Seçilmedi" value={text} onChange={(e) => onChange(e.target.value)} />;
    case "enum":
      return (
        <Select
          {...common}
          options={field.options.map((o) => ({ value: o, label: o }))}
          placeholder="Seçiniz"
          value={text}
          onChange={(e) => onChange(e.target.value)}
        />
      );
    case "date":
      return <Input {...common} type="date" value={text} onChange={(e) => onChange(e.target.value)} />;
    case "multi_enum": {
      const chosen = Array.isArray(value) ? value : [];
      return (
        <fieldset
          id={id}
          tabIndex={-1}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? `${id}-error` : undefined}
          className="flex flex-col gap-1.5"
          disabled={disabled}
        >
          <legend className="mb-1.5 text-body font-medium text-fg">
            {field.label}
            {field.required ? (
              <span className="text-danger-fg" aria-hidden="true">
                {" "}
                *
              </span>
            ) : null}
            {field.required ? <span className="sr-only"> (zorunlu)</span> : null}
          </legend>
          <div className={cn("flex flex-wrap gap-x-4 gap-y-2 rounded-md border px-3 py-2", error ? "border-danger-fg" : "border-line")}>
            {field.options.map((option) => (
              <label key={option} className="inline-flex min-h-6 items-center gap-2 text-body text-fg">
                <input
                  type="checkbox"
                  className="size-4 accent-(--bos-brand-primary)"
                  checked={chosen.includes(option)}
                  onChange={(e) =>
                    onChange(e.target.checked ? [...chosen, option] : chosen.filter((c) => c !== option))
                  }
                />
                {option}
              </label>
            ))}
          </div>
          {error ? (
            <p id={`${id}-error`} className="text-caption text-danger-fg">
              {error}
            </p>
          ) : null}
        </fieldset>
      );
    }
    case "range": {
      const range = typeof value === "object" && !Array.isArray(value) ? value : { min: "", max: "" };
      return (
        <fieldset
          id={id}
          tabIndex={-1}
          aria-describedby={error ? `${id}-error` : undefined}
          className="flex flex-col gap-1.5"
          disabled={disabled}
        >
          <legend className="mb-1.5 text-body font-medium text-fg">
            {field.label}
            {field.unit ? <span className="font-normal text-fg-muted"> ({field.unit})</span> : null}
            {field.required ? (
              <span className="text-danger-fg" aria-hidden="true">
                {" "}
                *
              </span>
            ) : null}
            {field.required ? <span className="sr-only"> (zorunlu)</span> : null}
          </legend>
          <div className="grid grid-cols-2 gap-2">
            <Input
              id={`${id}-min`}
              label={`${field.label} en az`}
              hideLabel
              placeholder="En az"
              inputMode="decimal"
              value={range.min}
              onChange={(e) => onChange({ ...range, min: e.target.value })}
              aria-invalid={error ? true : undefined}
              autoComplete="off"
            />
            <Input
              id={`${id}-max`}
              label={`${field.label} en çok`}
              hideLabel
              placeholder="En çok"
              inputMode="decimal"
              value={range.max}
              onChange={(e) => onChange({ ...range, max: e.target.value })}
              aria-invalid={error ? true : undefined}
              autoComplete="off"
            />
          </div>
          {error ? (
            <p id={`${id}-error`} className="text-caption text-danger-fg">
              {error}
            </p>
          ) : null}
        </fieldset>
      );
    }
    default:
      return <Input {...common} value={text} onChange={(e) => onChange(e.target.value)} maxLength={500} autoComplete="off" />;
  }
}
