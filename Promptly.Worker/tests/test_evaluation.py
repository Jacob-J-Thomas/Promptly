from __future__ import annotations

from typing import Any

import pytest
from fastapi.testclient import TestClient

from llm import LlmConfigurationError
from main import create_app
from routers import evaluation
from tests.fakes import FakeLlmClient

TRACE = {
    "messages": [
        {"role": "user", "content": "Question"},
        {"role": "assistant", "content": "Answer"},
    ],
    "toolCalls": [
        {"name": "lookup", "argumentsJson": '{"id":1}'},
    ],
    "usage": None,
    "retrievedDocs": [],
    "rawResponse": None,
}


@pytest.fixture
def client() -> TestClient:
    return TestClient(create_app())


def test_llm_judge_returns_clamped_provider_score(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    llm = FakeLlmClient('{"score":1.0,"reason":"Strong answer"}')
    monkeypatch.setattr(evaluation, "get_llm_client", lambda provider=None: llm)

    response = client.post(
        "/eval/llm-judge",
        json={
            "rubric": "Be accurate",
            "min_score": 0.7,
            "trace": TRACE,
            "model": "judge-model",
            "provider": "openai",
            "metadata": {"case": "one"},
        },
    )

    assert response.status_code == 200
    assert response.json() == {"score": 1.0, "reason": "Strong answer"}
    call = llm.completions.calls[0]
    assert call["model"] == "judge-model"
    assert "Answer" in call["messages"][1]["content"]
    assert "lookup" in call["messages"][1]["content"]
    assert llm.closed


@pytest.mark.parametrize(
    "provider_response", ['{"score":-0.2,"reason":"Low"}', '{"reason":"Missing score"}']
)
def test_llm_judge_rejects_out_of_contract_scores(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    provider_response: str,
) -> None:
    monkeypatch.setattr(
        evaluation,
        "get_llm_client",
        lambda provider=None: FakeLlmClient(provider_response),
    )

    response = client.post(
        "/eval/llm-judge",
        json={"rubric": "Be accurate", "min_score": 0.7, "trace": TRACE},
    )

    assert response.status_code == 502


@pytest.mark.parametrize(
    "provider_content",
    [None, "not-json", '{"score":"not-a-number"}'],
)
def test_llm_judge_returns_safe_failure_for_invalid_provider_output(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    provider_content: str | None,
) -> None:
    monkeypatch.setattr(
        evaluation,
        "get_llm_client",
        lambda provider=None: FakeLlmClient(provider_content),
    )

    response = client.post(
        "/eval/llm-judge",
        json={"rubric": "Be accurate", "min_score": 0.7, "trace": TRACE},
    )

    assert response.status_code == 502
    assert response.json()["detail"]["error"]["message"] == "Evaluation could not be completed"


def test_llm_judge_does_not_expose_provider_exception(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    llm = FakeLlmClient(error=RuntimeError("secret detail"))
    monkeypatch.setattr(
        evaluation,
        "get_llm_client",
        lambda provider=None: llm,
    )

    response = client.post(
        "/eval/llm-judge",
        json={"rubric": "Be accurate", "min_score": 0.7, "trace": TRACE},
    )

    assert response.status_code == 502
    assert "secret detail" not in response.text
    assert llm.closed


def test_llm_judge_works_without_tool_calls(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    llm = FakeLlmClient('{"score":0.8,"reason":"Good"}')
    monkeypatch.setattr(evaluation, "get_llm_client", lambda provider=None: llm)
    trace = {**TRACE, "toolCalls": []}

    response = client.post(
        "/eval/llm-judge",
        json={"rubric": "Be accurate", "min_score": 0.7, "trace": trace},
    )

    assert response.status_code == 200
    assert "Tool Calls:" not in llm.completions.calls[0]["messages"][1]["content"]


def test_llm_judge_reports_missing_provider_configuration(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def missing_configuration(provider: str | None = None) -> Any:
        raise LlmConfigurationError("PROMPTLY_LLM_API_KEY is required")

    monkeypatch.setattr(evaluation, "get_llm_client", missing_configuration)

    response = client.post(
        "/eval/llm-judge",
        json={"rubric": "Be accurate", "min_score": 0.7, "trace": TRACE},
    )

    assert response.status_code == 503
    assert response.json()["detail"]["error"]["message"] == ("Evaluation service is not configured")


def test_llm_judge_rejects_an_unsupported_provider_override(client: TestClient) -> None:
    response = client.post(
        "/eval/llm-judge",
        json={
            "rubric": "Be accurate",
            "min_score": 0.7,
            "trace": TRACE,
            "provider": "unsupported",
        },
    )

    assert response.status_code == 400
    assert response.json()["detail"]["error"]["message"] == "Unsupported LLM provider"


def test_groundedness_uses_explicit_documents(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    llm = FakeLlmClient('{"score":0.75,"reason":"Mostly grounded"}')
    monkeypatch.setattr(evaluation, "get_llm_client", lambda provider=None: llm)

    response = client.post(
        "/eval/groundedness",
        json={
            "min_score": 0.7,
            "trace": TRACE,
            "docs": [{"id": "doc-1", "title": "Source", "content": "Fact"}],
        },
    )

    assert response.status_code == 200
    assert response.json() == {"score": 0.75, "reason": "Mostly grounded"}
    assert "Document 1:\nFact" in llm.completions.calls[0]["messages"][1]["content"]


def test_groundedness_falls_back_to_trace_documents(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    llm = FakeLlmClient('{"score":0.5,"reason":"Partial"}')
    monkeypatch.setattr(evaluation, "get_llm_client", lambda provider=None: llm)
    trace = {
        **TRACE,
        "retrievedDocs": [{"content": "Trace document"}],
    }

    response = client.post(
        "/eval/groundedness",
        json={"min_score": 0.7, "trace": trace, "docs": []},
    )

    assert response.status_code == 200
    assert response.json()["score"] == 0.5
    assert "Trace document" in llm.completions.calls[0]["messages"][1]["content"]


def test_groundedness_short_circuits_when_no_documents_exist(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def unexpected_client(provider: str | None = None) -> Any:
        raise AssertionError(f"provider should not be called: {provider}")

    monkeypatch.setattr(evaluation, "get_llm_client", unexpected_client)

    response = client.post(
        "/eval/groundedness",
        json={"min_score": 0.7, "trace": TRACE, "docs": []},
    )

    assert response.status_code == 200
    assert response.json() == {
        "score": 0.0,
        "reason": "No retrieved documents were available for groundedness evaluation",
    }


def test_groundedness_returns_safe_failure_when_provider_fails(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(
        evaluation,
        "get_llm_client",
        lambda provider=None: FakeLlmClient(error=RuntimeError("secret detail")),
    )

    response = client.post(
        "/eval/groundedness",
        json={
            "min_score": 0.7,
            "trace": TRACE,
            "docs": [{"content": "Fact"}],
        },
    )

    assert response.status_code == 502


def test_get_llm_client_passes_provider_to_shared_factory(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    captured: list[str | None] = []
    sentinel = object()

    def shared_factory(provider: str | None = None) -> object:
        captured.append(provider)
        return sentinel

    monkeypatch.setattr(evaluation, "create_llm_client", shared_factory)

    assert evaluation.get_llm_client("openai") is sentinel
    assert captured == ["openai"]
