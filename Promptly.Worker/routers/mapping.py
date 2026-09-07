"""Mapping-proposal API backed by the configured LLM provider."""

from __future__ import annotations

import json
import os
from collections.abc import Callable
from contextlib import suppress
from decimal import Decimal, DecimalException, localcontext
from typing import Annotated, Any, Literal, NamedTuple, Self

from fastapi import APIRouter, HTTPException
from pydantic import (
    AfterValidator,
    BaseModel,
    ConfigDict,
    Field,
    StringConstraints,
    model_validator,
)

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
_DOUBLE_QUOTED_MEMBER = r'\["[^"\\\x00-\x1f]*"\]'
_SINGLE_QUOTED_MEMBER = r"\['[^'\\\x00-\x1f]*'\]"
_COMPATIBLE_JSONPATH_PATTERN = (
    rf"^\$(?:{_DOT_MEMBER}|{_INDEX_OR_WILDCARD}|"
    rf"{_DOUBLE_QUOTED_MEMBER}|{_SINGLE_QUOTED_MEMBER})*$"
)
_INT32_MIN = -(2**31)
_INT32_MAX = 2**31 - 1
_INT64_MIN = -(2**63)
_INT64_MAX = 2**63 - 1
_DECIMAL_OVERFLOW_THRESHOLD = Decimal("79228162514264337593543950335.5")
_MAX_JSON_DEPTH = 64
_SAFE_EXPONENT_DIGITS = 9
_MAX_SAFE_ARRAY_INDEX = "9007199254740991"
_SIMPLE_MEMBER_CHARACTERS = frozenset(
    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_"
)


def _validate_jsonpath_index_bounds(path: str) -> str:
    position = 1
    while position < len(path):
        if path[position] == ".":
            position += 1
            while position < len(path) and path[position] in _SIMPLE_MEMBER_CHARACTERS:
                position += 1
            continue

        if path[position + 1] in {'"', "'"}:
            position = path.index(path[position + 1] + "]", position + 2) + 2
            continue

        end = path.index("]", position + 1)
        selector = path[position + 1 : end]
        if selector != "*" and (
            len(selector) > len(_MAX_SAFE_ARRAY_INDEX)
            or (len(selector) == len(_MAX_SAFE_ARRAY_INDEX) and selector > _MAX_SAFE_ARRAY_INDEX)
        ):
            raise ValueError(f"array index must not exceed {_MAX_SAFE_ARRAY_INDEX}")
        position = end + 1
    return path


JsonPath = Annotated[
    str,
    StringConstraints(
        strip_whitespace=True,
        min_length=1,
        pattern=_COMPATIBLE_JSONPATH_PATTERN,
    ),
    AfterValidator(_validate_jsonpath_index_bounds),
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

    version: Literal[1]
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


class _JsonNumber(str):
    """A validated JSON number retained losslessly for .NET conversion parity."""


def get_llm_client() -> LlmClient:
    """Router-level client seam for deterministic tests."""

    return create_llm_client()


def _reject_nonstandard_json_constant(value: str) -> None:
    raise ValueError(f"{value} is not valid JSON")


def _reject_duplicate_keys(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON property {key!r}")
        result[key] = value
    return result


def _validate_json_depth(value: str) -> None:
    depth = 0
    in_string = False
    escaped = False
    for character in value:
        if in_string:
            if escaped:
                escaped = False
            elif character == "\\":
                escaped = True
            elif character == '"':
                in_string = False
            continue
        if character == '"':
            in_string = True
        elif character in "[{":
            depth += 1
            if depth > _MAX_JSON_DEPTH:
                raise ValueError(f"JSON exceeds maximum depth {_MAX_JSON_DEPTH}")
        elif character in "]}":
            depth -= 1


def _reject_surrogates(value: object) -> None:
    if isinstance(value, str):
        if any("\ud800" <= character <= "\udfff" for character in value):
            raise ValueError("JSON contains an invalid Unicode surrogate")
    elif isinstance(value, list):
        for item in value:
            _reject_surrogates(item)
    elif isinstance(value, dict):
        for key, item in value.items():
            _reject_surrogates(key)
            _reject_surrogates(item)


def _loads_strict_json(value: str, *, preserve_numbers: bool = False) -> object:
    _validate_json_depth(value)
    number_options = (
        {"parse_int": _JsonNumber, "parse_float": _JsonNumber}
        if preserve_numbers
        else {"parse_float": Decimal}
    )
    parsed = json.loads(
        value,
        parse_constant=_reject_nonstandard_json_constant,
        object_pairs_hook=_reject_duplicate_keys,
        **number_options,
    )
    _reject_surrogates(parsed)
    return parsed


def _parse_input_json(value: str, field_name: str) -> object:
    try:
        return _loads_strict_json(value, preserve_numbers=True)
    except (DecimalException, json.JSONDecodeError, RecursionError, ValueError) as exc:
        position = f" at position {exc.pos}" if isinstance(exc, json.JSONDecodeError) else ""
        raise HTTPException(
            status_code=400,
            detail=f"Invalid JSON in {field_name}{position}",
        ) from exc


def _extract_json_object(value: str) -> object:
    text = value.strip()
    if text.startswith("```json") and text.endswith("```"):
        text = text[7:-3].strip()
    return _loads_strict_json(text)


class _PathSegment(NamedTuple):
    kind: Literal["member", "index", "wildcard"]
    value: str | int | None = None


def _parse_compatible_jsonpath(path: str) -> list[_PathSegment]:
    segments: list[_PathSegment] = []
    position = 1
    while position < len(path):
        if path[position] == ".":
            end = position + 1
            while end < len(path) and path[end] in _SIMPLE_MEMBER_CHARACTERS:
                end += 1
            segments.append(_PathSegment("member", path[position + 1 : end]))
            position = end
            continue

        if path[position + 1] in {'"', "'"}:
            end = path.index(path[position + 1] + "]", position + 2) + 1
        else:
            end = path.index("]", position)
        selector = path[position + 1 : end]
        if selector == "*":
            segments.append(_PathSegment("wildcard"))
        elif selector[0] in {'"', "'"}:
            segments.append(_PathSegment("member", selector[1:-1]))
        else:
            segments.append(_PathSegment("index", int(selector)))
        position = end + 1
    return segments


def _evaluate_jsonpath(path: str, value: object) -> list[object]:
    matches = [value]
    for segment in _parse_compatible_jsonpath(path):
        next_matches: list[object] = []
        for match in matches:
            if segment.kind == "member" and isinstance(match, dict):
                member = segment.value
                if isinstance(member, str) and member in match:
                    next_matches.append(match[member])
            elif segment.kind == "index" and isinstance(match, list):
                index = segment.value
                if isinstance(index, int) and index < len(match):
                    next_matches.append(match[index])
            elif segment.kind == "wildcard":
                if isinstance(match, list):
                    next_matches.extend(match)
                elif isinstance(match, dict):
                    next_matches.extend(match.values())
        matches = next_matches
    return [match for match in matches if match is not None]


def _select_required(path: str, value: object, label: str) -> list[object]:
    matches = _evaluate_jsonpath(path, value)
    if not matches:
        raise ValueError(f"{label} does not match the sample response")
    return matches


def _select_singular(
    value: object,
    label: str,
    path: str,
) -> object:
    matches = _evaluate_jsonpath(path, value)
    if len(matches) != 1:
        raise ValueError(f"{label} must match exactly one value")
    return matches[0]


def _validate_required_singular_paths(
    values: list[object],
    paths: tuple[tuple[str, str, Callable[[object], bool] | None, str | None], ...],
) -> None:
    for value in values:
        for label, path, predicate, expected_shape in paths:
            match = _select_singular(value, label, path)
            if predicate is not None and not predicate(match):
                raise ValueError(f"{label} must match {expected_shape}")


def _validate_optional_singular_path(
    values: list[object],
    label: str,
    path: str | None,
    predicate: Callable[[object], bool] | None = None,
    expected_shape: str | None = None,
) -> None:
    if path is None:
        return
    matched = False
    for value in values:
        matches = _evaluate_jsonpath(path, value)
        if len(matches) > 1:
            raise ValueError(f"{label} must match at most one value per item")
        if not matches:
            continue
        matched = True
        if predicate is not None and not predicate(matches[0]):
            raise ValueError(f"{label} must match {expected_shape}")
    if not matched:
        raise ValueError(f"{label} does not match the sample response")


def _is_int32(value: object) -> bool:
    if not isinstance(value, _JsonNumber) or any(character in value for character in ".eE"):
        return False
    return _integer_token_in_range(value, _INT32_MIN, _INT32_MAX)


def _is_int64(value: object) -> bool:
    if not isinstance(value, _JsonNumber) or any(character in value for character in ".eE"):
        return False
    return _integer_token_in_range(value, _INT64_MIN, _INT64_MAX)


def _integer_token_in_range(value: _JsonNumber, minimum: int, maximum: int) -> bool:
    negative = value.startswith("-")
    digits = value[1:] if negative else value
    normalized = digits.lstrip("0") or "0"
    limit = str(abs(minimum) if negative else maximum)
    if len(normalized) != len(limit):
        return len(normalized) < len(limit)
    return normalized <= limit


def _is_decimal_compatible(value: object) -> bool:
    if not isinstance(value, _JsonNumber):
        return False
    mantissa, separator, exponent_text = value.lower().partition("e")
    exponent_sign = exponent_text[:1] if exponent_text[:1] in "+-" else ""
    exponent_digits = exponent_text[len(exponent_sign) :].lstrip("0") or "0"
    normalized_exponent = exponent_sign + exponent_digits
    exponent = (
        int(normalized_exponent)
        if separator and len(exponent_digits) <= _SAFE_EXPONENT_DIGITS
        else None
    )
    if separator and exponent is None:
        is_zero = all(character in "-0." for character in mantissa)
        return is_zero or exponent_text.startswith("-")
    digits = sum(character.isdigit() for character in value)
    try:
        with localcontext() as context:
            context.prec = max(29, digits)
            decimal_value = Decimal(value)
    except DecimalException:
        return False
    return decimal_value.is_finite() and decimal_value.copy_abs() < _DECIMAL_OVERFLOW_THRESHOLD


def _is_string(value: object) -> bool:
    return isinstance(value, str) and not isinstance(value, _JsonNumber)


def _is_json_number(value: object) -> bool:
    return isinstance(value, _JsonNumber)


def _is_document_id(value: object) -> bool:
    return _is_string(value) or _is_json_number(value)


def _select_object_items(path: str, value: object, label: str) -> list[object]:
    items = _select_required(path, value, label)
    if any(not isinstance(item, dict) for item in items):
        raise ValueError(f"{label} must match objects")
    return items


def _validate_messages(spec: MessagesMapping, sample_response: object) -> None:
    values = _select_object_items(spec.items_path, sample_response, "messages.itemsPath")
    _validate_required_singular_paths(
        values,
        (
            ("messages.rolePath", spec.role_path, _is_string, "a string"),
            ("messages.contentPath", spec.content_path, None, None),
        ),
    )


def _validate_fallback(spec: FallbackMapping, sample_response: object) -> None:
    content = _select_singular(
        sample_response,
        "fallback.singleAssistantContentPath",
        spec.single_assistant_content_path,
    )
    if not _is_string(content):
        raise ValueError("fallback.singleAssistantContentPath must match a string")


def _validate_mapping_against_sample(
    spec: MappingSpecSchema,
    sample_response: object,
) -> None:
    if spec.messages is not None:
        try:
            _validate_messages(spec.messages, sample_response)
        except ValueError:
            if spec.fallback is None:
                raise
            _validate_fallback(spec.fallback, sample_response)
    elif spec.fallback is not None:
        _validate_fallback(spec.fallback, sample_response)
    if spec.tool_calls is not None:
        values = _select_object_items(
            spec.tool_calls.items_path,
            sample_response,
            "toolCalls.itemsPath",
        )
        _validate_required_singular_paths(
            values,
            (
                ("toolCalls.namePath", spec.tool_calls.name_path, _is_string, "a string"),
                ("toolCalls.argumentsPath", spec.tool_calls.arguments_path, None, None),
            ),
        )
    if spec.usage is not None:
        usage = _select_singular(sample_response, "usage.objectPath", spec.usage.object_path)
        if not isinstance(usage, dict):
            raise ValueError("usage.objectPath must match an object")
        for label, path, predicate, expected_shape in (
            (
                "usage.promptTokensPath",
                spec.usage.prompt_tokens_path,
                _is_int32,
                "a 32-bit integer",
            ),
            (
                "usage.completionTokensPath",
                spec.usage.completion_tokens_path,
                _is_int32,
                "a 32-bit integer",
            ),
            (
                "usage.totalTokensPath",
                spec.usage.total_tokens_path,
                _is_int32,
                "a 32-bit integer",
            ),
            ("usage.costPath", spec.usage.cost_path, _is_decimal_compatible, "a decimal number"),
            (
                "usage.latencyMsPath",
                spec.usage.latency_ms_path,
                _is_int64,
                "a 64-bit integer",
            ),
        ):
            if path is None:
                continue
            value = _select_singular(usage, label, path)
            if not predicate(value):
                raise ValueError(f"{label} must match {expected_shape}")
    if spec.retrieved_docs is not None:
        values = _select_object_items(
            spec.retrieved_docs.items_path,
            sample_response,
            "retrievedDocs.itemsPath",
        )
        _validate_required_singular_paths(
            values,
            (
                (
                    "retrievedDocs.contentPath",
                    spec.retrieved_docs.content_path,
                    _is_string,
                    "a string",
                ),
            ),
        )
        _validate_optional_singular_path(
            values,
            "retrievedDocs.metadataPath",
            spec.retrieved_docs.metadata_path,
            lambda value: isinstance(value, dict),
            "an object",
        )
        _validate_optional_singular_path(
            values,
            "retrievedDocs.idPath",
            spec.retrieved_docs.id_path,
            _is_document_id,
            "a string or number",
        )
        _validate_optional_singular_path(
            values,
            "retrievedDocs.titlePath",
            spec.retrieved_docs.title_path,
            _is_string,
            "a string",
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
        f"{request.sample_response_json}\n\n"
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
