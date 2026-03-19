from __future__ import annotations

import time

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import JSONResponse

try:
    from application.settings_service import SettingsServiceError, SettingsVersionConflictError
    from models.schemas import DutySettingsPatchRequest
except ImportError:
    from ..application.settings_service import SettingsServiceError, SettingsVersionConflictError
    from ..models.schemas import DutySettingsPatchRequest

router = APIRouter(prefix="/api/v1", tags=["Settings"])


def _resolve_request_meta(request: Request, runtime) -> tuple[str, str]:
    trace_id = (request.headers.get("X-Duty-Trace-Id") or "").strip() or runtime.new_trace_id()
    request_source = (request.headers.get("X-Duty-Request-Source") or "").strip() or "api"
    return trace_id, request_source


@router.get("/settings")
async def get_settings(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")

    trace_id, request_source = _resolve_request_meta(request, runtime)
    started_at = time.monotonic()
    runtime.logger.info("SettingsRoute", "Received GET /api/v1/settings.", trace_id=trace_id, request_source=request_source)
    try:
        payload = runtime.settings_service.get_settings(trace_id=trace_id, request_source=request_source)
        runtime.logger.info(
            "SettingsRoute",
            "Completed GET /api/v1/settings.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        return payload
    except Exception as ex:
        runtime.logger.error(
            "SettingsRoute",
            "GET /api/v1/settings failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        raise


@router.patch("/settings")
async def patch_settings(settings_patch: DutySettingsPatchRequest, request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")

    trace_id, request_source = _resolve_request_meta(request, runtime)
    started_at = time.monotonic()
    patch_payload = settings_patch.model_dump(exclude_none=True, exclude_unset=True)
    runtime.logger.info(
        "SettingsRoute",
        "Received PATCH /api/v1/settings.",
        trace_id=trace_id,
        request_source=request_source,
        patch_keys=sorted(list((patch_payload.get("changes") or {}).keys())),
    )
    try:
        payload = runtime.settings_service.patch_settings(
            patch_payload,
            trace_id=trace_id,
            request_source=request_source,
        )
        runtime.logger.info(
            "SettingsRoute",
            "Completed PATCH /api/v1/settings.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
            success=payload.get("success", False),
        )
        return payload
    except SettingsVersionConflictError as ex:
        runtime.logger.warn(
            "SettingsRoute",
            "PATCH /api/v1/settings rejected due to version conflict.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        return JSONResponse(status_code=ex.status_code, content=ex.result)
    except SettingsServiceError as ex:
        runtime.logger.error(
            "SettingsRoute",
            "PATCH /api/v1/settings failed.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
            success=ex.result.get("success", False),
        )
        return JSONResponse(status_code=ex.status_code, content=ex.result)
    except Exception as ex:
        runtime.logger.error(
            "SettingsRoute",
            "PATCH /api/v1/settings threw unexpected exception.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        raise
