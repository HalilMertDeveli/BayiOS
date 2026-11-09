import { describe, expect, it } from "vitest";
import { taskListQuery } from "./api";
import { TASK_PRIORITIES, formatDeliveryDate, isTaskPriority, taskPriorityMeta, taskPriorityOptions } from "./labels";
import { summariseReservationMoves } from "@/lib/stock-documents/api";

describe("task priority (#98)", () => {
  it("names every API priority in Turkish, most urgent first in selects", () => {
    expect(TASK_PRIORITIES.map((p) => taskPriorityMeta[p].label)).toEqual(["Düşük", "Normal", "Yüksek", "Acil"]);
    expect(taskPriorityOptions.map((o) => o.value)).toEqual(["urgent", "high", "normal", "low"]);
    expect(isTaskPriority("urgent")).toBe(true);
    expect(isTaskPriority("critical")).toBe(false);
    expect(isTaskPriority(null)).toBe(false);
  });

  it("sends ?priority= and ?orderId= on the list query only when set", () => {
    expect(taskListQuery({ statuses: ["open", "in_progress"], priority: "urgent" })).toBe("status=open%2Cin_progress&priority=urgent&limit=50");
    expect(taskListQuery({ orderId: "o-1", type: "pick" })).toBe("type=pick&orderId=o-1&limit=50");
    expect(taskListQuery({ priority: null }, "c1")).toBe("limit=50&cursor=c1");
  });

  it("formats the requested delivery date as a calendar date", () => {
    expect(formatDeliveryDate("2026-11-12")).toMatch(/12/);
    expect(formatDeliveryDate("2026-11-12")).toMatch(/2026/);
    expect(formatDeliveryDate(null)).toBeNull();
    expect(formatDeliveryDate("not-a-date")).toBeNull();
  });
});

describe("relocation reservation moves (#98 includeReserved)", () => {
  it("sums the carried reservations and counts their orders", () => {
    expect(summariseReservationMoves(null)).toEqual({ quantity: 0, orders: 0 });
    expect(
      summariseReservationMoves([
        { orderId: "o1", quantity: 1.5 },
        { orderId: "o1", quantity: "0.5" },
        { orderId: "o2", quantity: 2 },
      ]),
    ).toEqual({ quantity: 4, orders: 2 });
  });
});
