#!/usr/bin/env bash
# Builds a BayiOS release tarball for the self-hosted Linux deployment (infra/linux/README.md).
#
#   NEXT_PUBLIC_API_BASE_URL=https://api.example.com \
#   NEXT_PUBLIC_SUPABASE_URL=https://<project-ref>.supabase.co \
#   NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY=<publishable key> \
#     infra/linux/scripts/build-release.sh <version> [out-dir]
#
# Output: <out-dir>/bayios-<version>.tar.gz (+ .sha256) with
#   api/      framework-dependent `dotnet publish` output (run with `dotnet BayiOS.Api.dll`)
#   web/      Next.js build: .next (without .next/cache), production node_modules, package.json, next.config.ts
#   RELEASE   version, git commit, build time, API base URL baked into the web bundle
#
# Requirements: Linux x64 (same arch as the server; node_modules contain native binaries), .NET 10 SDK, Node 24 + npm,
# tar, gzip. NEXT_PUBLIC_* values are inlined into the web bundle at build time and are PUBLIC (never a secret):
# a release is therefore built per environment/domain. The repository checkout is only read: sources are copied to
# a temporary directory first (no bin/obj/node_modules are written into the checkout).
set -euo pipefail

usage() { echo "usage: $0 <version> [out-dir]" >&2; exit 2; }
[[ $# -ge 1 && $# -le 2 ]] || usage

version="$1"
out_dir="${2:-$PWD/dist}"
if [[ ! "$version" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]]; then
  echo "error: version must match [A-Za-z0-9][A-Za-z0-9._-]{0,63}" >&2
  exit 2
fi
: "${NEXT_PUBLIC_API_BASE_URL:?set NEXT_PUBLIC_API_BASE_URL (public API origin, e.g. https://api.example.com)}"
export NEXT_PUBLIC_API_BASE_URL
: "${NEXT_PUBLIC_SUPABASE_URL:?set NEXT_PUBLIC_SUPABASE_URL (https://<project-ref>.supabase.co)}"
: "${NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY:?set NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY (public publishable key)}"
export NEXT_PUBLIC_SUPABASE_URL NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY
export NEXT_PUBLIC_TURNSTILE_SITE_KEY="${NEXT_PUBLIC_TURNSTILE_SITE_KEY:-}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 NEXT_TELEMETRY_DISABLED=1

for tool in dotnet node npm tar gzip sha256sum; do
  command -v "$tool" >/dev/null || { echo "error: '$tool' not found on PATH" >&2; exit 1; }
done
node_major="$(node -p 'process.versions.node.split(".")[0]')"
if (( node_major < 24 )); then
  echo "error: Node >= 24 required (found $(node --version))" >&2
  exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
# BUILD_COMMIT: set by CI / the caller when the checkout is not a usable git repository (e.g. a mounted worktree).
git_sha="${BUILD_COMMIT:-$(git -C "$repo_root" rev-parse HEAD 2>/dev/null || echo unknown)}"
mkdir -p "$out_dir"
out_dir="$(cd "$out_dir" && pwd)"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
src="$work/src"
stage="$work/stage"
mkdir -p "$src" "$stage/api" "$stage/web"

echo "==> copying sources (api, web) to $src"
tar -C "$repo_root" \
  --exclude='bin' --exclude='obj' --exclude='TestResults' \
  --exclude='node_modules' --exclude='.next' --exclude='.env' --exclude='.env.*' \
  -cf - services/api apps/web | tar -C "$src" -xf -

echo "==> dotnet publish (Release, framework-dependent, no apphost)"
dotnet publish "$src/services/api/src/BayiOS.Api/BayiOS.Api.csproj" \
  --configuration Release \
  --output "$stage/api" \
  -p:UseAppHost=false
rm -f "$stage/api/appsettings.Development.json"
[[ -f "$stage/api/BayiOS.Api.dll" ]] || { echo "error: publish produced no BayiOS.Api.dll" >&2; exit 1; }

echo "==> web: npm ci + next build (NEXT_PUBLIC_API_BASE_URL=$NEXT_PUBLIC_API_BASE_URL)"
(
  cd "$src/apps/web"
  npm ci --no-audit --no-fund
  npm run build
  npm prune --omit=dev --no-audit --no-fund
  rm -rf .next/cache
)
for item in .next node_modules package.json package-lock.json next.config.ts public; do
  if [[ -e "$src/apps/web/$item" ]]; then
    cp -a "$src/apps/web/$item" "$stage/web/"
  fi
done
[[ -f "$stage/web/.next/BUILD_ID" ]] || { echo "error: next build produced no .next/BUILD_ID" >&2; exit 1; }

cat >"$stage/RELEASE" <<EOF
version=$version
git_commit=$git_sha
built_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
next_public_api_base_url=$NEXT_PUBLIC_API_BASE_URL
dotnet_sdk=$(dotnet --version)
node=$(node --version)
EOF

tarball="$out_dir/bayios-$version.tar.gz"
echo "==> packing $tarball"
tar -C "$stage" --owner=0 --group=0 --numeric-owner -czf "$tarball" RELEASE api web
(cd "$out_dir" && sha256sum "bayios-$version.tar.gz" >"bayios-$version.tar.gz.sha256")
echo "==> done: $tarball ($(du -h "$tarball" | cut -f1))"
cat "$stage/RELEASE"
