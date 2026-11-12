"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { CircleAlert, LogIn, LogOut, MailCheck, RotateCcw, UserPlus } from "@/components/ui/icons";
import { FormAlert } from "@/components/catalog/form-alert";
import { Button, buttonClasses } from "@/components/ui/button";
import { createApiClient } from "@/lib/api/client";
import { ROUTES, signInUrl } from "@/lib/auth/routes";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { acceptInvitation } from "@/lib/members/api";
import { acceptFailure, type AcceptFailure } from "@/lib/members/errors";
import { INVITE_PATH, captureInviteToken, clearInviteToken } from "@/lib/members/invite-link";
import { writeActiveTenantId } from "@/lib/session/active-tenant";
import { getAccessToken, getSupabaseBrowserClient } from "@/lib/supabase/client";

type Phase =
  | { kind: "init" }
  | { kind: "no-token"; signedIn: boolean }
  | { kind: "signed-out" }
  | { kind: "ready"; email: string | null; userId: string }
  | { kind: "accepting"; email: string | null; userId: string }
  | { kind: "failed"; email: string | null; userId: string; failure: AcceptFailure }
  | { kind: "joined"; tenantName: string };

/** /kayit?next=/davet — the sign-up form sends the confirmation link back here. */
const SIGN_UP_HREF = `${ROUTES.signUp}?next=${encodeURIComponent(INVITE_PATH)}`;

/**
 * /davet (#129): the invitee's side. The token arrives in the URL fragment, moves to sessionStorage (this tab only)
 * and the fragment is stripped from the address bar. Signed out → sign in / sign up and come back here. Signed in →
 * an explicit "Daveti kabul et" (consent; nothing joins automatically) → POST /invitations/accept → the joined tenant
 * becomes the active one → home. The token is never put in a query string or logged, and is removed once it is used
 * or can never work again.
 */
export function AcceptInvitation() {
  const router = useRouter();
  const [phase, setPhase] = useState<Phase>({ kind: "init" });
  const [token, setToken] = useState<string | null>(null);
  const [keys] = useState(() => new IdempotencyKeys());
  const api = useMemo(() => createApiClient({ getAccessToken }), []);

  useEffect(() => {
    const captured = captureInviteToken(window.location.hash);
    // Strip the fragment: the token must not linger in the address bar, history entries or screenshots.
    if (window.location.hash) window.history.replaceState(window.history.state, "", INVITE_PATH);
    let cancelled = false;
    void getSupabaseBrowserClient()
      .auth.getSession()
      .then(({ data }) => {
        if (cancelled) return;
        const user = data.session?.user ?? null;
        setToken(captured);
        if (!captured) setPhase({ kind: "no-token", signedIn: user !== null });
        else if (!user) setPhase({ kind: "signed-out" });
        else setPhase({ kind: "ready", email: user.email ?? null, userId: user.id });
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const accept = async (refreshFirst = false) => {
    if (!token || (phase.kind !== "ready" && phase.kind !== "failed")) return;
    const { email, userId } = phase;
    setPhase({ kind: "accepting", email, userId });
    // After confirming the e-mail elsewhere, the access token still carries the old claims: refresh it first.
    if (refreshFirst) await getSupabaseBrowserClient().auth.refreshSession().catch(() => undefined);
    try {
      const joined = await acceptInvitation(api, token, keys.keyFor({ token }));
      clearInviteToken();
      setToken(null);
      writeActiveTenantId(userId, joined.tenantId);
      setPhase({ kind: "joined", tenantName: joined.tenantName });
      router.replace(ROUTES.home);
    } catch (error) {
      const failure = acceptFailure(error);
      if (failure.final) {
        clearInviteToken();
        setToken(null);
      }
      setPhase({ kind: "failed", email, userId, failure });
    }
  };

  const switchAccount = async () => {
    // Local sign-out only (this browser); the token stays in this tab for the next account.
    await getSupabaseBrowserClient().auth.signOut({ scope: "local" });
    router.push(signInUrl(INVITE_PATH));
  };

  if (phase.kind === "init") {
    return (
      <p role="status" className="text-body text-fg-muted">
        Davet kontrol ediliyor…
      </p>
    );
  }

  if (phase.kind === "joined") {
    return (
      <FormAlert tone="success" title={`${phase.tenantName} işletmesine katıldınız`}>
        Ana sayfaya yönlendiriliyorsunuz…
      </FormAlert>
    );
  }

  if (phase.kind === "no-token") {
    return (
      <div className="flex flex-col gap-4">
        <FormAlert tone="warning" title="Davet bağlantısı bulunamadı">
          Size iletilen davet bağlantısını bu tarayıcıda yeniden açın. E-postanızı başka bir sekmede doğruladıysanız, doğrulamadan sonra davet bağlantısını tekrar
          açmanız gerekir.
        </FormAlert>
        <Link href={phase.signedIn ? ROUTES.home : ROUTES.signIn} className={buttonClasses("secondary", "lg")}>
          {phase.signedIn ? "Ana sayfaya git" : "Giriş sayfasına git"}
        </Link>
      </div>
    );
  }

  if (phase.kind === "signed-out") {
    return (
      <div className="flex flex-col gap-4">
        <p className="text-body text-fg-secondary">
          Daveti kabul etmek için <strong className="font-semibold">davet edildiğiniz e-posta adresiyle</strong> giriş yapın. Hesabınız yoksa aynı adresle kayıt
          olun ve e-postanızı doğrulayın; ardından bu sayfaya dönersiniz.
        </p>
        <Link href={signInUrl(INVITE_PATH)} className={buttonClasses("primary", "lg")}>
          <LogIn className="size-4" aria-hidden="true" />
          Giriş yap
        </Link>
        <Link href={SIGN_UP_HREF} className={buttonClasses("secondary", "lg")}>
          <UserPlus className="size-4" aria-hidden="true" />
          Hesap oluştur
        </Link>
        <p className="text-caption text-fg-muted">Davet bu sekmede saklanır. Doğrulama bağlantısını aynı tarayıcıda açın.</p>
      </div>
    );
  }

  const failure = phase.kind === "failed" ? phase.failure : null;
  const busy = phase.kind === "accepting";
  return (
    <div className="flex flex-col gap-4">
      <p className="text-body text-fg-secondary">
        {phase.email ? (
          <>
            <strong className="font-semibold text-fg">{phase.email}</strong> olarak giriş yaptınız.
          </>
        ) : (
          "Giriş yaptınız."
        )}{" "}
        Daveti kabul ettiğinizde işletmeye size verilen rolle katılırsınız.
      </p>
      {failure ? (
        <FormAlert tone="error" title={failure.title}>
          {failure.message}
        </FormAlert>
      ) : null}
      {!failure || failure.next === "retry" || failure.next === "verify" ? (
        <Button
          size="lg"
          loading={busy}
          icon={failure ? <RotateCcw className="size-4" /> : <MailCheck className="size-4" />}
          onClick={() => void accept(failure?.next === "verify")}
        >
          {failure ? "Tekrar dene" : "Daveti kabul et"}
        </Button>
      ) : null}
      {failure?.next === "switch-account" ? (
        <Button size="lg" icon={<LogOut className="size-4" />} onClick={() => void switchAccount()}>
          Başka hesapla giriş yap
        </Button>
      ) : null}
      {failure?.next === "go-app" ? (
        <Link href={ROUTES.home} className={buttonClasses("primary", "lg")}>
          Uygulamaya git
        </Link>
      ) : null}
      {failure?.next === "none" ? (
        <p className="flex items-start gap-2 text-caption text-fg-muted">
          <CircleAlert className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
          Bu bağlantı artık kullanılamaz.
        </p>
      ) : null}
      {!failure || failure.next !== "switch-account" ? (
        <button type="button" onClick={() => void switchAccount()} className="text-body text-brand-fg underline-offset-2 hover:underline">
          Farklı bir hesapla giriş yap
        </button>
      ) : null}
    </div>
  );
}
