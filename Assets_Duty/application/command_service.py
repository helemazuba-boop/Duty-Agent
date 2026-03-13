from __future__ import annotations

from typing import Any, Callable, Dict, Optional

from engine import run_schedule
from state_ops import Context, patch_config


class CommandService:
    def __init__(self, runtime):
        self._runtime = runtime

    def run_schedule(self, payload: Dict[str, Any], progress_callback: Optional[Callable] = None, stop_event=None) -> Dict[str, Any]:
        request_payload = dict(payload or {})
        request_payload.setdefault("trace_id", self._runtime.new_trace_id())
        request_payload.setdefault("request_source", "api")

        context = Context(self._runtime.data_dir)
        result = run_schedule(context, request_payload, progress_callback, stop_event)
        result.setdefault("trace_id", request_payload["trace_id"])
        return result

    def update_config(self, patch_payload: Dict[str, Any]) -> Dict[str, Any]:
        context = Context(self._runtime.data_dir)
        return patch_config(context, patch_payload)
