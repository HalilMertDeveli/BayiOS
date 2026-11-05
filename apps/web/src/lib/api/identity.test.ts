import { describe, expect, it } from "vitest";
import type { ApiClient, RequestOptions } from "./client";
import { listActiveMembers, memberLabel, type Member } from "./identity";

const member = (userId: string, displayName: string | null, extra: Partial<Member> = {}): Member => ({
  userId,
  displayName,
  fullName: null,
  email: null,
  role: "warehouse_staff",
  status: "active",
  createdAt: "2026-11-01T00:00:00Z",
  ...extra,
});

describe("GET /members (staff picker)", () => {
  it("loads every page of active members and sorts them by name (Turkish collation)", async () => {
    const paths: string[] = [];
    const pages = [
      { items: [member("u3", "Zeynep"), member("u1", "Çağla")], page: { nextCursor: "c1", hasMore: true, limit: 200 } },
      { items: [member("u2", null, { email: "ahmet@example.test" })], page: { nextCursor: null, hasMore: false, limit: 200 } },
    ];
    const api: ApiClient = {
      async request<T>(path: string, options: RequestOptions = {}) {
        paths.push(path);
        expect(options.tenant).toBe(true);
        return { data: pages[paths.length - 1] as T, status: 200, etag: null };
      },
    };
    const members = await listActiveMembers(api);
    expect(members.map(memberLabel)).toEqual(["ahmet@example.test", "Çağla", "Zeynep"]);
    expect(paths).toEqual(["/api/v1/members?status=active&limit=200", "/api/v1/members?status=active&limit=200&cursor=c1"]);
  });
});
