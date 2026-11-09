import type { BadgeTone } from "@/components/ui/badge";
import { ROLES, roleLabels, type Role } from "@/lib/navigation";

/** Display label of a role string from the API; unknown roles are shown as-is (never mapped to a real role). */
export function roleLabel(role: string): string {
  return (ROLES as readonly string[]).includes(role) ? roleLabels[role as Role] : role;
}

/** One-line explanation of what a role does (roles-permissions §2), shown next to the role picker. */
export const roleDescriptions: Record<Role, string> = {
  owner: "Her şey; paket, sahiplik ve ekip yönetimi.",
  admin: "Ayarlar, ekip (müdür ve altı), fiyatlar, raporlar.",
  manager: "Sipariş, stok, depo ve görev yönetimi; ekibi görür.",
  sales: "Müşteri ve sipariş; işletme kurallarına göre onay ve iskonto.",
  warehouse_staff: "Toplama, paketleme, sevkiyat ve mal kabul.",
  viewer: "Salt okunur; raporları görür ve dışa aktarır.",
};

export const memberStatusMeta: Record<string, { label: string; tone: BadgeTone }> = {
  active: { label: "Aktif", tone: "success" },
  disabled: { label: "Askıda", tone: "warning" },
  invited: { label: "Davet edildi", tone: "info" },
};

export function memberStatusLabel(status: string): { label: string; tone: BadgeTone } {
  return memberStatusMeta[status] ?? { label: status, tone: "neutral" };
}

export const invitationStatusMeta: Record<string, { label: string; tone: BadgeTone }> = {
  pending: { label: "Bekliyor", tone: "info" },
  expired: { label: "Süresi doldu", tone: "warning" },
  accepted: { label: "Kabul edildi", tone: "success" },
  revoked: { label: "İptal edildi", tone: "neutral" },
};

export function invitationStatusLabel(status: string): { label: string; tone: BadgeTone } {
  return invitationStatusMeta[status] ?? { label: status, tone: "neutral" };
}

const dateFormat = new Intl.DateTimeFormat("tr-TR", { day: "numeric", month: "long", year: "numeric", hour: "2-digit", minute: "2-digit", timeZone: "Europe/Istanbul" });

export function formatDateTime(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : dateFormat.format(date);
}
