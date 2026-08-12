#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
base_compose="$repo_root/docker/docker-compose.yml"
test_compose="$repo_root/docker/docker-compose.egress-test.yml"
project="promptly-egress-$PPID-$$"
artifact_dir="${PROMPTLY_EGRESS_ARTIFACT_DIR:-$repo_root/artifacts/test-results/$project}"

if ! command -v docker >/dev/null 2>&1; then
  echo "docker is required for the egress policy proof" >&2
  exit 1
fi
docker info >/dev/null
docker compose version >/dev/null

if [[ -d "$artifact_dir" ]] && [[ -n "$(find "$artifact_dir" -mindepth 1 -print -quit)" ]]; then
  echo "egress artifact directory must be empty: $artifact_dir" >&2
  exit 1
fi
mkdir -p "$artifact_dir"
chmod 0711 "$artifact_dir"
artifact_dir="$(cd "$artifact_dir" && pwd -P)"

# The non-root test containers need write access only to these fixed evidence
# files, not to the artifact directory itself. Pre-create them with narrow,
# temporary permissions and return the artifact tree to its owner during cleanup.
container_evidence=(
  allowed.jsonl
  allowed-tls.jsonl
  private.jsonl
  metadata.jsonl
  rebind.jsonl
  reserved-v6.jsonl
  dns.jsonl
)
for evidence_name in "${container_evidence[@]}"; do
  : >"$artifact_dir/$evidence_name"
  chmod 0666 "$artifact_dir/$evidence_name"
done

export PROMPTLY_EGRESS_ARTIFACT_DIR="$artifact_dir"
export PROMPTLY_EGRESS_PROXY_CONTAINER_NAME="$project-proxy"
export PROMPTLY_EVAL_CONTAINER_NAME="$project-eval"
export JWT__Key="QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFB"
export JWT__RetiredKeyFingerprints=""
export PROMPTLY_LLM_API_KEY="verification-only"
export PROMPTLY_LLM_AZURE_ENDPOINT=""
export PROMPTLY_LLM_BASE_URL="${PROMPTLY_LLM_BASE_URL:-https://api.openai.com/v1}"

base=(docker compose --project-name "$project" --file "$base_compose")
test_stack=(docker compose --project-name "$project" --file "$base_compose" --file "$test_compose")

cleanup() {
  "${test_stack[@]}" down --remove-orphans >/dev/null 2>&1 || true
  "${base[@]}" down --remove-orphans >/dev/null 2>&1 || true
  for evidence_name in "${container_evidence[@]}"; do
    chmod 0600 "$artifact_dir/$evidence_name" >/dev/null 2>&1 || true
  done
  chmod 0700 "$artifact_dir" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

"${base[@]}" config --quiet
"${test_stack[@]}" config --quiet
"${base[@]}" config --format json \
  | python3 "$repo_root/docker/egress-proxy/tests/verify_topology.py" \
      >"$artifact_dir/production-topology.json"

python3 - \
  "$repo_root/docker/egress-proxy/tests/dns_rebinder.py" \
  "$repo_root/docker/egress-proxy/tests/dns_preflight.py" \
  "$repo_root/docker/egress-proxy/tests/recorder.py" \
  "$repo_root/docker/egress-proxy/tests/policy_client.py" \
  "$repo_root/docker/egress-proxy/tests/verify_topology.py" <<'PY'
from pathlib import Path
import sys

for raw_path in sys.argv[1:]:
    path = Path(raw_path)
    compile(path.read_text(encoding="utf-8"), str(path), "exec")
PY

# Inspect the production topology before the test overlay adds its synthetic
# destination networks.
"${base[@]}" up --detach --build --wait --wait-timeout 240 promptly-egress-proxy
production_proxy_id="$("${base[@]}" ps --quiet promptly-egress-proxy)"
docker inspect "$production_proxy_id" >"$artifact_dir/production-proxy-inspect.json"

python3 - "$artifact_dir/production-proxy-inspect.json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    proxy = json.load(source)[0]
networks = proxy["NetworkSettings"]["Networks"]
assert len(networks) == 2, networks
assert any(name.endswith("_promptly-control") for name in networks), networks
assert any(name.endswith("_promptly-egress") for name in networks), networks
host = proxy["HostConfig"]
assert host["ReadonlyRootfs"] is True
assert "ALL" in host["CapDrop"]
assert "no-new-privileges:true" in host["SecurityOpt"]
assert host["PidsLimit"] == 128
assert host["Memory"] == 128 * 1024 * 1024
assert host["NanoCpus"] == 1_000_000_000
assert proxy["Config"]["User"] == "65532:65532"
assert proxy["Config"]["Healthcheck"]["Test"] == ["CMD", "/smokescreen-healthcheck"]
assert not host.get("PortBindings")
assert all(value is None for value in proxy["NetworkSettings"].get("Ports", {}).values())
assert proxy["State"]["Running"] is True
assert proxy["State"]["Health"]["Status"] == "healthy"
PY

"${base[@]}" down --remove-orphans

"${test_stack[@]}" up --detach --build egress-policy-client
client_id="$("${test_stack[@]}" ps --all --quiet egress-policy-client)"
if [[ -z "$client_id" ]]; then
  echo "egress policy client container was not created" >&2
  exit 1
fi
client_exit="$(docker wait "$client_id")"
"${test_stack[@]}" ps --all >"$artifact_dir/compose-ps.txt"
"${test_stack[@]}" logs --no-color >"$artifact_dir/compose.log" 2>&1
docker inspect "$client_id" >"$artifact_dir/policy-client-inspect.json"
test_proxy_id="$("${test_stack[@]}" ps --all --quiet promptly-egress-proxy)"
docker inspect "$test_proxy_id" >"$artifact_dir/test-proxy-inspect.json"

if [[ "$client_exit" != "0" ]]; then
  echo "egress policy client failed with exit code $client_exit" >&2
  tail -200 "$artifact_dir/compose.log" >&2
  exit "$client_exit"
fi

python3 - \
  "$artifact_dir/policy-client-inspect.json" \
  "$artifact_dir/test-proxy-inspect.json" \
  "$artifact_dir/production-topology.json" \
  "$artifact_dir/allowed.jsonl" \
  "$artifact_dir/allowed-tls.jsonl" \
  "$artifact_dir/private.jsonl" \
  "$artifact_dir/metadata.jsonl" \
  "$artifact_dir/rebind.jsonl" \
  "$artifact_dir/reserved-v6.jsonl" \
  "$artifact_dir/dns.jsonl" \
  "$artifact_dir/compose.log" <<'PY'
import json
from pathlib import Path
import sys

client = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))[0]
test_proxy = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))[0]
production_topology = json.loads(Path(sys.argv[3]).read_text(encoding="utf-8"))

client_networks = client["NetworkSettings"]["Networks"]
assert len(client_networks) == 1, client_networks
assert next(iter(client_networks)).endswith("_promptly-control"), client_networks
assert client["HostConfig"]["ReadonlyRootfs"] is True
assert "ALL" in client["HostConfig"]["CapDrop"]

assert not test_proxy["HostConfig"].get("PortBindings")
assert test_proxy["HostConfig"]["ReadonlyRootfs"] is True
assert "ALL" in test_proxy["HostConfig"]["CapDrop"]
assert "no-new-privileges:true" in test_proxy["HostConfig"]["SecurityOpt"]

allowed_path = Path(sys.argv[4])
assert allowed_path.stat().st_size > 0
allowed = [json.loads(line) for line in allowed_path.read_text(encoding="utf-8").splitlines()]
assert len(allowed) == 1, allowed
assert allowed[0]["bytes"] > 0
assert allowed[0]["marker"] == "allowed"
assert allowed[0]["request_line"].startswith("GET ")
assert allowed[0]["tls"] is False

allowed_tls_path = Path(sys.argv[5])
assert allowed_tls_path.stat().st_size > 0
allowed_tls = [
    json.loads(line)
    for line in allowed_tls_path.read_text(encoding="utf-8").splitlines()
]
assert len(allowed_tls) >= 2, allowed_tls
assert all(row["bytes"] > 0 for row in allowed_tls)
assert all(row["marker"] == "allowed-tls" for row in allowed_tls)
assert all(row["tls"] is True for row in allowed_tls)
raw_connect_requests = [
    row for row in allowed_tls
    if row["request_line"] == "GET /allowed-tls HTTP/1.1"
]
assert len(raw_connect_requests) == 1, allowed_tls
sdk_requests = [
    row for row in allowed_tls
    if row["request_line"] == "GET /v1/models HTTP/1.1"
]
assert sdk_requests, allowed_tls
assert all(row["authorization_scheme"] == "Bearer" for row in sdk_requests)

for raw_path in sys.argv[6:10]:
    path = Path(raw_path)
    assert path.exists(), path
    assert path.stat().st_size == 0, (path, path.read_text(encoding="utf-8"))

dns = [json.loads(line) for line in Path(sys.argv[10]).read_text(encoding="utf-8").splitlines()]
rebind_answers = [row["answer"] for row in dns if row["name"] == "rebind.test" and row["type"] == 1]
assert rebind_answers[:2] == ["11.251.0.11", "10.252.0.11"], rebind_answers
rebind_v6_answers = [row["answer"] for row in dns if row["name"] == "rebind-v6.test" and row["type"] == 28]
assert rebind_v6_answers[:2] == ["2600:ff::11", "2001:2::10"], rebind_v6_answers

log = Path(sys.argv[11]).read_text(encoding="utf-8")
assert '"direct_bypass": "blocked"' in log
assert '"allowed": 200' in log
assert '"allowed_https_certificate": "verified"' in log
assert '"allowed_https_connect": 200' in log
assert '"allowed_https_origin": 200' in log
assert '"client_upstream_proxy": 407' in log
assert '"private_connect": 407' in log
for hostname in ("private.test", "metadata.test", "rebind.test", "rebind-v6.test"):
    assert hostname in log
assert '"policy_denial_message": "verified"' in log

assert production_topology == {
    "checks": {
        "application_services_control_network_only": True,
        "control_network_internal": True,
        "egress_network_external": True,
        "evaluator_provider_proxy_configured": True,
        "evaluator_test_instance_isolated": True,
        "host_ports_loopback_only": True,
        "proxy_is_only_dual_homed_service": True,
        "proxy_runtime_hardened": True,
        "server_ambient_proxy_cleared": True,
        "server_endpoint_proxy_required": True,
    },
    "published_ports": {
        "postgres": {"host_ip": "127.0.0.1", "published": 5432},
        "promptly-server": {"host_ip": "127.0.0.1", "published": 5000},
        "promptly-web": {"host_ip": "127.0.0.1", "published": 3000},
    },
    "schema": 1,
}
PY

echo "Egress policy verification passed. Evidence: $artifact_dir"
