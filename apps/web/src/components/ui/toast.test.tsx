import { afterEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { ToastProvider, useToast, type ToastOptions } from "./toast";

function Trigger({ options }: { options: ToastOptions }) {
  const { toast } = useToast();
  return <button onClick={() => toast(options)}>Göster</button>;
}

function renderWith(options: ToastOptions) {
  render(
    <ToastProvider>
      <Trigger options={options} />
    </ToastProvider>,
  );
  fireEvent.click(screen.getByRole("button", { name: "Göster" }));
}

const liveList = () => screen.getByRole("region", { name: "Bildirimler" }).querySelector("[aria-live]")!;

afterEach(() => {
  vi.useRealTimers();
});

describe("Toast", () => {
  it("adds toasts to an always-mounted polite live region", () => {
    renderWith({ title: "Kaydedildi", description: "Değişiklikler kaydedildi.", tone: "success" });
    expect(liveList().getAttribute("aria-live")).toBe("polite");
    expect(within(liveList() as HTMLElement).getByText("Kaydedildi")).toBeDefined();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("uses role=alert for error toasts", () => {
    renderWith({ title: "Kaydedilemedi", tone: "error" });
    expect(screen.getByRole("alert").textContent).toContain("Kaydedilemedi");
  });

  it("can be dismissed with its close button", () => {
    renderWith({ title: "Kaydedildi" });
    fireEvent.click(screen.getByRole("button", { name: "Bildirimi kapat" }));
    expect(screen.queryByText("Kaydedildi")).toBeNull();
  });

  it("auto-dismisses after 5 s, and pauses while hovered", () => {
    vi.useFakeTimers();
    renderWith({ title: "Kaydedildi" });
    const item = screen.getByText("Kaydedildi").closest("li")!;

    act(() => vi.advanceTimersByTime(3000));
    fireEvent.mouseEnter(item);
    act(() => vi.advanceTimersByTime(10_000));
    expect(screen.getByText("Kaydedildi")).toBeDefined();

    fireEvent.mouseLeave(item);
    act(() => vi.advanceTimersByTime(1900));
    expect(screen.getByText("Kaydedildi")).toBeDefined();
    act(() => vi.advanceTimersByTime(200));
    expect(screen.queryByText("Kaydedildi")).toBeNull();
  });

  it("runs the undo action, closes, and gives action toasts 8 s", () => {
    vi.useFakeTimers();
    const onAction = vi.fn();
    renderWith({ title: "Satır silindi", action: { label: "Geri al", onAction } });

    act(() => vi.advanceTimersByTime(6000));
    expect(screen.getByText("Satır silindi")).toBeDefined();

    fireEvent.click(screen.getByRole("button", { name: "Geri al" }));
    expect(onAction).toHaveBeenCalledTimes(1);
    expect(screen.queryByText("Satır silindi")).toBeNull();
  });

  it("throws a clear error outside the provider", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    expect(() => render(<Trigger options={{ title: "x" }} />)).toThrow(/ToastProvider/);
    spy.mockRestore();
  });
});
