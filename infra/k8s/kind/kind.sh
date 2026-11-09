#!/usr/bin/env bash
# BayiOS on a throwaway kind cluster — used by .github/workflows/ci-k8s.yml and runnable locally (Linux, macOS,
# Git Bash on Windows with Docker Desktop):
#
#   bash infra/k8s/kind/kind.sh up      # build images, generate migration scripts, create cluster, helm install
#   bash infra/k8s/kind/kind.sh smoke   # probe API /health, /health/ready, an auth-protected endpoint, web /
#   bash infra/k8s/kind/kind.sh down    # delete the cluster
#
# Env (all optional):
#   CLUSTER=bayios  NAMESPACE=bayios  RELEASE=bayios
#   SKIP_BUILD=1          reuse existing bayios-api:kind / bayios-web:kind images
#   SCRIPTS_DIR=<dir>     reuse EF Core scripts already produced by infra/ci/ef-migrations.sh (else generated)
#   KIND, KUBECTL, HELM   binaries to use (default: from PATH)
#
# No secret is committed or printed: the PostgreSQL passwords are random per cluster and live only in its Secret.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
CLUSTER="${CLUSTER:-bayios}"
NAMESPACE="${NAMESPACE:-bayios}"
RELEASE="${RELEASE:-bayios}"
KIND="${KIND:-kind}"
KUBECTL="${KUBECTL:-kubectl}"
HELM="${HELM:-helm}"
CHART="${REPO_ROOT}/infra/k8s/helm/bayios"
VALUES="${REPO_ROOT}/infra/k8s/kind/values-kind.yaml"
CONTEXT="kind-${CLUSTER}"

log() { printf '\n==> %s\n' "$*"; }
fail() { printf '::error::%s\n' "$*" >&2; exit 1; }
k() { "$KUBECTL" --context "$CONTEXT" -n "$NAMESPACE" "$@"; }
native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
random_secret() { od -An -N24 -tx1 /dev/urandom | tr -d ' \n'; }

need() { command -v "$1" >/dev/null 2>&1 || fail "$1 not found (set ${2:-} or add it to PATH)"; }

up() {
  need "$KIND" KIND; need "$KUBECTL" KUBECTL; need "$HELM" HELM; need docker

  if [ "${SKIP_BUILD:-0}" != 1 ]; then
    log "Building images"
    docker build -t bayios-api:kind "$(native_path "${REPO_ROOT}/services/api")"
    # NEXT_PUBLIC_* are public build args; the browser talks to the API through a port-forward in this setup.
    docker build -t bayios-web:kind \
      --build-arg NEXT_PUBLIC_API_BASE_URL=http://localhost:8080 \
      "$(native_path "${REPO_ROOT}/apps/web")"
  fi

  if [ -z "${SCRIPTS_DIR:-}" ]; then
    SCRIPTS_DIR="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/bayios-k8s-migrations"
    log "Generating idempotent EF Core scripts into ${SCRIPTS_DIR}"
    OUT_DIR="$SCRIPTS_DIR" bash "${REPO_ROOT}/infra/ci/ef-migrations.sh"
  fi
  [ -s "${SCRIPTS_DIR}/manifest.txt" ] || fail "${SCRIPTS_DIR}/manifest.txt missing"

  if "$KIND" get clusters 2>/dev/null | grep -qx "$CLUSTER"; then
    log "kind cluster '${CLUSTER}' already exists"
  else
    log "Creating kind cluster '${CLUSTER}'"
    "$KIND" create cluster --name "$CLUSTER" --config "$(native_path "${REPO_ROOT}/infra/k8s/kind/cluster.yaml")" --wait 120s
  fi

  log "Loading images into the cluster"
  "$KIND" load docker-image --name "$CLUSTER" bayios-api:kind bayios-web:kind

  "$KUBECTL" --context "$CONTEXT" create namespace "$NAMESPACE" --dry-run=client -o yaml \
    | "$KUBECTL" --context "$CONTEXT" apply -f - >/dev/null
  "$KUBECTL" --context "$CONTEXT" label namespace "$NAMESPACE" --overwrite \
    pod-security.kubernetes.io/enforce=restricted pod-security.kubernetes.io/enforce-version=latest >/dev/null

  log "Migration scripts ConfigMap"
  k create configmap bayios-migrations --from-file="$(native_path "$SCRIPTS_DIR")" --dry-run=client -o yaml \
    | k apply -f - >/dev/null

  # Reuse the passwords of an existing install (the data volume was initialised with them).
  local pg_secret="${RELEASE}-postgres" pg app mig
  if k get secret "$pg_secret" >/dev/null 2>&1; then
    pg="$(k get secret "$pg_secret" -o jsonpath='{.data.postgres-password}' | base64 -d)"
    app="$(k get secret "$pg_secret" -o jsonpath='{.data.app-password}' | base64 -d)"
    mig="$(k get secret "$pg_secret" -o jsonpath='{.data.migrator-password}' | base64 -d)"
  else
    pg="$(random_secret)"; app="$(random_secret)"; mig="$(random_secret)"
  fi

  log "helm upgrade --install ${RELEASE}"
  # Values go through a temporary file (mode 600) so they never appear in a process list or a CI log.
  local secrets_file
  secrets_file="$(mktemp)"
  chmod 600 "$secrets_file"
  trap 'rm -f "$secrets_file"' RETURN
  printf 'postgres:\n  auth:\n    postgresPassword: "%s"\n    appPassword: "%s"\n    migratorPassword: "%s"\napi:\n  secret:\n    values:\n      Audit__IpHashKey: "%s"\n' \
    "$pg" "$app" "$mig" "$(random_secret)" > "$secrets_file"
  if ! "$HELM" --kube-context "$CONTEXT" upgrade --install "$RELEASE" "$(native_path "$CHART")" \
      --namespace "$NAMESPACE" \
      -f "$(native_path "$VALUES")" -f "$(native_path "$secrets_file")" \
      --wait --wait-for-jobs --timeout 10m; then
    diagnostics
    fail "helm install did not become ready"
  fi
  k get pods -o wide
}

diagnostics() {
  log "Diagnostics"
  k get all,pvc,networkpolicy,pdb -o wide || true
  k get events --sort-by=.lastTimestamp | tail -n 40 || true
  for pod in $(k get pods -o name 2>/dev/null); do
    echo "--- logs ${pod}"
    k logs "$pod" --all-containers --tail=60 || true
  done
}

smoke() {
  need "$KUBECTL" KUBECTL; need curl
  local api_pf web_pf
  # `port-forward svc/...` pins one pod when it starts; make sure no old pod of a rollout is still terminating.
  k rollout status "deployment/${RELEASE}-api" --timeout=5m
  k rollout status "deployment/${RELEASE}-web" --timeout=5m
  for _ in $(seq 1 90); do
    [ -z "$(k get pods -o jsonpath='{.items[?(@.metadata.deletionTimestamp)].metadata.name}')" ] && break
    sleep 2
  done
  # Fresh random local ports each run: a port-forward left over from an earlier run (e.g. on Windows, where
  # killing the bash job may not stop kubectl.exe) must never answer for this one.
  local api_port=$(( 20000 + RANDOM % 10000 )) web_port=$(( 30000 + RANDOM % 10000 ))
  log "Port-forwarding API (${api_port}) and web (${web_port})"
  k port-forward "svc/${RELEASE}-api" "${api_port}:80" >/dev/null 2>&1 & api_pf=$!
  k port-forward "svc/${RELEASE}-web" "${web_port}:80" >/dev/null 2>&1 & web_pf=$!
  trap 'kill "$api_pf" "$web_pf" 2>/dev/null || true' RETURN

  local c=(curl --silent --show-error --max-time 10 --retry 20 --retry-all-errors --retry-delay 1)
  local failed=0

  body="$("${c[@]}" --fail http://127.0.0.1:${api_port}/health)" || { echo "API /health failed"; failed=1; }
  echo "API /health        -> ${body:-}"
  body="$("${c[@]}" --fail http://127.0.0.1:${api_port}/health/ready)" || { echo "API /health/ready failed"; failed=1; }
  echo "API /health/ready  -> ${body:-}"

  # Fail-closed check: a data endpoint without a token must be rejected, never served.
  code="$("${c[@]}" -o /dev/null -w '%{http_code}' http://127.0.0.1:${api_port}/api/v1/products)" || true
  echo "API /api/v1/products (no token) -> HTTP ${code}"
  [ "$code" = 401 ] || { echo "expected 401 without a token"; failed=1; }

  code="$("${c[@]}" -o /tmp/bayios-web-home.html -w '%{http_code}' http://127.0.0.1:${web_port}/)" || true
  echo "web /              -> HTTP ${code}"
  if [ "$code" != 200 ] || ! grep -q 'BayiOS' /tmp/bayios-web-home.html; then
    echo "web home page did not render"; failed=1
  fi
  csp="$("${c[@]}" -o /dev/null -D - http://127.0.0.1:${web_port}/ | grep -i '^content-security-policy:' || true)"
  [ -n "$csp" ] || { echo "web response has no Content-Security-Policy header"; failed=1; }

  log "Security posture"
  # Every container must run as non-root with a read-only root filesystem (Pod Security 'restricted' enforces most).
  k get pods -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{range .spec.containers[*]}{.securityContext.readOnlyRootFilesystem}{" "}{end}{"\n"}{end}'
  [ "$failed" = 0 ] || { diagnostics; fail "smoke test failed"; }
  log "OK: API and web are healthy on kind"
}

down() {
  need "$KIND" KIND
  "$KIND" delete cluster --name "$CLUSTER"
}

case "${1:-}" in
  up) up ;;
  smoke) smoke ;;
  down) down ;;
  diagnostics) diagnostics ;;
  *) echo "usage: $0 up|smoke|down|diagnostics" >&2; exit 2 ;;
esac
