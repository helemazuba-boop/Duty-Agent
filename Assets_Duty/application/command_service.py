from __future__ import annotations

from typing import Any, Callable, Dict, Optional

from engine import run_schedule
from state_ops import Context, patch_config, save_roster_entries


class CommandService:
    def __init__(self, runtime):
        self._runtime = runtime

    def run_schedule(self, payload: Dict[str, Any], progress_callback: Optional[Callable] = None, stop_event=None) -> Dict[str, Any]:
        request_payload = dict(payload or {})
        request_payload.setdefault("trace_id", self._runtime.new_trace_id())
        request_payload.setdefault("request_source", "api")

        self._runtime.logger.info(
            "CommandService",
            "Starting run_schedule.",
            trace_id=request_payload["trace_id"],
            request_source=request_payload["request_source"],
            apply_mode=request_payload.get("apply_mode", ""),
            instruction_length=len(str(request_payload.get("instruction", "") or "")),
        )

        if not self._runtime.schedule_run_lock.acquire(blocking=False):
            message = "Another schedule run is already in progress."
            self._runtime.logger.warn(
                "CommandService",
                "Rejected concurrent run_schedule request.",
                trace_id=request_payload["trace_id"],
                request_source=request_payload["request_source"],
            )
            return {
                "status": "error",
                "message": message,
                "trace_id": request_payload["trace_id"],
            }

        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=request_payload["trace_id"],
            request_source=request_payload["request_source"],
        )
        try:
            result = run_schedule(context, request_payload, progress_callback, stop_event)
            result.setdefault("trace_id", request_payload["trace_id"])
            if result.get("status") == "error":
                self._runtime.logger.error(
                    "CommandService",
                    "run_schedule returned error result.",
                    trace_id=request_payload["trace_id"],
                    request_source=request_payload["request_source"],
                    status=result.get("status", ""),
                    selected_executor=result.get("selected_executor", ""),
                    error_message=str(result.get("message", "") or ""),
                )
            else:
                self._runtime.logger.info(
                    "CommandService",
                    "Finished run_schedule.",
                    trace_id=request_payload["trace_id"],
                    request_source=request_payload["request_source"],
                    status=result.get("status", ""),
                    selected_executor=result.get("selected_executor", ""),
                )
            return result
        finally:
            self._runtime.schedule_run_lock.release()

    def update_config(
        self,
        patch_payload: Dict[str, Any],
        trace_id: str | None = None,
        request_source: str = "api",
    ) -> Dict[str, Any]:
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        self._runtime.logger.info(
            "CommandService",
            "Starting update_config.",
            trace_id=effective_trace_id,
            request_source=request_source,
            patch_keys=sorted(list((patch_payload or {}).keys())),
        )
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        result = patch_config(context, patch_payload)
        self._runtime.logger.info(
            "CommandService",
            "Finished update_config.",
            trace_id=effective_trace_id,
            request_source=request_source,
            model=result.get("model", ""),
            model_profile=result.get("model_profile", ""),
            orchestration_mode=result.get("orchestration_mode", ""),
        )
        return result

    def update_roster(
        self,
        roster_payload: list[dict],
        trace_id: str | None = None,
        request_source: str = "api",
    ) -> list[dict]:
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        self._runtime.logger.info(
            "CommandService",
            "Starting update_roster.",
            trace_id=effective_trace_id,
            request_source=request_source,
            roster_count=len(roster_payload or []),
        )
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        result = save_roster_entries(context, roster_payload)
        self._runtime.logger.info(
            "CommandService",
            "Finished update_roster.",
            trace_id=effective_trace_id,
            request_source=request_source,
            roster_count=len(result),
            active_count=sum(1 for item in result if item.get("active")),
        )
        return result
