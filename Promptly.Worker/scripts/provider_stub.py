"""Deterministic OpenAI-compatible target for smoke and contract tests."""

from __future__ import annotations

import json
import os
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

EXPECTED_KEY = "verification-only"
OPENAI_MODELS_PATH = "/v1/models"
AZURE_MODELS_PATH = "/openai/models"
CHAT_COMPLETIONS_PATH = "/v1/chat/completions"
AZURE_CHAT_COMPLETIONS_PREFIX = "/openai/deployments/"
AZURE_CHAT_COMPLETIONS_SUFFIX = "/chat/completions"
DEFAULT_PORT = 8080


class ProviderHandler(BaseHTTPRequestHandler):
    """Serve authenticated deterministic model and chat-completion responses."""

    def do_GET(self) -> None:
        parsed = urlparse(self.path)
        if parsed.path not in {OPENAI_MODELS_PATH, AZURE_MODELS_PATH}:
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        if not self._authorized(parsed.path):
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
        if not self._authorized(parsed.path):
            self.send_error(HTTPStatus.UNAUTHORIZED)
            return
        if is_azure_chat and not parse_qs(parsed.query).get("api-version"):
            self.send_error(HTTPStatus.BAD_REQUEST)
            return

        content_length = int(self.headers.get("Content-Length", "0"))
        try:
            request = json.loads(self.rfile.read(content_length))
        except (json.JSONDecodeError, ValueError):
            self.send_error(HTTPStatus.BAD_REQUEST)
            return

        prompts = "\n".join(
            str(message.get("content", ""))
            for message in request.get("messages", [])
            if isinstance(message, dict)
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

    def log_message(self, format: str, *args: object) -> None:
        """Keep successful verifier output quiet while retaining HTTP semantics."""


if __name__ == "__main__":
    port = int(os.environ.get("PROMPTLY_PROVIDER_STUB_PORT", str(DEFAULT_PORT)))
    host = os.environ.get("PROMPTLY_PROVIDER_STUB_HOST", "0.0.0.0")
    ThreadingHTTPServer((host, port), ProviderHandler).serve_forever()
