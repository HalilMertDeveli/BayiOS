import { ApiStatus } from "@/components/api-status";

export default function Home() {
  return (
    <main className="mx-auto flex w-full max-w-2xl flex-1 flex-col justify-center gap-4 px-4 py-16">
      <h1 className="text-3xl font-semibold tracking-tight">BayiOS</h1>
      <p className="text-lg text-neutral-700 dark:text-neutral-200">
        WhatsApp&apos;tan gelen siparişten depodan çıkan son ürüne kadar stok ve sipariş operasyonlarınız tek yerde.
      </p>
      <ApiStatus />
    </main>
  );
}
