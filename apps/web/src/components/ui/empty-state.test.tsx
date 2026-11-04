import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { EmptyState } from "./empty-state";
import { Button } from "./button";

describe("EmptyState", () => {
  it("renders a heading, description and action", () => {
    render(
      <EmptyState title="Henüz ürün yok" description="İlk ürününüzü ekleyin." action={<Button>Ürün ekle</Button>} />,
    );
    expect(screen.getByRole("heading", { level: 2, name: "Henüz ürün yok" })).toBeDefined();
    expect(screen.getByText("İlk ürününüzü ekleyin.")).toBeDefined();
    expect(screen.getByRole("button", { name: "Ürün ekle" })).toBeDefined();
  });

  it("supports a lower heading level and hides the icon from assistive tech", () => {
    const { container } = render(<EmptyState title="Kayıt yok" headingLevel="h3" />);
    expect(screen.getByRole("heading", { level: 3, name: "Kayıt yok" })).toBeDefined();
    expect(container.querySelector("[aria-hidden='true'] svg")).not.toBeNull();
  });
});
