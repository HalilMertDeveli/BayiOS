"use client";

import { useState } from "react";
import { Ban, Crown, RotateCcw, Save, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Select } from "@/components/ui/select";
import { memberLabel } from "@/lib/api/identity";
import { IdempotencyKeys } from "@/lib/catalog/idempotency";
import { changeMemberRole, revokeInvitation, setMemberEnabled, transferOwnership, type Invitation, type Member, type OwnershipTransfer } from "@/lib/members/api";
import { memberErrorMessage } from "@/lib/members/errors";
import { roleDescriptions, roleLabel } from "@/lib/members/labels";
import type { Role } from "@/lib/navigation";
import { useSession } from "@/lib/session/session-context";

function ErrorText({ message }: { message: string | null }) {
  return message ? (
    <p role="alert" className="text-body text-danger-fg">
      {message}
    </p>
  ) : null;
}

/** Shared busy/error/idempotency plumbing: one key per dialog so a retry after a lost response replays the first result. */
function useAction<T>(onFailed?: (error: unknown) => void) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [keys] = useState(() => new IdempotencyKeys());
  const run = async (body: unknown, call: (key: string) => Promise<T>, done: (value: T) => void) => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      done(await call(keys.keyFor(body)));
    } catch (err) {
      setError(memberErrorMessage(err));
      onFailed?.(err);
      setBusy(false);
    }
  };
  return { busy, error, run };
}

export function RoleDialog({ member, roles, onClose, onDone }: { member: Member; roles: readonly Role[]; onClose: () => void; onDone: (m: Member) => void }) {
  const { api } = useSession();
  const [role, setRole] = useState<string>(roles.includes(member.role as Role) ? member.role : (roles[0] ?? ""));
  const { busy, error, run } = useAction<Member>();
  const unchanged = role === member.role;
  const options = roles.map((r) => ({ value: r, label: roleLabel(r) }));
  return (
    <Dialog
      open
      onClose={onClose}
      title="Rolü değiştir"
      description={`${memberLabel(member)} · şu an ${roleLabel(member.role)}`}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button
            loading={busy}
            disabled={unchanged || !role}
            icon={<Save className="size-4" />}
            onClick={() => void run({ userId: member.userId, role }, (key) => changeMemberRole(api, member.userId, role, key), onDone)}
          >
            Rolü kaydet
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        <Select
          label="Yeni rol"
          options={options}
          value={role}
          hint={(roleDescriptions as Record<string, string>)[role]}
          onChange={(e) => setRole(e.target.value)}
        />
        <p className="text-caption text-fg-muted">Değişiklik kişinin bir sonraki işleminde geçerli olur. İşletme sahipliği yalnızca “Sahipliği devret” ile verilir.</p>
        <ErrorText message={error} />
      </div>
    </Dialog>
  );
}

export function StatusDialog({ member, onClose, onDone }: { member: Member; onClose: () => void; onDone: (m: Member) => void }) {
  const { api } = useSession();
  const enable = member.status === "disabled";
  const { busy, error, run } = useAction<Member>();
  const name = memberLabel(member);
  return (
    <Dialog
      open
      role="alertdialog"
      onClose={onClose}
      dismissOnBackdrop={false}
      title={enable ? "Üye yeniden etkinleştirilsin mi?" : "Üye askıya alınsın mı?"}
      description={name}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button
            variant={enable ? "primary" : "danger"}
            loading={busy}
            icon={enable ? <RotateCcw className="size-4" /> : <Ban className="size-4" />}
            onClick={() => void run({ userId: member.userId, enable }, (key) => setMemberEnabled(api, member.userId, enable, key), onDone)}
          >
            {enable ? "Yeniden etkinleştir" : "Askıya al"}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        {enable ? (
          <p>Kişi bu işletmeye yeniden erişebilir. Etkin üyeler paketinizdeki kullanıcı sınırına sayılır.</p>
        ) : (
          <ul className="list-disc pl-5">
            <li>Kişi bu işletmeye bir sonraki işleminde erişemez; verileri ve geçmişi silinmez.</li>
            <li>Başka işletmelerdeki üyelikleri ve hesabı etkilenmez.</li>
            <li>İstediğiniz zaman yeniden etkinleştirebilirsiniz.</li>
          </ul>
        )}
        <ErrorText message={error} />
      </div>
    </Dialog>
  );
}

/** Strong confirmation: the target's e-mail (or name) must be typed exactly. A speed bump, not a security control. */
export function TransferDialog({ member, onClose, onDone }: { member: Member; onClose: () => void; onDone: (r: OwnershipTransfer) => void }) {
  const { api } = useSession();
  const [typed, setTyped] = useState("");
  const { busy, error, run } = useAction<OwnershipTransfer>();
  const expected = member.email ?? memberLabel(member);
  const confirmed = typed.trim().toLocaleLowerCase("tr-TR") === expected.trim().toLocaleLowerCase("tr-TR");
  const go = () => {
    if (confirmed) void run({ userId: member.userId }, (key) => transferOwnership(api, member.userId, key), onDone);
  };
  return (
    <Dialog
      open
      role="alertdialog"
      onClose={onClose}
      dismissOnBackdrop={false}
      title="İşletme sahipliği devredilsin mi?"
      description={`${memberLabel(member)} işletme sahibi olur.`}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button variant="danger" loading={busy} disabled={!confirmed} icon={<Crown className="size-4" />} onClick={go}>
            Sahipliği devret
          </Button>
        </>
      }
    >
      <form
        noValidate
        className="flex flex-col gap-3"
        onSubmit={(e) => {
          e.preventDefault();
          go();
        }}
      >
        <ul className="list-disc pl-5">
          <li>{memberLabel(member)} işletme sahibi olur: paket, ekip ve sahiplik dahil her yetkiye sahip olur.</li>
          <li>Siz yönetici (admin) olursunuz; paket yönetimi ve sahiplik devri yetkinizi kaybedersiniz.</li>
          <li>Bu işlemi yalnızca yeni sahip geri alabilir.</li>
        </ul>
        <Input
          label={`Onaylamak için yazın: ${expected}`}
          value={typed}
          autoComplete="off"
          spellCheck={false}
          onChange={(e) => setTyped(e.target.value)}
          hint="Metin tam olarak eşleşince düğme etkinleşir."
        />
        <ErrorText message={error} />
      </form>
    </Dialog>
  );
}

export function RevokeDialog({
  invitation,
  onClose,
  onDone,
  onStale,
}: {
  invitation: Invitation;
  onClose: () => void;
  onDone: (i: Invitation) => void;
  onStale: (error: unknown) => void;
}) {
  const { api } = useSession();
  const { busy, error, run } = useAction<Invitation>(onStale);
  return (
    <Dialog
      open
      role="alertdialog"
      onClose={onClose}
      dismissOnBackdrop={false}
      title="Davet iptal edilsin mi?"
      description={`${invitation.email} · ${roleLabel(invitation.role)}`}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={busy}>
            Vazgeç
          </Button>
          <Button
            variant="danger"
            loading={busy}
            icon={<Trash2 className="size-4" />}
            onClick={() => void run({ invitationId: invitation.id }, (key) => revokeInvitation(api, invitation.id, key), onDone)}
          >
            Daveti iptal et
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        <p>Gönderdiğiniz bağlantı hemen geçersiz olur ve kullanıcı koltuğu boşalır. Gerekirse yeniden davet edebilirsiniz.</p>
        <ErrorText message={error} />
      </div>
    </Dialog>
  );
}
