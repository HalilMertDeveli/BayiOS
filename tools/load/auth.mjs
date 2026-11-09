// Test-only identity provider for the load stack (#58). Replaces Supabase Auth so the load run needs no Supabase and
// can never touch a developer's local Supabase (54321). It:
//   1. generates a throwaway ES256 key pair in memory (never written to disk, never a real key),
//   2. writes <state>/tokens.json: access tokens shaped like Supabase's (iss/aud/sub/email/user_metadata), valid 8 h,
//   3. serves the public JWKS at http://127.0.0.1:<port>/auth/v1/.well-known/jwks.json — the API validates every token
//      against it exactly like production (asymmetric only, issuer + audience + lifetime checked).
//
// Usage: node tools/load/auth.mjs <stateDir> <port> [users=8]
import { generateKeyPairSync, randomUUID, sign } from "node:crypto";
import { writeFileSync } from "node:fs";
import { createServer } from "node:http";
import path from "node:path";

const [stateDir, portArg, usersArg] = process.argv.slice(2);
if (!stateDir || !portArg) {
  console.error("usage: node tools/load/auth.mjs <stateDir> <port> [users]");
  process.exit(2);
}
const port = Number(portArg);
const users = Number(usersArg ?? 8);
const issuer = `http://127.0.0.1:${port}/auth/v1`;
const kid = `load-${randomUUID().slice(0, 8)}`;
const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
const jwk = { ...publicKey.export({ format: "jwk" }), kid, alg: "ES256", use: "sig" };

const b64url = (value) => Buffer.from(typeof value === "string" ? value : JSON.stringify(value)).toString("base64url");

function token(sub, email) {
  const now = Math.floor(Date.now() / 1000);
  const header = b64url({ alg: "ES256", kid, typ: "JWT" });
  const payload = b64url({
    iss: issuer,
    aud: "authenticated",
    sub,
    email,
    role: "authenticated",
    iat: now,
    nbf: now - 5,
    exp: now + 8 * 3600,
    is_anonymous: false,
    user_metadata: { email, email_verified: true, sub },
  });
  const signature = sign("sha256", Buffer.from(`${header}.${payload}`), { key: privateKey, dsaEncoding: "ieee-p1363" });
  return `${header}.${payload}.${signature.toString("base64url")}`;
}

const accounts = Array.from({ length: users }, (_, i) => {
  const sub = randomUUID();
  const email = `load.user${i + 1}@example.test`;
  return { sub, email, token: token(sub, email) };
});
writeFileSync(path.join(stateDir, "tokens.json"), JSON.stringify({ issuer, users: accounts }, null, 2));

createServer((req, res) => {
  if (req.url === "/auth/v1/.well-known/jwks.json") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ keys: [jwk] }));
    return;
  }
  res.writeHead(404).end();
}).listen(port, "127.0.0.1", () => console.log(`load auth: issuer ${issuer}, ${users} users, kid ${kid}`));
