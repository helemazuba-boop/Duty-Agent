from __future__ import annotations

import asyncio
import json
import threading

from fastapi import APIRouter, HTTPException, Request, WebSocket, WebSocketDisconnect
from fastapi.responses import StreamingResponse
from auth import WEBSOCKET_BUSY_CODE, WEBSOCKET_UNAUTHORIZED_CODE, is_websocket_authorized

try:
    from models.schemas import DutyRequest, DutyScheduleEntrySaveRequest, DutyScheduleEntrySaveResponse
except ImportError:
    from ..models.schemas import DutyRequest, DutyScheduleEntrySaveRequest, DutyScheduleEntrySaveResponse

router = APIRouter(prefix="/api/v1/duty", tags=["Duty"])


def _resolve_request_meta(request: Request, runtime, request_data) -> tuple[str, str]:
    trace_value = str(getattr(request_data, "trace_id", "") or "").strip()
    source_value = str(getattr(request_data, "request_source", "") or "").strip()
    trace_id = (request.headers.get("X-Duty-Trace-Id") or "").strip() or trace_value or runtime.new_trace_id()
    request_source = (
        (request.headers.get("X-Duty-Request-Source") or "").strip()
        or source_value
        or "api"
    )
    return trace_id, request_source


def _resolve_websocket_meta(websocket: WebSocket, runtime) -> tuple[str, str]:
    trace_id = (websocket.headers.get("X-Duty-Trace-Id") or "").strip() or runtime.new_trace_id()
    request_source = (websocket.headers.get("X-Duty-Request-Source") or "").strip() or "api"
    return trace_id, request_source


async def _enqueue_send(send_queue: asyncio.Queue, payload: dict | None):
    await send_queue.put(payload)


async def _socket_sender(websocket: WebSocket, send_queue: asyncio.Queue, runtime, trace_id: str, request_source: str):
    try:
        while True:
            payload = await send_queue.get()
            if payload is None:
                return
            await websocket.send_json(payload)
    except Exception as ex:
        runtime.logger.error(
            "DutyLive",
            "Duty control channel sender failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
        )
        raise


@router.post("/schedule")
async def schedule(request_data: DutyRequest, request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        return StreamingResponse(
            iter(['event: complete\ndata: {"status":"error","message":"Runtime is not initialized."}\n\n']),
            media_type="text/event-stream",
        )
    trace_id, request_source = _resolve_request_meta(request, runtime, request_data)
    stop_event = threading.Event()

    async def event_generator():
        loop = asyncio.get_event_loop()
        queue = asyncio.Queue()

        def put_progress(phase, message, stream_chunk=None):
            loop.call_soon_threadsafe(queue.put_nowait, {
                "type": "progress",
                "data": {"phase": phase, "message": message, "stream_chunk": stream_chunk}
            })

        def run_task():
            try:
                payload = request_data.model_dump(exclude_none=True, exclude_unset=True)
                payload.setdefault("trace_id", trace_id)
                payload.setdefault("request_source", request_source)
                result = runtime.command_service.run_schedule(payload, put_progress, stop_event)
                loop.call_soon_threadsafe(queue.put_nowait, {"type": "done", "data": result})
            except InterruptedError:
                loop.call_soon_threadsafe(queue.put_nowait, {"type": "error", "message": "Cancelled by user."})
            except Exception as e:
                loop.call_soon_threadsafe(queue.put_nowait, {"type": "error", "message": str(e)})

        worker_thread = threading.Thread(target=run_task, daemon=True)
        worker_thread.start()

        try:
            while True:
                msg = await queue.get()
                if msg["type"] == "progress":
                    payload = json.dumps(msg["data"], ensure_ascii=False)
                    yield f"data: {payload}\n\n"
                elif msg["type"] == "done":
                    payload = json.dumps(msg["data"], ensure_ascii=False)
                    yield f"event: complete\ndata: {payload}\n\n"
                    break
                elif msg["type"] == "error":
                    payload = json.dumps({"status": "error", "message": msg["message"]}, ensure_ascii=False)
                    yield f"event: complete\ndata: {payload}\n\n"
                    break
        finally:
            stop_event.set()

    return StreamingResponse(event_generator(), media_type="text/event-stream")


@router.post("/schedule-entry", response_model=DutyScheduleEntrySaveResponse)
async def save_schedule_entry(request_data: DutyScheduleEntrySaveRequest, request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")

    trace_id, request_source = _resolve_request_meta(request, runtime, request_data)
    payload = request_data.model_dump(exclude_none=True, exclude_unset=True)
    payload.setdefault("trace_id", trace_id)
    payload.setdefault("request_source", request_source)
    return await asyncio.to_thread(
        runtime.command_service.save_schedule_entry,
        payload,
        trace_id,
        request_source,
    )


@router.post("/schedule-rollback")
async def schedule_rollback(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")

    trace_id = (request.headers.get("X-Duty-Trace-Id") or "").strip() or runtime.new_trace_id()
    request_source = (request.headers.get("X-Duty-Request-Source") or "").strip() or "api"
    return await asyncio.to_thread(
        runtime.command_service.rollback_schedule,
        trace_id=trace_id,
        request_source=request_source,
    )


@router.websocket("/live")
async def duty_live(websocket: WebSocket):
    runtime = getattr(websocket.app.state, "runtime", None)
    if runtime is None:
        await websocket.accept()
        await websocket.send_json({"type": "error", "message": "Runtime is not initialized."})
        await websocket.close(code=1011)
        return
    if not is_websocket_authorized(websocket, runtime):
        await websocket.close(code=WEBSOCKET_UNAUTHORIZED_CODE, reason="Unauthorized")
        return

    trace_id, request_source = _resolve_websocket_meta(websocket, runtime)
    owner_claimed = runtime.try_claim_duty_live_owner(trace_id)
    if not owner_claimed:
        current_owner = runtime.get_duty_live_owner()
        runtime.logger.warn(
            "DutyLive",
            "Rejected extra WebSocket duty control channel because another owner is already connected.",
            trace_id=trace_id,
            request_source=request_source,
            current_owner=current_owner,
        )
        await websocket.accept()
        await websocket.send_json(
            {
                "type": "error",
                "trace_id": trace_id,
                "request_source": request_source,
                "message": "Duty live channel is busy. Another owner is already connected.",
            }
        )
        await websocket.close(code=WEBSOCKET_BUSY_CODE, reason="Duty live channel is busy.")
        return

    await websocket.accept()
    runtime.logger.info(
        "DutyLive",
        "Accepted WebSocket duty control channel.",
        trace_id=trace_id,
        request_source=request_source,
    )

    active_schedule_tasks: dict[str, asyncio.Task] = {}
    active_schedule_stops: dict[str, threading.Event] = {}
    send_queue: asyncio.Queue = asyncio.Queue()
    sender_task = asyncio.create_task(_socket_sender(websocket, send_queue, runtime, trace_id, request_source))

    try:
        while True:
            if sender_task.done():
                await sender_task
            message = await websocket.receive_json()
            message_type = str((message or {}).get("type") or "").strip().lower()
            msg_trace_id = str((message or {}).get("trace_id") or "").strip() or trace_id
            msg_request_source = str((message or {}).get("request_source") or "").strip() or request_source
            client_change_id = str((message or {}).get("client_change_id") or "").strip()

            if message_type == "hello":
                await _enqueue_send(send_queue, {
                    "type": "hello",
                    "trace_id": msg_trace_id,
                    "request_source": msg_request_source,
                })
            elif message_type == "schedule_run":
                task, stop_event = _start_schedule_run(
                    send_queue, runtime, message, client_change_id, msg_trace_id, msg_request_source
                )
                if task is not None:
                    active_schedule_tasks[client_change_id] = task
                    active_schedule_stops[client_change_id] = stop_event
            elif message_type == "schedule_entry_save":
                await _handle_schedule_entry_save(
                    send_queue,
                    runtime,
                    message,
                    client_change_id,
                    msg_trace_id,
                    msg_request_source,
                )
            elif message_type == "schedule_cancel":
                _cancel_schedule_run(active_schedule_stops, client_change_id, runtime, msg_trace_id)
            elif message_type == "schedule_rollback":
                result = runtime.command_service.rollback_schedule(
                    trace_id=msg_trace_id, request_source=msg_request_source,
                )
                await _enqueue_send(send_queue, {
                    "type": "rollback_complete",
                    "client_change_id": client_change_id,
                    "trace_id": msg_trace_id,
                    "request_source": msg_request_source,
                    **result,
                })
            else:
                await _enqueue_send(send_queue, {
                    "type": "error",
                    "client_change_id": client_change_id,
                    "trace_id": msg_trace_id,
                    "request_source": msg_request_source,
                    "message": f"Unsupported message type: {message_type or '<empty>'}",
                })

            done_ids = [cid for cid, task in active_schedule_tasks.items() if task.done()]
            for cid in done_ids:
                active_schedule_tasks.pop(cid, None)
                active_schedule_stops.pop(cid, None)

    except WebSocketDisconnect:
        runtime.logger.info(
            "DutyLive",
            "WebSocket duty control channel disconnected.",
            trace_id=trace_id,
            request_source=request_source,
        )
    except Exception as ex:
        runtime.logger.error(
            "DutyLive",
            "WebSocket duty control channel error.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
        )
    finally:
        for stop_event in active_schedule_stops.values():
            stop_event.set()
        for task in active_schedule_tasks.values():
            task.cancel()
        try:
            await _enqueue_send(send_queue, None)
            await sender_task
        except Exception:
            pass
        runtime.release_duty_live_owner(trace_id)


def _start_schedule_run(
    send_queue: asyncio.Queue, runtime,
    message: dict, client_change_id: str, trace_id: str, request_source: str,
) -> tuple[asyncio.Task | None, threading.Event]:
    stop_event = threading.Event()
    loop = asyncio.get_event_loop()

    async def _run():
        await _enqueue_send(send_queue, {
            "type": "accepted",
            "client_change_id": client_change_id,
            "trace_id": trace_id,
            "request_source": request_source,
        })

        queue: asyncio.Queue = asyncio.Queue()

        def put_progress(phase, msg, stream_chunk=None):
            loop.call_soon_threadsafe(queue.put_nowait, {
                "type": "progress",
                "data": {"phase": phase, "message": msg, "stream_chunk": stream_chunk},
            })

        def run_task():
            try:
                payload = {
                    "instruction": str((message or {}).get("instruction") or "").strip(),
                    "request_source": request_source,
                    "trace_id": trace_id,
                }
                result = runtime.command_service.run_schedule(payload, put_progress, stop_event)
                loop.call_soon_threadsafe(queue.put_nowait, {"type": "done", "data": result})
            except InterruptedError:
                loop.call_soon_threadsafe(queue.put_nowait, {"type": "cancelled"})
            except Exception as e:
                loop.call_soon_threadsafe(queue.put_nowait, {"type": "error", "message": str(e)})

        worker_thread = threading.Thread(target=run_task, daemon=True)
        worker_thread.start()

        try:
            while True:
                msg_item = await queue.get()
                if msg_item["type"] == "progress":
                    await _enqueue_send(send_queue, {
                        "type": "schedule_progress",
                        "client_change_id": client_change_id,
                        "trace_id": trace_id,
                        "request_source": request_source,
                        **msg_item["data"],
                    })
                elif msg_item["type"] == "done":
                    result_data = msg_item.get("data") or {}
                    result_data.setdefault("trace_id", trace_id)
                    await _enqueue_send(send_queue, {
                        "type": "schedule_complete",
                        "client_change_id": client_change_id,
                        "trace_id": trace_id,
                        "request_source": request_source,
                        **result_data,
                    })
                    break
                elif msg_item["type"] == "cancelled":
                    await _enqueue_send(send_queue, {
                        "type": "schedule_cancelled",
                        "client_change_id": client_change_id,
                        "trace_id": trace_id,
                        "request_source": request_source,
                    })
                    break
                elif msg_item["type"] == "error":
                    await _enqueue_send(send_queue, {
                        "type": "schedule_complete",
                        "client_change_id": client_change_id,
                        "trace_id": trace_id,
                        "request_source": request_source,
                        "status": "error",
                        "message": msg_item.get("message", "Unknown error."),
                    })
                    break
        except asyncio.CancelledError:
            stop_event.set()
        finally:
            stop_event.set()

    try:
        task = asyncio.ensure_future(_run())
        return task, stop_event
    except Exception as ex:
        runtime.logger.error(
            "DutyLive",
            "Failed to start schedule run.",
            trace_id=trace_id,
            client_change_id=client_change_id,
            exc=ex,
        )
        return None, stop_event


def _cancel_schedule_run(active_stops: dict[str, threading.Event], client_change_id: str, runtime, trace_id: str):
    stop_event = active_stops.get(client_change_id)
    if stop_event is not None:
        stop_event.set()
        runtime.logger.info(
            "DutyLive",
            "Schedule run cancel requested.",
            trace_id=trace_id,
            client_change_id=client_change_id,
        )


async def _handle_schedule_entry_save(
    send_queue: asyncio.Queue,
    runtime,
    message: dict,
    client_change_id: str,
    trace_id: str,
    request_source: str,
):
    payload = {
        "source_date": (message or {}).get("source_date"),
        "target_date": (message or {}).get("target_date"),
        "day": (message or {}).get("day"),
        "area_assignments": (message or {}).get("area_assignments"),
        "note": (message or {}).get("note"),
        "confirm_overwrite": bool((message or {}).get("confirm_overwrite", False)),
        "ledger_mode": str((message or {}).get("ledger_mode") or "record").strip().lower() or "record",
        "trace_id": trace_id,
        "request_source": request_source,
    }

    try:
        response = await asyncio.to_thread(
            runtime.command_service.save_schedule_entry,
            payload,
            trace_id,
            request_source,
        )
        await _enqueue_send(send_queue, {
            "type": "schedule_entry_saved",
            "client_change_id": client_change_id,
            "trace_id": trace_id,
            "request_source": request_source,
            **response,
        })
    except Exception as ex:
        runtime.logger.error(
            "DutyLive",
            "Failed to save schedule entry.",
            trace_id=trace_id,
            request_source=request_source,
            client_change_id=client_change_id,
            exc=ex,
        )
        await _enqueue_send(send_queue, {
            "type": "error",
            "client_change_id": client_change_id,
            "trace_id": trace_id,
            "request_source": request_source,
            "message": str(ex),
        })
