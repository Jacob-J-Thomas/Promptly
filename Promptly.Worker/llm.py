"""LLM provider configuration and client construction.

Configuration loading is local and deterministic. The readiness helper performs
a bounded, non-inference provider request so invalid credentials and unavailable
provider endpoints do not report ready.
"""

from __future__ import annotations

import os
from contextlib import suppress
from dataclasses import dataclass
from math import isfinite
from typing import Literal, TypeAlias
from urllib.parse import urlparse

import httpx
from openai import AzureOpenAI, OpenAI

ProviderName: TypeAlias = Literal["openai", "azureopenai"]
LlmClient: TypeAlias = OpenAI | AzureOpenAI

DEFAULT_PROVIDER: ProviderName = "azureopenai"
DEFAULT_MODEL = "gpt-4o-mini"
DEFAULT_OPENAI_BASE_URL = "https://api.openai.com/v1"
DEFAULT_AZURE_API_VERSION = "2024-08-01-preview"
DEFAULT_TIMEOUT_SECONDS = 90.0
MAX_TIMEOUT_SECONDS = 110.0
READINESS_TIMEOUT_SECONDS = 5.0
MAX_RETRIES = 0


class LlmConfigurationError(ValueError):
    """Raised when the worker's local provider configuration is invalid."""


class UnsupportedProviderError(LlmConfigurationError):
    """Raised when a provider name is outside the supported wire contract."""


class LlmDependencyError(RuntimeError):
    """Raised when the configured upstream provider is not ready."""


@dataclass(frozen=True)
class LlmSettings:
    provider: ProviderName
    api_key: str
    model: str
    base_url: str | None = None
    azure_endpoint: str | None = None
    api_version: str | None = None
    timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS


def _required_environment_value(name: str) -> str:
    value = os.getenv(name, "").strip()
    if not value:
        raise LlmConfigurationError(f"{name} is required")
    return value


def _timeout_seconds() -> float:
    raw_value = os.getenv(
        "PROMPTLY_LLM_TIMEOUT_SECONDS",
        str(DEFAULT_TIMEOUT_SECONDS),
    ).strip()
    try:
        value = float(raw_value)
    except ValueError as exc:
        raise LlmConfigurationError("PROMPTLY_LLM_TIMEOUT_SECONDS must be a number") from exc
    if not isfinite(value) or value <= 0 or value > MAX_TIMEOUT_SECONDS:
        message = (
            "PROMPTLY_LLM_TIMEOUT_SECONDS must be greater than 0 "
            f"and at most {MAX_TIMEOUT_SECONDS:g}"
        )
        raise LlmConfigurationError(message)
    return value


def _validated_http_url(name: str, value: str) -> str:
    parsed = urlparse(value)
    if (
        parsed.scheme not in {"http", "https"}
        or not parsed.hostname
        or parsed.username is not None
        or parsed.password is not None
        or parsed.fragment
    ):
        raise LlmConfigurationError(f"{name} must be a valid HTTP(S) URL")
    return value


def resolve_provider(provider: str | None = None) -> ProviderName:
    configured = (
        provider if provider is not None else os.getenv("PROMPTLY_LLM_PROVIDER", DEFAULT_PROVIDER)
    )
    normalized = configured.strip().lower()
    if normalized == "openai":
        return "openai"
    if normalized == "azureopenai":
        return "azureopenai"
    raise UnsupportedProviderError("PROMPTLY_LLM_PROVIDER must be 'openai' or 'azureopenai'")


def load_llm_settings(provider: str | None = None) -> LlmSettings:
    """Load and validate all configuration required to make an LLM request."""

    provider_name = resolve_provider(provider)
    api_key = _required_environment_value("PROMPTLY_LLM_API_KEY")
    model = os.getenv("PROMPTLY_LLM_MODEL_DEFAULT", DEFAULT_MODEL).strip()
    if not model:
        raise LlmConfigurationError("PROMPTLY_LLM_MODEL_DEFAULT is required")
    timeout_seconds = _timeout_seconds()

    if provider_name == "azureopenai":
        return LlmSettings(
            provider=provider_name,
            api_key=api_key,
            model=model,
            azure_endpoint=_validated_http_url(
                "PROMPTLY_LLM_AZURE_ENDPOINT",
                _required_environment_value("PROMPTLY_LLM_AZURE_ENDPOINT"),
            ),
            api_version=os.getenv("PROMPTLY_LLM_API_VERSION", DEFAULT_AZURE_API_VERSION).strip()
            or DEFAULT_AZURE_API_VERSION,
            timeout_seconds=timeout_seconds,
        )

    return LlmSettings(
        provider=provider_name,
        api_key=api_key,
        model=model,
        base_url=_validated_http_url(
            "PROMPTLY_LLM_BASE_URL",
            os.getenv("PROMPTLY_LLM_BASE_URL", DEFAULT_OPENAI_BASE_URL).strip()
            or DEFAULT_OPENAI_BASE_URL,
        ),
        timeout_seconds=timeout_seconds,
    )


def _client_timeout(settings: LlmSettings) -> httpx.Timeout:
    return httpx.Timeout(
        settings.timeout_seconds,
        connect=min(10.0, settings.timeout_seconds),
    )


def _build_llm_client(settings: LlmSettings) -> LlmClient:
    if settings.provider == "azureopenai":
        if settings.azure_endpoint is None or settings.api_version is None:
            raise LlmConfigurationError("Azure OpenAI configuration is incomplete")
        return AzureOpenAI(
            api_key=settings.api_key,
            azure_endpoint=settings.azure_endpoint,
            api_version=settings.api_version,
            timeout=_client_timeout(settings),
            max_retries=MAX_RETRIES,
        )

    if settings.base_url is None:
        raise LlmConfigurationError("OpenAI configuration is incomplete")
    return OpenAI(
        api_key=settings.api_key,
        base_url=settings.base_url,
        timeout=_client_timeout(settings),
        max_retries=MAX_RETRIES,
    )


def get_llm_client(provider: str | None = None) -> LlmClient:
    """Construct a configured client without issuing a network request."""

    return _build_llm_client(load_llm_settings(provider))


def probe_llm_dependency(settings: LlmSettings | None = None) -> None:
    """Verify provider authentication/reachability without running inference."""

    try:
        resolved_settings = settings or load_llm_settings()
        client = _build_llm_client(resolved_settings)
    except LlmConfigurationError:
        raise
    except Exception as exc:
        raise LlmDependencyError("LLM provider readiness probe failed") from exc

    try:
        client.models.list(
            timeout=min(READINESS_TIMEOUT_SECONDS, resolved_settings.timeout_seconds)
        )
    except Exception as exc:
        raise LlmDependencyError("LLM provider readiness probe failed") from exc
    finally:
        with suppress(Exception):
            client.close()


def llm_configuration_status() -> tuple[str, bool]:
    """Return a non-secret status summary suitable for liveness responses."""

    configured_provider = os.getenv("PROMPTLY_LLM_PROVIDER", DEFAULT_PROVIDER).strip().lower()
    try:
        settings = load_llm_settings()
    except LlmConfigurationError:
        return configured_provider, False
    return settings.provider, True
