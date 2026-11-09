import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { NO_ACCESS } from "@/lib/session/access";
import { SessionContext } from "@/lib/session/session-context";
import { TEST_USER, membership, mockSession } from "@/test/session";
import { TenantPicker } from "./tenant-picker";

const replace = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace }) }));

describe("TenantPicker — business status (#111)", () => {
  it("greys out suspended and closed businesses with a label; choosing one still opens it (the app shows the notice)", async () => {
    const memberships = [
      membership("owner"),
      membership("manager", { tenantId: "t-2", tenantName: "Askıdaki Bayi", tenantStatus: "suspended" }),
      membership("viewer", { tenantId: "t-3", tenantName: "Kapanan Bayi", tenantStatus: "closed" }),
    ];
    const selectTenant = vi.fn();
    render(
      <SessionContext
        value={mockSession("owner", { selectTenant, state: { status: "ready", user: TEST_USER, memberships, active: null, access: NO_ACCESS } })}
      >
        <TenantPicker />
      </SessionContext>,
    );
    const list = screen.getByRole("list", { name: "İşletmeleriniz" });
    const active = within(list).getByRole("button", { name: /Örnek Motor/ });
    const suspended = within(list).getByRole("button", { name: /Askıdaki Bayi/ });
    expect(active.textContent).not.toContain("Askıya alındı");
    expect(active.getAttribute("aria-describedby")).toBeNull();
    expect(suspended.textContent).toContain("Askıya alındı");
    expect(suspended.className).toContain("bg-sunken");
    expect(document.getElementById(suspended.getAttribute("aria-describedby") ?? "")?.textContent).toContain("şu anda kullanılamıyor");
    expect(within(list).getByRole("button", { name: /Kapanan Bayi/ }).textContent).toContain("Kapatıldı");

    await userEvent.click(suspended);
    expect(selectTenant).toHaveBeenCalledWith("t-2");
    expect(replace).toHaveBeenCalledWith("/pano");
  });
});
