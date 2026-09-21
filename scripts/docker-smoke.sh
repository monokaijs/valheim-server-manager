#!/usr/bin/env bash
set -euo pipefail

: "${SERVER_PASSWORD:=vsm-smoke-secret}"
export SERVER_PASSWORD SERVER_NAME="VSM Validation" SERVER_WORLD="VSMSmoke" VSM_HTTP_PORT="${VSM_HTTP_PORT:-18080}"
export VSM_PUBLIC_URL="http://127.0.0.1:${VSM_HTTP_PORT}"
project="vsm-smoke"
base="http://127.0.0.1:${VSM_HTTP_PORT}"
cleanup() {
  if [[ "${KEEP_SMOKE_STACK:-false}" != "true" ]]; then docker compose -p "$project" down -v; fi
}
trap cleanup EXIT

docker compose -p "$project" up --build -d
for _ in $(seq 1 180); do
  curl -fsS "$base/healthz" >/dev/null 2>&1 && break
  sleep 5
done
curl -fsS "$base/healthz" >/dev/null

for _ in $(seq 1 180); do
  status="$(curl -fsS "$base/healthz")"
  [[ "$status" == *'"agentConnected":true'* && "$status" == *'"serverStatus":"running"'* ]] && break
  sleep 5
done
[[ "$status" == *'"agentConnected":true'* && "$status" == *'"serverStatus":"running"'* ]]

docker compose -p "$project" restart valheim-manager

for _ in $(seq 1 120); do
  status="$(curl -fsS "$base/healthz" 2>/dev/null || true)"
  [[ "$status" == *'"agentConnected":true'* && "$status" == *'"serverStatus":"running"'* ]] && break
  sleep 5
done
[[ "$status" == *'"agentConnected":true'* && "$status" == *'"serverStatus":"running"'* ]]
echo "Docker smoke test passed."
