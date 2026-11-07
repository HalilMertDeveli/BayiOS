import { afterEach, describe, expect, it, vi } from "vitest";
import { useRef, useState } from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { frameBeforeCommit } from "@/test/frame-before-commit";
import { useFocusAfterCommit } from "./use-focus-after-commit";

/** A save that fails after an await and shows an alert (MOCK, no API). */
function FailingSave({ message = "Kaydedilemedi" }: { message?: string }) {
  const [error, setError] = useState<string | null>(null);
  const alertRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();
  const save = async () => {
    await Promise.resolve();
    setError(message);
    focusAfterCommit(() => alertRef.current);
  };
  return (
    <>
      <button type="button" onClick={() => void save()}>
        Kaydet
      </button>
      <button type="button">Başka</button>
      {error ? (
        <div ref={alertRef} role="alert" tabIndex={-1}>
          {error}
        </div>
      ) : null}
    </>
  );
}

afterEach(() => vi.restoreAllMocks());

describe("useFocusAfterCommit (#152)", () => {
  it("focuses an alert that mounts after an await, even if a frame runs before the commit", async () => {
    frameBeforeCommit();
    render(<FailingSave />);
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole("alert")));
  });

  it("the same error shown again moves focus back to it", async () => {
    render(<FailingSave />);
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole("alert")));
    await userEvent.click(screen.getByRole("button", { name: "Başka" }));
    await userEvent.click(screen.getByRole("button", { name: "Kaydet" }));
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole("alert")));
  });
});
