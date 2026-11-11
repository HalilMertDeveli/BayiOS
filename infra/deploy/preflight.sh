#!/usr/bin/env bash
# Deploy preflight (issue #70): decides, from which secrets are PRESENT (values are never printed), whether a deploy
# job runs, is skipped, or must fail. Used by deploy-preview.yml / deploy-production.yml; runnable locally:
#
#   ENVIRONMENT=preview REQUIRED="PREVIEW_MIGRATOR_CONNECTION" PREVIEW_MIGRATOR_CONNECTION=... bash infra/deploy/preflight.sh
#
# Rules (fail closed, never fail open):
#   * none of REQUIRED set -> "skipped: secrets missing" notice, outputs ready=false, exit 0 (environment not set up yet)
#   * all of REQUIRED set   -> ready=true
#   * some but not all set  -> error naming the missing secrets, exit 1 (half-configured environment must not deploy:
#                              e.g. API without its migrations)
#   * STRICT=1 (production)  -> anything missing is an error; there is no "skip"
# OPTIONAL secrets only toggle their own step (has_<name>=true|false).
# Writes outputs to $GITHUB_OUTPUT and a summary to $GITHUB_STEP_SUMMARY when present.
set -euo pipefail

: "${ENVIRONMENT:?}" "${REQUIRED:?}"
OUT="${GITHUB_OUTPUT:-/dev/null}"
SUMMARY="${GITHUB_STEP_SUMMARY:-/dev/null}"

present=(); missing=()
for name in $REQUIRED; do
  if [ -n "${!name:-}" ]; then present+=("$name"); else missing+=("$name"); fi
done

{
  echo "### Deploy preflight: \`${ENVIRONMENT}\`"
  echo
  echo "| Secret / variable | Present |"
  echo "|---|---|"
  for name in $REQUIRED; do
    if [ -n "${!name:-}" ]; then echo "| \`${name}\` (required) | yes |"; else echo "| \`${name}\` (required) | **no** |"; fi
  done
  for name in ${OPTIONAL:-}; do
    if [ -n "${!name:-}" ]; then echo "| \`${name}\` (optional) | yes |"; else echo "| \`${name}\` (optional) | no |"; fi
  done
  echo
} >> "$SUMMARY"

for name in ${OPTIONAL:-}; do
  lower="$(printf '%s' "$name" | tr '[:upper:]' '[:lower:]')"
  if [ -n "${!name:-}" ]; then echo "has_${lower}=true" >> "$OUT"; else echo "has_${lower}=false" >> "$OUT"; fi
done

if [ "${#present[@]}" -eq 0 ] && [ "${STRICT:-0}" != 1 ]; then
  msg="skipped: secrets missing (${missing[*]}) in GitHub Environment '${ENVIRONMENT}'. Nothing was pushed, migrated or deployed. Owner setup: docs/operations/deploy-and-rollback.md §8."
  echo "::notice title=Deploy ${ENVIRONMENT} skipped::${msg}"
  echo "**${msg}**" >> "$SUMMARY"
  echo "ready=false" >> "$OUT"
  exit 0
fi

if [ "${#missing[@]}" -gt 0 ]; then
  msg="environment '${ENVIRONMENT}' is half-configured: missing ${missing[*]} (present: ${present[*]}). Refusing to deploy."
  echo "::error title=Deploy ${ENVIRONMENT} misconfigured::${msg}"
  echo "**error: ${msg}**" >> "$SUMMARY"
  echo "ready=false" >> "$OUT"
  exit 1
fi

echo "All required secrets present for '${ENVIRONMENT}'."
echo "ready=true" >> "$OUT"
