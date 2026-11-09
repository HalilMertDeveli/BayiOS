#!/usr/bin/env bash
# ci-gate: the single required status check for PRs (issue #71, ci-cd.md §1/§6).
#
# Path-filtered workflows (e.g. ci-container, ci-migrations) never start when their paths are untouched, so they
# cannot be required checks themselves (the PR would wait forever). This gate waits until every other workflow run
# triggered by the pull_request event for the PR head commit has finished, plus every non-Actions check run
# (e.g. the CodeQL alert check) and commit status, and fails if any of them did not succeed.
# A workflow skipped by its path filter has no run at all, so it is legitimately ignored.
#
# Env: GH_TOKEN, REPO (owner/name), SHA (PR head), SELF_RUN_ID, SELF_WORKFLOW_PATH, SELF_CHECK_NAME,
#      optional INITIAL_DELAY (s, default 30), POLL_INTERVAL (s, default 20), TIMEOUT (s, default 3300).
# Local dry run against an existing commit:
#   GH_TOKEN=$(gh auth token) REPO=owner/name SHA=<sha> SELF_RUN_ID=0 INITIAL_DELAY=0 bash infra/ci/ci-gate.sh
set -euo pipefail

: "${REPO:?}" "${SHA:?}"
SELF_RUN_ID="${SELF_RUN_ID:-0}"
SELF_WORKFLOW_PATH="${SELF_WORKFLOW_PATH:-.github/workflows/ci-gate.yml}"
SELF_CHECK_NAME="${SELF_CHECK_NAME:-ci gate}"
INITIAL_DELAY="${INITIAL_DELAY:-30}"
POLL_INTERVAL="${POLL_INTERVAL:-20}"
TIMEOUT="${TIMEOUT:-3300}"

ok_conclusion() { case "$1" in success|neutral|skipped) return 0 ;; *) return 1 ;; esac; }

# Give every workflow triggered by the same event time to be created before the first look.
echo "Gate for ${REPO}@${SHA}; waiting ${INITIAL_DELAY}s for workflows to be queued"
sleep "$INITIAL_DELAY"
deadline=$(( $(date +%s) + TIMEOUT ))

while :; do
  pending=0
  failed=0
  report=""

  # Latest run per workflow for the PR event (a re-run or re-opened PR supersedes older runs of the same workflow).
  runs="$(gh api --paginate "repos/${REPO}/actions/runs?head_sha=${SHA}&event=pull_request&per_page=100" \
    --jq ".workflow_runs[] | select(.id != ${SELF_RUN_ID} and .path != \"${SELF_WORKFLOW_PATH}\")
          | [.workflow_id, .id, .status, (.conclusion // \"-\"), .name] | @tsv" \
    | sort -t$'\t' -k1,1n -k2,2nr | awk -F'\t' '!seen[$1]++')"
  while IFS=$'\t' read -r _ id status conclusion name; do
    [ -n "${id:-}" ] || continue
    if [ "$status" != completed ]; then pending=$((pending + 1)); state="… ${status}"
    elif ok_conclusion "$conclusion"; then state="✓ ${conclusion}"
    else failed=$((failed + 1)); state="✗ ${conclusion}"; fi
    report+="  workflow  ${name}: ${state}  (https://github.com/${REPO}/actions/runs/${id})"$'\n'
  done <<< "$runs"

  # Check runs from other apps (Actions jobs are already covered by their workflow run above).
  checks="$(gh api --paginate "repos/${REPO}/commits/${SHA}/check-runs?filter=latest&per_page=100" \
    --jq ".check_runs[] | select(.app.slug != \"github-actions\" and .name != \"${SELF_CHECK_NAME}\")
          | [.name, .status, (.conclusion // \"-\"), .app.slug] | @tsv")"
  while IFS=$'\t' read -r name status conclusion app; do
    [ -n "${name:-}" ] || continue
    if [ "$status" != completed ]; then pending=$((pending + 1)); state="… ${status}"
    elif ok_conclusion "$conclusion"; then state="✓ ${conclusion}"
    else failed=$((failed + 1)); state="✗ ${conclusion}"; fi
    report+="  check     ${name} (${app}): ${state}"$'\n'
  done <<< "$checks"

  # Legacy commit statuses (none today; e.g. external deploy previews).
  statuses="$(gh api "repos/${REPO}/commits/${SHA}/status" --jq '.statuses[] | [.context, .state] | @tsv')"
  while IFS=$'\t' read -r context state_raw; do
    [ -n "${context:-}" ] || continue
    case "$state_raw" in
      success) state="✓ success" ;;
      pending) pending=$((pending + 1)); state="… pending" ;;
      *) failed=$((failed + 1)); state="✗ ${state_raw}" ;;
    esac
    report+="  status    ${context}: ${state}"$'\n'
  done <<< "$statuses"

  printf '\n[%s] %d pending, %d failed\n%s' "$(date -u +%H:%M:%S)" "$pending" "$failed" "$report"

  if [ "$failed" -gt 0 ]; then
    echo "::error::ci-gate: ${failed} workflow(s)/check(s) did not succeed. After fixing or re-running them, re-run this gate."
    exit 1
  fi
  if [ "$pending" -eq 0 ]; then
    # Require two consecutive green polls: some checks (e.g. the CodeQL alert check) are created only after
    # their workflow finishes.
    if [ "${green_once:-0}" = 1 ]; then
      echo "ci-gate: all workflows and checks for ${SHA} passed (path-filtered workflows that did not start are skipped)."
      exit 0
    fi
    green_once=1
  else
    green_once=0
  fi
  if [ "$(date +%s)" -ge "$deadline" ]; then
    echo "::error::ci-gate: timed out after ${TIMEOUT}s waiting for ${pending} workflow(s)/check(s)."
    exit 1
  fi
  sleep "$POLL_INTERVAL"
done
