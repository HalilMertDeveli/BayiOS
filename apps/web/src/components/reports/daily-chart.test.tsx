import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { DailyChart, chartSummary, fillDays } from "./daily-chart";

describe("fillDays", () => {
  it("sums values per day and fills missing days with 0", () => {
    const points = fillDays(
      ["2026-11-01", "2026-11-02", "2026-11-03"],
      [
        { day: "2026-11-01", value: 2 },
        { day: "2026-11-01", value: 3 },
        { day: "2026-11-03", value: 1 },
        { day: "2026-10-01", value: 9 },
      ],
    );
    expect(points).toEqual([
      { day: "2026-11-01", value: 5 },
      { day: "2026-11-02", value: 0 },
      { day: "2026-11-03", value: 1 },
    ]);
  });
});

describe("DailyChart", () => {
  it("has an accessible name and a text summary with total and busiest day", () => {
    const points = fillDays(["2026-11-01", "2026-11-02"], [{ day: "2026-11-02", value: 1200 }]);
    render(<DailyChart title="Günlük sipariş sayısı" label="Günlük sipariş" points={points} />);
    const chart = screen.getByRole("img", { name: "Günlük sipariş sayısı grafiği" });
    const summary = document.getElementById(chart.getAttribute("aria-describedby") ?? "");
    expect(summary?.textContent).toBe("Günlük sipariş: 2 günde toplam 1.200; en yüksek 1.200 (2 Kas 2026).");
  });

  it("says when there is nothing to show", () => {
    expect(chartSummary(fillDays(["2026-11-01"], []), "Günlük sipariş")).toBe("Günlük sipariş: seçilen aralıkta kayıt yok.");
  });
});
