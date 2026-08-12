"""Promptly's FastAPI worker application."""

from __future__ import annotations

from typing import Literal

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

from llm import (
    LlmConfigurationError,
    LlmDependencyError,
    llm_configuration_status,
    load_llm_settings,
    probe_llm_dependency,
)
from routers import evaluation, mapping


class HealthResponse(BaseModel):
    status: Literal["healthy", "ready"]
    llm_provider: str
    llm_configured: bool


def _liveness_response() -> HealthResponse:
    provider, configured = llm_configuration_status()
    return HealthResponse(
        status="healthy",
        llm_provider=provider,
        llm_configured=configured,
    )


def create_app() -> FastAPI:
    """Build the worker app; exposed as a deterministic integration-test seam."""

    worker = FastAPI(title="Promptly Worker", version="1.0.0")
    worker.include_router(mapping.router, prefix="/mapping", tags=["mapping"])
    worker.include_router(evaluation.router, prefix="/eval", tags=["evaluation"])

    @worker.get("/health/live", response_model=HealthResponse)
    def health_live() -> HealthResponse:
        # Liveness reports configuration state but never depends on it.
        return _liveness_response()

    @worker.get("/health", response_model=HealthResponse)
    def health_alias() -> HealthResponse:
        return _liveness_response()

    @worker.get("/health/ready", response_model=HealthResponse)
    def health_ready() -> HealthResponse:
        try:
            settings = load_llm_settings()
            probe_llm_dependency(settings)
        except (LlmConfigurationError, LlmDependencyError) as exc:
            raise HTTPException(
                status_code=503,
                detail={
                    "status": "not_ready",
                    "message": str(exc),
                },
            ) from exc

        return HealthResponse(
            status="ready",
            llm_provider=settings.provider,
            llm_configured=True,
        )

    return worker


app = create_app()
