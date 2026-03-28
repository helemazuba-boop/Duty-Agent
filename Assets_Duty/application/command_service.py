from __future__ import annotations

from typing import Any, Callable, Dict, Optional

from engine import run_schedule
from state_ops import Context, has_previous_state, load_config, load_roster_entries, load_state, patch_config, rollback_state, save_roster_entries, save_schedule_entry_edit


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

    def save_schedule_entry(
        self,
        schedule_payload: Dict[str, Any],
        trace_id: str | None = None,
        request_source: str = "api",
    ) -> Dict[str, Any]:
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        self._runtime.logger.info(
            "CommandService",
            "Starting save_schedule_entry.",
            trace_id=effective_trace_id,
            request_source=request_source,
            ledger_mode=str((schedule_payload or {}).get("ledger_mode", "") or ""),
            target_date=str((schedule_payload or {}).get("target_date", "") or ""),
            source_date=str((schedule_payload or {}).get("source_date", "") or ""),
            confirm_overwrite=bool((schedule_payload or {}).get("confirm_overwrite", False)),
        )
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        result = save_schedule_entry_edit(context, schedule_payload)
        try:
            roster = load_roster_entries(context.paths["roster"])
        except (FileNotFoundError, ValueError):
            roster = []
        response = {
            "status": result.get("status", "error"),
            "message": result.get("message", ""),
            "ledger_mode": result.get("ledger_mode", "record"),
            "ledger_applied": bool(result.get("ledger_applied", False)),
            "snapshot": {
                "config": load_config(context),
                "roster": roster,
                "state": result.get("state") or load_state(context.paths["state"]),
            },
            "overwrite_target_date": result.get("overwrite_target_date"),
            "existing_entry": result.get("existing_entry"),
            "proposed_entry": result.get("proposed_entry"),
        }
        self._runtime.logger.info(
            "CommandService",
            "Finished save_schedule_entry.",
            trace_id=effective_trace_id,
            request_source=request_source,
            status=response["status"],
            ledger_mode=response["ledger_mode"],
            ledger_applied=response["ledger_applied"],
            schedule_count=len(response["snapshot"]["state"].get("schedule_pool", [])),
        )
        return response

    def rollback_schedule(
        self,
        trace_id: str | None = None,
        request_source: str = "api",
    ) -> Dict[str, Any]:
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        if not has_previous_state(context.paths["state"]):
            return {
                "status": "error",
                "message": "No previous state available to rollback.",
            }
        self._runtime.logger.info(
            "CommandService",
            "Starting rollback_schedule.",
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        rolled_back = rollback_state(context.paths["state"])
        self._runtime.logger.info(
            "CommandService",
            "Finished rollback_schedule.",
            trace_id=effective_trace_id,
            request_source=request_source,
            schedule_count=len(rolled_back.get("schedule_pool", [])),
        )
        return {
            "status": "success",
            "state": rolled_back,
        }
