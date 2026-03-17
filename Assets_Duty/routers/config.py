from __future__ import annotations

import time

from fastapi import APIRouter, HTTPException, Request

try:
    from models.schemas import DutyBackendConfigPatch
    from state_ops import ConfigVersionConflictError
except ImportError:
    from ..models.schemas import DutyBackendConfigPatch
    from ..state_ops import ConfigVersionConflictError

router = APIRouter(prefix="/api/v1", tags=["Config"])


def _resolve_request_meta(request: Request, runtime) -> tuple[str, str]:
    trace_id = (request.headers.get("X-Duty-Trace-Id") or "").strip() or runtime.new_trace_id()
    request_source = (request.headers.get("X-Duty-Request-Source") or "").strip() or "api"
    return trace_id, request_source


@router.get("/config")
async def get_config(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    started_at = time.monotonic()
    runtime.logger.info("ConfigRoute", "Received GET /api/v1/config.", trace_id=trace_id, request_source=request_source)
    try:
        payload = runtime.query_service.get_config(trace_id=trace_id, request_source=request_source)
        runtime.logger.info(
            "ConfigRoute",
            "Completed GET /api/v1/config.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        return payload
    except Exception as ex:
        runtime.logger.error(
            "ConfigRoute",
            "GET /api/v1/config failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        raise


@router.patch("/config")
async def patch_config(config_patch: DutyBackendConfigPatch, request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    started_at = time.monotonic()
    patch_payload = config_patch.model_dump(exclude_none=True, exclude_unset=True)
    runtime.logger.info(
        "ConfigRoute",
        "Received PATCH /api/v1/config.",
        trace_id=trace_id,
        request_source=request_source,
        patch_keys=sorted(list(patch_payload.keys())),
    )
    try:
        payload = runtime.command_service.update_config(
            patch_payload,
            trace_id=trace_id,
            request_source=request_source,
        )
        runtime.logger.info(
            "ConfigRoute",
            "Completed PATCH /api/v1/config.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        return payload
    except ConfigVersionConflictError as ex:
        runtime.logger.warn(
            "ConfigRoute",
            "PATCH /api/v1/config rejected due to version conflict.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
            patch_keys=sorted(list(patch_payload.keys())),
        )
        raise HTTPException(status_code=409, detail=str(ex)) from ex
    except Exception as ex:
        runtime.logger.error(
            "ConfigRoute",
            "PATCH /api/v1/config failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
            patch_keys=sorted(list(patch_payload.keys())),
        )
        raise


@router.get("/snapshot")
async def get_snapshot(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    started_at = time.monotonic()
    runtime.logger.info("ConfigRoute", "Received GET /api/v1/snapshot.", trace_id=trace_id, request_source=request_source)
    try:
        payload = runtime.query_service.get_snapshot(trace_id=trace_id, request_source=request_source)
        runtime.logger.info(
            "ConfigRoute",
            "Completed GET /api/v1/snapshot.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        return payload
    except Exception as ex:
        runtime.logger.error(
            "ConfigRoute",
            "GET /api/v1/snapshot failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        raise
