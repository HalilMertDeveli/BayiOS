"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { homeFor } from "@/lib/navigation";
import { useSession } from "@/lib/session/session-context";

/**
 * On the generic home (/pano, where sign-in lands): users with their own start page (warehouse tasks without the order
 * desk → Görevlerim, from the GET /me permissions) are sent there. Navigation only — nothing is hidden for security reasons.
 */
export function RoleHomeRedirect({ from }: { from: string }) {
  const { access } = useSession();
  const router = useRouter();
  const ready = access.grants.length > 0;
  const target = homeFor(access);
  useEffect(() => {
    if (ready && target !== from) router.replace(target);
  }, [ready, target, from, router]);
  return null;
}
