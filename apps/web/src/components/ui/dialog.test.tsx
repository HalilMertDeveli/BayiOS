import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { Dialog } from "./dialog";

function Harness({ onClose = () => {}, dismissOnBackdrop = true }: { onClose?: () => void; dismissOnBackdrop?: boolean }) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button onClick={() => setOpen(true)}>Aç</button>
      <Dialog
        open={open}
        onClose={() => {
          onClose();
          setOpen(false);
        }}
        title="Siparişi iptal et"
        description="Bu işlem geri alınamaz."
        dismissOnBackdrop={dismissOnBackdrop}
        role="alertdialog"
        footer={<button onClick={() => setOpen(false)}>Vazgeç</button>}
      >
        Ayrılan stok serbest bırakılır.
      </Dialog>
    </>
  );
}

const dialogEl = () => document.querySelector("dialog")!;

describe("Dialog", () => {
  it("opens as a labelled, described modal and renders content only while open", async () => {
    render(<Harness />);
    expect(dialogEl().open).toBe(false);
    expect(screen.queryByText("Ayrılan stok serbest bırakılır.")).toBeNull();

    await userEvent.click(screen.getByRole("button", { name: "Aç" }));
    const dialog = screen.getByRole("alertdialog", { name: "Siparişi iptal et" });
    expect(dialogEl().open).toBe(true);
    expect(dialog.getAttribute("aria-modal")).toBe("true");
    expect(document.getElementById(dialog.getAttribute("aria-describedby")!)?.textContent).toBe(
      "Bu işlem geri alınamaz.",
    );
    expect(screen.getByText("Ayrılan stok serbest bırakılır.")).toBeDefined();
  });

  it("closes via the close button, Escape (cancel event) and footer actions", async () => {
    const onClose = vi.fn();
    render(<Harness onClose={onClose} />);
    const open = screen.getByRole("button", { name: "Aç" });

    await userEvent.click(open);
    await userEvent.click(screen.getByRole("button", { name: "Kapat" }));
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(dialogEl().open).toBe(false);

    await userEvent.click(open);
    const cancel = new Event("cancel", { cancelable: true });
    fireEvent(dialogEl(), cancel);
    expect(cancel.defaultPrevented).toBe(true);
    expect(onClose).toHaveBeenCalledTimes(2);
    expect(dialogEl().open).toBe(false);

    await userEvent.click(open);
    await userEvent.click(screen.getByRole("button", { name: "Vazgeç" }));
    expect(dialogEl().open).toBe(false);
  });

  it("returns focus to the trigger after closing", async () => {
    render(<Harness />);
    const open = screen.getByRole("button", { name: "Aç" });
    await userEvent.click(open);
    await userEvent.click(screen.getByRole("button", { name: "Kapat" }));
    expect(document.activeElement).toBe(open);
  });

  it("closes on backdrop click only when allowed", async () => {
    const onClose = vi.fn();
    const { unmount } = render(<Harness onClose={onClose} dismissOnBackdrop={false} />);
    await userEvent.click(screen.getByRole("button", { name: "Aç" }));
    fireEvent.click(dialogEl());
    expect(onClose).not.toHaveBeenCalled();
    unmount();

    render(<Harness onClose={onClose} />);
    await userEvent.click(screen.getByRole("button", { name: "Aç" }));
    fireEvent.click(dialogEl());
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
