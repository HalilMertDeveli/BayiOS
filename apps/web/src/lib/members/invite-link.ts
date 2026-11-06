/**
 * Invitation links and the one-time token (#129, threat-model §4.1 I1).
 *
 * - The link is `<origin>/davet#<token>`: the token sits in the URL fragment, which browsers never send to a server
 *   (no access logs, no Referer). It is never put in a query string, a path or a log line.
 * - The inviter's screen keeps the token only in component state while the "link created" dialog is open.
 * - The invitee's /davet page moves the token from the fragment into sessionStorage (this tab only) and strips the
 *   fragment from the address bar, so sign-in / sign-up can happen in between; it is removed after the accept call
 *   settles for good (success or a final error).
 */

export const INVITE_PATH = "/davet";

/** sessionStorage key for the pending invitation token (this tab only, never localStorage). */
export const INVITE_TOKEN_STORAGE_KEY = "bayios.invitationToken";

/** The API's tokens are 32 random bytes as base64url (43 chars); accept any plausible base64url string. */
const TOKEN_PATTERN = /^[A-Za-z0-9_-]{16,512}$/;

export function isPlausibleToken(value: string): boolean {
  return TOKEN_PATTERN.test(value);
}

/** `<origin>/davet#<token>`. Throws on a token the API would never issue (keeps junk out of links). */
export function buildInviteLink(origin: string, token: string): string {
  if (!isPlausibleToken(token)) throw new Error("Invalid invitation token");
  return `${origin.replace(/\/+$/, "")}${INVITE_PATH}#${token}`;
}

/** Token from a location hash (`#abc…`, also tolerates `#token=abc…`); null when absent or malformed. */
export function tokenFromHash(hash: string): string | null {
  let value = hash.startsWith("#") ? hash.slice(1) : hash;
  if (value.startsWith("token=")) value = value.slice("token=".length);
  try {
    value = decodeURIComponent(value).trim();
  } catch {
    return null;
  }
  return isPlausibleToken(value) ? value : null;
}

type StorageLike = Pick<Storage, "getItem" | "setItem" | "removeItem">;

function session(): StorageLike | null {
  try {
    return typeof window === "undefined" ? null : window.sessionStorage;
  } catch {
    return null;
  }
}

export function storeInviteToken(token: string, storage: StorageLike | null = session()): void {
  try {
    storage?.setItem(INVITE_TOKEN_STORAGE_KEY, token);
  } catch {
    // Storage unavailable (private mode): the token lives only in the page's memory; a reload needs the link again.
  }
}

export function readInviteToken(storage: StorageLike | null = session()): string | null {
  try {
    const value = storage?.getItem(INVITE_TOKEN_STORAGE_KEY) ?? null;
    return value && isPlausibleToken(value) ? value : null;
  } catch {
    return null;
  }
}

export function clearInviteToken(storage: StorageLike | null = session()): void {
  try {
    storage?.removeItem(INVITE_TOKEN_STORAGE_KEY);
  } catch {
    // ignore
  }
}

/**
 * On /davet: takes the token from the fragment (preferred, it is the newest link) or from sessionStorage, stores it in
 * sessionStorage and returns it. The caller strips the fragment from the address bar afterwards.
 */
export function captureInviteToken(hash: string, storage: StorageLike | null = session()): string | null {
  const fromHash = tokenFromHash(hash);
  if (fromHash) {
    storeInviteToken(fromHash, storage);
    return fromHash;
  }
  return readInviteToken(storage);
}
