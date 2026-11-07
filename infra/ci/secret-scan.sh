#!/usr/bin/env bash
# Secret scan with gitleaks (threat model R6 / #54; RC-1 risk R-7). Not yet wired into a workflow (that needs a
# .github/workflows change — see docs/security/audit-2026-11.md §6); runnable locally and in CI from the repo root
# (needs bash, git, Docker):
#
#   bash infra/ci/secret-scan.sh                          # every commit of every local branch (git history)
#   SCAN_DIR=apps/web/.next bash infra/ci/secret-scan.sh --no-git   # a directory, e.g. the web build output
#
# Exits 1 on any finding. Output is redacted; the JSON report is written to $REPORT (default: gitleaks-report.json in
# the current directory). The image is pinned by digest.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
IMAGE="${GITLEAKS_IMAGE:-zricethezav/gitleaks:v8.28.0@sha256:cdbb7c955abce02001a9f6c9f602fb195b7fadc1e812065883f695d1eeaba854}"
REPORT="${REPORT:-$PWD/gitleaks-report.json}"
native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/out"

if [ "${1:-}" = "--no-git" ]; then
  target="$(cd "${REPO_ROOT}/${SCAN_DIR:?SCAN_DIR is required with --no-git}" && pwd)"
  args=(dir /scan)
else
  # A plain clone (also works from a git worktree, whose .git is only a pointer file): all local branches + tags.
  git clone --quiet --no-checkout "$REPO_ROOT" "$work/repo"
  git -C "$REPO_ROOT" for-each-ref --format='%(refname:short)' refs/heads | while read -r branch; do
    git -C "$work/repo" fetch --quiet origin "refs/heads/${branch}:refs/heads/${branch}" 2>/dev/null || true
  done
  target="$work/repo"
  args=(git /scan --log-opts=--all)
fi

status=0
MSYS_NO_PATHCONV=1 docker run --rm -v "$(native_path "$target"):/scan:ro" -v "$(native_path "$work/out"):/out" \
  -v "$(native_path "${REPO_ROOT}/infra/ci"):/cfg:ro" "$IMAGE" \
  "${args[@]}" --config /cfg/gitleaks.toml --redact --no-banner --exit-code 1 --report-format json --report-path /out/report.json || status=$?
cp "$work/out/report.json" "$REPORT" 2>/dev/null || true
exit "$status"
