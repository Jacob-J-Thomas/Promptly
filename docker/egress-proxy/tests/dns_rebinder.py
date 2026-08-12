#!/usr/bin/env python3
"""Deterministic DNS server for the Compose egress policy proof."""

from __future__ import annotations

import ipaddress
import json
import os
import socket
import struct
from collections import defaultdict
from pathlib import Path


LISTEN_ADDRESS = "11.252.0.53"
LISTEN_PORT = 5353
EVIDENCE_PATH = Path("/evidence/dns.jsonl")
ANSWERS = {
    "allowed.test": {1: ("11.253.0.10",)},
    # Smokescreen asks for both address families when `network: ip`. Return an
    # IPv4-mapped IPv6 answer alongside A so Go's resolver can select the same
    # deterministic IPv4 target without treating AAAA NODATA as lookup failure.
    "allowed-tls.test": {
        1: ("11.253.0.11",),
        28: ("::ffff:11.253.0.11",),
    },
    "private.test": {1: ("10.253.0.10",)},
    "metadata.test": {1: ("100.100.100.200",)},
    "rebind.test": {1: ("11.251.0.11", "10.252.0.11")},
    "rebind-v6.test": {28: ("2600:ff::11", "2001:2::10")},
}


def parse_question(packet: bytes) -> tuple[str, int, int, int]:
    offset = 12
    labels: list[str] = []
    while True:
        size = packet[offset]
        offset += 1
        if size == 0:
            break
        labels.append(packet[offset : offset + size].decode("ascii"))
        offset += size
    query_type, query_class = struct.unpack("!HH", packet[offset : offset + 4])
    return ".".join(labels).lower(), query_type, query_class, offset + 4


def response_for(
    packet: bytes,
    question_end: int,
    query_type: int,
    answer: str | None,
) -> bytes:
    # Do not copy EDNS OPT or any other additional records from the request in
    # front of our answer. The response header declares one question and no
    # additional records, so only the exact wire-format question belongs here.
    question = packet[12:question_end]
    answer_count = 1 if query_type in (1, 28) and answer is not None else 0
    header = packet[:2] + struct.pack("!HHHHH", 0x8180, 1, answer_count, 0, 0)
    if answer_count == 0:
        return header + question
    record = (
        b"\xc0\x0c"
        + struct.pack("!HHIH", query_type, 1, 0, len(ipaddress.ip_address(answer).packed))
        + ipaddress.ip_address(answer).packed
    )
    return header + question + record


def main() -> None:
    counts: defaultdict[tuple[str, int], int] = defaultdict(int)

    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as server:
        # The proof assigns this address to the DNS container. Binding only that
        # interface prevents the harness from becoming a wildcard DNS listener.
        server.bind((LISTEN_ADDRESS, LISTEN_PORT))
        while True:
            packet, peer = server.recvfrom(4096)
            try:
                name, query_type, query_class, question_end = parse_question(packet)
                configured = ANSWERS.get(name, {}).get(query_type)
                answer: str | None = None
                count_key = (name, query_type)
                if query_class == 1 and configured:
                    sequence = counts[count_key]
                    answer = configured[min(sequence, len(configured) - 1)]
                    counts[count_key] += 1
                response = response_for(packet, question_end, query_type, answer)
                record = {
                    "name": name,
                    "type": query_type,
                    "answer": answer,
                    "sequence": counts[count_key],
                    "peer": peer[0],
                }
                with EVIDENCE_PATH.open("a", encoding="utf-8") as evidence:
                    evidence.write(json.dumps(record, sort_keys=True) + "\n")
                    evidence.flush()
                    os.fsync(evidence.fileno())
                server.sendto(response, peer)
            except (IndexError, UnicodeDecodeError, ValueError, struct.error):
                # Malformed packets receive a standards-compatible FORMERR.
                header = packet[:2] + struct.pack("!HHHHH", 0x8181, 0, 0, 0, 0)
                server.sendto(header, peer)


if __name__ == "__main__":
    main()
