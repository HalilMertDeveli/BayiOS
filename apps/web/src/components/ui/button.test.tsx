import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Button } from "./button";

describe("Button", () => {
  it("is a non-submitting button by default and handles clicks and keyboard", async () => {
    const onClick = vi.fn();
    render(<Button onClick={onClick}>Kaydet</Button>);
    const button = screen.getByRole("button", { name: "Kaydet" });
    expect(button.getAttribute("type")).toBe("button");

    await userEvent.click(button);
    button.focus();
    await userEvent.keyboard("{Enter}");
    await userEvent.keyboard(" ");
    expect(onClick).toHaveBeenCalledTimes(3);
  });

  it("is busy and not clickable while loading, keeping its label", async () => {
    const onClick = vi.fn();
    render(
      <Button loading onClick={onClick}>
        Kaydediliyor
      </Button>,
    );
    const button = screen.getByRole("button", { name: "Kaydediliyor" });
    expect(button.getAttribute("aria-busy")).toBe("true");
    expect((button as HTMLButtonElement).disabled).toBe(true);
    await userEvent.click(button);
    expect(onClick).not.toHaveBeenCalled();
  });

  it("renders decorative icons hidden from assistive tech", () => {
    render(<Button icon={<svg data-testid="icon" aria-hidden="true" />}>Ekle</Button>);
    expect(screen.getByRole("button", { name: "Ekle" })).toBeDefined();
    expect(screen.getByTestId("icon").getAttribute("aria-hidden")).toBe("true");
  });

  it("allows type=submit for forms", () => {
    render(<Button type="submit">Gönder</Button>);
    expect(screen.getByRole("button").getAttribute("type")).toBe("submit");
  });
});
