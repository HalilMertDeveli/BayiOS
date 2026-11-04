"use client";

import { createBrowserClient } from "@supabase/ssr";
import type { SupabaseClient } from "@supabase/supabase-js";
import { supabaseEnv } from "./env";

let client: SupabaseClient | undefined;

/**
 * Browser Supabase client. The session lives in cookies (written by @supabase/ssr) so that the
 * proxy can refresh it and redirect signed-out visitors before a page renders.
 * Supabase is used for identity only — no table is read or written through it (web writes go to the API).
 */
export function getSupabaseBrowserClient(): SupabaseClient {
  if (!client) {
    const { url, publishableKey } = supabaseEnv();
    client = createBrowserClient(url, publishableKey);
  }
  return client;
}

/** Current access token (refreshed by the client when close to expiry), or null when signed out. */
export async function getAccessToken(): Promise<string | null> {
  const { data } = await getSupabaseBrowserClient().auth.getSession();
  return data.session?.access_token ?? null;
}
