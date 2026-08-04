from __future__ import annotations

from fastapi import APIRouter, HTTPException, Request

try:
    from models.schemas import DutyModelProbeRequest
    from state_ops import sanitize_error_for_client
except ImportError:
    from ..models.schemas import DutyModelProbeRequest
    from ..state_ops import sanitize_error_for_client

router = APIRouter(prefix="/api/v1", tags=["Readiness"])


def _resolve_request_meta(request: Request, runtime) -> tuple[str, str]:
    trace_id = (request.headers.get("X-Duty-Trace-Id") or "").strip() or runtime.new_trace_id()
    request_source = (request.headers.get("X-Duty-Request-Source") or "").strip() or "api"
    return trace_id, request_source


@router.get("/readiness")
async def get_readiness(request: Request, probe_model: bool = False):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    try:
        return runtime.query_service.get_readiness(
            probe=bool(probe_model),
            trace_id=trace_id,
            request_source=request_source,
        )
    except Exception as ex:  # noqa: BLE001 - surface a sanitized error
        runtime.logger.error(
            "ReadinessRoute",
            "GET /api/v1/readiness failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
        )
        raise HTTPException(status_code=400, detail=sanitize_error_for_client(str(ex))) from ex


@router.post("/duty/model-probe")
async def model_probe(request_data: DutyModelProbeRequest, request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    return runtime.query_service.probe_model_connectivity(
        request_data.base_url,
        request_data.model,
        request_data.api_key or "",
    )
