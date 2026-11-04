"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { usePathname, useRouter } from "next/navigation";
import { ApiError, createApiClient, type ApiClient } from "@/lib/api/client";
import { getMe } from "@/lib/api/identity";
import { ROUTES, signInUrl } from "@/lib/auth/routes";
import type { Role } from "@/lib/navigation";
import { getAccessToken, getSupabaseBrowserClient } from "@/lib/supabase/client";
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
  | { status: "ready"; user: SessionUser; memberships: Membership[]; active: Membership | null };

export type SessionContextValue = {
  state: SessionState;
  /** API client bound to this session (Bearer token + active tenant). */
  api: ApiClient;
  /** Menu role of the active membership; null until a tenant is active. */
  role: Role | null;
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
 * Real session: Supabase identity (cookie session) + GET /api/v1/me for memberships and roles.
 * Nothing here is authorization; the API re-checks token, membership and role on every request.
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
        const me = await getMe(api, signal);
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
        return { state: { status: "ready", user, memberships, active }, memberships };
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
      if (state.status !== "ready") return;
      const membership = state.memberships.find((m) => m.tenantId === tenantId);
      if (!membership) return;
      writeActiveTenantId(state.user.id, tenantId);
      store.setTenantId(tenantId);
      setState({ ...state, active: membership });
    },
    [state, store],
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

  const value = useMemo<SessionContextValue>(
    () => ({ state, api, role, selectTenant, reload, signOut }),
    [state, api, role, selectTenant, reload, signOut],
  );

  return <SessionContext value={value}>{children}</SessionContext>;
}
