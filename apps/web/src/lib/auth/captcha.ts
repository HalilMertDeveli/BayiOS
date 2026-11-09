/**
 * CAPTCHA for the Supabase Auth forms (OD-05, #61): Cloudflare Turnstile, verified by Supabase Auth itself
 * (`options.captchaToken`; the secret key lives only in the Supabase dashboard → Auth → Bot protection).
 *
 * Off unless NEXT_PUBLIC_TURNSTILE_SITE_KEY is set — local, CI and e2e stacks run without it. The site key is public by
 * design (it is embedded in every page that shows the widget); it is not a secret. When it is set, next.config.ts also
 * allows TURNSTILE_ORIGIN in the CSP `script-src` / `frame-src` (and only then).
 * Owner steps: docs/operations/environments.md §2.1.
 */
export const TURNSTILE_ORIGIN = "https://challenges.cloudflare.com";
/** Explicit rendering: the form decides where and when the widget appears. Must be loaded from Cloudflare, never proxied. */
export const TURNSTILE_SCRIPT_URL = `${TURNSTILE_ORIGIN}/turnstile/v0/api.js?render=explicit`;

/** Site key, or null when CAPTCHA is off. `process.env.NEXT_PUBLIC_*` is referenced literally so Next.js inlines it. */
export function turnstileSiteKey(): string | null {
  const key = process.env.NEXT_PUBLIC_TURNSTILE_SITE_KEY?.trim();
  return key ? key : null;
}

export const CAPTCHA_MESSAGES = {
  required: "Devam etmek için güvenlik doğrulamasını tamamlayın.",
  expired: "Güvenlik doğrulamasının süresi doldu. Lütfen doğrulamayı yeniden tamamlayın.",
  unavailable:
    "Güvenlik doğrulaması yüklenemedi. İnternet bağlantınızı kontrol edip sayfayı yenileyin; sorun sürerse reklam/izleme engelleyicinizi bu site için kapatın.",
} as const;

/** Minimal surface of `window.turnstile` that the widget uses. */
export type TurnstileApi = {
  render(
    container: HTMLElement,
    options: {
      sitekey: string;
      action?: string;
      language?: string;
      theme?: "auto" | "light" | "dark";
      size?: "normal" | "flexible" | "compact";
      callback?: (token: string) => void;
      "expired-callback"?: () => void;
      "timeout-callback"?: () => void;
      "error-callback"?: (code: string) => void;
    },
  ): string | undefined;
  reset(widgetId: string): void;
  remove(widgetId: string): void;
};

declare global {
  interface Window {
    turnstile?: TurnstileApi;
  }
}

let loader: Promise<TurnstileApi> | null = null;

/** Loads the Turnstile script once per page; a failed load can be retried (next mount). */
export function loadTurnstile(): Promise<TurnstileApi> {
  if (typeof window === "undefined") return Promise.reject(new Error("Turnstile needs a browser"));
  if (window.turnstile) return Promise.resolve(window.turnstile);
  loader ??= new Promise<TurnstileApi>((resolve, reject) => {
    const script = document.createElement("script");
    script.src = TURNSTILE_SCRIPT_URL;
    script.async = true;
    script.onload = () => (window.turnstile ? resolve(window.turnstile) : reject(new Error("Turnstile did not initialise")));
    script.onerror = () => {
      loader = null;
      script.remove();
      reject(new Error("Turnstile script failed to load"));
    };
    document.head.appendChild(script);
  });
  return loader;
}
