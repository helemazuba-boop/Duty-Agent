import json
from fastapi import APIRouter, Request, BackgroundTasks
from fastapi.responses import StreamingResponse
from typing import Callable, Optional
import asyncio

try:
    from models.schemas import DutyRequest
except ImportError:
    from ..models.schemas import DutyRequest

# Note: We need to import the core logic from the parent directory
import sys
import os
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# Import run_duty_agent and Context from core
# Since we are refactoring, we'll assume they will be available in core.py
from core import run_duty_agent, Context

router = APIRouter(prefix="/api/v1/duty", tags=["Duty"])

@router.post("/schedule")
async def schedule(request_data: DutyRequest, request: Request):
    data_dir = getattr(request.app.state, "data_dir", "data")
    ctx = Context(data_dir)
    
    async def event_generator():
        # SSE implementation
        def sse_callback(phase: str, message: str, stream_chunk: Optional[str] = None):
            payload = {
                "phase": phase,
                "message": message
            }
            if stream_chunk:
                payload["stream_chunk"] = stream_chunk
            
            # Since run_duty_agent is likely synchronous, this callback might be called from a thread
            # We'll use a queue or just write sync if StreamingResponse supports it
            # But the best way is to make run_duty_agent take a queue
            pass

        # For now, we simulate the sync-to-async bridge
        # In a real heavy-duty app, we'd use a thread pool
        loop = asyncio.get_event_loop()
        queue = asyncio.Queue()

        def put_progress(phase, message, stream_chunk=None):
            loop.call_soon_threadsafe(queue.put_nowait, {
                "type": "progress",
                "data": {"phase": phase, "message": message, "stream_chunk": stream_chunk}
            })

        def run_task():
            try:
                result = run_duty_agent(ctx, request_data.model_dump(), put_progress)
                loop.call_soon_threadsafe(queue.put_nowait, {"type": "done", "data": result})
            except Exception as e:
                loop.call_soon_threadsafe(queue.put_nowait, {"type": "error", "message": str(e)})

        # Run in separate thread to avoid blocking event loop
        import threading
        thread = threading.Thread(target=run_task, daemon=True)
        thread.start()

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

    return StreamingResponse(event_generator(), media_type="text/event-stream")
