import { beforeEach, describe, expect, it } from "vitest";
import type { ApiClient, RequestOptions } from "@/lib/api/client";
import { acceptInvitation, createInvitation, resendInvitation } from "./api";
import {
  INVITE_TOKEN_STORAGE_KEY,
  buildInviteLink,
  captureInviteToken,
  clearInviteToken,
  readInviteToken,
  tokenFromHash,
} from "./invite-link";

// Shape of the API's tokens (32 random bytes, base64url) — synthetic.
const TOKEN = "Qm9ndXNUb2tlbkZvclRlc3RzT25seS0xMjM0NTY3ODk_";

beforeEach(() => window.sessionStorage.clear());

describe("invite link", () => {
  it("puts the token in the fragment, never in a query string or path", () => {
    const link = buildInviteLink("https://app.example.test/", TOKEN);
    expect(link).toBe(`https://app.example.test/davet#${TOKEN}`);
    const url = new URL(link);
    expect(url.search).toBe("");
    expect(url.pathname).toBe("/davet");
    expect(url.hash).toBe(`#${TOKEN}`);
  });

  it("refuses something that is not a token", () => {
    expect(() => buildInviteLink("https://x.test", "a b?c")).toThrow();
    expect(() => buildInviteLink("https://x.test", "")).toThrow();
  });

  it("reads the token from the hash and rejects junk", () => {
    expect(tokenFromHash(`#${TOKEN}`)).toBe(TOKEN);
    expect(tokenFromHash(`#token=${TOKEN}`)).toBe(TOKEN);
    expect(tokenFromHash("")).toBeNull();
    expect(tokenFromHash("#<script>")).toBeNull();
    expect(tokenFromHash("#%E0%A4%A")).toBeNull();
  });
});

describe("token storage (this tab only)", () => {
  it("moves the fragment token into sessionStorage, not localStorage", () => {
    expect(captureInviteToken(`#${TOKEN}`)).toBe(TOKEN);
    expect(window.sessionStorage.getItem(INVITE_TOKEN_STORAGE_KEY)).toBe(TOKEN);
    expect(Object.keys(window.localStorage)).toHaveLength(0);
  });

  it("falls back to the stored token (after sign-in / sign-up) and is cleared after use", () => {
    captureInviteToken(`#${TOKEN}`);
    expect(captureInviteToken("")).toBe(TOKEN);
    clearInviteToken();
    expect(readInviteToken()).toBeNull();
    expect(captureInviteToken("")).toBeNull();
  });

  it("a newer link wins over a stored token", () => {
    window.sessionStorage.setItem(INVITE_TOKEN_STORAGE_KEY, "b2xkLXRva2VuLW9sZC10b2tlbi1vbGQ");
    expect(captureInviteToken(`#${TOKEN}`)).toBe(TOKEN);
    expect(readInviteToken()).toBe(TOKEN);
  });
});

describe("API calls never carry the token in the URL", () => {
  const recorder = () => {
    const calls: { path: string; options: RequestOptions }[] = [];
    const api: ApiClient = {
      async request<T>(path: string, options: RequestOptions = {}) {
        calls.push({ path, options });
        return { data: { invitation: { id: "i-1" }, token: TOKEN } as T, status: 200, etag: null };
      },
    };
    return { api, calls };
  };

  it("accept sends the token only in the JSON body, without X-Tenant-Id, with an Idempotency-Key", async () => {
    const { api, calls } = recorder();
    await acceptInvitation(api, TOKEN, "key-1");
    expect(calls[0].path).toBe("/api/v1/invitations/accept");
    expect(calls[0].path).not.toContain(TOKEN);
    expect(calls[0].options).toMatchObject({ method: "POST", body: { token: TOKEN }, idempotencyKey: "key-1" });
    expect(calls[0].options.tenant).toBeUndefined();
  });

  it("create and resend send no Idempotency-Key (the API keeps no replay copy of the token)", async () => {
    const { api, calls } = recorder();
    await createInvitation(api, { email: "a@b.test", role: "sales" });
    await resendInvitation(api, "i-1");
    expect(calls.map((c) => c.options.idempotencyKey)).toEqual([undefined, undefined]);
    expect(calls[1].path).toBe("/api/v1/members/invitations/i-1/resend");
  });
});
