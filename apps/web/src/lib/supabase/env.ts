/**
 * Public Supabase settings. Both values are safe to ship to the browser: the publishable key only
 * identifies the project (it is NOT the service-role key, which never reaches the web app — ADR 0003 §6).
 * `process.env.NEXT_PUBLIC_*` must be referenced literally so Next.js can inline it at build time.
 */
export type SupabaseEnv = { url: string; publishableKey: string };

export function supabaseEnv(): SupabaseEnv {
  const url = process.env.NEXT_PUBLIC_SUPABASE_URL;
  const publishableKey = process.env.NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY;
  if (!url || !publishableKey) {
    throw new Error("NEXT_PUBLIC_SUPABASE_URL and NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY must be configured");
  }
  return { url, publishableKey };
}
