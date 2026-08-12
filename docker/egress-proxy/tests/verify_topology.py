#!/usr/bin/env python3
"""Validate production Compose topology without persisting interpolated secrets."""

from __future__ import annotations

import json
import os
from pathlib import Path
import sys


if len(sys.argv) != 2:
    raise SystemExit("usage: verify_topology.py OUTPUT_PATH")

compose = json.load(sys.stdin)
services = compose["services"]

for name in ("postgres", "promptly-server", "promptly-eval", "promptly-web"):
    assert set(services[name]["networks"]) == {"promptly-control"}, (
        name,
        services[name]["networks"],
    )
assert set(services["promptly-egress-proxy"]["networks"]) == {
    "promptly-control",
    "promptly-egress",
}
assert compose["networks"]["promptly-control"]["internal"] is True
assert not compose["networks"]["promptly-egress"].get("internal", False)
assert services["promptly-eval"]["container_name"] == os.environ[
    "PROMPTLY_EVAL_CONTAINER_NAME"
]

proxy = services["promptly-egress-proxy"]
assert not proxy.get("ports")
assert proxy["read_only"] is True
assert proxy["user"] == "65532:65532"
assert "ALL" in proxy["cap_drop"]
assert "no-new-privileges:true" in proxy["security_opt"]
assert proxy["pids_limit"] == 128
assert int(proxy["mem_limit"]) == 128 * 1024 * 1024
assert proxy["healthcheck"]["test"] == ["CMD", "/smokescreen-healthcheck"]

published_ports: dict[str, dict[str, object]] = {}
for name, port in (
    ("postgres", 5432),
    ("promptly-server", 5000),
    ("promptly-web", 3000),
):
    bindings = services[name]["ports"]
    assert len(bindings) == 1
    assert bindings[0]["host_ip"] == "127.0.0.1"
    assert int(bindings[0]["published"]) == port
    published_ports[name] = {"host_ip": "127.0.0.1", "published": port}

server_env = services["promptly-server"]["environment"]
assert server_env["EndpointEgress__RequireProxy"] == "true"
assert server_env["EndpointEgress__ProxyUrl"] == "http://promptly-egress-proxy:4750"
for key in (
    "HTTP_PROXY",
    "HTTPS_PROXY",
    "ALL_PROXY",
    "http_proxy",
    "https_proxy",
    "all_proxy",
):
    assert server_env[key] == ""

eval_env = services["promptly-eval"]["environment"]
assert eval_env["PROMPTLY_LLM_BASE_URL"] == os.environ["PROMPTLY_LLM_BASE_URL"]
for key in ("HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy"):
    assert eval_env[key] == "http://promptly-egress-proxy:4750"
for key in ("NO_PROXY", "no_proxy"):
    assert "promptly-eval" in eval_env[key] and "127.0.0.1" in eval_env[key]

# Never serialize the source Compose object: it contains fully interpolated
# environment values and may therefore contain operator credentials. Persist
# only this fixed-schema, non-secret attestation of the checks above.
attestation = {
    "schema": 1,
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
    "published_ports": published_ports,
}
output_path = Path(sys.argv[1])
output_path.write_text(
    json.dumps(attestation, indent=2, sort_keys=True) + "\n",
    encoding="utf-8",
)
