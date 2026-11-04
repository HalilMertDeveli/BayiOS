import Link from "next/link";
import { ListPlus } from "lucide-react";
import { ModulePlaceholder, placeholderMetadata } from "@/components/shell/module-placeholder";

export const metadata = placeholderMetadata("/ayarlar");

export default function AyarlarPage() {
  return (
    <ModulePlaceholder href="/ayarlar">
      <section aria-labelledby="ready-settings" className="flex flex-col gap-3">
        <h2 id="ready-settings" className="text-h3 text-fg">
          Kullanıma hazır ayarlar
        </h2>
        <Link
          href="/ayarlar/ozel-alanlar"
          className="flex max-w-md items-start gap-3 rounded-lg border border-line bg-card p-4 shadow-e1 hover:bg-sunken"
        >
          <ListPlus className="mt-0.5 size-5 shrink-0 text-brand-fg" aria-hidden="true" />
          <span className="flex flex-col gap-1">
            <span className="font-medium text-fg">Özel alanlar</span>
            <span className="text-caption text-fg-muted">Ürün ve varyantlara işletmenize özel bilgi alanları ekleyin.</span>
          </span>
        </Link>
      </section>
    </ModulePlaceholder>
  );
}
