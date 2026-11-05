"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { usePathname, useRouter } from "next/navigation";
import { ApiError, createApiClient, type ApiClient } from "@/lib/api/client";
import { getMe } from "@/lib/api/identity";
import { createEntitlements, type Entitlements, type TenantEntitlements } from "@/lib/billing/entitlements";
import { ROUTES, signInUrl } from "@/lib/auth/routes";
import type { Role } from "@/lib/navigation";
import { getAccessToken, getSupabaseBrowserClient } from "@/lib/supabase/client";
import { NO_ACCESS, createAccess, type Access } from "./access";
import {
  activeMemberships,
  clearActiveTenantId,
  readActiveTenantId,
  resolveActiveTenant,
  toRole,
  writeActiveTenantId,
  type Membership,
} from "./active-tenant";

export type SessionUser = { id: string; email: string | null; fullName: string | null };

export type SessionState =
  | { status: "loading" }
  | { status: "error"; message: string }
  | {
      status: "ready";
      user: SessionUser;
      memberships: Membership[];
      active: Membership | null;
      /** Effective permissions of `active` from GET /me (`activeTenant.permissions`); NO_ACCESS without a tenant. */
      access: Access;
      /** Plan / features / limits of `active` from GET /me (`activeTenant.entitlements`, #51); absent without a tenant. */
      entitlements?: TenantEntitlements | null;
    }
  /** The selected business is suspended or closed (403 `tenant.inactive`, or /me returned no activeTenant for it). */
  | { status: "inactive"; user: SessionUser; memberships: Membership[]; tenant: Membership };

export type SessionContextValue = {
  state: SessionState;
  /** API client bound to this session (Bearer token + active tenant). */
  api: ApiClient;
  /** Role of the active membership, for display only (labels); null until a tenant is active. */
  role: Role | null;
  /** What the UI may show for the active tenant (GET /me effective permissions). Visibility only — the API authorizes. */
  access: Access;
  /**
   * What the active tenant's plan includes (GET /me `activeTenant.entitlements`). Visibility only — the API enforces
   * entitlements; UNKNOWN_ENTITLEMENTS (nothing locked) until loaded.
   */
  entitlements: Entitlements;
  selectTenant: (tenantId: string) => void;
  /** Reloads /me (e.g. after creating a tenant). Resolves with the new memberships. */
  reload: () => Promise<Membership[]>;
  signOut: () => Promise<void>;
};

export const SessionContext = createContext<SessionContextValue | null>(null);

export function useSession(): SessionContextValue {
  const value = useContext(SessionContext);
  if (!value) throw new Error("useSession must be used inside <SessionProvider>.");
  return value;
}

export type SessionProviderProps = {
  /**
   * "tenant": the page needs an active tenant (the (app) group) — users without a membership go to
   * onboarding, users with several and no valid selection go to the tenant picker.
   * "user": a signed-in user is enough (onboarding, tenant picker).
   */
  require: "tenant" | "user";
  children: ReactNode;
};

/** Mutable, non-render state shared by the API client callbacks (selection, user, current path). */
function createSessionStore(initialPath: string) {
  let tenantId: string | null = null;
  let userId: string | null = null;
  let path = initialPath;
  return {
    getTenantId: () => tenantId,
    setTenantId: (value: string | null) => {
      tenantId = value;
    },
    getUserId: () => userId,
    setUserId: (value: string | null) => {
      userId = value;
    },
    getPath: () => path,
    setPath: (value: string) => {
      path = value;
    },
  };
}

type LoadResult = { state: SessionState | null; memberships: Membership[] };

/**
 * Real session: Supabase identity (cookie session) + GET /api/v1/me for memberships, the active membership's role and
 * its effective permissions (X-Tenant-Id is sent on /me). Nothing here is authorization; the API re-checks token,
 * membership, tenant status and permission on every request.
 */
export function SessionProvider({ require, children }: SessionProviderProps) {
  const router = useRouter();
  const pathname = usePathname();
  const [state, setState] = useState<SessionState>({ status: "loading" });
  const [store] = useState(() => createSessionStore(pathname));
  useEffect(() => store.setPath(pathname), [store, pathname]);

  const toSignIn = useCallback(() => router.replace(signInUrl(store.getPath())), [router, store]);

  const api = useMemo(
    () =>
      createApiClient({
        getAccessToken,
        getTenantId: store.getTenantId,
        onUnauthorized: () => {
          // The token was rejected (expired beyond refresh, revoked user): drop the local session.
          void getSupabaseBrowserClient().auth.signOut({ scope: "local" }).finally(toSignIn);
        },
        onTenantAccessDenied: () => {
          const userId = store.getUserId();
          if (userId) clearActiveTenantId(userId);
          store.setTenantId(null);
          router.replace(ROUTES.tenantPicker);
        },
        onTenantInactive: () => {
          // Suspended/closed while the user was working: the page is replaced by the notice (no tenant data stays).
          setState((prev) =>
            prev.status === "ready" && prev.active
              ? { status: "inactive", user: prev.user, memberships: prev.memberships, tenant: prev.active }
              : prev,
          );
        },
      }),
    [router, store, toSignIn],
  );

  /** Loads /me and resolves the active tenant. Performs redirects; returns the state to apply (if any). */
  const load = useCallback(
    async (signal?: AbortSignal): Promise<LoadResult> => {
      const { data } = await getSupabaseBrowserClient().auth.getSession();
      if (!data.session) {
        toSignIn();
        return { state: null, memberships: [] };
      }
      try {
        // Ask for the stored selection's permissions straight away; a second call only when the selection changes.
        let me = await getMe(api, { tenantId: readActiveTenantId(data.session.user.id), signal });
        store.setUserId(me.userId);
        const memberships = activeMemberships(me);
        const resolution = resolveActiveTenant(memberships, readActiveTenantId(me.userId));
        // Display only: the profile name from the API, else the name given at sign-up (Supabase user metadata).
        const metadataName = data.session.user.user_metadata?.full_name;
        const fullName = me.fullName ?? (typeof metadataName === "string" && metadataName.trim() ? metadataName.trim() : null);
        const user: SessionUser = { id: me.userId, email: me.email ?? null, fullName };

        if (require === "tenant" && resolution.kind !== "active") {
          router.replace(resolution.kind === "onboarding" ? ROUTES.onboarding : ROUTES.tenantPicker);
          return { state: null, memberships };
        }

        const active = resolution.kind === "active" ? resolution.membership : null;
        if (active) writeActiveTenantId(me.userId, active.tenantId);
        store.setTenantId(active?.tenantId ?? null);
        if (require === "user" || !active) {
          return { state: { status: "ready", user, memberships, active, access: NO_ACCESS }, memberships };
        }

        if (me.activeTenant?.tenantId !== active.tenantId) me = await getMe(api, { tenantId: active.tenantId, signal });
        const granted = me.activeTenant?.tenantId === active.tenantId ? me.activeTenant : null;
        if (!granted) {
          // An active membership the API will not open: the business is suspended or closed.
          return { state: { status: "inactive", user, memberships, tenant: active }, memberships };
        }
        return {
          state: {
            status: "ready",
            user,
            memberships,
            active: { ...active, role: granted.role },
            access: createAccess(granted.permissions),
            entitlements: granted.entitlements ?? null,
          },
          memberships,
        };
      } catch (error) {
        if (signal?.aborted) return { state: null, memberships: [] };
        if (error instanceof ApiError && error.status === 401) return { state: null, memberships: [] }; // onUnauthorized redirects
        const message =
          error instanceof ApiError ? error.userMessage : "Oturum bilgileri yüklenemedi. Lütfen tekrar deneyin.";
        return { state: { status: "error", message }, memberships: [] };
      }
    },
    [api, require, router, store, toSignIn],
  );

  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal).then((result) => {
      if (result.state && !controller.signal.aborted) setState(result.state);
    });
    const { data } = getSupabaseBrowserClient().auth.onAuthStateChange((event) => {
      // Signed out in another tab or the refresh token was revoked.
      if (event === "SIGNED_OUT") toSignIn();
    });
    return () => {
      controller.abort();
      data.subscription.unsubscribe();
    };
  }, [load, toSignIn]);

  const selectTenant = useCallback(
    (tenantId: string) => {
      if (state.status !== "ready" && state.status !== "inactive") return;
      const membership = state.memberships.find((m) => m.tenantId === tenantId);
      if (!membership) return;
      writeActiveTenantId(state.user.id, tenantId);
      store.setTenantId(tenantId);
      if (require === "user") {
        // Onboarding / tenant picker: the next page loads its own session (and the permissions) for this tenant.
        setState({ status: "ready", user: state.user, memberships: state.memberships, active: membership, access: NO_ACCESS });
        return;
      }
      // Another tenant means other permissions: reload /me for it before rendering anything of that tenant.
      setState({ status: "loading" });
      void load().then((result) => {
        if (result.state) setState(result.state);
      });
    },
    [state, store, require, load],
  );

  const signOut = useCallback(async () => {
    await getSupabaseBrowserClient().auth.signOut();
    router.replace(ROUTES.signIn);
  }, [router]);

  const reload = useCallback(async () => {
    const result = await load();
    if (result.state) setState(result.state);
    return result.memberships;
  }, [load]);

  const role = state.status === "ready" && state.active ? toRole(state.active.role) : null;
  const access = state.status === "ready" ? state.access : NO_ACCESS;
  const rawEntitlements = state.status === "ready" ? (state.entitlements ?? null) : null;
  const entitlements = useMemo(() => createEntitlements(rawEntitlements), [rawEntitlements]);

  const value = useMemo<SessionContextValue>(
    () => ({ state, api, role, access, entitlements, selectTenant, reload, signOut }),
    [state, api, role, access, entitlements, selectTenant, reload, signOut],
  );

  return <SessionContext value={value}>{children}</SessionContext>;
}
