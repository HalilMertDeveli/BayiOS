import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { SessionContext } from "@/lib/session/session-context";
import { mockSession } from "@/test/session";
import { VariantScanner } from "./variant-scanner";

// MOCK lookups: the first one is held open until released, like a slow network while the scanner keeps scanning.
const release: Array<() => void> = [];
vi.mock("@/lib/inventory/api", () => ({
  findVariantByCode: vi.fn(
    (_api: unknown, code: string) =>
      new Promise((resolve) => {
        const answer = () => resolve({ id: `v-${code}`, sku: code, name: null, barcode: null });
        if (code === "SLOW-1") release.push(answer);
        else answer();
      }),
  ),
  searchVariants: vi.fn(async () => []),
}));

describe("VariantScanner (#136)", () => {
  it("a code scanned while the previous lookup is running is queued and added in order, not dropped", async () => {
    const onPick = vi.fn();
    render(
      <SessionContext value={mockSession("owner")}>
        <VariantScanner label="Barkod veya SKU okutun" onPick={onPick} pickedText={(v) => `${v.sku} eklendi.`} />
      </SessionContext>,
    );
    const input = screen.getByLabelText("Barkod veya SKU okutun") as HTMLInputElement;
    fireEvent.change(input, { target: { value: "SLOW-1" } });
    fireEvent.keyDown(input, { key: "Enter" });
    fireEvent.change(input, { target: { value: "FAST-2" } });
    fireEvent.keyDown(input, { key: "Enter" });
    // Queued: the field is free for the next scan.
    expect(input.value).toBe("");
    expect(onPick).not.toHaveBeenCalled();

    await waitFor(() => expect(release).toHaveLength(1));
    release[0]();
    await waitFor(() => expect(onPick).toHaveBeenCalledTimes(2));
    expect(onPick.mock.calls.map(([v]) => v.sku)).toEqual(["SLOW-1", "FAST-2"]);
    expect(screen.getByRole("status").textContent).toBe("FAST-2 eklendi.");
  });

  it("text typed while a lookup runs is not wiped when that lookup finishes", async () => {
    release.length = 0;
    const onPick = vi.fn();
    render(
      <SessionContext value={mockSession("owner")}>
        <VariantScanner label="Okut" onPick={onPick} />
      </SessionContext>,
    );
    const input = screen.getByLabelText("Okut") as HTMLInputElement;
    fireEvent.change(input, { target: { value: "SLOW-1" } });
    fireEvent.keyDown(input, { key: "Enter" });
    fireEvent.change(input, { target: { value: "NEXT-3" } });
    await waitFor(() => expect(release).toHaveLength(1));
    release[0]();
    await waitFor(() => expect(onPick).toHaveBeenCalledTimes(1));
    expect(input.value).toBe("NEXT-3");
  });
});
