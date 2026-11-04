"use client";

import { useCallback, useSyncExternalStore } from "react";

const listeners = new Set<() => void>();
// Fallback when localStorage is unavailable: the preference lasts for this page load only.
const memory = new Map<string, boolean>();

function read(key: string, fallback: boolean): boolean {
  try {
    const value = window.localStorage.getItem(key);
    return value === null ? fallback : value === "1";
  } catch {
    return memory.get(key) ?? fallback;
  }
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  window.addEventListener("storage", listener);
  return () => {
    listeners.delete(listener);
    window.removeEventListener("storage", listener);
  };
}

/**
 * A UI preference (not data) kept in localStorage. The server render and first client
 * render use `fallback`, so there is no hydration mismatch.
 */
export function usePersistentBoolean(key: string, fallback = false): [boolean, (next: boolean) => void] {
  const value = useSyncExternalStore(
    subscribe,
    () => read(key, fallback),
    () => fallback,
  );

  const set = useCallback(
    (next: boolean) => {
      try {
        window.localStorage.setItem(key, next ? "1" : "0");
      } catch {
        memory.set(key, next);
      }
      listeners.forEach((l) => l());
    },
    [key],
  );

  return [value, set];
}
