"""LLM judge and groundedness evaluation endpoints."""

from __future__ import annotations

import json
import os
from contextlib import suppress
from typing import Any, Never

from fastapi import APIRouter, HTTPException
from openai.types.chat import ChatCompletionMessageParam
from openai.types.shared_params import ResponseFormatJSONObject
from pydantic import BaseModel, ConfigDict, Field

from llm import (
    LlmClient,
    LlmConfigurationError,
    UnsupportedProviderError,
)
from llm import (
    get_llm_client as create_llm_client,
)

router = APIRouter()


class Message(BaseModel):
    role: str
    content: str


class ToolCall(BaseModel):
    name: str
    arguments_json: str = Field(alias="argumentsJson")


class Usage(BaseModel):
    prompt_tokens: int | None = Field(default=None, alias="promptTokens")
    completion_tokens: int | None = Field(default=None, alias="completionTokens")
    total_tokens: int | None = Field(default=None, alias="totalTokens")
    cost: float | None = None
    latency_ms: int | None = Field(default=None, alias="latencyMs")


class RetrievedDoc(BaseModel):
    id: str | None = None
    title: str | None = None
    content: str
    metadata: dict[str, Any] | None = None


class CanonicalTrace(BaseModel):
    messages: list[Message] = Field(default_factory=list)
    tool_calls: list[ToolCall] = Field(default_factory=list, alias="toolCalls")
    usage: Usage | None = None
    retrieved_docs: list[RetrievedDoc] = Field(default_factory=list, alias="retrievedDocs")
    raw_response: str | None = Field(default=None, alias="rawResponse")


class LlmJudgeRequest(BaseModel):
    rubric: str
    min_score: float = Field(ge=0.0, le=1.0)
    trace: CanonicalTrace
    model: str | None = None
    provider: str | None = None
    metadata: dict[str, Any] | None = None


class GroundednessRequest(BaseModel):
    min_score: float = Field(ge=0.0, le=1.0)
    trace: CanonicalTrace
    docs: list[RetrievedDoc] = Field(default_factory=list)
    model: str | None = None
    provider: str | None = None


class EvaluationResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    score: float = Field(ge=0.0, le=1.0)
    reason: str


def get_llm_client(provider: str | None = None) -> LlmClient:
    """Router-level client seam for deterministic tests."""

    return create_llm_client(provider)


def _assistant_content(trace: CanonicalTrace) -> str:
    return "\n\n".join(message.content for message in trace.messages if message.role == "assistant")


def _call_evaluator(
    *,
    provider: str | None,
    model: str | None,
    system_prompt: str,
    user_prompt: str,
) -> EvaluationResponse:
    client = get_llm_client(provider)
    try:
        selected_model = model or os.getenv("PROMPTLY_LLM_MODEL_DEFAULT") or "gpt-4o-mini"
        messages: list[ChatCompletionMessageParam] = [
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": user_prompt},
        ]
        response_format: ResponseFormatJSONObject = {"type": "json_object"}
        response = client.chat.completions.create(
            model=selected_model,
            messages=messages,
            response_format=response_format,
            temperature=0.1,
        )
        content = response.choices[0].message.content
        if not content:
            raise ValueError("empty provider response")
        return EvaluationResponse.model_validate(json.loads(content))
    finally:
        with suppress(Exception):
            client.close()


def _raise_safe_evaluation_error(exc: Exception, *, provider_was_overridden: bool) -> Never:
    if isinstance(exc, UnsupportedProviderError) and provider_was_overridden:
        raise HTTPException(
            status_code=400,
            detail={"error": {"message": "Unsupported LLM provider"}},
        ) from exc
    if isinstance(exc, LlmConfigurationError):
        raise HTTPException(
            status_code=503,
            detail={
                "error": {
                    "message": "Evaluation service is not configured",
                    "details": str(exc),
                }
            },
        ) from exc
    raise HTTPException(
        status_code=502,
        detail={
            "error": {
                "message": "Evaluation could not be completed",
                "details": "The LLM provider returned an invalid response",
            }
        },
    ) from exc


@router.post("/llm-judge", response_model=EvaluationResponse)
def evaluate_llm_judge(request: LlmJudgeRequest) -> EvaluationResponse:
    """Evaluate an assistant response against a caller-supplied rubric."""

    tool_calls = ""
    if request.trace.tool_calls:
        tool_calls = "\n\nTool Calls:\n" + "\n".join(
            f"- {tool_call.name}: {tool_call.arguments_json}"
            for tool_call in request.trace.tool_calls
        )
    system_prompt = """You are an objective evaluator. Return only a JSON object
with exactly two fields: score (a float from 0.0 through 1.0) and reason (a
concise explanation)."""
    user_prompt = f"""Rubric:
{request.rubric}

Required minimum score: {request.min_score}

Assistant response:
{_assistant_content(request.trace)}{tool_calls}

Evaluate the response against the rubric."""

    try:
        return _call_evaluator(
            provider=request.provider,
            model=request.model,
            system_prompt=system_prompt,
            user_prompt=user_prompt,
        )
    except Exception as exc:
        _raise_safe_evaluation_error(exc, provider_was_overridden=request.provider is not None)


@router.post("/groundedness", response_model=EvaluationResponse)
def evaluate_groundedness(request: GroundednessRequest) -> EvaluationResponse:
    """Score whether the assistant response is supported by supplied documents."""

    documents = request.docs or request.trace.retrieved_docs
    if not documents:
        return EvaluationResponse(
            score=0.0,
            reason="No retrieved documents were available for groundedness evaluation",
        )

    docs_content = "\n\n".join(
        f"Document {index}:\n{document.content}"
        for index, document in enumerate(documents, start=1)
    )
    system_prompt = """Evaluate whether the assistant response is supported by
the supplied documents. Return only a JSON object with exactly two fields:
score (a float from 0.0 through 1.0) and reason (a concise explanation)."""
    user_prompt = f"""Retrieved documents:
{docs_content}

Assistant response:
{_assistant_content(request.trace)}

Required minimum score: {request.min_score}

Evaluate groundedness, penalizing unsupported claims."""

    try:
        return _call_evaluator(
            provider=request.provider,
            model=request.model,
            system_prompt=system_prompt,
            user_prompt=user_prompt,
        )
    except Exception as exc:
        _raise_safe_evaluation_error(exc, provider_was_overridden=request.provider is not None)
