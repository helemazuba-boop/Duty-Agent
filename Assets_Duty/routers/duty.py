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

@router.post("/schedule")
async def schedule(request_data: DutyRequest, request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        return StreamingResponse(
            iter(['event: complete\ndata: {"status":"error","message":"Runtime is not initialized."}\n\n']),
            media_type="text/event-stream",
        )
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
