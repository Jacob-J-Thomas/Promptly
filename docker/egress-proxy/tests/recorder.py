#!/usr/bin/env python3
"""TCP recorder: every accepted connection leaves durable byte evidence."""

from __future__ import annotations

import ipaddress
import json
import os
import socket
import ssl
from pathlib import Path


evidence_path = Path(os.environ["RECORDER_EVIDENCE_PATH"])
marker = os.environ["RECORDER_MARKER"]
bind_address = os.environ.get("RECORDER_BIND_ADDRESS", "0.0.0.0")
bind_port = int(os.environ.get("RECORDER_BIND_PORT", "8080"))
tls_certificate_path = os.environ.get("RECORDER_TLS_CERTIFICATE_PATH")
tls_key_path = os.environ.get("RECORDER_TLS_KEY_PATH")
response_body = os.environ.get(
    "RECORDER_RESPONSE_BODY", f"promptly-egress-recorder:{marker}\n"
).encode("utf-8")
response_content_type = os.environ.get("RECORDER_RESPONSE_CONTENT_TYPE", "text/plain")
if bool(tls_certificate_path) != bool(tls_key_path):
    raise RuntimeError("both TLS certificate and key paths must be configured")
tls_context: ssl.SSLContext | None = None
if tls_certificate_path and tls_key_path:
    tls_context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    tls_context.minimum_version = ssl.TLSVersion.TLSv1_2
    tls_context.load_cert_chain(tls_certificate_path, tls_key_path)
family = (
    socket.AF_INET6
    if ipaddress.ip_address(bind_address).version == 6
    else socket.AF_INET
)
evidence_path.parent.mkdir(parents=True, exist_ok=True)
evidence_path.touch()

with socket.socket(family, socket.SOCK_STREAM) as server:
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((bind_address, bind_port))
    server.listen(16)
    while True:
        raw_connection, peer = server.accept()
        try:
            connection = (
                tls_context.wrap_socket(raw_connection, server_side=True)
                if tls_context is not None
                else raw_connection
            )
        except ssl.SSLError:
            raw_connection.close()
            continue
        with connection:
            connection.settimeout(2)
            chunks: list[bytes] = []
            try:
                while sum(map(len, chunks)) < 65536:
                    chunk = connection.recv(4096)
                    if not chunk:
                        break
                    chunks.append(chunk)
                    if b"\r\n\r\n" in b"".join(chunks):
                        break
            except TimeoutError:
                pass
            received = b"".join(chunks)
            authorization_scheme: str | None = None
            for header_line in received.split(b"\r\n")[1:]:
                name, separator, value = header_line.partition(b":")
                if separator and name.lower() == b"authorization":
                    authorization_scheme = value.strip().split(b" ", 1)[0].decode(
                        "ascii", errors="replace"
                    )
                    break
            record = {
                "authorization_scheme": authorization_scheme,
                "bytes": len(received),
                "marker": marker,
                "peer": peer[0],
                "request_line": received.split(b"\r\n", 1)[0].decode(
                    "ascii", errors="replace"
                ),
                "tls": tls_context is not None,
            }
            with evidence_path.open("a", encoding="utf-8") as evidence:
                evidence.write(json.dumps(record, sort_keys=True) + "\n")
                evidence.flush()
                os.fsync(evidence.fileno())
            response = (
                b"HTTP/1.1 200 OK\r\n"
                + f"Content-Type: {response_content_type}\r\n".encode("ascii")
                + f"Content-Length: {len(response_body)}\r\n".encode("ascii")
                + b"Connection: close\r\n\r\n"
                + response_body
            )
            connection.sendall(response)
