import Link from "next/link";
import { ArrowLeft } from "lucide-react";

/** "← Ayarlar" above a settings sub-page. */
export function SettingsBackLink() {
  return (
    <Link href="/ayarlar" className="inline-flex min-h-6 w-fit items-center gap-1 text-body text-brand-fg underline-offset-2 hover:underline">
      <ArrowLeft className="size-4" aria-hidden="true" />
      Ayarlar
    </Link>
  );
}
