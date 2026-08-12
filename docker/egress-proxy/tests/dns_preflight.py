#!/usr/bin/env python3
"""Issue the safe first DNS answer used by the rebinding test."""

from __future__ import annotations

import ipaddress
import random
import socket
import struct
import time


DNS_SERVER = ("11.252.0.53", 5353)
EXPECTED = (
    ("rebind.test", 1, ipaddress.ip_address("11.251.0.11")),
    ("rebind-v6.test", 28, ipaddress.ip_address("2600:ff::11")),
)


def query(name: str, query_type: int) -> ipaddress.IPv4Address | ipaddress.IPv6Address:
    transaction_id = random.randrange(0, 65536)
    header = struct.pack("!HHHHHH", transaction_id, 0x0100, 1, 0, 0, 0)
    question = b"".join(
        bytes([len(label)]) + label.encode("ascii")
        for label in name.split(".")
    ) + b"\x00" + struct.pack("!HH", query_type, 1)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
        client.settimeout(1)
        client.sendto(header + question, DNS_SERVER)
        response, _ = client.recvfrom(4096)
    if response[:2] != struct.pack("!H", transaction_id) or len(response) < 16:
        raise RuntimeError("invalid DNS preflight response")
    answer_length = 4 if query_type == 1 else 16
    return ipaddress.ip_address(response[-answer_length:])


for name, query_type, expected in EXPECTED:
    for attempt in range(20):
        try:
            answer = query(name, query_type)
            if answer != expected:
                raise RuntimeError(f"expected public preflight {expected}, got {answer}")
            print(f"{name} rebinding preflight returned synthetic public address {answer}")
            break
        except (OSError, RuntimeError):
            if attempt == 19:
                raise
            time.sleep(0.25)
