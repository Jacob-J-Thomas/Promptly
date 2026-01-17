import json
import os
from typing import Optional, Dict, Any
from fastapi import APIRouter, HTTPException
from pydantic import BaseModel
from openai import OpenAI, AzureOpenAI

router = APIRouter()

# Pydantic models
class MappingProposeRequest(BaseModel):
    sample_response_json: str
    sample_request_json: Optional[str] = None
    hints: Optional[Dict[str, Any]] = None

class MappingSpecSchema(BaseModel):
    version: int = 1
    messages: Optional[Dict[str, str]] = None
    toolCalls: Optional[Dict[str, str]] = None
    usage: Optional[Dict[str, str]] = None
    retrievedDocs: Optional[Dict[str, str]] = None
    fallback: Optional[Dict[str, str]] = None

class MappingProposeResponse(BaseModel):
    mappingSpec: Dict[str, Any]
    reason: Optional[str] = None

class ErrorResponse(BaseModel):
    error: Dict[str, str]

def get_llm_client():
    """Get the configured LLM client (OpenAI or Azure OpenAI)"""
    provider = os.getenv("PROMPTLY_LLM_PROVIDER", "azureopenai").lower()
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

@router.post("/propose", response_model=MappingProposeResponse)
async def propose_mapping(request: MappingProposeRequest):
    """
    Propose a MappingSpec based on sample response JSON.
    Uses LLM to analyze the structure and generate appropriate JSONPath expressions.
    """
    try:
        # Validate that the sample response is valid JSON
        try:
            sample_json = json.loads(request.sample_response_json)
        except json.JSONDecodeError as e:
            raise HTTPException(
                status_code=400,
                detail=f"Invalid JSON in sample_response_json: {str(e)}"
            )

        # Get LLM client
        client = get_llm_client()
        model = os.getenv("PROMPTLY_LLM_MODEL_DEFAULT", "gpt-4o-mini")

        # Build the prompt for the LLM
        system_prompt = """You are an expert at analyzing JSON structures and creating JSONPath expressions.

Your task is to analyze a sample JSON response and propose a MappingSpec that can extract:
- messages: conversation messages with role and content
- toolCalls: function/tool calls made by the assistant
- usage: token usage and cost information
- retrievedDocs: documents retrieved for RAG

MappingSpec Format:
{
  "version": 1,
  "messages": {
    "itemsPath": "JSONPath to array of message objects",
    "rolePath": "$.role",
    "contentPath": "$.content"
  },
  "toolCalls": {
    "itemsPath": "JSONPath to array of tool call objects",
    "namePath": "$.name",
    "argumentsPath": "$.arguments"
  },
  "usage": {
    "objectPath": "JSONPath to usage object",
    "promptTokensPath": "$.prompt_tokens",
    "completionTokensPath": "$.completion_tokens",
    "totalTokensPath": "$.total_tokens"
  },
  "retrievedDocs": {
    "itemsPath": "JSONPath to array of doc objects",
    "contentPath": "$.content",
    "titlePath": "$.title",
    "idPath": "$.id"
  },
  "fallback": {
    "singleAssistantContentPath": "JSONPath to single content string if no messages array"
  }
}

Rules:
1. Use JSONPath expressions (e.g., $.data.messages, $.response.choices[*].message)
2. Only include sections where data exists in the sample
3. If messages don't exist but there's a single response content, use fallback.singleAssistantContentPath
4. Return ONLY the MappingSpec JSON, nothing else"""

        user_prompt = f"""Analyze this sample JSON response and propose a MappingSpec:

Sample Response:
{json.dumps(sample_json, indent=2)}

Hints: {request.hints if request.hints else "None provided"}

Return a valid MappingSpec JSON that can extract the data from this structure."""

        # Call LLM
        response = client.chat.completions.create(
            model=model,
            messages=[
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": user_prompt}
            ],
            response_format={"type": "json_object"},
            temperature=0.1
        )

        # Parse the LLM response
        mapping_spec_text = response.choices[0].message.content
        if not mapping_spec_text:
            raise ValueError("LLM returned empty response")

        try:
            mapping_spec = json.loads(mapping_spec_text)
        except json.JSONDecodeError as e:
            # Try to extract JSON from markdown code blocks
            if "```json" in mapping_spec_text:
                json_start = mapping_spec_text.find("```json") + 7
                json_end = mapping_spec_text.find("```", json_start)
                mapping_spec_text = mapping_spec_text[json_start:json_end].strip()
                mapping_spec = json.loads(mapping_spec_text)
            else:
                raise ValueError(f"LLM response is not valid JSON: {str(e)}")

        return MappingProposeResponse(
            mappingSpec=mapping_spec,
            reason="Mapping spec generated successfully based on sample structure"
        )

    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(
            status_code=500,
            detail={
                "error": {
                    "message": f"Failed to propose mapping: {str(e)}",
                    "details": type(e).__name__
                }
            }
        )
