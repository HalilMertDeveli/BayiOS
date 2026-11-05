"use client";

import { useState } from "react";
import { Search, UserRound } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { listCustomers, type Customer } from "@/lib/customers/api";
import { customerErrorMessage } from "@/lib/customers/errors";
import { useSession } from "@/lib/session/session-context";

/** The shown customer; name/code may still be loading (null) when only the id is known. */
export type PickedCustomer = { id: string; name: string | null; code: string | null };

/**
 * Customer search (name/code contains, exact tax no / phone / WhatsApp — CUS-05) with the matches as large buttons.
 * Once picked, shows the customer with a "remove" button. `status` limits the search (orders need active customers).
 */
export function CustomerPicker({
  value,
  error,
  onChange,
  label = "Müşteri (isteğe bağlı)",
  pickedLabel = "Müşteri",
  hint = "Boş bırakılırsa yalnızca varsayılan liste kullanılır.",
  status,
  disabled,
  id,
}: {
  value: PickedCustomer | null;
  error?: string;
  onChange: (c: Customer | null) => void;
  label?: string;
  pickedLabel?: string;
  hint?: string;
  /** API status filter, e.g. "active". */
  status?: string;
  disabled?: boolean;
  id?: string;
}) {
  const { api } = useSession();
  const [term, setTerm] = useState("");
  const [state, setState] = useState<{ kind: "idle" } | { kind: "searching" } | { kind: "results"; items: Customer[] } | { kind: "error"; message: string }>({ kind: "idle" });

  const search = async () => {
    const q = term.trim();
    if (!q) return;
    setState({ kind: "searching" });
    try {
      const params = new URLSearchParams({ q, limit: "10" });
      if (status) params.set("status", status);
      const page = await listCustomers(api, params.toString());
      setState({ kind: "results", items: page.items });
    } catch (err) {
      setState({ kind: "error", message: customerErrorMessage(err) });
    }
  };

  if (value) {
    return (
      <div className="flex flex-col gap-1.5">
        <span className="text-body font-medium text-fg">{pickedLabel}</span>
        <div className="flex flex-wrap items-center gap-2 rounded-md border border-line-strong p-3">
          <UserRound className="size-4 text-fg-muted" aria-hidden="true" />
          <span className="font-medium text-fg">{value.name ?? "Müşteri"}</span>
          {value.code ? <span className="font-mono text-caption text-fg-muted">{value.code}</span> : null}
          {disabled ? null : (
            <Button variant="ghost" size="sm" className="ml-auto" onClick={() => onChange(null)}>
              Müşteriyi kaldır
            </Button>
          )}
        </div>
        {error ? <p className="text-caption text-danger-fg">{error}</p> : null}
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-start gap-2">
        <Input
          id={id}
          label={label}
          className="flex-1"
          type="search"
          prefix={<Search className="size-4" />}
          value={term}
          error={error}
          hint={hint}
          autoComplete="off"
          disabled={disabled}
          onChange={(e) => setTerm(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              void search();
            }
          }}
        />
        <Button variant="secondary" className="mt-[1.625rem]" loading={state.kind === "searching"} disabled={disabled || !term.trim()} onClick={() => void search()}>
          Müşteri ara
        </Button>
      </div>
      {state.kind === "error" ? (
        <p role="alert" className="text-body text-danger-fg">
          {state.message}
        </p>
      ) : null}
      {state.kind === "results" ? (
        state.items.length === 0 ? (
          <p role="status" className="text-body text-fg-muted">
            Eşleşen müşteri yok.
          </p>
        ) : (
          <ul aria-label="Bulunan müşteriler" className="flex flex-col gap-2">
            {state.items.map((c) => (
              <li key={c.id}>
                <button
                  type="button"
                  onClick={() => {
                    onChange(c);
                    setState({ kind: "idle" });
                    setTerm("");
                  }}
                  className="flex min-h-12 w-full flex-col items-start justify-center rounded-md border border-line-strong bg-card px-4 py-2 text-left hover:bg-sunken"
                >
                  <span className="text-body text-fg">{c.name}</span>
                  <span className="font-mono text-caption text-fg-muted">{c.code}</span>
                </button>
              </li>
            ))}
          </ul>
        )
      ) : null}
    </div>
  );
}
