"use client";

import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import type { CustomField } from "@/lib/catalog/api";
import { fieldId } from "@/lib/catalog/errors";
import { BARCODE_MAX, NAME_MAX, SKU_MAX, type EditableStatus, type VariantFormValues } from "@/lib/catalog/product-form";
import { AttributeInput } from "./attribute-input";

export type VariantFieldsProps = {
  values: VariantFormValues;
  onChange: (values: VariantFormValues) => void;
  variantFields: readonly CustomField[];
  errors: Record<string, string>;
  /** Error-key prefix: "variants[0]." on the create form, "" in the variant dialog. */
  prefix: string;
  disabled?: boolean;
  /** Hides the status select (archived variants keep their status). */
  showStatus?: boolean;
};

/** Error keys rendered by <VariantFields> (used to tell field errors from summary-only errors). */
export function variantErrorKeys(prefix: string, variantFields: readonly CustomField[]): string[] {
  return [
    `${prefix}sku`,
    `${prefix}barcode`,
    `${prefix}name`,
    `${prefix}status`,
    ...variantFields.map((f) => `${prefix}attributes.${f.key}`),
  ];
}

export function VariantFields({ values, onChange, variantFields, errors, prefix, disabled, showStatus = true }: VariantFieldsProps) {
  const set = <K extends keyof VariantFormValues>(key: K, value: VariantFormValues[K]) => onChange({ ...values, [key]: value });

  return (
    <div className="flex flex-col gap-3">
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <Input
          id={fieldId(`${prefix}sku`)}
          label="SKU (stok kodu)"
          required
          value={values.sku}
          maxLength={SKU_MAX}
          onChange={(e) => set("sku", e.target.value)}
          error={errors[`${prefix}sku`]}
          disabled={disabled}
          autoComplete="off"
          spellCheck={false}
        />
        <Input
          id={fieldId(`${prefix}barcode`)}
          label="Barkod"
          value={values.barcode}
          maxLength={BARCODE_MAX}
          onChange={(e) => set("barcode", e.target.value)}
          error={errors[`${prefix}barcode`]}
          hint="İsteğe bağlı. Boşluk içermemeli."
          disabled={disabled}
          autoComplete="off"
          inputMode="numeric"
          spellCheck={false}
        />
        <Input
          id={fieldId(`${prefix}name`)}
          label="Varyant adı"
          value={values.name}
          maxLength={NAME_MAX}
          onChange={(e) => set("name", e.target.value)}
          error={errors[`${prefix}name`]}
          hint="İsteğe bağlı, ör. Kırmızı / XL"
          disabled={disabled}
          autoComplete="off"
        />
        {showStatus ? (
          <Select
            id={fieldId(`${prefix}status`)}
            label="Varyant durumu"
            options={[
              { value: "active", label: "Aktif" },
              { value: "inactive", label: "Pasif" },
            ]}
            value={values.status}
            onChange={(e) => set("status", e.target.value as EditableStatus)}
            error={errors[`${prefix}status`]}
            disabled={disabled}
          />
        ) : null}
      </div>
      {variantFields.length > 0 ? (
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          {variantFields.map((field) => {
            const key = `${prefix}attributes.${field.key}`;
            return (
              <AttributeInput
                key={field.key}
                field={field}
                value={values.attributes[field.key]}
                onChange={(value) => set("attributes", { ...values.attributes, [field.key]: value })}
                errorKey={key}
                error={errors[key]}
                disabled={disabled}
              />
            );
          })}
        </div>
      ) : null}
    </div>
  );
}
