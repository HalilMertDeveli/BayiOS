import { useId } from "react";
import { Info } from "@/components/ui/icons";

/** Credit line required by the UI kit's CC BY 4.0 licence (#222). Keep the wording as the owner approved it. */
export const UI_KIT_CREDIT =
  "UI tasarımı: Lukáš Marek, 'AI Inventory Management Web App UI Kit' (Figma Community), CC BY 4.0. Değiştirilerek kullanılmıştır.";

/** Ayarlar › Hakkında: product name and third-party credits. */
export function AboutSection() {
  const uid = useId();
  return (
    <section aria-labelledby={`${uid}-about`} className="flex flex-col gap-3 rounded-2xl border border-line bg-card p-5 shadow-e1">
      <h2 id={`${uid}-about`} className="flex items-center gap-2 text-h3 text-fg">
        <Info className="size-5 text-fg-muted" />
        Hakkında
      </h2>
      <p className="text-body text-fg-secondary">BayiOS — stok, depo ve sipariş yönetimi.</p>
      <p className="text-body text-fg-muted">
        {UI_KIT_CREDIT}{" "}
        <a
          href="https://creativecommons.org/licenses/by/4.0/deed.tr"
          target="_blank"
          rel="noopener noreferrer"
          className="font-medium text-link underline underline-offset-2"
        >
          Lisans
        </a>
      </p>
    </section>
  );
}
