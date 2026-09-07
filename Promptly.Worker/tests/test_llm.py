from __future__ import annotations

from typing import Any

import httpx
import pytest

import llm


def test_load_settings_rejects_an_empty_model(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PROMPTLY_LLM_PROVIDER", "openai")
    monkeypatch.setenv("PROMPTLY_LLM_API_KEY", "test-key")
    monkeypatch.setenv("PROMPTLY_LLM_MODEL_DEFAULT", "  ")

    with pytest.raises(llm.LlmConfigurationError, match="PROMPTLY_LLM_MODEL_DEFAULT"):
        llm.load_llm_settings()


def test_load_settings_uses_defaults_for_blank_optional_urls(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("PROMPTLY_LLM_API_KEY", "test-key")
    monkeypatch.setenv("PROMPTLY_LLM_PROVIDER", "openai")
    monkeypatch.setenv("PROMPTLY_LLM_BASE_URL", "  ")

    openai_settings = llm.load_llm_settings()
    assert openai_settings.base_url == llm.DEFAULT_OPENAI_BASE_URL

    monkeypatch.setenv("PROMPTLY_LLM_PROVIDER", "azureopenai")
    monkeypatch.setenv("PROMPTLY_LLM_AZURE_ENDPOINT", "https://azure.example")
    monkeypatch.setenv("PROMPTLY_LLM_API_VERSION", "  ")

    azure_settings = llm.load_llm_settings()
    assert azure_settings.api_version == llm.DEFAULT_AZURE_API_VERSION
    assert azure_settings.timeout_seconds == llm.DEFAULT_TIMEOUT_SECONDS


@pytest.mark.parametrize("value", ["not-a-number", "nan", "0", "110.1"])
def test_load_settings_rejects_invalid_timeout(
    monkeypatch: pytest.MonkeyPatch,
    value: str,
) -> None:
    monkeypatch.setenv("PROMPTLY_LLM_PROVIDER", "openai")
    monkeypatch.setenv("PROMPTLY_LLM_API_KEY", "test-key")
    monkeypatch.setenv("PROMPTLY_LLM_TIMEOUT_SECONDS", value)

    with pytest.raises(llm.LlmConfigurationError, match="PROMPTLY_LLM_TIMEOUT_SECONDS"):
        llm.load_llm_settings()


@pytest.mark.parametrize(
    ("provider", "name", "value"),
    [
        ("openai", "PROMPTLY_LLM_BASE_URL", "not-a-url"),
        ("openai", "PROMPTLY_LLM_BASE_URL", "https://user:pass@example.test/v1"),
        ("openai", "PROMPTLY_LLM_BASE_URL", "https://example.test/v1#fragment"),
        ("azureopenai", "PROMPTLY_LLM_AZURE_ENDPOINT", "file:///tmp/provider"),
    ],
)
def test_load_settings_rejects_invalid_provider_url(
    monkeypatch: pytest.MonkeyPatch,
    provider: str,
    name: str,
    value: str,
) -> None:
    monkeypatch.setenv("PROMPTLY_LLM_PROVIDER", provider)
    monkeypatch.setenv("PROMPTLY_LLM_API_KEY", "test-key")
    monkeypatch.setenv(name, value)

    with pytest.raises(llm.LlmConfigurationError, match=name):
        llm.load_llm_settings()


@pytest.mark.parametrize(
    "settings",
    [
        llm.LlmSettings(
            provider="openai",
            api_key="key",
            model="model",
            base_url=None,
        ),
        llm.LlmSettings(
            provider="azureopenai",
            api_key="key",
            model="model",
            azure_endpoint=None,
            api_version=None,
        ),
    ],
)
def test_client_factory_defensively_rejects_incomplete_settings(
    monkeypatch: pytest.MonkeyPatch,
    settings: llm.LlmSettings,
) -> None:
    monkeypatch.setattr(llm, "load_llm_settings", lambda provider=None: settings)

    with pytest.raises(llm.LlmConfigurationError, match="configuration is incomplete"):
        llm.get_llm_client()


def test_configuration_status_reports_complete_configuration(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("PROMPTLY_LLM_PROVIDER", "openai")
    monkeypatch.setenv("PROMPTLY_LLM_API_KEY", "test-key")

    assert llm.llm_configuration_status() == ("openai", True)


def test_resolve_provider_normalizes_explicit_provider() -> None:
    assert llm.resolve_provider(" OPENAI ") == "openai"


def test_client_factory_bounds_timeout_and_disables_retries(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    captured: dict[str, Any] = {}
    sentinel = object()

    def build_openai(**kwargs: Any) -> object:
        captured.update(kwargs)
        return sentinel

    monkeypatch.setenv("PROMPTLY_LLM_PROVIDER", "openai")
    monkeypatch.setenv("PROMPTLY_LLM_API_KEY", "test-key")
    monkeypatch.setenv("PROMPTLY_LLM_TIMEOUT_SECONDS", "42")
    monkeypatch.setattr(llm, "OpenAI", build_openai)

    assert llm.get_llm_client() is sentinel
    timeout = captured.pop("timeout")
    assert isinstance(timeout, httpx.Timeout)
    assert timeout.connect == 10
    assert timeout.read == 42
    assert captured == {
        "api_key": "test-key",
        "base_url": llm.DEFAULT_OPENAI_BASE_URL,
        "max_retries": 0,
    }


class _FakeModels:
    def __init__(self, error: Exception | None = None) -> None:
        self.error = error
        self.timeout: float | None = None

    def list(self, *, timeout: float) -> None:
        self.timeout = timeout
        if self.error is not None:
            raise self.error


class _FakeClient:
    def __init__(self, error: Exception | None = None) -> None:
        self.models = _FakeModels(error)
        self.closed = False

    def close(self) -> None:
        self.closed = True


def test_dependency_probe_uses_a_bounded_non_inference_request_and_closes_client(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    client = _FakeClient()
    settings = llm.LlmSettings(
        provider="openai",
        api_key="key",
        model="model",
        base_url="https://provider.example/v1",
        timeout_seconds=3,
    )
    monkeypatch.setattr(llm, "_build_llm_client", lambda configured: client)

    llm.probe_llm_dependency(settings)

    assert client.models.timeout == 3
    assert client.closed is True


def test_dependency_probe_sanitizes_provider_failures_and_closes_client(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    client = _FakeClient(RuntimeError("secret upstream detail"))
    settings = llm.LlmSettings(
        provider="openai",
        api_key="key",
        model="model",
        base_url="https://provider.example/v1",
    )
    monkeypatch.setattr(llm, "_build_llm_client", lambda configured: client)

    with pytest.raises(llm.LlmDependencyError, match="readiness probe failed") as exc_info:
        llm.probe_llm_dependency(settings)

    assert "secret upstream detail" not in str(exc_info.value)
    assert client.closed is True
