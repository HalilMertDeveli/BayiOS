import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError, type ApiClient, type RequestOptions } from "@/lib/api/client";
import type { Count } from "@/lib/stock-documents/api";
import { SessionContext } from "@/lib/session/session-context";
import { ToastProvider } from "@/components/ui/toast";
import { mockSession } from "@/test/session";
import type { FixtureRole } from "@/test/me-fixtures";
import { CountDetailPage } from "./count-detail";

// MOCK count (fixtures, not real data): blind, submitted, two counted lines in a warehouse without locations.
function count(hidden: boolean, status = "submitted"): Count {
  return {
    count: {
      id: "c-1",
      number: "SAY-0000C001",
      warehouseId: "w-1",
      warehouseName: "Ana depo",
      scope: "full",
      isBlind: true,
      quantitiesHidden: hidden,
      status,
      note: null,
      lineCount: 2,
      countedLineCount: 2,
      snapshotAt: "2026-11-09T08:00:00Z",
      submittedAt: "2026-11-09T09:00:00Z",
      appliedAt: null,
      createdAt: "2026-11-09T08:00:00Z",
      version: 3,
    },
    locationIds: [],
    variantIds: [],
    differenceCount: hidden ? null : 1,
    movedCount: hidden ? null : 1,
    createdBy: null,
    submittedBy: null,
    appliedBy: null,
    cancelledAt: null,
    cancelReason: null,
    lines: [
      { id: "l-1", variantId: "v-1", sku: "SKU-1", locationId: null, locationCode: null, expectedQuantity: hidden ? null : 10, countedQuantity: 8, difference: hidden ? null : -2, snapshotAt: "2026-11-09T08:00:00Z", countedAt: "2026-11-09T08:30:00Z", status: "counted", movedSinceSnapshot: !hidden, appliedDelta: null },
      { id: "l-2", variantId: "v-2", sku: "SKU-2", locationId: null, locationCode: null, expectedQuantity: hidden ? null : 4, countedQuantity: 4, difference: hidden ? null : 0, snapshotAt: "2026-11-09T08:00:00Z", countedAt: "2026-11-09T08:31:00Z", status: "counted", movedSinceSnapshot: false, appliedDelta: null },
    ],
  };
}

function setup(role: FixtureRole, hidden: boolean, onPost?: (path: string, options: RequestOptions) => unknown) {
  const posts: { path: string; options: RequestOptions }[] = [];
  const api: ApiClient = {
    async request<T>(path: string, options: RequestOptions = {}) {
      if (options.method === "POST") {
        posts.push({ path, options });
        const data = onPost?.(path, options);
        return { data: data as T, status: 200, etag: 'W/"4"' };
      }
      const data = path.startsWith("/api/v1/warehouses") ? { items: [{ id: "w-1", code: "ANA", name: "Ana depo", status: "active", usesLocations: false }] } : count(hidden);
      return { data: data as T, status: 200, etag: 'W/"3"' };
    },
  };
  render(
    <SessionContext value={mockSession(role, { api })}>
      <ToastProvider>
        <CountDetailPage countId="c-1" />
      </ToastProvider>
    </SessionContext>,
  );
  return posts;
}

describe("CountDetailPage — blind count", () => {
  it("a counter sees no system quantities and cannot apply", async () => {
    setup("warehouse_staff", true);
    expect(await screen.findByText("Sistemdeki miktarlar gizli. Rafta gördüğünüzü sayıp girin; farkı yönetici inceler.")).toBeTruthy();
    const table = screen.getByRole("table", { name: "Sayım satırları" });
    expect(within(table).queryByRole("columnheader", { name: "Sistem" })).toBeNull();
    expect(within(table).queryByRole("columnheader", { name: "Fark" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Onayla ve stoğa işle" })).toBeNull();
    expect(screen.getByText("Sayım bitirildi; yönetici onayı bekleniyor.")).toBeTruthy();
  });

  it("a manager sees the difference report and confirms moved lines with acceptMovedLines", async () => {
    let first = true;
    const posts = setup("manager", false, (path, options) => {
      if (first) {
        first = false;
        throw new ApiError("x", 409, { status: 409, code: "count.lines_moved", errors: { "l-1": ["SKU-1: stock moved after the snapshot."] } });
      }
      expect((options.body as { acceptMovedLines: boolean }).acceptMovedLines).toBe(true);
      return { count: count(false, "applied"), movementIds: ["m-1"] };
    });
    const table = await screen.findByRole("table", { name: "Sayım fark raporu" });
    expect(within(table).getByRole("columnheader", { name: "Sistem" })).toBeTruthy();
    expect(within(table).getByText("−2")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Onayla ve stoğa işle" }));
    await userEvent.click(within(screen.getByRole("alertdialog", { name: /stoğa işlensin mi/ })).getByRole("button", { name: "Onayla" }));
    const moved = await screen.findByRole("alertdialog", { name: "Bazı satırların stoğu sayımdan sonra değişti" });
    expect(within(moved).getByText("SKU-1")).toBeTruthy();
    await userEvent.click(within(moved).getByRole("button", { name: "Yine de uygula" }));
    await vi.waitFor(() => expect(posts).toHaveLength(2));
    expect(posts[0].options.idempotencyKey).not.toBe(posts[1].options.idempotencyKey);
    expect(posts[1].options.ifMatch).toBe('W/"3"');
    expect(await screen.findByText("Uygulandı")).toBeTruthy();
  });
});
