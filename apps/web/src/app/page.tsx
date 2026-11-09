import Link from "next/link";
import { ApiStatus } from "@/components/api-status";
import { buttonClasses } from "@/components/ui/button";

export default function Home() {
  return (
    <main className="mx-auto flex w-full max-w-2xl flex-1 flex-col justify-center gap-4 px-4 py-16">
      <h1 className="text-display text-fg">BayiOS</h1>
      <p className="text-body-lg text-fg-secondary">
        WhatsApp&apos;tan gelen siparişten depodan çıkan son ürüne kadar stok ve sipariş operasyonlarınız tek yerde.
      </p>
      <ApiStatus />
      <div className="flex flex-wrap gap-2">
        <Link href="/kayit" className={buttonClasses("primary", "md")}>
          Ücretsiz başla
        </Link>
        <Link href="/giris" className={buttonClasses("secondary", "md")}>
          Giriş yap
        </Link>
        <Link href="/pano" className={buttonClasses("ghost", "md")}>
          Panele git
        </Link>
      </div>
    </main>
  );
}
