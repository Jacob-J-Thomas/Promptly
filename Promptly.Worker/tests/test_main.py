from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

import main
from llm import LlmDependencyError
from main import app, create_app


def test_global_app_exposes_exact_server_contract_routes() -> None:
    paths = app.openapi()["paths"]

    assert "post" in paths["/mapping/propose"]
    assert "post" in paths["/eval/llm-judge"]
    assert "post" in paths["/eval/groundedness"]
    assert "get" in paths["/health"]
    assert "get" in paths["/health/live"]
    assert "get" in paths["/health/ready"]


def test_create_app_returns_an_independent_runnable_application() -> None:
    first = create_app()
    second = create_app()

    assert first is not second
    assert first.title == "Promptly Worker"
    assert TestClient(first).get("/health/live").status_code == 200


@pytest.mark.parametrize("path", ["/health", "/health/live"])
def test_liveness_does_not_require_external_provider_configuration(path: str) -> None:
    response = TestClient(create_app()).get(path)

    assert response.status_code == 200
    assert response.json() == {
        "status": "healthy",
        "llm_provider": "azureopenai",
        "llm_configured": False,
    }


def test_readiness_reports_missing_default_azure_configuration() -> None:
    response = TestClient(create_app()).get("/health/ready")

    assert response.status_code == 503
    assert response.json()["detail"]["status"] == "not_ready"
    assert "PROMPTLY_LLM_API_KEY" in response.text


def test_readiness_accepts_complete_openai_configuration(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("PROMPTLY_LLM_PROVIDER", "openai")
    monkeypatch.setenv("PROMPTLY_LLM_API_KEY", "test-key")
    monkeypatch.setattr(main, "probe_llm_dependency", lambda settings: None)

    response = TestClient(create_app()).get("/health/ready")

    assert response.status_code == 200
    assert response.json() == {
        "status": "ready",
        "llm_provider": "openai",
        "llm_configured": True,
    }


def test_readiness_accepts_complete_azure_configuration(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("PROMPTLY_LLM_PROVIDER", "azureopenai")
    monkeypatch.setenv("PROMPTLY_LLM_API_KEY", "test-key")
    monkeypatch.setenv("PROMPTLY_LLM_AZURE_ENDPOINT", "https://azure.example")
    monkeypatch.setattr(main, "probe_llm_dependency", lambda settings: None)

    response = TestClient(create_app()).get("/health/ready")

    assert response.status_code == 200
    assert response.json() == {
        "status": "ready",
        "llm_provider": "azureopenai",
        "llm_configured": True,
    }


def test_readiness_rejects_unsupported_provider(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("PROMPTLY_LLM_PROVIDER", "unsupported")
    monkeypatch.setenv("PROMPTLY_LLM_API_KEY", "test-key")

    response = TestClient(create_app()).get("/health/ready")

    assert response.status_code == 503
    assert response.json()["detail"]["status"] == "not_ready"
    assert "PROMPTLY_LLM_PROVIDER" in response.text


def test_readiness_rejects_an_unavailable_provider(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("PROMPTLY_LLM_PROVIDER", "openai")
    monkeypatch.setenv("PROMPTLY_LLM_API_KEY", "test-key")

    def unavailable_provider(settings: object) -> None:
        raise LlmDependencyError("LLM provider readiness probe failed")

    monkeypatch.setattr(main, "probe_llm_dependency", unavailable_provider)

    response = TestClient(create_app()).get("/health/ready")

    assert response.status_code == 503
    assert response.json()["detail"] == {
        "status": "not_ready",
        "message": "LLM provider readiness probe failed",
    }
