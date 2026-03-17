import json
import asyncio
import threading
from fastapi import APIRouter, Request
from fastapi.responses import StreamingResponse

try:
    from models.schemas import DutyRequest
except ImportError:
    from ..models.schemas import DutyRequest

router = APIRouter(prefix="/api/v1/duty", tags=["Duty"])


def _resolve_request_meta(request: Request, runtime, request_data: DutyRequest) -> tuple[str, str]:
    trace_id = (request.headers.get("X-Duty-Trace-Id") or "").strip() or (request_data.trace_id or "").strip() or runtime.new_trace_id()
    request_source = (
        (request.headers.get("X-Duty-Request-Source") or "").strip()
        or (request_data.request_source or "").strip()
        or "api"
    )
    return trace_id, request_source


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
