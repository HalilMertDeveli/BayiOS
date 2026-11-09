import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { TurnstileApi } from "@/lib/auth/captcha";
import { ResetPasswordForm } from "./reset-password-form";
import { SignInForm } from "./sign-in-form";
import { SignUpForm } from "./sign-up-form";

const auth = vi.hoisted(() => ({
  signInWithPassword: vi.fn(),
  signUp: vi.fn(),
  resetPasswordForEmail: vi.fn(),
}));
const replace = vi.hoisted(() => vi.fn());

vi.mock("@/lib/supabase/client", () => ({ getSupabaseBrowserClient: () => ({ auth }) }));
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace, push: vi.fn() }),
  useSearchParams: () => new URLSearchParams(),
}));

type RenderOptions = Parameters<TurnstileApi["render"]>[1];

/** Fake `window.turnstile`: records the render options so a test can "solve" the challenge. */
function installTurnstile() {
  const widgets: RenderOptions[] = [];
  const api: TurnstileApi = {
    render: vi.fn((_el: HTMLElement, options: RenderOptions) => {
      widgets.push(options);
      return `w${widgets.length}`;
    }),
    reset: vi.fn(),
    remove: vi.fn(),
  };
  window.turnstile = api;
  return { api, solve: (token: string) => act(() => widgets.at(-1)?.callback?.(token)), widgets };
}

beforeEach(() => {
  auth.signInWithPassword.mockResolvedValue({ data: { session: {} }, error: null });
  auth.signUp.mockResolvedValue({ data: { session: null }, error: null });
  auth.resetPasswordForEmail.mockResolvedValue({ data: {}, error: null });
});

afterEach(() => {
  vi.unstubAllEnvs();
  vi.clearAllMocks();
  delete window.turnstile;
});

async function fillSignIn() {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText(/E-posta/), "owner@example.com");
  await user.type(screen.getByLabelText(/^Şifre/), "Parola123");
  return user;
}

describe("CAPTCHA off (NEXT_PUBLIC_TURNSTILE_SITE_KEY unset)", () => {
  it("renders no widget and signs in without a token", async () => {
    vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "");
    render(<SignInForm />);
    expect(screen.queryByRole("group", { name: "Güvenlik doğrulaması" })).toBeNull();
    const user = await fillSignIn();
    await user.click(screen.getByRole("button", { name: "Giriş yap" }));
    expect(auth.signInWithPassword).toHaveBeenCalledWith({
      email: "owner@example.com",
      password: "Parola123",
      options: { captchaToken: undefined },
    });
    expect(document.querySelector('script[src*="challenges.cloudflare.com"]')).toBeNull();
  });
});

describe("CAPTCHA on", () => {
  beforeEach(() => vi.stubEnv("NEXT_PUBLIC_TURNSTILE_SITE_KEY", "1x00000000000000000000AA"));

  it("renders a labelled Turnstile widget in Turkish with the form's action", async () => {
    const { api } = installTurnstile();
    render(<SignInForm />);
    expect(await screen.findByRole("group", { name: "Güvenlik doğrulaması" })).toBeTruthy();
    await vi.waitFor(() => expect(api.render).toHaveBeenCalledTimes(1));
    expect(vi.mocked(api.render).mock.calls[0][1]).toMatchObject({
      sitekey: "1x00000000000000000000AA",
      action: "login",
      language: "tr",
      theme: "auto",
    });
  });

  it("blocks sign-in until the challenge is solved, then sends the token once and resets the widget", async () => {
    const { api, solve } = installTurnstile();
    render(<SignInForm />);
    await vi.waitFor(() => expect(api.render).toHaveBeenCalled());
    const user = await fillSignIn();

    await user.click(screen.getByRole("button", { name: "Giriş yap" }));
    expect(auth.signInWithPassword).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toBe("Devam etmek için güvenlik doğrulamasını tamamlayın.");

    solve("token-1");
    expect(screen.queryByRole("alert")).toBeNull();
    await user.click(screen.getByRole("button", { name: "Giriş yap" }));
    expect(auth.signInWithPassword).toHaveBeenCalledWith(expect.objectContaining({ options: { captchaToken: "token-1" } }));
    await vi.waitFor(() => expect(api.reset).toHaveBeenCalledWith("w1"));
  });

  it("asks for a fresh challenge after Supabase rejects the token", async () => {
    const { api, solve } = installTurnstile();
    auth.signInWithPassword.mockResolvedValue({ data: {}, error: { code: "captcha_failed", status: 400 } });
    render(<SignInForm />);
    await vi.waitFor(() => expect(api.render).toHaveBeenCalled());
    const user = await fillSignIn();
    solve("used-token");
    await user.click(screen.getByRole("button", { name: "Giriş yap" }));
    expect(await screen.findByText(/Güvenlik doğrulaması başarısız oldu/)).toBeTruthy();
    expect(api.reset).toHaveBeenCalled();
    // The spent token is gone: a second submit needs a new challenge.
    await user.click(screen.getByRole("button", { name: "Giriş yap" }));
    expect(auth.signInWithPassword).toHaveBeenCalledTimes(1);
  });

  it("explains in Turkish when the widget cannot load", async () => {
    installTurnstile();
    window.turnstile!.render = vi.fn((_el: HTMLElement, options: RenderOptions) => {
      options["error-callback"]?.("110200");
      return "w1";
    });
    render(<SignInForm />);
    expect((await screen.findByRole("alert")).textContent).toMatch(/Güvenlik doğrulaması yüklenemedi/);
  });

  it("sends the token with sign-up and password reset", async () => {
    const { api, solve } = installTurnstile();
    const user = userEvent.setup();
    const { unmount } = render(<SignUpForm />);
    await vi.waitFor(() => expect(api.render).toHaveBeenCalledTimes(1));
    expect(vi.mocked(api.render).mock.calls[0][1].action).toBe("signup");
    await user.type(screen.getByLabelText(/Ad soyad/), "Ayşe Yılmaz");
    await user.type(screen.getByLabelText(/E-posta/), "ayse@example.com");
    await user.type(screen.getByLabelText(/^Şifre \*?$/), "Parola123");
    await user.type(screen.getByLabelText(/Şifre \(tekrar\)/), "Parola123");
    solve("signup-token");
    await user.click(screen.getByRole("button", { name: "Hesap oluştur" }));
    expect(auth.signUp.mock.calls[0][0].options.captchaToken).toBe("signup-token");
    unmount();

    render(<ResetPasswordForm />);
    await vi.waitFor(() => expect(api.render).toHaveBeenCalledTimes(2));
    expect(vi.mocked(api.render).mock.calls[1][1].action).toBe("password-reset");
    await user.type(screen.getByLabelText(/E-posta/), "ayse@example.com");
    solve("reset-token");
    await user.click(screen.getByRole("button", { name: "Sıfırlama bağlantısı gönder" }));
    expect(auth.resetPasswordForEmail).toHaveBeenCalledWith("ayse@example.com", expect.objectContaining({ captchaToken: "reset-token" }));
  });
});
