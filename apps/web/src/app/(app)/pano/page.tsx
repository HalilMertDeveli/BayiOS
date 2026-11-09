import { Dashboard } from "@/components/dashboard/dashboard";
import { RoleHomeRedirect } from "@/components/shell/role-home-redirect";

export const metadata = { title: "Pano" };

/** W03 Pano (#129): role-aware overview. Warehouse staff (no order desk) are sent on to Görevlerim (W12). */
export default function PanoPage() {
  return (
    <>
      <RoleHomeRedirect from="/pano" />
      <Dashboard />
    </>
  );
}
