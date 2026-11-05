"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { homeFor } from "@/lib/navigation";
import { useSession } from "@/lib/session/session-context";

/**
 * On the generic home (/pano, where sign-in lands): roles with their own start page (warehouse_staff → Görevlerim) are
 * sent there. Navigation only — nothing is hidden for security reasons.
 */
export function RoleHomeRedirect({ from }: { from: string }) {
  const { role } = useSession();
  const router = useRouter();
  const target = homeFor(role);
  useEffect(() => {
    if (role && target !== from) router.replace(target);
  }, [role, target, from, router]);
  return null;
}
