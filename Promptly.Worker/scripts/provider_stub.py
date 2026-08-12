"""Deterministic OpenAI-compatible target for smoke and contract tests."""

from __future__ import annotations

import argparse
import json
import re
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Lock
from typing import Any, ClassVar
from urllib.parse import parse_qs, urlparse

EXPECTED_KEY = "verification-only"
OPENAI_MODELS_PATH = "/v1/models"
AZURE_MODELS_PATH = "/openai/models"
CHAT_COMPLETIONS_PATH = "/v1/chat/completions"
AZURE_CHAT_COMPLETIONS_PREFIX = "/openai/deployments/"
AZURE_CHAT_COMPLETIONS_SUFFIX = "/chat/completions"
DEFAULT_PORT = 8080
EVIDENCE_FILE_NAME = "provider-requests.jsonl"
CORRELATION_PATTERN = re.compile(r'"integrationCorrelation"\s*:\s*"([A-Za-z0-9-]{1,128})"')


class ProviderHandler(BaseHTTPRequestHandler):
    """Serve authenticated deterministic model and chat-completion responses."""

    _evidence_lock: ClassVar[Lock] = Lock()
    _request_sequence: ClassVar[int] = 0
    record_evidence: ClassVar[bool] = False

    def do_GET(self) -> None:
        parsed = urlparse(self.path)
        authorized = self._authorized(parsed.path)
        self._record_request(
            method="GET",
            path=parsed.path,
            kind="models" if parsed.path in {OPENAI_MODELS_PATH, AZURE_MODELS_PATH} else "unknown",
            authorized=authorized,
        )
        if parsed.path not in {OPENAI_MODELS_PATH, AZURE_MODELS_PATH}:
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        if not authorized:
            self.send_error(HTTPStatus.UNAUTHORIZED)
            return
        if parsed.path == AZURE_MODELS_PATH and not parse_qs(parsed.query).get("api-version"):
            self.send_error(HTTPStatus.BAD_REQUEST)
            return

        self._send_json({"object": "list", "data": []})

    def do_POST(self) -> None:
        parsed = urlparse(self.path)
        is_openai_chat = parsed.path == CHAT_COMPLETIONS_PATH
        is_azure_chat = parsed.path.startswith(
            AZURE_CHAT_COMPLETIONS_PREFIX
        ) and parsed.path.endswith(AZURE_CHAT_COMPLETIONS_SUFFIX)
        if not is_openai_chat and not is_azure_chat:
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        authorized = self._authorized(parsed.path)
        if not authorized:
            self._record_request(
                method="POST",
                path=parsed.path,
                kind="chat_completion",
                authorized=False,
            )
            self.send_error(HTTPStatus.UNAUTHORIZED)
            return
        if is_azure_chat and not parse_qs(parsed.query).get("api-version"):
            self.send_error(HTTPStatus.BAD_REQUEST)
            return

        content_length = int(self.headers.get("Content-Length", "0"))
        try:
            request = json.loads(self.rfile.read(content_length))
        except (json.JSONDecodeError, ValueError):
            self._record_request(
                method="POST",
                path=parsed.path,
                kind="chat_completion",
                authorized=True,
                valid_json=False,
            )
            self.send_error(HTTPStatus.BAD_REQUEST)
            return

        prompts = "\n".join(
            str(message.get("content", ""))
            for message in request.get("messages", [])
            if isinstance(message, dict)
        )
        correlation_match = CORRELATION_PATTERN.search(prompts)
        self._record_request(
            method="POST",
            path=parsed.path,
            kind="chat_completion",
            authorized=True,
            valid_json=True,
            model=str(request.get("model", "verification-model")),
            message_count=len(request.get("messages", [])),
            correlation_id=correlation_match.group(1) if correlation_match else None,
        )
        if "propose a MappingSpec" in prompts:
            content = json.dumps(
                {
                    "version": 1,
                    "fallback": {"singleAssistantContentPath": "$.answer"},
                }
            )
        elif "Evaluate groundedness" in prompts:
            content = json.dumps({"score": 0.75, "reason": "Deterministically grounded"})
        else:
            content = json.dumps({"score": 0.85, "reason": "Deterministically accurate"})

        self._send_json(
            {
                "id": "chatcmpl-verification",
                "object": "chat.completion",
                "created": 0,
                "model": request.get("model", "verification-model"),
                "choices": [
                    {
                        "index": 0,
                        "message": {"role": "assistant", "content": content},
                        "finish_reason": "stop",
                    }
                ],
                "usage": {"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
            }
        )

    def _authorized(self, path: str) -> bool:
        if path.startswith("/openai/"):
            return self.headers.get("api-key") == EXPECTED_KEY
        return self.headers.get("Authorization") == f"Bearer {EXPECTED_KEY}"

    def _send_json(self, payload: object) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    @classmethod
    def _record_request(
        cls,
        *,
        method: str,
        path: str,
        kind: str,
        authorized: bool,
        **details: Any,
    ) -> None:
        if not cls.record_evidence:
            return

        with cls._evidence_lock:
            cls._request_sequence += 1
            record: dict[str, Any] = {
                "sequence": cls._request_sequence,
                "method": method,
                "path": path,
                "kind": kind,
                "authorized": authorized,
                **details,
            }
            path_object = Path(EVIDENCE_FILE_NAME)
            with path_object.open("a", encoding="utf-8") as evidence:
                evidence.write(json.dumps(record, sort_keys=True, separators=(",", ":")))
                evidence.write("\n")

    def log_message(self, format: str, *args: object) -> None:
        """Keep successful verifier output quiet while retaining HTTP semantics."""


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--bind-all", action="store_true")
    parser.add_argument("--ephemeral-port", action="store_true")
    parser.add_argument("--record-evidence", action="store_true")
    arguments = parser.parse_args()
    ProviderHandler.record_evidence = arguments.record_evidence
    port = 0 if arguments.ephemeral_port else DEFAULT_PORT
    host = "0.0.0.0" if arguments.bind_all else "127.0.0.1"
    server = ThreadingHTTPServer((host, port), ProviderHandler)
    print(
        json.dumps(
            {
                "event": "provider_listening",
                "host": host,
                "port": server.server_address[1],
            },
            sort_keys=True,
            separators=(",", ":"),
        ),
        flush=True,
    )
    server.serve_forever()
