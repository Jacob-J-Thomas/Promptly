"""Mapping-proposal API backed by the configured LLM provider."""

from __future__ import annotations

import json
import os
from contextlib import suppress
from typing import Annotated, Any, Literal, Self

from fastapi import APIRouter, HTTPException
from jsonpath_ng import parse as parse_jsonpath  # type: ignore[import-untyped]
from pydantic import BaseModel, ConfigDict, Field, StringConstraints, model_validator

from llm import (
    LlmClient,
    LlmConfigurationError,
)
from llm import (
    get_llm_client as create_llm_client,
)

router = APIRouter()
_DOT_MEMBER = r"\.[A-Za-z_][A-Za-z0-9_]*"
_INDEX_OR_WILDCARD = r"\[(?:0|[1-9][0-9]*|\*)\]"
_DOUBLE_QUOTED_MEMBER = r'\["[^"\\\r\n]*"\]'
_SINGLE_QUOTED_MEMBER = r"\['[^'\\\r\n]*'\]"
_COMPATIBLE_JSONPATH_PATTERN = (
    rf"^\$(?:{_DOT_MEMBER}|{_INDEX_OR_WILDCARD}|"
    rf"{_DOUBLE_QUOTED_MEMBER}|{_SINGLE_QUOTED_MEMBER})*$"
)
JsonPath = Annotated[
    str,
    StringConstraints(
        strip_whitespace=True,
        min_length=1,
        pattern=_COMPATIBLE_JSONPATH_PATTERN,
    ),
]


class MappingProposeRequest(BaseModel):
    sample_response_json: str
    sample_request_json: str | None = None
    hints: dict[str, Any] | None = None


class MessagesMapping(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)

    items_path: JsonPath = Field(alias="itemsPath")
    role_path: JsonPath = Field(default="$.role", alias="rolePath")
    content_path: JsonPath = Field(default="$.content", alias="contentPath")


class ToolCallsMapping(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)

    items_path: JsonPath = Field(alias="itemsPath")
    name_path: JsonPath = Field(default="$.name", alias="namePath")
    arguments_path: JsonPath = Field(default="$.arguments", alias="argumentsPath")


class UsageMapping(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)

    object_path: JsonPath = Field(alias="objectPath")
    prompt_tokens_path: JsonPath | None = Field(default=None, alias="promptTokensPath")
    completion_tokens_path: JsonPath | None = Field(default=None, alias="completionTokensPath")
    total_tokens_path: JsonPath | None = Field(default=None, alias="totalTokensPath")
    cost_path: JsonPath | None = Field(default=None, alias="costPath")
    latency_ms_path: JsonPath | None = Field(default=None, alias="latencyMsPath")

    @model_validator(mode="after")
    def require_metric_path(self) -> Self:
        if not any(
            (
                self.prompt_tokens_path,
                self.completion_tokens_path,
                self.total_tokens_path,
                self.cost_path,
                self.latency_ms_path,
            )
        ):
            raise ValueError("usage requires at least one metric path")
        return self


class RetrievedDocsMapping(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)

    items_path: JsonPath = Field(alias="itemsPath")
    id_path: JsonPath | None = Field(default=None, alias="idPath")
    title_path: JsonPath | None = Field(default=None, alias="titlePath")
    content_path: JsonPath = Field(alias="contentPath")
    metadata_path: JsonPath | None = Field(default=None, alias="metadataPath")


class FallbackMapping(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)

    single_assistant_content_path: JsonPath = Field(alias="singleAssistantContentPath")


class MappingSpecSchema(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)

    version: Literal[1] = 1
    messages: MessagesMapping | None = None
    tool_calls: ToolCallsMapping | None = Field(default=None, alias="toolCalls")
    usage: UsageMapping | None = None
    retrieved_docs: RetrievedDocsMapping | None = Field(default=None, alias="retrievedDocs")
    fallback: FallbackMapping | None = None

    @model_validator(mode="after")
    def require_extraction_section(self) -> Self:
        if not any(
            (self.messages, self.tool_calls, self.usage, self.retrieved_docs, self.fallback)
        ):
            raise ValueError("mapping spec requires at least one extraction section")
        return self


class MappingProposeResponse(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    mapping_spec: MappingSpecSchema = Field(alias="mappingSpec")
    reason: str | None = None


def get_llm_client() -> LlmClient:
    """Router-level client seam for deterministic tests."""

    return create_llm_client()


def _parse_input_json(value: str, field_name: str) -> object:
    try:
        return json.loads(value)
    except json.JSONDecodeError as exc:
        raise HTTPException(
            status_code=400,
            detail=f"Invalid JSON in {field_name} at position {exc.pos}",
        ) from exc


def _extract_json_object(value: str) -> object:
    text = value.strip()
    if text.startswith("```json") and text.endswith("```"):
        text = text[7:-3].strip()
    return json.loads(text)


def _select_required(path: str, value: object, label: str) -> list[object]:
    try:
        matches = [match.value for match in parse_jsonpath(path).find(value)]
    except Exception as exc:
        raise ValueError(f"{label} is not a valid JSONPath") from exc
    if not matches:
        raise ValueError(f"{label} does not match the sample response")
    return matches


def _validate_relative_paths(
    values: list[object],
    paths: tuple[tuple[str, str | None], ...],
    *,
    require_every_value: bool,
) -> None:
    for label, path in paths:
        if path is None:
            continue
        try:
            expression = parse_jsonpath(path)
            matches_by_value = [bool(expression.find(value)) for value in values]
        except Exception as exc:
            raise ValueError(f"{label} is not a valid JSONPath") from exc
        matches = all(matches_by_value) if require_every_value else any(matches_by_value)
        if not matches:
            raise ValueError(f"{label} does not match the sample response")


def _validate_mapping_against_sample(
    spec: MappingSpecSchema,
    sample_response: object,
) -> None:
    if spec.messages is not None:
        values = _select_required(
            spec.messages.items_path,
            sample_response,
            "messages.itemsPath",
        )
        _validate_relative_paths(
            values,
            (
                ("messages.rolePath", spec.messages.role_path),
                ("messages.contentPath", spec.messages.content_path),
            ),
            require_every_value=True,
        )
    if spec.tool_calls is not None:
        values = _select_required(
            spec.tool_calls.items_path,
            sample_response,
            "toolCalls.itemsPath",
        )
        _validate_relative_paths(
            values,
            (
                ("toolCalls.namePath", spec.tool_calls.name_path),
                ("toolCalls.argumentsPath", spec.tool_calls.arguments_path),
            ),
            require_every_value=True,
        )
    if spec.usage is not None:
        values = _select_required(spec.usage.object_path, sample_response, "usage.objectPath")
        _validate_relative_paths(
            values,
            (
                ("usage.promptTokensPath", spec.usage.prompt_tokens_path),
                ("usage.completionTokensPath", spec.usage.completion_tokens_path),
                ("usage.totalTokensPath", spec.usage.total_tokens_path),
                ("usage.costPath", spec.usage.cost_path),
                ("usage.latencyMsPath", spec.usage.latency_ms_path),
            ),
            require_every_value=True,
        )
    if spec.retrieved_docs is not None:
        values = _select_required(
            spec.retrieved_docs.items_path,
            sample_response,
            "retrievedDocs.itemsPath",
        )
        _validate_relative_paths(
            values,
            (("retrievedDocs.contentPath", spec.retrieved_docs.content_path),),
            require_every_value=True,
        )
        _validate_relative_paths(
            values,
            (
                ("retrievedDocs.idPath", spec.retrieved_docs.id_path),
                ("retrievedDocs.titlePath", spec.retrieved_docs.title_path),
                ("retrievedDocs.metadataPath", spec.retrieved_docs.metadata_path),
            ),
            require_every_value=False,
        )
    if spec.fallback is not None:
        _select_required(
            spec.fallback.single_assistant_content_path,
            sample_response,
            "fallback.singleAssistantContentPath",
        )


@router.post(
    "/propose",
    response_model=MappingProposeResponse,
    response_model_exclude_none=True,
)
def propose_mapping(request: MappingProposeRequest) -> MappingProposeResponse:
    """Propose and strictly validate a schema-1 MappingSpec."""

    sample_response = _parse_input_json(request.sample_response_json, "sample_response_json")
    if request.sample_request_json is not None:
        _parse_input_json(request.sample_request_json, "sample_request_json")

    system_prompt = """You analyze JSON and return a schema-1 Promptly MappingSpec.
Return only one JSON object. Use only JSONPath expressions composed of the root
`$`, dot member selectors, bracket-quoted member selectors, non-negative array
indexes, and `[*]` wildcards. Do not use filters, recursive descent, slices,
unions, negative indexes, functions, or script expressions. Only include
sections present in the sample. Supported sections are messages, toolCalls,
usage, retrievedDocs, and fallback. Each collection section requires its
itemsPath; usage requires objectPath; fallback may use
singleAssistantContentPath. Do not invent data or return prose."""
    user_prompt = (
        "Analyze this sample response and propose a MappingSpec:\n"
        f"{json.dumps(sample_response, indent=2)}\n\n"
        "Optional sample request:\n"
        f"{request.sample_request_json or 'Not provided'}\n\n"
        "Hints:\n"
        f"{json.dumps(request.hints, sort_keys=True) if request.hints else 'None provided'}"
    )

    try:
        client = get_llm_client()
        try:
            response = client.chat.completions.create(
                model=os.getenv("PROMPTLY_LLM_MODEL_DEFAULT", "gpt-4o-mini"),
                messages=[
                    {"role": "system", "content": system_prompt},
                    {"role": "user", "content": user_prompt},
                ],
                response_format={"type": "json_object"},
                temperature=0.1,
            )
            content = response.choices[0].message.content
            if not content:
                raise ValueError("empty provider response")
            mapping_spec = MappingSpecSchema.model_validate(_extract_json_object(content))
            _validate_mapping_against_sample(mapping_spec, sample_response)
        finally:
            with suppress(Exception):
                client.close()
    except LlmConfigurationError as exc:
        raise HTTPException(
            status_code=503,
            detail={
                "error": {
                    "message": "Mapping proposal service is not configured",
                    "details": str(exc),
                }
            },
        ) from exc
    except Exception as exc:
        raise HTTPException(
            status_code=502,
            detail={
                "error": {
                    "message": "Failed to propose mapping",
                    "details": "The LLM provider returned an invalid response",
                }
            },
        ) from exc

    return MappingProposeResponse(
        mappingSpec=mapping_spec,
        reason="Mapping spec generated successfully based on sample structure",
    )
