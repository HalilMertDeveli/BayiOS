import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError, type ApiClient } from "@/lib/api/client";
import { SessionContext } from "@/lib/session/session-context";
import { frameBeforeCommit } from "@/test/frame-before-commit";
import { mockSession } from "@/test/session";
import { InviteDialog } from "./invite-dialogs";

afterEach(() => vi.restoreAllMocks());

describe("InviteDialog focus (#152)", () => {
  it("a failed invitation focuses the error even when a frame runs before the update commits", async () => {
    frameBeforeCommit();
    // MOCK API: the invitation is refused (no real request).
    const api: ApiClient = {
      request: () => Promise.reject(new ApiError("x", 500, { code: "server.error" })),
    };
    render(
      <SessionContext value={mockSession("owner", { api })}>
        <InviteDialog roles={["admin", "sales"]} onClose={vi.fn()} onIssued={vi.fn()} />
      </SessionContext>,
    );
    await userEvent.type(screen.getByLabelText(/E-posta/), "kisi@example.test");
    await userEvent.click(screen.getByRole("button", { name: "Davet bağlantısı oluştur" }));
    const alert = (await screen.findByText("Davet oluşturulamadı")).closest('[tabindex="-1"]');
    await waitFor(() => expect(document.activeElement).toBe(alert));
  });
});
