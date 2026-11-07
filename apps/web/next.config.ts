import type { NextConfig } from "next";
import { securityHeaders } from "./src/lib/security/headers";

const nextConfig: NextConfig = {
  /* config options here */
  cacheComponents: true,
  partialPrefetching: true,
  // No "X-Powered-By: Next.js" (security audit 2026-11 M-2).
  poweredByHeader: false,
  async headers() {
    return [
      {
        source: "/:path*",
        headers: securityHeaders({
          apiBaseUrl: process.env.NEXT_PUBLIC_API_BASE_URL,
          supabaseUrl: process.env.NEXT_PUBLIC_SUPABASE_URL,
          development: process.env.NODE_ENV === "development",
        }),
      },
    ];
  },
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
};

export default nextConfig;
