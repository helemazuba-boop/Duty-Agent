from __future__ import annotations

import asyncio
import json
import time
from queue import Empty

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import StreamingResponse

try:
    from models.schemas import DutyNotificationSettingsPatch, DutyNotificationTestRequest
    from state_ops import ConfigVersionConflictError, sanitize_error_for_client
except ImportError:
    from ..models.schemas import DutyNotificationSettingsPatch, DutyNotificationTestRequest
    from ..state_ops import ConfigVersionConflictError, sanitize_error_for_client

router = APIRouter(prefix="/api/v1/notifications", tags=["Notifications"])


def _resolve_request_meta(request: Request, runtime) -> tuple[str, str]:
    trace_id = (request.headers.get("X-Duty-Trace-Id") or "").strip() or runtime.new_trace_id()
    request_source = (request.headers.get("X-Duty-Request-Source") or "").strip() or "api"
    return trace_id, request_source


@router.get("/settings")
async def get_notification_settings(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    return runtime.query_service.get_notification_settings(trace_id=trace_id, request_source=request_source)


@router.patch("/settings")
async def patch_notification_settings(settings_patch: DutyNotificationSettingsPatch, request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    patch_payload = settings_patch.model_dump(exclude_none=True, exclude_unset=True)
    try:
        return runtime.command_service.update_notification_settings(
            patch_payload,
            trace_id=trace_id,
            request_source=request_source,
        )
    except ConfigVersionConflictError as ex:
        raise HTTPException(status_code=409, detail=str(ex)) from ex
    except Exception as ex:
        runtime.logger.error(
            "NotificationRoute",
            "PATCH /api/v1/notifications/settings failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
        )
        raise HTTPException(status_code=400, detail=sanitize_error_for_client(str(ex))) from ex


@router.post("/test")
async def test_notification(request_data: DutyNotificationTestRequest, request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    event = runtime.publish_notification(
        "test",
        request_data.title,
        request_data.body,
        level="info",
        route=request_data.route,
        source=request_source,
        targets=runtime.get_notification_targets(),
        data={"trace_id": trace_id},
    )
    return {"status": "queued", "event": event}


@router.get("/stream")
async def stream_notifications(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")

    trace_id, request_source = _resolve_request_meta(request, runtime)
    queue = runtime.subscribe_notifications()
    runtime.logger.info(
        "NotificationRoute",
        "Accepted notification stream.",
        trace_id=trace_id,
        request_source=request_source,
    )

    async def event_generator():
        try:
            yield _encode_sse("ready", {"status": "ready", "created_at": time.time()})
            while True:
                if await request.is_disconnected():
                    break
                try:
                    event = await asyncio.to_thread(queue.get, True, 10.0)
                except Empty:
                    yield _encode_sse("ping", {"created_at": time.time()})
                    continue
                yield _encode_sse("notification", event)
        finally:
            runtime.unsubscribe_notifications(queue)
            runtime.logger.info(
                "NotificationRoute",
                "Notification stream disconnected.",
                trace_id=trace_id,
                request_source=request_source,
            )

    return StreamingResponse(event_generator(), media_type="text/event-stream")


def _encode_sse(event_type: str, payload: dict) -> str:
    data = json.dumps(payload, ensure_ascii=False)
    return f"event: {event_type}\ndata: {data}\n\n"
