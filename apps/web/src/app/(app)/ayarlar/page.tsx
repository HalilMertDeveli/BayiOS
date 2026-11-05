import { OrderSettings } from "@/components/settings/order-settings";
import { SettingsLinks } from "@/components/settings/settings-links";
import { PageHeader } from "@/components/ui/page-header";

export const metadata = { title: "Ayarlar" };

/**
 * Ayarlar: order settings (#92), plan & add-ons (#51/#52) and the settings pages that are ready. Team, roles and the
 * business profile come later.
 */
export default function AyarlarPage() {
  return (
    <>
      <PageHeader title="Ayarlar" description="İşletmenizin sipariş kuralları ve kullanıma hazır ayarlar." />
      <OrderSettings />
      <section aria-labelledby="ready-settings" className="flex flex-col gap-3">
        <h2 id="ready-settings" className="text-h3 text-fg">
          Diğer ayarlar
        </h2>
        <SettingsLinks />
        <p className="text-caption text-fg-muted">Ekip, roller ve işletme profili ayarları henüz geliştiriliyor.</p>
      </section>
    </>
  );
}
