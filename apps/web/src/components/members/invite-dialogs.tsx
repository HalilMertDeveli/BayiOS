"use client";

import { useRef, useState, type FormEvent, useId } from "react";
import { Check, Copy, RefreshCw, Send } from "@/components/ui/icons";
import { FormAlert } from "@/components/catalog/form-alert";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { ApiError } from "@/lib/api/client";
import { validateEmail } from "@/lib/auth/validation";
import { translateFieldMessage } from "@/lib/catalog/errors";
import { createInvitation, resendInvitation, type IssuedInvitation } from "@/lib/members/api";
import { memberErrorMessage, pendingInvitationId } from "@/lib/members/errors";
import { buildInviteLink } from "@/lib/members/invite-link";
import { formatDateTime, roleDescriptions, roleLabel } from "@/lib/members/labels";
import type { Role } from "@/lib/navigation";
import { useSession } from "@/lib/session/session-context";
import { useFocusAfterCommit } from "@/lib/use-focus-after-commit";

/** A freshly issued link: lives only in the parent's state while the link dialog is open, never stored or logged. */
export type IssuedLink = { link: string; email: string; role: string; expiresAt: string; renewed: boolean };

export function toIssuedLink(issued: IssuedInvitation, renewed: boolean): IssuedLink {
  return {
    link: buildInviteLink(window.location.origin, issued.token),
    email: issued.invitation.email,
    role: issued.invitation.role,
    expiresAt: issued.invitation.expiresAt,
    renewed,
  };
}

/** E-mail + role → POST /members/invitations. 409 already_pending offers a new link for the existing invitation. */
export function InviteDialog({ roles, onClose, onIssued }: { roles: readonly Role[]; onClose: () => void; onIssued: (link: IssuedLink) => void }) {
  const uid = useId();
  const { api } = useSession();
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<string>(roles.includes("sales") ? "sales" : (roles[roles.length - 1] ?? ""));
  const [errors, setErrors] = useState<{ email?: string; role?: string }>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [pendingId, setPendingId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const alertRef = useRef<HTMLDivElement>(null);
  const focusAfterCommit = useFocusAfterCommit();

  const fail = (message: string) => {
    setFormError(message);
    focusAfterCommit(() => alertRef.current);
  };

  const submit = async (event?: FormEvent) => {
    event?.preventDefault();
    if (busy) return;
    const emailError = validateEmail(email);
    const roleError = role ? undefined : "Bir rol seçin.";
    setErrors({ email: emailError, role: roleError });
    setFormError(null);
    setPendingId(null);
    if (emailError || roleError) return;
    setBusy(true);
    try {
      const issued = await createInvitation(api, { email: email.trim(), role });
      onIssued(toIssuedLink(issued, false));
    } catch (error) {
      setBusy(false);
      if (error instanceof ApiError && error.status === 422) {
        setErrors({
          email: error.fieldErrors.email ? translateFieldMessage(error.fieldErrors.email[0]) : undefined,
          role: error.fieldErrors.role ? "Listeden bir rol seçin." : undefined,
        });
      }
      setPendingId(pendingInvitationId(error));
      fail(memberErrorMessage(error));
    }
  };

  const resend = async () => {
    if (!pendingId || busy) return;
    setBusy(true);
    try {
      onIssued(toIssuedLink(await resendInvitation(api, pendingId), true));
    } catch (error) {
      setBusy(false);
      setPendingId(null);
      fail(memberErrorMessage(error));
    }
  };

  const roleOptions = roles.map((r) => ({ value: r, label: roleLabel(r) }));
  return (
    <Dialog
      open
      onClose={onClose}
      title="Kişi davet et"
      description="Davet bağlantısı oluşturulur; e-posta gönderilmez. Bağlantıyı kişiye siz iletirsiniz."
      dismissOnBackdrop={false}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button type="submit" form={`${uid}-invite-form`} loading={busy} icon={<Send className="size-4" />}>
            Davet bağlantısı oluştur
          </Button>
        </>
      }
    >
      <form id={`${uid}-invite-form`} noValidate onSubmit={submit} aria-label="Davet formu" className="flex flex-col gap-4">
        {formError ? (
          <FormAlert
            ref={alertRef}
            tone="error"
            title="Davet oluşturulamadı"
            actions={
              pendingId ? (
                <Button size="sm" icon={<RefreshCw className="size-4" />} loading={busy} onClick={() => void resend()}>
                  Yeni bağlantı oluştur
                </Button>
              ) : undefined
            }
          >
            {formError}
          </FormAlert>
        ) : null}
        <Input
          label="E-posta"
          type="email"
          required
          autoComplete="off"
          spellCheck={false}
          value={email}
          error={errors.email}
          hint="Kişi bu adresle kayıt olup adresi doğrulamalı."
          onChange={(e) => setEmail(e.target.value)}
        />
        <Select
          label="Rol"
          required
          options={roleOptions}
          value={role}
          error={errors.role}
          hint={(roleDescriptions as Record<string, string>)[role]}
          onChange={(e) => setRole(e.target.value)}
        />
      </form>
    </Dialog>
  );
}

/**
 * Shows the invitation link ONCE. Closing the dialog drops it from memory; a lost link is replaced with "Yeni bağlantı
 * oluştur" (the old one stops working).
 */
export function InviteLinkDialog({ issued, onClose }: { issued: IssuedLink; onClose: () => void }) {
  const [copied, setCopied] = useState<"idle" | "done" | "failed">("idle");
  const inputRef = useRef<HTMLInputElement>(null);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(issued.link);
      setCopied("done");
    } catch {
      // Clipboard blocked (permissions, http): select the text so Ctrl+C works.
      inputRef.current?.select();
      setCopied("failed");
    }
  };

  return (
    <Dialog
      open
      role="alertdialog"
      onClose={onClose}
      title={issued.renewed ? "Yeni davet bağlantısı hazır" : "Davet bağlantısı hazır"}
      description={
        <>
          <strong className="font-semibold text-fg">{issued.email}</strong> · {roleLabel(issued.role)}
        </>
      }
      dismissOnBackdrop={false}
      footer={
        <Button variant="secondary" onClick={onClose}>
          Tamam
        </Button>
      }
    >
      <div className="flex flex-col gap-4">
        <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
          <Input
            ref={inputRef}
            label="Davet bağlantısı"
            readOnly
            value={issued.link}
            spellCheck={false}
            className="min-w-0 flex-1"
            onFocus={(e) => e.currentTarget.select()}
          />
          <Button onClick={() => void copy()} icon={copied === "done" ? <Check className="size-4" /> : <Copy className="size-4" />}>
            {copied === "done" ? "Kopyalandı" : "Bağlantıyı kopyala"}
          </Button>
        </div>
        <p role="status" className="sr-only">
          {copied === "done" ? "Bağlantı panoya kopyalandı." : copied === "failed" ? "Kopyalanamadı; bağlantı seçildi, Ctrl+C ile kopyalayın." : ""}
        </p>
        {copied === "failed" ? <p className="text-caption text-warning-fg">Otomatik kopyalanamadı. Bağlantı seçildi; Ctrl+C (Mac: ⌘C) ile kopyalayın.</p> : null}
        <FormAlert tone="warning" title="Bu bağlantı yalnızca bir kez gösterilir">
          Bu pencereyi kapattıktan sonra bağlantıyı yeniden göremezsiniz. Kaybederseniz bekleyen davetlerden “Yeni bağlantı oluştur” ile yenisini alın; eskisi
          geçersiz olur.
        </FormAlert>
        <ul className="flex list-disc flex-col gap-1 pl-5 text-body text-fg-secondary">
          <li>Bu bağlantıyı kişiye siz iletin (e-posta, mesaj). BayiOS e-posta göndermez.</li>
          <li>
            Kişi <strong className="font-semibold">{issued.email}</strong> adresiyle kayıt olup adresini doğrulamalı ve daveti onaylamalı.
          </li>
          <li>Bağlantı 7 gün geçerli: {formatDateTime(issued.expiresAt)} tarihine kadar.</li>
          <li>Bağlantıyı yalnızca davet ettiğiniz kişiyle paylaşın.</li>
        </ul>
      </div>
    </Dialog>
  );
}
