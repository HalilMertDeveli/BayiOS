"use client";

import Link from "next/link";
import { ListPlus, Puzzle, SlidersHorizontal, Users, type LucideIcon } from "lucide-react";
import { PLAN_PAGE_HREF } from "@/lib/billing/labels";
import { useSession } from "@/lib/session/session-context";

function SettingsLink({ href, title, description, Icon }: { href: string; title: string; description: string; Icon: LucideIcon }) {
  return (
    <li>
      <Link href={href} className="flex h-full items-start gap-3 rounded-lg border border-line bg-card p-4 shadow-e1 hover:bg-sunken">
        <Icon className="mt-0.5 size-5 shrink-0 text-brand-fg" aria-hidden="true" />
        <span className="flex flex-col gap-1">
          <span className="font-medium text-fg">{title}</span>
          <span className="text-caption text-fg-muted">{description}</span>
        </span>
      </Link>
    </li>
  );
}

/**
 * The settings pages that are ready: "Ekip" for members.read (owner, admin, manager), "İş kuralları" for
 * tenant.settings.manage and "Paket ve modüller" for billing.read (owner, admin). Visibility only.
 */
export function SettingsLinks() {
  const { access } = useSession();
  return (
    <ul aria-label="Diğer ayarlar" className="grid max-w-3xl gap-3 md:grid-cols-2">
      {access.canSome("members.read") ? (
        <SettingsLink href="/ayarlar/ekip" title="Ekip" description="Kişileri davet edin, rollerini değiştirin, üyeliği askıya alın." Icon={Users} />
      ) : null}
      {access.can("tenant.settings.manage") ? (
        <SettingsLink
          href="/ayarlar/is-kurallari"
          title="İş kuralları"
          description="Satış onayı, iskonto sınırı, alış fiyatı görünürlüğü ve depo yetkileri."
          Icon={SlidersHorizontal}
        />
      ) : null}
      <SettingsLink href="/ayarlar/ozel-alanlar" title="Özel alanlar" description="Ürün ve varyantlara işletmenize özel bilgi alanları ekleyin." Icon={ListPlus} />
      {access.can("billing.read") ? (
        <SettingsLink
          href={PLAN_PAGE_HREF}
          title="Paket ve modüller"
          description="Mevcut paket, deneme süresi, kullanım sınırları ve ek modüller. Ödeme yok."
          Icon={Puzzle}
        />
      ) : null}
    </ul>
  );
}
