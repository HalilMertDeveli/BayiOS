"use client";

import { useEffect, useState } from "react";
import { getMeta, type Meta } from "@/lib/api/client";

type State =
  | { kind: "loading" }
  | { kind: "ready"; meta: Meta }
  | { kind: "error" };

export function ApiStatus() {
  const [state, setState] = useState<State>({ kind: "loading" });

  useEffect(() => {
    const controller = new AbortController();
    getMeta(controller.signal)
      .then((meta) => setState({ kind: "ready", meta }))
      .catch(() => {
        if (!controller.signal.aborted) setState({ kind: "error" });
      });
    return () => controller.abort();
  }, []);

  return (
    <p role="status" aria-live="polite" className="text-sm text-neutral-600 dark:text-neutral-300">
      {state.kind === "loading" && "API bağlantısı kontrol ediliyor…"}
      {state.kind === "ready" &&
        `API bağlı: ${state.meta.name} ${state.meta.version} (${state.meta.environment})`}
      {state.kind === "error" && "API'ye ulaşılamadı. Lütfen daha sonra tekrar deneyin."}
    </p>
  );
}
