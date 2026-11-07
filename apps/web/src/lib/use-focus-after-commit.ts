"use client";

import { useCallback, useEffect, useRef, useState } from "react";

type Target = () => { focus(options?: FocusOptions): void } | null | undefined;

/**
 * Moves focus to an element once React has put it on screen (#152).
 *
 * `requestAnimationFrame(() => ref.current?.focus())` right after a state update is not enough when the update comes
 * after an `await`: React commits it on a later task, and on a slow device a frame can run first. The alert is not
 * mounted (or the input is still disabled) yet, so focus stays on <body> and a keyboard / screen reader user loses
 * their place. This hook asks for focus with a state change of its own, batched with the update that shows the
 * element, and focuses in an effect after that commit (the pattern of reports/export-buttons.tsx).
 *
 * Usage: `const focusAfterCommit = useFocusAfterCommit(); … setError(m); focusAfterCommit(() => alertRef.current);`
 * Each call focuses again (the same error shown twice still moves focus back to it).
 */
export function useFocusAfterCommit(): (target: Target) => void {
  const [request, setRequest] = useState(0);
  const target = useRef<Target | null>(null);

  useEffect(() => {
    const get = target.current;
    if (!get) return;
    target.current = null;
    get()?.focus();
  }, [request]);

  return useCallback((next: Target) => {
    target.current = next;
    setRequest((n) => n + 1);
  }, []);
}
