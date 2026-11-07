import { defineConfig, globalIgnores } from "eslint/config";
import nextVitals from "eslint-config-next/core-web-vitals";
import nextTs from "eslint-config-next/typescript";

const eslintConfig = defineConfig([
  ...nextVitals,
  ...nextTs,
  {
    // Threat-model §3.1: no raw HTML sinks. The CSP cannot use nonces (Cache Components, see
    // src/lib/security/headers.ts), so React's escaping is the line of defence for inline injection.
    rules: {
      "react/no-danger": "error",
      "no-eval": "error",
      "no-implied-eval": "error",
    },
  },
  {
    // Reading innerHTML in a test assertion is not a sink.
    files: ["src/**/*.{ts,tsx}"],
    ignores: ["**/*.test.{ts,tsx}", "src/test/**"],
    rules: {
      "no-restricted-properties": [
        "error",
        { property: "innerHTML", message: "Raw HTML sink (threat-model §3.1)." },
        { property: "outerHTML", message: "Raw HTML sink (threat-model §3.1)." },
        { property: "insertAdjacentHTML", message: "Raw HTML sink (threat-model §3.1)." },
      ],
    },
  },
  // Override default ignores of eslint-config-next.
  globalIgnores([
    // Default ignores of eslint-config-next:
    ".next/**",
    "out/**",
    "build/**",
    "next-env.d.ts",
  ]),
]);

export default eslintConfig;
