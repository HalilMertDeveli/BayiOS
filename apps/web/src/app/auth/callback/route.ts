import { NextResponse, type NextRequest } from "next/server";
import { createServerClient } from "@supabase/ssr";
import type { EmailOtpType } from "@supabase/supabase-js";
import { ROUTES, safeNextPath } from "@/lib/auth/routes";
import { supabaseEnv } from "@/lib/supabase/env";

/**
 * Landing route for Supabase e-mail links (sign-up confirmation, password reset).
 * PKCE: `?code=` is exchanged for a session using the code verifier cookie written when the link was
 * requested. `?token_hash=&type=` (custom e-mail templates) is verified directly. The session cookies are
 * written onto the redirect; `next` is restricted to same-origin paths.
 */
export async function GET(request: NextRequest) {
  const { searchParams } = request.nextUrl;
  const next = safeNextPath(searchParams.get("next"));
  const code = searchParams.get("code");
  const tokenHash = searchParams.get("token_hash");
  const type = searchParams.get("type") as EmailOtpType | null;

  const success = NextResponse.redirect(new URL(next, request.url));
  const { url, publishableKey } = supabaseEnv();
  const supabase = createServerClient(url, publishableKey, {
    cookies: {
      getAll: () => request.cookies.getAll(),
      setAll(cookiesToSet) {
        for (const { name, value, options } of cookiesToSet) success.cookies.set(name, value, options);
      },
    },
  });

  let ok = false;
  if (code) {
    ok = !(await supabase.auth.exchangeCodeForSession(code)).error;
  } else if (tokenHash && type) {
    ok = !(await supabase.auth.verifyOtp({ token_hash: tokenHash, type })).error;
  }

  if (ok) return success;
  return NextResponse.redirect(new URL(`${ROUTES.signIn}?durum=baglanti-gecersiz`, request.url));
}
