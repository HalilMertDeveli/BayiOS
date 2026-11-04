import { ModulePlaceholder, placeholderMetadata } from "@/components/shell/module-placeholder";

export const metadata = placeholderMetadata("/urunler");

export default function UrunlerPage() {
  return <ModulePlaceholder href="/urunler" />;
}
