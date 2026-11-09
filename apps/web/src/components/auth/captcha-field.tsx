"use client";

import { useEffect, useEffectEvent, useId, useRef, useState } from "react";
import { CAPTCHA_MESSAGES, loadTurnstile, turnstileSiteKey } from "@/lib/auth/captcha";

/** Turnstile `action` (analytics label in the Cloudflare dashboard): which form the token was issued for. */
export type CaptchaAction = "login" | "signup" | "password-reset";

type CaptchaFieldProps = {
  siteKey: string;
  action: CaptchaAction;
  error?: string;
  /** Increments after every auth request: Turnstile tokens are single-use. */
  resetCount: number;
  onToken: (token: string | null) => void;
};

/**
 * Form state for the optional CAPTCHA. With NEXT_PUBLIC_TURNSTILE_SITE_KEY unset, `fieldProps` is null and
 * `tokenForSubmit()` returns undefined, so the forms behave exactly as before (local/dev/e2e).
 */
export function useCaptcha(action: CaptchaAction) {
  const siteKey = turnstileSiteKey();
  const [token, setToken] = useState<string | null>(null);
  const [error, setError] = useState<string | undefined>();
  const [resetCount, setResetCount] = useState(0);

  return {
    /**
     * The token to pass as `captchaToken`: undefined when CAPTCHA is off; null when it is on but not solved yet —
     * the field then shows an error and the caller must not submit.
     */
    tokenForSubmit(): string | undefined | null {
      if (!siteKey) return undefined;
      if (!token) {
        setError(CAPTCHA_MESSAGES.required);
        return null;
      }
      return token;
    },
    /** Call after every auth request (success or failure): a token cannot be verified twice. */
    reset() {
      if (!siteKey) return;
      setToken(null);
      setResetCount((c) => c + 1);
    },
    fieldProps: siteKey
      ? ({
          siteKey,
          action,
          error,
          resetCount,
          onToken: (next: string | null) => {
            setToken(next);
            if (next) setError(undefined);
          },
        } satisfies CaptchaFieldProps)
      : null,
  };
}

/**
 * Cloudflare Turnstile widget (managed mode: usually completes by itself, otherwise one checkbox that works with the
 * keyboard and screen readers inside Cloudflare's frame). Labelled as a group; errors are announced (role="alert")
 * because the frame cannot be linked to them with aria-describedby.
 */
export function CaptchaField({ siteKey, action, error, resetCount, onToken }: CaptchaFieldProps) {
  const id = useId();
  const container = useRef<HTMLDivElement>(null);
  const widgetId = useRef<string | undefined>(undefined);
  const [unavailable, setUnavailable] = useState(false);

  const emit = useEffectEvent((token: string | null) => {
    if (token) setUnavailable(false);
    onToken(token);
  });
  const fail = useEffectEvent(() => {
    setUnavailable(true);
    onToken(null);
  });

  useEffect(() => {
    let cancelled = false;
    loadTurnstile().then(
      (api) => {
        if (cancelled || !container.current) return;
        widgetId.current = api.render(container.current, {
          sitekey: siteKey,
          action,
          language: "tr",
          theme: "auto",
          size: "flexible",
          callback: (token) => emit(token),
          "expired-callback": () => emit(null),
          "timeout-callback": () => emit(null),
          "error-callback": () => fail(),
        });
      },
      () => {
        if (!cancelled) fail();
      },
    );
    return () => {
      cancelled = true;
      if (widgetId.current) window.turnstile?.remove(widgetId.current);
      widgetId.current = undefined;
    };
  }, [siteKey, action]);

  useEffect(() => {
    if (resetCount > 0 && widgetId.current) window.turnstile?.reset(widgetId.current);
  }, [resetCount]);

  const message = unavailable ? CAPTCHA_MESSAGES.unavailable : error;
  return (
    <div role="group" aria-labelledby={`${id}-label`} aria-describedby={`${id}-message`} className="flex flex-col gap-1.5">
      <span id={`${id}-label`} className="text-body font-medium text-fg">
        Güvenlik doğrulaması
      </span>
      <div ref={container} className="min-h-[65px]" data-testid="captcha-widget" />
      {message ? (
        <p id={`${id}-message`} role="alert" className="text-caption text-danger-fg">
          {message}
        </p>
      ) : (
        <p id={`${id}-message`} className="text-caption text-fg-muted">
          Otomatik isteklere karşı koruma. Gerekirse kutucuğu işaretleyin.
        </p>
      )}
    </div>
  );
}
