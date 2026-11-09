import { describe, expect, it } from "vitest";
import { render, screen, within } from "@testing-library/react";
import { Table, type TableColumn } from "./table";
import { EmptyState } from "./empty-state";

type Row = { id: string; name: string; qty: number };
const columns: TableColumn<Row>[] = [
  { key: "name", header: "Ürün", cell: (r) => r.name },
  { key: "qty", header: "Miktar", cell: (r) => r.qty, numeric: true },
];
const rows: Row[] = [
  { id: "1", name: "Filtre", qty: 12 },
  { id: "2", name: "Conta", qty: 3 },
];

describe("Table", () => {
  it("renders an accessible table with caption, column headers and rows", () => {
    render(<Table caption="Ürünler" columns={columns} rows={rows} getRowId={(r) => r.id} />);
    const table = screen.getByRole("table", { name: "Ürünler" });
    const headers = within(table).getAllByRole("columnheader");
    expect(headers.map((h) => h.textContent)).toEqual(["Ürün", "Miktar"]);
    headers.forEach((h) => expect(h.getAttribute("scope")).toBe("col"));
    expect(within(table).getAllByRole("row")).toHaveLength(3);
    expect(table.getAttribute("aria-busy")).toBeNull();
  });

  it("wraps the table in a keyboard-focusable, labelled scroll region with a sticky header", () => {
    render(<Table caption="Ürünler" columns={columns} rows={rows} getRowId={(r) => r.id} maxHeight="10rem" />);
    const region = screen.getByRole("region", { name: "Ürünler" });
    expect(region.tabIndex).toBe(0);
    expect(region.style.maxHeight).toBe("10rem");
    expect(screen.getAllByRole("columnheader")[0].className).toContain("sticky");
  });

  it("right-aligns numeric cells with tabular numbers", () => {
    render(<Table caption="Ürünler" columns={columns} rows={rows} getRowId={(r) => r.id} />);
    const cell = screen.getByRole("cell", { name: "12" });
    expect(cell.className).toContain("text-right");
    expect(cell.className).toContain("tabular-nums");
  });

  it("supports a compact density", () => {
    render(<Table caption="Ürünler" columns={columns} rows={rows} getRowId={(r) => r.id} density="compact" />);
    expect(screen.getByRole("cell", { name: "Filtre" }).className).toContain("py-1.5");
  });

  it("shows a busy skeleton while loading and hides rows", () => {
    render(<Table caption="Ürünler" columns={columns} rows={rows} getRowId={(r) => r.id} loading loadingRows={3} />);
    const table = screen.getByRole("table", { name: "Ürünler" });
    expect(table.getAttribute("aria-busy")).toBe("true");
    expect(screen.queryByText("Filtre")).toBeNull();
    expect(screen.getByText("Yükleniyor…")).toBeDefined();
  });

  it("shows the default or a custom empty state when there are no rows", () => {
    const { rerender } = render(<Table caption="Ürünler" columns={columns} rows={[]} getRowId={(r) => r.id} />);
    expect(screen.getByRole("heading", { name: "Kayıt yok" })).toBeDefined();
    rerender(
      <Table
        caption="Ürünler"
        columns={columns}
        rows={[]}
        getRowId={(r) => r.id}
        empty={<EmptyState headingLevel="h3" title="Filtreye uyan ürün yok" />}
      />,
    );
    expect(screen.getByRole("heading", { name: "Filtreye uyan ürün yok" })).toBeDefined();
  });

  it("keeps numeric and nowrap columns on one line, right-aligning numbers", () => {
    const cols: TableColumn<Row>[] = [
      { key: "name", header: "Ürün", cell: (r) => r.name, nowrap: true },
      { key: "qty", header: "Miktar", cell: (r) => r.qty, numeric: true },
    ];
    render(<Table caption="Ürünler" columns={cols} rows={rows} getRowId={(r) => r.id} />);
    const [name, qty] = within(screen.getAllByRole("row")[1]).getAllByRole("cell");
    expect(name.className).toContain("whitespace-nowrap");
    expect(qty.className).toContain("whitespace-nowrap");
    expect(qty.className).toContain("text-right");
    expect(qty.className).toContain("tabular-nums");
  });
});
