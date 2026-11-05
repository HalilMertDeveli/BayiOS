import { History } from "lucide-react";
import type { OrderHistoryEntry } from "@/lib/orders/api";
import { historyActionLabels, orderStatusLabel } from "@/lib/orders/labels";

const dateTime = new Intl.DateTimeFormat("tr-TR", { dateStyle: "medium", timeStyle: "short", timeZone: "Europe/Istanbul" });

/**
 * Status history (order_status_history, PRD ORD-12), oldest first as the API returns it. `changedBy` is a user id
 * only: the current user is shown as "Siz", others as "Başka bir kullanıcı" (no user directory in the contract yet).
 */
export function OrderTimeline({ history, currentUserId }: { history: readonly OrderHistoryEntry[]; currentUserId: string | null }) {
  const entries = [...history].sort((a, b) => a.changedAt.localeCompare(b.changedAt));
  return (
    <section aria-labelledby="order-history" className="flex flex-col gap-3">
      <h2 id="order-history" className="flex items-center gap-2 text-h3 text-fg">
        <History className="size-5 text-fg-muted" aria-hidden="true" />
        Durum geçmişi
      </h2>
      {entries.length === 0 ? (
        <p className="text-body text-fg-muted">Kayıt yok.</p>
      ) : (
        <ol aria-label="Durum geçmişi" className="flex flex-col border-l-2 border-line pl-4">
          {entries.map((h) => (
            <li key={h.id} className="relative pb-4 last:pb-0">
              <span aria-hidden="true" className="absolute top-1.5 -left-[1.3rem] size-2.5 rounded-full bg-brand" />
              <p className="text-body font-medium text-fg">{historyActionLabels[h.action] ?? h.action}</p>
              <p className="text-caption text-fg-muted">
                <time dateTime={h.changedAt}>{dateTime.format(new Date(h.changedAt))}</time>
                {" · "}
                {h.changedBy && h.changedBy === currentUserId ? "Siz" : h.changedBy ? "Başka bir kullanıcı" : "Sistem"}
                {h.fromStatus ? ` · ${orderStatusLabel(h.fromStatus)} → ${orderStatusLabel(h.toStatus)}` : ` · ${orderStatusLabel(h.toStatus)}`}
              </p>
              {h.reason ? <p className="mt-1 text-body text-fg-secondary">Sebep: {h.reason}</p> : null}
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
