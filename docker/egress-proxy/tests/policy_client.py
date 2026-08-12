#!/usr/bin/env python3
"""Exercise the real proxy boundary from a control-network-only client."""

from __future__ import annotations

import json
import socket
import ssl
import time


PROXY = ("promptly-egress-proxy", 4750)
TLS_CA_PATH = "/test/tls/ca.crt"


def response_status(response: bytes) -> int:
    first_line = response.split(b"\r\n", 1)[0].decode("ascii")
    return int(first_line.split(" ", 2)[1])


def receive_until_close(connection: socket.socket) -> bytes:
    response = bytearray()
    while True:
        chunk = connection.recv(4096)
        if not chunk:
            break
        response.extend(chunk)
    return bytes(response)


def request_through_proxy(host: str, path: str) -> tuple[int, str]:
    with socket.create_connection(PROXY, timeout=3) as connection:
        connection.settimeout(5)
        wire = (
            f"GET http://{host}:8080{path} HTTP/1.1\r\n"
            f"Host: {host}:8080\r\n"
            "Connection: close\r\n"
            "User-Agent: promptly-egress-policy-test\r\n\r\n"
        ).encode("ascii")
        connection.sendall(wire)
        response = receive_until_close(connection)
    return response_status(response), response.decode("utf-8", errors="replace")


def connect_through_proxy(
    host: str,
    port: int,
    extra_headers: tuple[tuple[str, str], ...] = (),
) -> tuple[socket.socket, int, str]:
    connection = socket.create_connection(PROXY, timeout=3)
    connection.settimeout(5)
    authority = f"{host}:{port}"
    headers = (
        f"CONNECT {authority} HTTP/1.1\r\n"
        f"Host: {authority}\r\n"
        "User-Agent: promptly-egress-policy-test\r\n"
    )
    headers += "".join(f"{name}: {value}\r\n" for name, value in extra_headers)
    wire = (headers + "\r\n").encode("ascii")
    connection.sendall(wire)
    response = bytearray()
    while b"\r\n\r\n" not in response:
        chunk = connection.recv(4096)
        if not chunk:
            break
        response.extend(chunk)
        if len(response) > 65536:
            connection.close()
            raise RuntimeError("proxy CONNECT response headers exceeded 64 KiB")
    if b"\r\n\r\n" not in response:
        connection.close()
        raise RuntimeError("proxy closed before completing CONNECT response headers")
    headers, trailing = bytes(response).split(b"\r\n\r\n", 1)
    status = response_status(headers)
    if status == 200 and trailing:
        connection.close()
        raise RuntimeError("proxy sent unexpected bytes after CONNECT response headers")
    return (
        connection,
        status,
        headers.decode("utf-8", errors="replace"),
    )


def request_https_through_proxy(host: str, path: str) -> tuple[int, str]:
    connection, connect_status, _ = connect_through_proxy(host, 8443)
    if connect_status != 200:
        connection.close()
        raise RuntimeError(f"proxy CONNECT returned HTTP {connect_status}")

    context = ssl.create_default_context(cafile=TLS_CA_PATH)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    with context.wrap_socket(connection, server_hostname=host) as tls_connection:
        tls_connection.settimeout(5)
        tls_version = tls_connection.version()
        tls_connection.sendall(
            (
                f"GET {path} HTTP/1.1\r\n"
                f"Host: {host}:8443\r\n"
                "Connection: close\r\n"
                "User-Agent: promptly-egress-policy-test\r\n\r\n"
            ).encode("ascii")
        )
        response = receive_until_close(tls_connection)
    if tls_version is None:
        raise RuntimeError("TLS negotiation did not report a protocol version")
    return response_status(response), tls_version


def eventually_allowed() -> tuple[int, str]:
    last_error: Exception | None = None
    for _ in range(30):
        try:
            status, response = request_through_proxy("allowed.test", "/allowed")
            if status == 200 and "promptly-egress-recorder:allowed" in response:
                return status, response
            last_error = RuntimeError(f"unexpected allowed response status {status}")
        except (OSError, RuntimeError, ValueError) as error:
            last_error = error
        time.sleep(0.25)
    raise RuntimeError("synthetic public destination never succeeded") from last_error


try:
    direct = socket.create_connection(("11.253.0.10", 8080), timeout=1)
except OSError:
    direct_bypass = "blocked"
else:
    direct.close()
    raise RuntimeError("control-network client bypassed the egress proxy")

allowed_status, _ = eventually_allowed()
allowed_tls_status, allowed_tls_version = request_https_through_proxy(
    "allowed-tls.test", "/allowed-tls"
)
if allowed_tls_status != 200:
    raise RuntimeError(f"allowed HTTPS origin returned HTTP {allowed_tls_status}")
results: dict[str, object] = {
    "direct_bypass": direct_bypass,
    "allowed": allowed_status,
    "allowed_https_certificate": "verified",
    "allowed_https_connect": 200,
    "allowed_https_origin": allowed_tls_status,
    "allowed_https_tls_version": allowed_tls_version,
}

private_connect, private_connect_status, private_connect_response = connect_through_proxy(
    "private.test", 8080
)
private_connect.close()
if private_connect_status < 400:
    raise RuntimeError(
        f"private.test CONNECT unexpectedly returned HTTP {private_connect_status}"
    )
if "Promptly outbound policy denied this destination" not in private_connect_response:
    raise RuntimeError("private.test CONNECT denial did not come from the outbound policy")
results["private_connect"] = private_connect_status

upstream_connect, upstream_connect_status, upstream_connect_response = (
    connect_through_proxy(
        "allowed-tls.test",
        8443,
        (("X-Upstream-Https-Proxy", "https://allowed.test:8080"),),
    )
)
upstream_connect.close()
if upstream_connect_status < 400:
    raise RuntimeError(
        "client-selected upstream proxy unexpectedly returned "
        f"HTTP {upstream_connect_status}"
    )
if "Promptly outbound policy denied this destination" not in upstream_connect_response:
    raise RuntimeError("client-selected upstream proxy denial was not policy-generated")
results["client_upstream_proxy"] = upstream_connect_status

for name in ("private.test", "metadata.test", "rebind.test", "rebind-v6.test"):
    status, response = request_through_proxy(name, "/must-not-arrive")
    if status < 400:
        raise RuntimeError(f"{name} unexpectedly returned HTTP {status}")
    if "Promptly outbound policy denied this destination" not in response:
        raise RuntimeError(f"{name} denial did not come from the outbound policy")
    results[name] = status

results["policy_denial_message"] = "verified"
print(json.dumps(results, sort_keys=True))
