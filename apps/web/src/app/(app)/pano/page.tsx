import { ApiStatus } from "@/components/api-status";
import { ModulePlaceholder, placeholderMetadata } from "@/components/shell/module-placeholder";

export const metadata = placeholderMetadata("/pano");

export default function PanoPage() {
  return (
    <ModulePlaceholder href="/pano">
      <section aria-labelledby="system-status" className="rounded-lg border border-line bg-card p-4 shadow-e1">
        <h2 id="system-status" className="text-h3 text-fg">
          Sistem durumu
        </h2>
        <div className="mt-2">
          <ApiStatus />
        </div>
      </section>
    </ModulePlaceholder>
  );
}
