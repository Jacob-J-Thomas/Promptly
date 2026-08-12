from __future__ import annotations

import sys
from collections.abc import Iterator
from pathlib import Path

import pytest

WORKER_ROOT = Path(__file__).resolve().parents[1]
if str(WORKER_ROOT) not in sys.path:
    sys.path.insert(0, str(WORKER_ROOT))


@pytest.fixture(autouse=True)
def clean_llm_environment(monkeypatch: pytest.MonkeyPatch) -> Iterator[None]:
    for name in (
        "PROMPTLY_LLM_PROVIDER",
        "PROMPTLY_LLM_API_KEY",
        "PROMPTLY_LLM_AZURE_ENDPOINT",
        "PROMPTLY_LLM_API_VERSION",
        "PROMPTLY_LLM_BASE_URL",
        "PROMPTLY_LLM_MODEL_DEFAULT",
        "PROMPTLY_LLM_TIMEOUT_SECONDS",
    ):
        monkeypatch.delenv(name, raising=False)
    yield
