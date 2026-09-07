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


def _propose(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    spec: dict[str, Any] | str,
    sample: object | str,
) -> Any:
    content = spec if isinstance(spec, str) else json.dumps(spec)
    sample_json = sample if isinstance(sample, str) else json.dumps(sample, ensure_ascii=False)
    monkeypatch.setattr(mapping, "get_llm_client", lambda: FakeLlmClient(content))
    return client.post(
        "/mapping/propose",
        json={"sample_response_json": sample_json},
    )


def _assert_safe_502(response: Any) -> None:
    assert response.status_code == 502
    assert response.json() == {
        "detail": {
            "error": {
                "message": "Failed to propose mapping",
                "details": "The LLM provider returned an invalid response",
            }
        }
    }


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


@pytest.mark.parametrize(
    ("metric_path", "value"),
    [
        ("promptTokensPath", -(2**31)),
        ("completionTokensPath", 2**31 - 1),
        ("totalTokensPath", 0),
        ("costPath", 79228162514264337593543950335),
        ("latencyMsPath", -(2**63)),
        ("latencyMsPath", 2**63 - 1),
    ],
)
def test_propose_mapping_accepts_server_compatible_usage_value_boundaries(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    metric_path: str,
    value: object,
) -> None:
    content = json.dumps(
        {
            "version": 1,
            "usage": {
                "objectPath": "$.usage",
                metric_path: "$.value",
            },
        }
    )
    monkeypatch.setattr(mapping, "get_llm_client", lambda: FakeLlmClient(content))
    sample = {"usage": {"value": value}}

    response = client.post(
        "/mapping/propose",
        json={"sample_response_json": json.dumps(sample)},
    )

    assert response.status_code == 200


@pytest.mark.parametrize(
    ("metric_path", "invalid_value"),
    [
        ("promptTokensPath", True),
        ("completionTokensPath", 2**31),
        ("totalTokensPath", 1.0),
        ("latencyMsPath", -(2**63) - 1),
        ("latencyMsPath", 1.0),
        ("costPath", True),
        ("costPath", "0.2"),
        ("costPath", 79228162514264337593543950336),
    ],
)
def test_propose_mapping_rejects_any_server_incompatible_usage_match(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    metric_path: str,
    invalid_value: object,
) -> None:
    content = json.dumps(
        {
            "version": 1,
            "usage": {
                "objectPath": "$.usage",
                metric_path: "$.value",
            },
        }
    )
    monkeypatch.setattr(mapping, "get_llm_client", lambda: FakeLlmClient(content))

    response = client.post(
        "/mapping/propose",
        json={"sample_response_json": json.dumps({"usage": {"value": invalid_value}})},
    )

    assert response.status_code == 502
    assert response.json() == {
        "detail": {
            "error": {
                "message": "Failed to propose mapping",
                "details": "The LLM provider returned an invalid response",
            }
        }
    }


@pytest.mark.parametrize("invalid_metadata", ["source", [], 1, True])
def test_propose_mapping_rejects_any_non_object_metadata_match(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    invalid_metadata: object,
) -> None:
    content = json.dumps(
        {
            "version": 1,
            "retrievedDocs": {
                "itemsPath": "$.documents[*]",
                "contentPath": "$.content",
                "metadataPath": "$.metadata",
            },
        }
    )
    monkeypatch.setattr(mapping, "get_llm_client", lambda: FakeLlmClient(content))
    sample = {
        "documents": [
            {"content": "first", "metadata": {"source": "valid"}},
            {"content": "second", "metadata": invalid_metadata},
        ]
    }

    response = client.post(
        "/mapping/propose",
        json={"sample_response_json": json.dumps(sample)},
    )

    assert response.status_code == 502
    assert response.json()["detail"]["error"] == {
        "message": "Failed to propose mapping",
        "details": "The LLM provider returned an invalid response",
    }


@pytest.mark.parametrize(
    ("path", "sample"),
    [
        ("$[*].content", {"first": {"content": "one"}}),
        ('$["a]b"]', {"a]b": "one"}),
        ("$['c]d']", {"c]d": "one"}),
        ('$["apostrophe\'s"]', {"apostrophe's": "one"}),
        ("$['double\"quote']", {'double"quote': "one"}),
        ('$["[9007199254740992]"]', {"[9007199254740992]": "one"}),
    ],
)
def test_propose_mapping_matches_jsonpath_net_object_and_quoted_member_semantics(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    path: str,
    sample: object,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {"version": 1, "fallback": {"singleAssistantContentPath": path}},
        sample,
    )

    assert response.status_code == 200


@pytest.mark.parametrize(
    "path",
    [
        '$["tab\tkey"]',
        "$['control\x01key']",
        '$["nul\x00key"]',
        "$['unit-separator\x1fkey']",
    ],
)
def test_propose_mapping_rejects_control_characters_in_quoted_members(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    path: str,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {"version": 1, "fallback": {"singleAssistantContentPath": path}},
        {"answer": "unused"},
    )

    _assert_safe_502(response)


def test_propose_mapping_accepts_the_maximum_safe_array_index_before_fallback(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {
            "version": 1,
            "messages": {"itemsPath": "$[9007199254740991]"},
            "fallback": {"singleAssistantContentPath": "$.answer"},
        },
        {"answer": "fallback"},
    )

    assert response.status_code == 200


@pytest.mark.parametrize("index", ["9007199254740992", "9" * 5000])
def test_propose_mapping_rejects_unsafe_dormant_messages_index_before_fallback(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    index: str,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {
            "version": 1,
            "messages": {"itemsPath": f"$[{index}]"},
            "fallback": {"singleAssistantContentPath": "$.answer"},
        },
        {"answer": "fallback"},
    )

    _assert_safe_502(response)


@pytest.mark.parametrize(
    ("path", "sample"),
    [
        ("$[0]", '"abc"'),
        ("$[*]", '"abc"'),
        ("$[0]", {"0": "value"}),
        ("$[*]", 1),
        ("$.answer", {"answer": None}),
    ],
)
def test_propose_mapping_rejects_jsonpath_net_nonmatches_and_omits_null(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    path: str,
    sample: object,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {"version": 1, "fallback": {"singleAssistantContentPath": path}},
        sample,
    )

    _assert_safe_502(response)


def test_propose_mapping_accepts_null_optional_metadata_as_absent(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {
            "version": 1,
            "retrievedDocs": {
                "itemsPath": "$.documents[*]",
                "contentPath": "$.content",
                "metadataPath": "$.metadata",
            },
        },
        {
            "documents": [
                {"content": "one", "metadata": None},
                {"content": "two", "metadata": {"source": "valid"}},
            ]
        },
    )

    assert response.status_code == 200


def test_propose_mapping_does_not_evaluate_fallback_after_messages_succeed(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {
            "version": 1,
            "messages": {"itemsPath": "$.messages[*]"},
            "fallback": {"singleAssistantContentPath": "$.missing"},
        },
        {"messages": [{"role": "assistant", "content": "primary"}]},
    )

    assert response.status_code == 200


@pytest.mark.parametrize(
    "messages",
    [
        [],
        [{"role": "assistant", "content": "partial"}, {"role": "assistant"}],
        [{"role": 1, "content": "wrong role type"}],
    ],
)
def test_propose_mapping_accepts_valid_fallback_after_messages_fail(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    messages: list[object],
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {
            "version": 1,
            "messages": {"itemsPath": "$.messages[*]"},
            "fallback": {"singleAssistantContentPath": "$.answer"},
        },
        {"messages": messages, "answer": "fallback"},
    )

    assert response.status_code == 200


def test_propose_mapping_rejects_when_messages_and_fallback_both_fail(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {
            "version": 1,
            "messages": {"itemsPath": "$.messages[*]"},
            "fallback": {"singleAssistantContentPath": "$.answer"},
        },
        {"messages": [{"role": "assistant"}], "answer": 42},
    )

    _assert_safe_502(response)


@pytest.mark.parametrize("fallback", [42, True, [], {}, None])
def test_propose_mapping_requires_string_fallback_content(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    fallback: object,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {"version": 1, "fallback": {"singleAssistantContentPath": "$.answer"}},
        {"answer": fallback},
    )

    _assert_safe_502(response)


@pytest.mark.parametrize(
    ("section", "sample"),
    [
        (
            {"messages": {"itemsPath": "$.items[*]"}},
            {"items": [{"role": 1, "content": "content"}]},
        ),
        (
            {"toolCalls": {"itemsPath": "$.items[*]"}},
            {"items": [{"name": 1, "arguments": {}}]},
        ),
        (
            {"retrievedDocs": {"itemsPath": "$.items[*]", "contentPath": "$.content"}},
            {"items": [{"content": 1}]},
        ),
    ],
)
def test_propose_mapping_requires_string_canonical_fields(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    section: dict[str, Any],
    sample: dict[str, Any],
) -> None:
    response = _propose(client, monkeypatch, {"version": 1, **section}, sample)

    _assert_safe_502(response)


@pytest.mark.parametrize(
    ("section", "sample"),
    [
        ({"messages": {"itemsPath": "$.items[*]"}}, {"items": ["message"]}),
        ({"toolCalls": {"itemsPath": "$.items[*]"}}, {"items": [1]}),
        (
            {"retrievedDocs": {"itemsPath": "$.items[*]", "contentPath": "$.content"}},
            {"items": [True]},
        ),
        (
            {"usage": {"objectPath": "$.usage", "totalTokensPath": "$.total"}},
            {"usage": [{"total": 1}]},
        ),
    ],
)
def test_propose_mapping_requires_object_items_and_usage_object(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    section: dict[str, Any],
    sample: dict[str, Any],
) -> None:
    response = _propose(client, monkeypatch, {"version": 1, **section}, sample)

    _assert_safe_502(response)


@pytest.mark.parametrize(
    ("section", "sample"),
    [
        (
            {"usage": {"objectPath": "$.usages[*]", "totalTokensPath": "$.total"}},
            {"usages": [{"total": 1}, {"total": 2}]},
        ),
        (
            {"usage": {"objectPath": "$.usage", "totalTokensPath": "$.values[*]"}},
            {"usage": {"values": [1, 2]}},
        ),
        (
            {
                "messages": {
                    "itemsPath": "$.items[*]",
                    "rolePath": "$.roles[*]",
                }
            },
            {"items": [{"roles": ["assistant", "user"], "content": "hello"}]},
        ),
        (
            {
                "retrievedDocs": {
                    "itemsPath": "$.items[*]",
                    "contentPath": "$.content",
                    "idPath": "$.ids[*]",
                }
            },
            {"items": [{"content": "doc", "ids": [1, 2]}]},
        ),
    ],
)
def test_propose_mapping_rejects_multiple_matches_for_singular_paths(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    section: dict[str, Any],
    sample: dict[str, Any],
) -> None:
    response = _propose(client, monkeypatch, {"version": 1, **section}, sample)

    _assert_safe_502(response)


@pytest.mark.parametrize("document_id", ["doc-1", 1, 1.25])
def test_propose_mapping_accepts_string_or_numeric_document_id(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    document_id: object,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {
            "version": 1,
            "retrievedDocs": {
                "itemsPath": "$.items[*]",
                "contentPath": "$.content",
                "idPath": "$.id",
                "titlePath": "$.title",
            },
        },
        {"items": [{"content": "doc", "id": document_id, "title": "title"}]},
    )

    assert response.status_code == 200


@pytest.mark.parametrize(
    ("path", "value"),
    [
        ("idPath", True),
        ("idPath", {}),
        ("idPath", []),
        ("titlePath", 1),
        ("titlePath", False),
        ("titlePath", {}),
    ],
)
def test_propose_mapping_rejects_invalid_document_id_or_title_type(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    path: str,
    value: object,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {
            "version": 1,
            "retrievedDocs": {
                "itemsPath": "$.items[*]",
                "contentPath": "$.content",
                path: "$.value",
            },
        },
        {"items": [{"content": "doc", "value": value}]},
    )

    _assert_safe_502(response)


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
        '{"fallback":{"singleAssistantContentPath":"$.answer"}}',
        '{"version":null,"fallback":{"singleAssistantContentPath":"$.answer"}}',
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
        ({"sample_response_json": '{"value":NaN}'}, "sample_response_json"),
        ({"sample_response_json": '{"value":Infinity}'}, "sample_response_json"),
        ({"sample_response_json": '{"value":-Infinity}'}, "sample_response_json"),
        ({"sample_response_json": '{"answer":1,"answer":2}'}, "sample_response_json"),
        ({"sample_response_json": '{"answer":"\\ud800"}'}, "sample_response_json"),
        (
            {
                "sample_response_json": "{}",
                "sample_request_json": "{broken",
            },
            "sample_request_json",
        ),
        (
            {
                "sample_response_json": "{}",
                "sample_request_json": '{"value":NaN}',
            },
            "sample_request_json",
        ),
        (
            {
                "sample_response_json": "{}",
                "sample_request_json": '{"value":1,"value":2}',
            },
            "sample_request_json",
        ),
        (
            {
                "sample_response_json": "{}",
                "sample_request_json": '{"value":"\\ud800"}',
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


def test_propose_mapping_matches_system_text_json_depth_limit(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    spec = {"version": 1, "fallback": {"singleAssistantContentPath": "$.answer"}}
    accepted = '{"answer":"ok","nested":' + "[" * 63 + "0" + "]" * 63 + "}"
    rejected = '{"answer":"ok","nested":' + "[" * 64 + "0" + "]" * 64 + "}"

    accepted_response = _propose(client, monkeypatch, spec, accepted)
    rejected_response = _propose(client, monkeypatch, spec, rejected)

    assert accepted_response.status_code == 200
    assert rejected_response.status_code == 400


def test_propose_mapping_accepts_unrelated_valid_number_with_extreme_exponent(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {"version": 1, "fallback": {"singleAssistantContentPath": "$.answer"}},
        '{"answer":"ok","unrelated":1e-999999999999999999999}',
    )

    assert response.status_code == 200


def test_propose_mapping_accepts_unrelated_integer_beyond_python_digit_limit(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {"version": 1, "fallback": {"singleAssistantContentPath": "$.answer"}},
        '{"answer":"ok","unrelated":' + "9" * 5000 + "}",
    )

    assert response.status_code == 200


def test_propose_mapping_preserves_exact_sample_literals_in_provider_prompt(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    sample = '{"usage":{"cost":7.9228162514264337593543950335e28}}'
    llm = FakeLlmClient('{"version":1,"usage":{"objectPath":"$.usage","costPath":"$.cost"}}')
    monkeypatch.setattr(mapping, "get_llm_client", lambda: llm)

    response = client.post(
        "/mapping/propose",
        json={"sample_response_json": sample},
    )

    assert response.status_code == 200
    assert sample in llm.completions.calls[0]["messages"][1]["content"]


@pytest.mark.parametrize(
    ("sample", "expected_status"),
    [
        ('{"usage":{"cost":7.9228162514264337593543950335e28}}', 200),
        ('{"usage":{"cost":79228162514264337593543950335.4}}', 200),
        ('{"usage":{"cost":79228162514264337593543950335.5}}', 502),
        ('{"usage":{"cost":7.9228162514264337593543950336e28}}', 502),
        ('{"usage":{"cost":1e-999999999999999999999}}', 200),
        ('{"usage":{"cost":1e999999999999999999999}}', 502),
        ('{"usage":{"cost":0e999999999999999999999}}', 200),
        ('{"usage":{"cost":-0e+999999999999999999999}}', 200),
        ('{"usage":{"cost":1e+0000000001}}', 200),
    ],
)
def test_propose_mapping_matches_dotnet_decimal_boundaries_without_rounding_input(
    client: TestClient,
    monkeypatch: pytest.MonkeyPatch,
    sample: str,
    expected_status: int,
) -> None:
    response = _propose(
        client,
        monkeypatch,
        {"version": 1, "usage": {"objectPath": "$.usage", "costPath": "$.cost"}},
        sample,
    )

    assert response.status_code == expected_status


@pytest.mark.parametrize(
    "content",
    [
        None,
        "not-json",
        "```json\n{broken\n```",
        '{"version":NaN,"fallback":{"singleAssistantContentPath":"$.answer"}}',
        '{"version":1,"fallback":{"singleAssistantContentPath":"$.answer"},"x":1,"x":2}',
        '{"version":1,"fallback":{"singleAssistantContentPath":"$.\\ud800"}}',
        "[" * 65 + "0" + "]" * 65,
    ],
)
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
