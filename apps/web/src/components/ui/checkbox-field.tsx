import { useId, type ReactNode } from "react";

/** Labelled native checkbox with a 44 px touch row and an optional hint (linked via aria-describedby). */
export function CheckboxField({
  label,
  hint,
  checked,
  disabled,
  onChange,
}: {
  label: string;
  hint?: ReactNode;
  checked: boolean;
  disabled?: boolean;
  onChange: (value: boolean) => void;
}) {
  const hintId = useId();
  return (
    <div className="flex flex-col gap-1">
      <label className="inline-flex min-h-11 items-center gap-3 text-body text-fg">
        <input
          type="checkbox"
          className="size-5 accent-(--bos-brand-primary)"
          checked={checked}
          disabled={disabled}
          aria-describedby={hint ? hintId : undefined}
          onChange={(e) => onChange(e.target.checked)}
        />
        {label}
      </label>
      {hint ? (
        <p id={hintId} className="text-caption text-fg-muted">
          {hint}
        </p>
      ) : null}
    </div>
  );
}
