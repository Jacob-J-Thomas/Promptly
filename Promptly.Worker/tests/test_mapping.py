from __future__ import annotations

import json
from typing import Any

import httpx
import pytest
from fastapi.testclient import TestClient

from llm import LlmConfigurationError
from main import create_app
from routers import mapping
from tests.fakes import FakeLlmClient


@pytest.fixture
def client() -> TestClient:
    return TestClient(create_app())


def test_propose_mapping_returns_structured_provider_result(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    llm = FakeLlmClient('{"version":1,"fallback":{"singleAssistantContentPath":"$.answer"}}')
    monkeypatch.setattr(mapping, "get_llm_client", lambda: llm)

    response = client.post(
        "/mapping/propose",
        json={
            "sample_response_json": '{"answer":"hello"}',
            "sample_request_json": '{"prompt":"hi"}',
            "hints": {"provider": "custom"},
        },
    )

    assert response.status_code == 200
    assert response.json() == {
        "mappingSpec": {
            "version": 1,
            "fallback": {"singleAssistantContentPath": "$.answer"},
        },
        "reason": "Mapping spec generated successfully based on sample structure",
    }
    assert llm.completions.calls[0]["model"] == "gpt-4o-mini"
    assert llm.completions.calls[0]["response_format"] == {"type": "json_object"}
    assert "Do not use filters" in llm.completions.calls[0]["messages"][0]["content"]
    assert "custom" in llm.completions.calls[0]["messages"][1]["content"]
    assert llm.closed


def test_propose_mapping_accepts_json_in_a_markdown_fence(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    llm = FakeLlmClient(
        '```json\n{"version":1,"fallback":{"singleAssistantContentPath":"$.answer"}}\n```'
    )
    monkeypatch.setattr(mapping, "get_llm_client", lambda: llm)

    response = client.post(
        "/mapping/propose",
        json={"sample_response_json": '{"answer":"hello"}'},
    )

    assert response.status_code == 200
    assert response.json()["mappingSpec"] == {
        "version": 1,
        "fallback": {"singleAssistantContentPath": "$.answer"},
    }


def test_propose_mapping_validates_every_supported_section_against_the_sample(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    llm = FakeLlmClient(
        """{
          "version": 1,
          "messages": {
            "itemsPath": "$.messages[*]",
            "rolePath": "$.role",
            "contentPath": "$.content"
          },
          "toolCalls": {
            "itemsPath": "$.tool_calls[*]",
            "namePath": "$.name",
            "argumentsPath": "$.arguments"
          },
          "usage": {
            "objectPath": "$.usage",
            "promptTokensPath": "$.prompt",
            "completionTokensPath": "$.completion",
            "totalTokensPath": "$.total",
            "costPath": "$.cost",
            "latencyMsPath": "$.latency_ms"
          },
          "retrievedDocs": {
            "itemsPath": "$.documents[*]",
            "idPath": "$.id",
            "titlePath": "$.title",
            "contentPath": "$.content",
            "metadataPath": "$.metadata"
          }
        }"""
    )
    monkeypatch.setattr(mapping, "get_llm_client", lambda: llm)
    sample = {
        "messages": [{"role": "assistant", "content": "hello"}],
        "tool_calls": [{"name": "search", "arguments": {"q": "hello"}}],
        "usage": {
            "prompt": 2,
            "completion": 3,
            "total": 5,
            "cost": 0.01,
            "latency_ms": 50,
        },
        "documents": [
            {"content": "Evidence without optional metadata"},
            {
                "id": "doc-1",
                "title": "Title",
                "content": "Evidence",
                "metadata": {"source": "test"},
            },
        ],
    }

    response = client.post(
        "/mapping/propose",
        json={"sample_response_json": json.dumps(sample)},
    )

    assert response.status_code == 200
    assert response.json()["mappingSpec"]["toolCalls"]["itemsPath"] == "$.tool_calls[*]"
    assert response.json()["mappingSpec"]["retrievedDocs"]["contentPath"] == "$.content"


def test_propose_mapping_accepts_the_cross_runtime_jsonpath_subset(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    path = "$[\"hyphen-key\"][0]['spaced key']"
    content = json.dumps(
        {
            "version": 1,
            "fallback": {"singleAssistantContentPath": path},
        }
    )
    monkeypatch.setattr(mapping, "get_llm_client", lambda: FakeLlmClient(content))

    response = client.post(
        "/mapping/propose",
        json={"sample_response_json": '{"hyphen-key":[{"spaced key":"hello"}]}'},
    )

    assert response.status_code == 200
    assert response.json()["mappingSpec"]["fallback"]["singleAssistantContentPath"] == path


@pytest.mark.parametrize(
    "path",
    [
        '$.items[?(@.name =~ "foo.*")]',
        "$.items..content",
        "$.items[0:2]",
        "$.items[-1]",
        "$.items[0,1]",
        "$.items.`sorted`",
    ],
)
def test_propose_mapping_rejects_jsonpath_outside_the_cross_runtime_subset(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    path: str,
) -> None:
    content = json.dumps(
        {
            "version": 1,
            "fallback": {"singleAssistantContentPath": path},
        }
    )
    monkeypatch.setattr(mapping, "get_llm_client", lambda: FakeLlmClient(content))

    response = client.post(
        "/mapping/propose",
        json={
            "sample_response_json": json.dumps(
                {"items": [{"name": "foo", "content": "first"}, {"content": "second"}]}
            )
        },
    )

    assert response.status_code == 502
    assert response.json()["detail"]["error"]["message"] == "Failed to propose mapping"


@pytest.mark.parametrize(
    ("section", "sample"),
    [
        (
            {
                "messages": {
                    "itemsPath": "$.items[*]",
                    "rolePath": "$.role",
                    "contentPath": "$.content",
                }
            },
            {"items": [{"role": "assistant", "content": "first"}, {"role": "assistant"}]},
        ),
        (
            {
                "messages": {
                    "itemsPath": "$.items[*]",
                    "rolePath": "$.role",
                    "contentPath": "$.content",
                }
            },
            {"items": [{"role": "assistant", "content": "first"}, {"content": "second"}]},
        ),
        (
            {
                "toolCalls": {
                    "itemsPath": "$.items[*]",
                    "namePath": "$.name",
                    "argumentsPath": "$.arguments",
                }
            },
            {"items": [{"name": "search", "arguments": {}}, {"name": "lookup"}]},
        ),
        (
            {
                "toolCalls": {
                    "itemsPath": "$.items[*]",
                    "namePath": "$.name",
                    "argumentsPath": "$.arguments",
                }
            },
            {"items": [{"name": "search", "arguments": {}}, {"arguments": {}}]},
        ),
        (
            {
                "retrievedDocs": {
                    "itemsPath": "$.items[*]",
                    "contentPath": "$.content",
                }
            },
            {"items": [{"content": "first"}, {"title": "missing content"}]},
        ),
    ],
)
def test_propose_mapping_requires_mandatory_paths_on_every_selected_item(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    section: dict[str, Any],
    sample: dict[str, Any],
) -> None:
    content = json.dumps({"version": 1, **section})
    monkeypatch.setattr(mapping, "get_llm_client", lambda: FakeLlmClient(content))

    response = client.post(
        "/mapping/propose",
        json={"sample_response_json": json.dumps(sample)},
    )

    assert response.status_code == 502
    assert response.json()["detail"]["error"]["message"] == "Failed to propose mapping"


@pytest.mark.parametrize(
    "content",
    [
        '{"version":1}',
        '{"version":1,"fallback":{"singleAssistantContentPath":"   "}}',
        '{"version":1,"fallback":{"singleAssistantContentPath":"$["}}',
        '{"version":1,"fallback":{"singleAssistantContentPath":"$.missing"}}',
        '{"version":1,"usage":{"objectPath":"$.usage"}}',
    ],
)
def test_propose_mapping_rejects_empty_invalid_or_nonmatching_specs(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    content: str,
) -> None:
    monkeypatch.setattr(mapping, "get_llm_client", lambda: FakeLlmClient(content))

    response = client.post(
        "/mapping/propose",
        json={"sample_response_json": '{"answer":"hello","usage":{"total":1}}'},
    )

    assert response.status_code == 502
    assert response.json()["detail"]["error"]["message"] == "Failed to propose mapping"


@pytest.mark.parametrize(
    ("payload", "field"),
    [
        ({"sample_response_json": "{broken"}, "sample_response_json"),
        (
            {
                "sample_response_json": "{}",
                "sample_request_json": "{broken",
            },
            "sample_request_json",
        ),
    ],
)
def test_propose_mapping_rejects_malformed_sample_json(
    client: TestClient,
    payload: dict[str, Any],
    field: str,
) -> None:
    response = client.post("/mapping/propose", json=payload)

    assert response.status_code == 400
    assert field in str(response.json()["detail"])


@pytest.mark.parametrize("content", [None, "not-json", "```json\n{broken\n```"])
def test_propose_mapping_handles_invalid_provider_output_without_leaking_details(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    content: str | None,
) -> None:
    monkeypatch.setattr(mapping, "get_llm_client", lambda: FakeLlmClient(content))

    response = client.post(
        "/mapping/propose",
        json={"sample_response_json": "{}"},
    )

    assert response.status_code == 502
    assert "secret" not in response.text.lower()


def test_propose_mapping_handles_provider_failure_without_exposing_exception(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    llm = FakeLlmClient(error=RuntimeError("secret provider detail"))
    monkeypatch.setattr(
        mapping,
        "get_llm_client",
        lambda: llm,
    )

    response = client.post(
        "/mapping/propose",
        json={"sample_response_json": "{}"},
    )

    assert response.status_code == 502
    assert "secret provider detail" not in response.text
    assert llm.closed


def test_propose_mapping_reports_missing_provider_configuration(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def missing_configuration() -> Any:
        raise LlmConfigurationError("PROMPTLY_LLM_API_KEY is required")

    monkeypatch.setattr(mapping, "get_llm_client", missing_configuration)

    response = client.post(
        "/mapping/propose",
        json={"sample_response_json": "{}"},
    )

    assert response.status_code == 503
    assert response.json()["detail"]["error"]["message"] == (
        "Mapping proposal service is not configured"
    )


def test_get_llm_client_builds_openai_client(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    captured: dict[str, Any] = {}
    sentinel = object()

    def build_openai(**kwargs: Any) -> object:
        captured.update(kwargs)
        return sentinel

    monkeypatch.setenv("PROMPTLY_LLM_PROVIDER", "openai")
    monkeypatch.setenv("PROMPTLY_LLM_API_KEY", "test-key")
    monkeypatch.setenv("PROMPTLY_LLM_BASE_URL", "https://provider.example/v1")
    monkeypatch.setattr("llm.OpenAI", build_openai)

    assert mapping.get_llm_client() is sentinel
    timeout = captured.pop("timeout")
    assert isinstance(timeout, httpx.Timeout)
    assert captured == {
        "api_key": "test-key",
        "base_url": "https://provider.example/v1",
        "max_retries": 0,
    }


def test_get_llm_client_builds_azure_client(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    captured: dict[str, Any] = {}
    sentinel = object()

    def build_azure(**kwargs: Any) -> object:
        captured.update(kwargs)
        return sentinel

    monkeypatch.setenv("PROMPTLY_LLM_PROVIDER", "azureopenai")
    monkeypatch.setenv("PROMPTLY_LLM_API_KEY", "test-key")
    monkeypatch.setenv("PROMPTLY_LLM_AZURE_ENDPOINT", "https://azure.example")
    monkeypatch.setenv("PROMPTLY_LLM_API_VERSION", "2026-01-01")
    monkeypatch.setattr("llm.AzureOpenAI", build_azure)

    assert mapping.get_llm_client() is sentinel
    timeout = captured.pop("timeout")
    assert isinstance(timeout, httpx.Timeout)
    assert captured == {
        "api_key": "test-key",
        "azure_endpoint": "https://azure.example",
        "api_version": "2026-01-01",
        "max_retries": 0,
    }


@pytest.mark.parametrize(
    ("environment", "message"),
    [
        ({}, "PROMPTLY_LLM_API_KEY"),
        (
            {
                "PROMPTLY_LLM_PROVIDER": "azureopenai",
                "PROMPTLY_LLM_API_KEY": "key",
            },
            "PROMPTLY_LLM_AZURE_ENDPOINT",
        ),
        (
            {
                "PROMPTLY_LLM_PROVIDER": "unsupported",
                "PROMPTLY_LLM_API_KEY": "key",
            },
            "PROMPTLY_LLM_PROVIDER",
        ),
    ],
)
def test_get_llm_client_rejects_invalid_configuration(
    monkeypatch: pytest.MonkeyPatch,
    environment: dict[str, str],
    message: str,
) -> None:
    for name, value in environment.items():
        monkeypatch.setenv(name, value)

    with pytest.raises(ValueError, match=message):
        mapping.get_llm_client()
