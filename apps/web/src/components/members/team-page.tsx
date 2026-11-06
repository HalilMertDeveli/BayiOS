"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { Ban, CircleAlert, Crown, Lock, MailPlus, RefreshCw, RotateCcw, Trash2, UserCog, Users } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/empty-state";
import { PageHeader } from "@/components/ui/page-header";
import { Select } from "@/components/ui/select";
import { Table, type TableColumn } from "@/components/ui/table";
import { useToast } from "@/components/ui/toast";
import { ApiError } from "@/lib/api/client";
import { memberLabel } from "@/lib/api/identity";
import { getUsage, type UsageItem } from "@/lib/billing/api";
import { PLAN_PAGE_HREF } from "@/lib/billing/labels";
import { listInvitations, listMembers, resendInvitation, type Invitation, type Member } from "@/lib/members/api";
import { isStaleMemberState, memberErrorMessage } from "@/lib/members/errors";
import { formatDateTime, invitationStatusLabel, memberStatusLabel, roleLabel } from "@/lib/members/labels";
import { canHandleInvitation, canManageMember, canTransferOwnershipTo, grantableRoles, invitableRoles } from "@/lib/members/permissions";
import { ROLES } from "@/lib/navigation";
import { useSession } from "@/lib/session/session-context";
import { SettingsBackLink } from "@/components/settings/settings-back-link";
import { InviteDialog, InviteLinkDialog, toIssuedLink, type IssuedLink } from "./invite-dialogs";
import { RevokeDialog, RoleDialog, StatusDialog, TransferDialog } from "./member-dialogs";

type MembersData =
  | { key: string; status: "loading" }
  | { key: string; status: "error"; message: string }
  | { key: string; status: "ready"; items: Member[]; nextCursor: string | null; hasMore: boolean };

type InvitationsData = { status: "loading" } | { status: "error"; message: string } | { status: "ready"; items: Invitation[] };

type Open =
  | { kind: "none" }
  | { kind: "invite" }
  | { kind: "link"; issued: IssuedLink }
  | { kind: "role"; member: Member }
  | { kind: "status"; member: Member }
  | { kind: "transfer"; member: Member }
  | { kind: "revoke"; invitation: Invitation };

const ROLE_FILTERS = [{ value: "", label: "Tüm roller" }, ...ROLES.map((r) => ({ value: r, label: roleLabel(r) }))];
const STATUS_FILTERS = [
  { value: "", label: "Tüm durumlar" },
  { value: "active", label: "Aktif" },
  { value: "disabled", label: "Askıda" },
];

/**
 * Ayarlar → Ekip (#129): members (server-side role/status filters, keyset paging), pending invitations with one-time
 * links, role change, suspend / re-enable, ownership transfer and seat usage vs max_users. What is offered follows the
 * GET /me permissions and the role hierarchy (lib/members/permissions.ts); the API checks every call again.
 */
export function TeamPage() {
  const { api, access, state, reload } = useSession();
  const { toast } = useToast();
  const userId = state.status === "ready" ? state.user.id : null;
  const callerRole = state.status === "ready" ? (state.active?.role ?? null) : null;
  const canRead = access.canSome("members.read");
  const inviteRoles = invitableRoles(callerRole, access);
  const manageRoles = grantableRoles(callerRole, access);

  const [roleFilter, setRoleFilter] = useState("");
  const [statusFilter, setStatusFilter] = useState("");
  const [membersData, setMembers] = useState<MembersData>({ key: "", status: "loading" });
  const [more, setMore] = useState<{ loading: boolean; error: string | null }>({ loading: false, error: null });
  const [invitations, setInvitations] = useState<InvitationsData>({ status: "loading" });
  const [seats, setSeats] = useState<UsageItem | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [open, setOpen] = useState<Open>({ kind: "none" });
  const [busyInvitation, setBusyInvitation] = useState<string | null>(null);
  const close = () => setOpen({ kind: "none" });
  const refresh = useCallback(() => setAttempt((n) => n + 1), []);
  // The list belongs to one query (filters + attempt); a newer query shows the loading state until its page arrives.
  const queryKey = `${roleFilter}|${statusFilter}|${attempt}`;
  const members: MembersData = membersData.key === queryKey ? membersData : { key: queryKey, status: "loading" };

  useEffect(() => {
    if (!canRead) return;
    const controller = new AbortController();
    const key = `${roleFilter}|${statusFilter}|${attempt}`;
    listMembers(api, { role: roleFilter || undefined, status: statusFilter || undefined }, controller.signal)
      .then((page) => setMembers({ key, status: "ready", items: page.items, nextCursor: page.page.nextCursor, hasMore: page.page.hasMore }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setMembers({ key, status: "error", message: error instanceof ApiError ? error.userMessage : "Ekip yüklenemedi." });
      });
    return () => controller.abort();
  }, [api, canRead, roleFilter, statusFilter, attempt]);

  useEffect(() => {
    if (!canRead) return;
    const controller = new AbortController();
    listInvitations(api, "pending,expired", controller.signal)
      .then((items) => setInvitations({ status: "ready", items }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setInvitations({ status: "error", message: error instanceof ApiError ? error.userMessage : "Davetler yüklenemedi." });
      });
    return () => controller.abort();
  }, [api, canRead, attempt]);

  const canSeeUsage = access.can("billing.read");
  useEffect(() => {
    if (!canSeeUsage) return;
    const controller = new AbortController();
    getUsage(api, controller.signal)
      .then((usage) => setSeats(usage.items.find((i) => i.code === "max_users") ?? null))
      .catch(() => {
        if (!controller.signal.aborted) setSeats(null);
      });
    return () => controller.abort();
  }, [api, canSeeUsage, attempt]);

  const loadMore = async () => {
    if (members.status !== "ready" || !members.nextCursor || more.loading) return;
    setMore({ loading: true, error: null });
    try {
      const page = await listMembers(api, { role: roleFilter || undefined, status: statusFilter || undefined, cursor: members.nextCursor });
      setMembers({ key: members.key, status: "ready", items: [...members.items, ...page.items], nextCursor: page.page.nextCursor, hasMore: page.page.hasMore });
      setMore({ loading: false, error: null });
    } catch (error) {
      setMore({ loading: false, error: error instanceof ApiError ? error.userMessage : "Daha fazla üye yüklenemedi." });
    }
  };

  const replaceMember = (next: Member) =>
    setMembers((current) => (current.status === "ready" ? { ...current, items: current.items.map((m) => (m.userId === next.userId ? next : m)) } : current));

  const resend = async (invitation: Invitation) => {
    if (busyInvitation) return;
    setBusyInvitation(invitation.id);
    try {
      const issued = await resendInvitation(api, invitation.id);
      setOpen({ kind: "link", issued: toIssuedLink(issued, true) });
      refresh();
    } catch (error) {
      toast({ tone: "error", title: "Yeni bağlantı oluşturulamadı", description: memberErrorMessage(error) });
      if (isStaleMemberState(error)) refresh();
    } finally {
      setBusyInvitation(null);
    }
  };

  if (!canRead) {
    return (
      <>
        <SettingsBackLink />
        <PageHeader title="Ekip" />
        <EmptyState
          icon={<Lock className="size-6" />}
          title="Ekibi görüntüleme yetkiniz yok"
          description="Ekip üyelerini işletme sahibi, yöneticiler ve operasyon müdürleri görür. Davet ve rol değişikliği için işletme yöneticinizle görüşün."
        />
      </>
    );
  }

  const memberColumns: TableColumn<Member>[] = [
    {
      key: "name",
      header: "Kişi",
      cell: (m) => (
        <span className="flex min-w-40 flex-col">
          <span className="font-medium">
            {memberLabel(m)}
            {m.userId === userId ? <span className="font-normal text-fg-muted"> (siz)</span> : null}
          </span>
          {m.email && m.email !== memberLabel(m) ? <span className="text-caption text-fg-muted [overflow-wrap:anywhere]">{m.email}</span> : null}
        </span>
      ),
    },
    { key: "role", header: "Rol", cell: (m) => <span className="whitespace-nowrap">{roleLabel(m.role)}</span> },
    {
      key: "status",
      header: "Durum",
      cell: (m) => {
        const meta = memberStatusLabel(m.status);
        return <Badge tone={meta.tone}>{meta.label}</Badge>;
      },
    },
    {
      key: "actions",
      header: <span className="sr-only">İşlemler</span>,
      cell: (m) => {
        const manageable = canManageMember(callerRole, userId, access, m);
        const transferable = canTransferOwnershipTo(userId, access, m);
        if (!manageable && !transferable) return null;
        const name = memberLabel(m);
        return (
          <span className="flex flex-wrap justify-end gap-1">
            {manageable && m.status === "active" ? (
              <Button size="sm" variant="ghost" icon={<UserCog className="size-4" />} aria-label={`Rolü değiştir: ${name}`} onClick={() => setOpen({ kind: "role", member: m })}>
                Rolü değiştir
              </Button>
            ) : null}
            {manageable && m.status === "active" ? (
              <Button size="sm" variant="ghost" icon={<Ban className="size-4" />} aria-label={`Askıya al: ${name}`} onClick={() => setOpen({ kind: "status", member: m })}>
                Askıya al
              </Button>
            ) : null}
            {manageable && m.status === "disabled" ? (
              <Button
                size="sm"
                variant="ghost"
                icon={<RotateCcw className="size-4" />}
                aria-label={`Etkinleştir: ${name}`}
                onClick={() => setOpen({ kind: "status", member: m })}
              >
                Etkinleştir
              </Button>
            ) : null}
            {transferable ? (
              <Button size="sm" variant="ghost" icon={<Crown className="size-4" />} aria-label={`Sahipliği devret: ${name}`} onClick={() => setOpen({ kind: "transfer", member: m })}>
                Sahipliği devret
              </Button>
            ) : null}
          </span>
        );
      },
    },
  ];

  const invitationColumns: TableColumn<Invitation>[] = [
    { key: "email", header: "E-posta", cell: (i) => <span className="inline-block min-w-40 font-medium [overflow-wrap:anywhere]">{i.email}</span> },
    { key: "role", header: "Rol", cell: (i) => <span className="whitespace-nowrap">{roleLabel(i.role)}</span> },
    {
      key: "status",
      header: "Durum",
      cell: (i) => {
        const meta = invitationStatusLabel(i.status);
        return <Badge tone={meta.tone}>{meta.label}</Badge>;
      },
    },
    { key: "expires", header: "Geçerlilik", cell: (i) => <span className="whitespace-nowrap">{formatDateTime(i.expiresAt)}</span> },
    {
      key: "actions",
      header: <span className="sr-only">İşlemler</span>,
      cell: (i) =>
        canHandleInvitation(callerRole, access, i.role) ? (
          <span className="flex flex-wrap justify-end gap-1">
            <Button
              size="sm"
              variant="ghost"
              icon={<RefreshCw className="size-4" />}
              loading={busyInvitation === i.id}
              disabled={busyInvitation !== null && busyInvitation !== i.id}
              aria-label={`Yeni bağlantı: ${i.email}`}
              onClick={() => void resend(i)}
            >
              Yeni bağlantı
            </Button>
            <Button size="sm" variant="ghost" icon={<Trash2 className="size-4" />} aria-label={`Daveti iptal et: ${i.email}`} onClick={() => setOpen({ kind: "revoke", invitation: i })}>
              Daveti iptal et
            </Button>
          </span>
        ) : null,
    },
  ];

  const seatsFull = seats !== null && seats.limit !== null && Number(seats.current) >= Number(seats.limit);

  return (
    <>
      <SettingsBackLink />
      <PageHeader
        title="Ekip"
        description="İşletmenizdeki kişiler, rolleri ve bekleyen davetler."
        actions={
          inviteRoles.length > 0 ? (
            <Button icon={<MailPlus className="size-4" />} onClick={() => setOpen({ kind: "invite" })}>
              Kişi davet et
            </Button>
          ) : undefined
        }
      />

      {seats ? (
        <section aria-labelledby="seats" className="flex flex-col gap-1 rounded-lg border border-line bg-card p-4 shadow-e1">
          <h2 id="seats" className="text-body font-semibold text-fg">
            Kullanıcı sayısı
          </h2>
          <p className="text-body text-fg">
            {seats.limit === null ? (
              <>{Number(seats.current).toLocaleString("tr-TR")} kullanıcı · paketinizde sınır yok</>
            ) : (
              <>
                <span className="tabular-nums font-semibold">
                  {Number(seats.current).toLocaleString("tr-TR")} / {Number(seats.limit).toLocaleString("tr-TR")}
                </span>{" "}
                kullanıcı (aktif üyeler ve bekleyen davetler)
              </>
            )}
          </p>
          {seatsFull ? (
            <p className="text-caption text-warning-fg">
              Sınıra ulaştınız: yeni davet için bir daveti iptal edin, bir üyeyi askıya alın veya{" "}
              <Link href={PLAN_PAGE_HREF} className="font-medium underline underline-offset-2">
                paketinizi inceleyin
              </Link>
              .
            </p>
          ) : null}
        </section>
      ) : null}

      <section aria-labelledby="members" className="flex flex-col gap-3">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
          <h2 id="members" className="text-h3 text-fg">
            Üyeler
          </h2>
          <div className="grid grid-cols-2 gap-2 sm:flex">
            <Select label="Rol" options={ROLE_FILTERS} value={roleFilter} onChange={(e) => {
                setRoleFilter(e.target.value);
                setMore({ loading: false, error: null });
              }} />
            <Select label="Durum" options={STATUS_FILTERS} value={statusFilter} onChange={(e) => {
                setStatusFilter(e.target.value);
                setMore({ loading: false, error: null });
              }} />
          </div>
        </div>
        {members.status === "error" ? (
          <div role="alert">
            <EmptyState icon={<CircleAlert className="size-6" />} title="Ekip yüklenemedi" description={members.message} action={<Button onClick={refresh}>Tekrar dene</Button>} />
          </div>
        ) : (
          <Table
            caption="Ekip üyeleri"
            columns={memberColumns}
            rows={members.status === "ready" ? members.items : []}
            getRowId={(m) => m.userId}
            loading={members.status === "loading"}
            loadingRows={3}
            empty={
              <EmptyState
                headingLevel="h3"
                icon={<Users className="size-6" />}
                title={roleFilter || statusFilter ? "Bu filtrelerle üye yok" : "Henüz üye yok"}
                description={roleFilter || statusFilter ? "Filtreleri değiştirin." : undefined}
              />
            }
          />
        )}
        {members.status === "ready" && members.hasMore ? (
          <div className="flex flex-col items-center gap-2">
            <Button variant="secondary" loading={more.loading} onClick={() => void loadMore()}>
              Daha fazla yükle
            </Button>
            {more.error ? (
              <p role="alert" className="text-caption text-danger-fg">
                {more.error}
              </p>
            ) : null}
          </div>
        ) : null}
      </section>

      <section aria-labelledby="invitations" className="flex flex-col gap-3">
        <div className="flex flex-col gap-1">
          <h2 id="invitations" className="text-h3 text-fg">
            Bekleyen davetler
          </h2>
          <p className="text-caption text-fg-muted">Bağlantılar güvenlik için yeniden gösterilmez. Kaybolan bağlantı için “Yeni bağlantı” oluşturun; eskisi geçersiz olur.</p>
        </div>
        {invitations.status === "error" ? (
          <div role="alert">
            <EmptyState icon={<CircleAlert className="size-6" />} title="Davetler yüklenemedi" description={invitations.message} action={<Button onClick={refresh}>Tekrar dene</Button>} />
          </div>
        ) : (
          <Table
            caption="Bekleyen davetler"
            columns={invitationColumns}
            rows={invitations.status === "ready" ? invitations.items : []}
            getRowId={(i) => i.id}
            loading={invitations.status === "loading"}
            loadingRows={2}
            empty={<EmptyState headingLevel="h3" icon={<MailPlus className="size-6" />} title="Bekleyen davet yok" />}
          />
        )}
      </section>

      {open.kind === "invite" ? (
        <InviteDialog
          roles={inviteRoles}
          onClose={close}
          onIssued={(issued) => {
            setOpen({ kind: "link", issued });
            refresh();
          }}
        />
      ) : null}
      {open.kind === "link" ? <InviteLinkDialog issued={open.issued} onClose={close} /> : null}
      {open.kind === "role" ? (
        <RoleDialog
          member={open.member}
          roles={manageRoles}
          onClose={close}
          onDone={(m) => {
            replaceMember(m);
            close();
            toast({ tone: "success", title: "Rol güncellendi", description: `${memberLabel(m)}: ${roleLabel(m.role)}` });
          }}
        />
      ) : null}
      {open.kind === "status" ? (
        <StatusDialog
          member={open.member}
          onClose={close}
          onDone={(m) => {
            replaceMember(m);
            close();
            toast({ tone: "success", title: m.status === "disabled" ? "Üye askıya alındı" : "Üye yeniden etkinleştirildi", description: memberLabel(m) });
            refresh();
          }}
        />
      ) : null}
      {open.kind === "transfer" ? (
        <TransferDialog
          member={open.member}
          onClose={close}
          onDone={(result) => {
            close();
            toast({ tone: "success", title: "Sahiplik devredildi", description: `${memberLabel(result.newOwner)} artık işletme sahibi; siz yöneticisiniz.` });
            // The caller's own permissions changed (owner → admin): reload /me before anything else is offered.
            void reload().then(refresh);
          }}
        />
      ) : null}
      {open.kind === "revoke" ? (
        <RevokeDialog
          invitation={open.invitation}
          onClose={close}
          onStale={(error) => {
            if (isStaleMemberState(error)) refresh();
          }}
          onDone={() => {
            close();
            toast({ tone: "success", title: "Davet iptal edildi", description: open.invitation.email });
            refresh();
          }}
        />
      ) : null}
    </>
  );
}
