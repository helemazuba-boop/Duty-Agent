from __future__ import annotations

import time

from fastapi import APIRouter, HTTPException, Request

router = APIRouter(prefix="/api/v1/bridge", tags=["Bridge"])


def _resolve_request_meta(request: Request, runtime) -> tuple[str, str]:
    trace_id = (request.headers.get("X-Duty-Trace-Id") or "").strip() or runtime.new_trace_id()
    request_source = (request.headers.get("X-Duty-Request-Source") or "").strip() or "bridge"
    return trace_id, request_source


def _get_runtime(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    return runtime


@router.post("/heartbeat")
async def bridge_heartbeat(request: Request):
    runtime = _get_runtime(request)
    trace_id, request_source = _resolve_request_meta(request, runtime)
    started_at = time.monotonic()

    payload = runtime.mark_bridge_heartbeat(request_source=request_source)
    runtime.logger.info(
        "BridgeRoute",
        "Received POST /api/v1/bridge/heartbeat.",
        trace_id=trace_id,
        request_source=request_source,
        duration_ms=round((time.monotonic() - started_at) * 1000, 2),
    )
    return payload


@router.get("/status")
async def bridge_status(request: Request):
    runtime = _get_runtime(request)
    trace_id, request_source = _resolve_request_meta(request, runtime)
    payload = runtime.get_bridge_status()
    runtime.logger.info(
        "BridgeRoute",
        "Completed GET /api/v1/bridge/status.",
        trace_id=trace_id,
        request_source=request_source,
        status=payload.get("status"),
    )
    return payload
