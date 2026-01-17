import json
import os
from typing import Optional, Dict, Any, List
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel
from openai import OpenAI, AzureOpenAI

router = APIRouter()

# Pydantic models
class Message(BaseModel):
    role: str
    content: str

class ToolCall(BaseModel):
    name: str
    argumentsJson: str

class Usage(BaseModel):
    promptTokens: Optional[int] = None
    completionTokens: Optional[int] = None
    totalTokens: Optional[int] = None
    cost: Optional[float] = None
    latencyMs: Optional[int] = None

class RetrievedDoc(BaseModel):
    id: Optional[str] = None
    title: Optional[str] = None
    content: str
    metadata: Optional[Dict[str, Any]] = None

class CanonicalTrace(BaseModel):
    messages: List[Message]
    toolCalls: List[ToolCall] = []
    usage: Optional[Usage] = None
    retrievedDocs: List[RetrievedDoc] = []
    rawResponse: Optional[str] = None

class LlmJudgeRequest(BaseModel):
    rubric: str
    min_score: float
    trace: CanonicalTrace
    model: Optional[str] = None
    provider: Optional[str] = None
    metadata: Optional[Dict[str, Any]] = None

class GroundednessRequest(BaseModel):
    min_score: float
    trace: CanonicalTrace
    docs: List[RetrievedDoc]
    model: Optional[str] = None
    provider: Optional[str] = None

class EvaluationResponse(BaseModel):
    score: float
    reason: str

class ErrorResponse(BaseModel):
    error: Dict[str, str]

def get_llm_client(provider: Optional[str] = None):
    """Get the configured LLM client"""
    provider = provider or os.getenv("PROMPTLY_LLM_PROVIDER", "azureopenai").lower()
    api_key = os.getenv("PROMPTLY_LLM_API_KEY")

    if not api_key:
        raise ValueError("PROMPTLY_LLM_API_KEY environment variable is not set")

    if provider == "azureopenai":
        azure_endpoint = os.getenv("PROMPTLY_LLM_AZURE_ENDPOINT")
        api_version = os.getenv("PROMPTLY_LLM_API_VERSION", "2024-08-01-preview")

        if not azure_endpoint:
            raise ValueError("PROMPTLY_LLM_AZURE_ENDPOINT is required for Azure OpenAI (e.g., https://your-resource.openai.azure.com)")

        return AzureOpenAI(
            api_key=api_key,
            azure_endpoint=azure_endpoint,
            api_version=api_version
        )
    else:
        base_url = os.getenv("PROMPTLY_LLM_BASE_URL", "https://api.openai.com/v1")
        return OpenAI(api_key=api_key, base_url=base_url)

@router.post("/llm-judge", response_model=EvaluationResponse)
async def evaluate_llm_judge(request: LlmJudgeRequest):
    """
    Evaluate assistant's response against a rubric using an LLM judge.
    Returns a score between 0 and 1.
    """
    try:
        client = get_llm_client(request.provider)
        model = request.model or os.getenv("PROMPTLY_LLM_MODEL_DEFAULT", "gpt-4o-mini")

        # Extract assistant messages and tool calls
        assistant_messages = [m for m in request.trace.messages if m.role == "assistant"]
        assistant_content = "\n\n".join([m.content for m in assistant_messages])

        tool_calls_summary = ""
        if request.trace.toolCalls:
            tool_calls_summary = "\n\nTool Calls:\n" + "\n".join(
                [f"- {tc.name}: {tc.argumentsJson}" for tc in request.trace.toolCalls]
            )

        # Build evaluation prompt
        system_prompt = """You are an expert evaluator. Your task is to score an AI assistant's response against a specific rubric.

You must return a JSON object with exactly two fields:
- "score": a float between 0.0 and 1.0 (where 1.0 is perfect)
- "reason": a brief explanation of your scoring

Be objective and consistent in your evaluation."""

        user_prompt = f"""Rubric:
{request.rubric}

Assistant's Response:
{assistant_content}
{tool_calls_summary}

Evaluate this response against the rubric. Return a JSON object with "score" (0.0-1.0) and "reason"."""

        # Call LLM judge
        response = client.chat.completions.create(
            model=model,
            messages=[
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt}
            ],
            response_format={"type": "json_object"},
            temperature=0.1
        )

        # Parse response
        result_text = response.choices[0].message.content
        if not result_text:
            raise ValueError("LLM judge returned empty response")

        result = json.loads(result_text)
        score = float(result.get("score", 0.0))
        reason = result.get("reason", "No reason provided")

        # Clamp score to [0, 1]
        score = max(0.0, min(1.0, score))

        return EvaluationResponse(score=score, reason=reason)

    except Exception as e:
        # Return error response instead of crashing
        return EvaluationResponse(
            score=0.0,
            reason=f"Evaluation failed: {str(e)}"
        )

@router.post("/groundedness", response_model=EvaluationResponse)
async def evaluate_groundedness(request: GroundednessRequest):
    """
    Evaluate if assistant's response is grounded in the retrieved documents.
    Returns a score between 0 and 1.
    """
    try:
        client = get_llm_client(request.provider)
        model = request.model or os.getenv("PROMPTLY_LLM_MODEL_DEFAULT", "gpt-4o-mini")

        # Extract assistant messages
        assistant_messages = [m for m in request.trace.messages if m.role == "assistant"]
        assistant_content = "\n\n".join([m.content for m in assistant_messages])

        # Format retrieved documents
        docs_content = "\n\n".join([
            f"Document {i+1}:\n{doc.content}"
            for i, doc in enumerate(request.docs or request.trace.retrievedDocs)
        ])

        if not docs_content:
            return EvaluationResponse(
                score=1.0,
                reason="No retrieved documents to check groundedness against"
            )

        # Build evaluation prompt
        system_prompt = """You are an expert at evaluating whether AI responses are grounded in source documents.

Your task is to determine if the assistant's response is supported by the retrieved documents.

Return a JSON object with:
- "score": 1.0 if fully grounded, 0.5 if partially grounded, 0.0 if not grounded or hallucinated
- "reason": explanation of your assessment

Consider:
- Are claims in the response supported by the documents?
- Are there any hallucinated facts not present in the documents?
- Is the response faithful to the source material?"""

        user_prompt = f"""Retrieved Documents:
{docs_content}

Assistant's Response:
{assistant_content}

Evaluate if the response is grounded in the retrieved documents. Return JSON with "score" (0.0-1.0) and "reason"."""

        # Call LLM judge
        response = client.chat.completions.create(
            model=model,
            messages=[
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt}
            ],
            response_format={"type": "json_object"},
            temperature=0.1
        )

        # Parse response
        result_text = response.choices[0].message.content
        if not result_text:
            raise ValueError("LLM judge returned empty response")

        result = json.loads(result_text)
        score = float(result.get("score", 0.0))
        reason = result.get("reason", "No reason provided")

        # Clamp score to [0, 1]
        score = max(0.0, min(1.0, score))

        return EvaluationResponse(score=score, reason=reason)

    except Exception as e:
        # Return error response instead of crashing
        return EvaluationResponse(
            score=0.0,
            reason=f"Evaluation failed: {str(e)}"
        )
