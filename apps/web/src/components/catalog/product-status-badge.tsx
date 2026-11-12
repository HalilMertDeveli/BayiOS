import { Archive, CircleCheck, CirclePause } from "@/components/ui/icons";
import { Badge } from "@/components/ui/badge";

/** Product/variant status (active | inactive | archived) as a text badge. */
export function ProductStatusBadge({ status }: { status: string }) {
  if (status === "archived") {
    return (
      <Badge tone="warning" icon={<Archive className="size-3.5" aria-hidden="true" />}>
        Arşivlenmiş
      </Badge>
    );
  }
  if (status === "inactive") {
    return (
      <Badge tone="neutral" icon={<CirclePause className="size-3.5" aria-hidden="true" />}>
        Pasif
      </Badge>
    );
  }
  return (
    <Badge tone="success" icon={<CircleCheck className="size-3.5" aria-hidden="true" />}>
      Aktif
    </Badge>
  );
}
