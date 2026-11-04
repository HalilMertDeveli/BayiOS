import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { Badge, OrderStatusBadge, orderStatusMeta, type OrderStatus } from "./badge";

describe("Badge", () => {
  it("renders its text with the tone classes", () => {
    render(<Badge tone="success">Satılabilir</Badge>);
    const badge = screen.getByText("Satılabilir");
    expect(badge.className).toContain("bg-success-bg");
    expect(badge.className).toContain("text-success-fg");
  });

  it.each(Object.entries(orderStatusMeta) as [OrderStatus, (typeof orderStatusMeta)[OrderStatus]][])(
    "OrderStatusBadge %s shows the Turkish label with a decorative icon",
    (status, meta) => {
      const { container } = render(<OrderStatusBadge status={status} />);
      expect(screen.getByText(meta.label)).toBeDefined();
      const svg = container.querySelector("svg");
      expect(svg?.getAttribute("aria-hidden")).toBe("true");
    },
  );

  it("maps the documented order states to labels", () => {
    expect(Object.fromEntries(Object.entries(orderStatusMeta).map(([k, v]) => [k, v.label]))).toEqual({
      Draft: "Taslak",
      PendingApproval: "Onay Bekliyor",
      Approved: "Onaylandı",
      Picking: "Toplanıyor",
      Packed: "Paketlendi",
      Shipped: "Sevk Edildi",
      PartiallyShipped: "Kısmi Sevk",
      Completed: "Tamamlandı",
      Cancelled: "İptal",
    });
  });
});
