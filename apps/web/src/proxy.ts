import { NextResponse, type NextRequest } from "next/server";
import { createServerClient } from "@supabase/ssr";
import { ROUTES, isGuestOnlyPath, isPublicPath, signInUrl } from "@/lib/auth/routes";
import { supabaseEnv } from "@/lib/supabase/env";

/**
 * Session refresh + route protection (Next.js 16 `proxy`, formerly middleware).
 *
 * 1. Refreshes an expired Supabase session from the refresh-token cookie and writes the new cookies
 *    to both the forwarded request and the response (@supabase/ssr contract).
 * 2. Optimistic gate: no session → /giris?next=…; signed in on /giris or /kayit → /pano.
 *
 * This is deliberately NOT an authorization check. The web app renders no tenant data on the server;
 * every data call goes from the browser to the API, which verifies the JWT signature via JWKS and the
 * tenant membership on each request (ADR 0002/0003). A forged cookie therefore only reaches an empty shell.
 */
export async function proxy(request: NextRequest) {
  let response = NextResponse.next({ request });
  const { url, publishableKey } = supabaseEnv();

  const supabase = createServerClient(url, publishableKey, {
    cookies: {
      getAll() {
        return request.cookies.getAll();
      },
      setAll(cookiesToSet, headers) {
        for (const { name, value } of cookiesToSet) request.cookies.set(name, value);
        response = NextResponse.next({ request });
        for (const { name, value, options } of cookiesToSet) response.cookies.set(name, value, options);
        for (const [key, value] of Object.entries(headers ?? {})) response.headers.set(key, value);
      },
    },
  });

  // Loads the cookie session and refreshes it when the access token has expired.
  const { data } = await supabase.auth.getSession();
  const signedIn = data.session !== null;
  const { pathname, search } = request.nextUrl;

  const redirect = (target: string) => {
    const redirectResponse = NextResponse.redirect(new URL(target, request.url));
    // Keep any refreshed/cleared auth cookies on the redirect.
    for (const cookie of response.cookies.getAll()) redirectResponse.cookies.set(cookie);
    return redirectResponse;
  };

  if (!signedIn && !isPublicPath(pathname)) {
    return redirect(signInUrl(`${pathname}${search}`));
  }
  if (signedIn && isGuestOnlyPath(pathname)) {
    return redirect(ROUTES.home);
  }
  return response;
}

export const config = {
  matcher: [
    // Everything except Next internals and static files.
    "/((?!_next/static|_next/image|favicon.ico|.*\\.(?:svg|png|jpg|jpeg|gif|webp|ico|txt|xml)$).*)",
  ],
};
