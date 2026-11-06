/** Route names shared by the proxy, the session layer and the auth screens. */
export const ROUTES = {
  signIn: "/giris",
  signUp: "/kayit",
  resetPassword: "/sifre-sifirla",
  updatePassword: "/sifre-yenile",
  authCallback: "/auth/callback",
  onboarding: "/isletme-olustur",
  tenantPicker: "/isletme-sec",
  home: "/pano",
  /** Invitation acceptance (#129): public, so a signed-out invitee keeps the #token fragment on this page. */
  invitation: "/davet",
} as const;

/** Reachable without a session. */
const PUBLIC_PATHS = new Set<string>(["/", ROUTES.signIn, ROUTES.signUp, ROUTES.resetPassword, ROUTES.authCallback, ROUTES.invitation]);

/** Pages a signed-in user has no reason to see; they are sent to the app instead. */
const GUEST_ONLY_PATHS = new Set<string>([ROUTES.signIn, ROUTES.signUp]);

export function isPublicPath(pathname: string): boolean {
  return PUBLIC_PATHS.has(pathname);
}

export function isGuestOnlyPath(pathname: string): boolean {
  return GUEST_ONLY_PATHS.has(pathname);
}

/**
 * Accepts only same-origin relative paths for post-login redirects (no open redirect):
 * must start with a single "/" and must not contain a scheme-relative "//" or a backslash.
 */
export function safeNextPath(value: string | null | undefined, fallback: string = ROUTES.home): string {
  if (!value || !value.startsWith("/") || value.startsWith("//") || value.includes("\\")) return fallback;
  try {
    const url = new URL(value, "http://bayios.invalid");
    if (url.origin !== "http://bayios.invalid") return fallback;
    return `${url.pathname}${url.search}${url.hash}`;
  } catch {
    return fallback;
  }
}

/** /giris?next=<current path> */
export function signInUrl(next?: string): string {
  const target = next ? safeNextPath(next, "") : "";
  return target ? `${ROUTES.signIn}?next=${encodeURIComponent(target)}` : ROUTES.signIn;
}
