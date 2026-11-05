"use client";

import { useCallback, useLayoutEffect, useRef, useState } from "react";

/**
 * "New record" pages stay mounted after navigating away (Next.js keeps recent routes in React `<Activity>` with Cache
 * Components), so coming back would show the values of the record just created and invite a duplicate (#96).
 *
 * Call `markCreated()` after a successful create; when the page is hidden (Activity runs layout-effect cleanups), the
 * `generation` changes. Use it as the form's `key` so the next visit starts from an empty form. Abandoned, unsaved
 * input is kept (only a successful create resets). Pattern: node_modules/next/dist/docs/01-app/02-guides/preserving-ui-state.md.
 */
export function useResetAfterCreate(): { generation: number; markCreated: () => void } {
  const [generation, setGeneration] = useState(0);
  const created = useRef(false);

  useLayoutEffect(
    () => () => {
      if (!created.current) return;
      created.current = false;
      setGeneration((n) => n + 1);
    },
    [],
  );

  const markCreated = useCallback(() => {
    created.current = true;
  }, []);

  return { generation, markCreated };
}
