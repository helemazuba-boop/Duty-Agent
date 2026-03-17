from __future__ import annotations

import time

from fastapi import APIRouter, HTTPException, Request

try:
    from models.schemas import DutyRosterResponse, DutyRosterUpdateRequest
except ImportError:
    from ..models.schemas import DutyRosterResponse, DutyRosterUpdateRequest

router = APIRouter(prefix="/api/v1", tags=["Roster"])


def _resolve_request_meta(request: Request, runtime) -> tuple[str, str]:
    trace_id = (request.headers.get("X-Duty-Trace-Id") or "").strip() or runtime.new_trace_id()
    request_source = (request.headers.get("X-Duty-Request-Source") or "").strip() or "api"
    return trace_id, request_source


@router.get("/roster", response_model=DutyRosterResponse)
async def get_roster(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    started_at = time.monotonic()
    runtime.logger.info("RosterRoute", "Received GET /api/v1/roster.", trace_id=trace_id, request_source=request_source)
    try:
        roster = runtime.query_service.get_roster(trace_id=trace_id, request_source=request_source)
        runtime.logger.info(
            "RosterRoute",
            "Completed GET /api/v1/roster.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
            roster_count=len(roster),
        )
        return {"roster": roster}
    except Exception as ex:
        runtime.logger.error(
            "RosterRoute",
            "GET /api/v1/roster failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        raise


@router.put("/roster", response_model=DutyRosterResponse)
async def put_roster(roster_request: DutyRosterUpdateRequest, request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    started_at = time.monotonic()
    roster_payload = [entry.model_dump(exclude_none=True, exclude_unset=True) for entry in roster_request.roster]
    runtime.logger.info(
        "RosterRoute",
        "Received PUT /api/v1/roster.",
        trace_id=trace_id,
        request_source=request_source,
        roster_count=len(roster_payload),
    )
    try:
        roster = runtime.command_service.update_roster(
            roster_payload,
            trace_id=trace_id,
            request_source=request_source,
        )
        runtime.logger.info(
            "RosterRoute",
            "Completed PUT /api/v1/roster.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
            roster_count=len(roster),
        )
        return {"roster": roster}
    except Exception as ex:
        runtime.logger.error(
            "RosterRoute",
            "PUT /api/v1/roster failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
            roster_count=len(roster_payload),
        )
        raise
