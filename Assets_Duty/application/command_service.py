from __future__ import annotations

from datetime import date, datetime, timedelta
from typing import Any, Callable, Dict, Optional

from absence_ops import merge_absence_entries
from engine import run_schedule
from execution_profiles import build_execution_plan, resolve_execution_profile
from single_pass_executor import apply_single_pass_completion, build_single_pass_request
from state_ops import Context, has_previous_state, load_config, load_roster, load_roster_entries, load_state, patch_config, patch_host_config, remap_state_ids, rollback_state, save_roster_entries, save_schedule_entry_edit, save_state, update_state, _is_roster_order_changed


def _messages_to_prompt_text(messages: list[dict]) -> str:
    parts: list[str] = []
    for message in messages or []:
        role = str((message or {}).get("role", "") or "").strip() or "user"
        content = str((message or {}).get("content", "") or "")
        parts.append(f"[{role}]\n{content}")
    return "\n\n".join(parts)


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
                "code": "busy",
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
            self._runtime.publish_schedule_result_notification(result)
            return result
        finally:
            self._runtime.schedule_run_lock.release()

    def build_schedule_prompt(
        self,
        payload: Dict[str, Any],
        trace_id: str | None = None,
        request_source: str = "cli",
    ) -> Dict[str, Any]:
        """Build a single_pass schedule prompt for external-AI delegation.

        Read-only: resolves the execution profile/plan and produces the
        anonymized prompt plus an opaque, JSON-serializable ``resume_context``
        handle. Rejects any non-single_pass mode (the caller must switch
        orchestration_mode). Does not hold the schedule lock.
        """
        request_payload = dict(payload or {})
        effective_trace_id = trace_id or request_payload.get("trace_id") or self._runtime.new_trace_id()
        request_payload.setdefault("trace_id", effective_trace_id)
        request_payload.setdefault("request_source", request_source)

        self._runtime.logger.info(
            "CommandService",
            "Starting build_schedule_prompt.",
            trace_id=effective_trace_id,
            request_source=request_source,
            instruction_length=len(str(request_payload.get("instruction", "") or "")),
        )

        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        config = load_config(context)
        profile = resolve_execution_profile(request_payload, config)
        plan = build_execution_plan(profile)

        if plan.runtime_mode != "single_pass":
            self._runtime.logger.warn(
                "CommandService",
                "Rejected build_schedule_prompt for non-single_pass mode.",
                trace_id=effective_trace_id,
                request_source=request_source,
                runtime_mode=plan.runtime_mode,
            )
            return {
                "status": "error",
                "message": (
                    "External-AI delegation currently supports only single_pass mode; "
                    f"resolved mode is '{plan.runtime_mode}'. Switch orchestration_mode to "
                    "single_pass (or model_profile that resolves to it) before requesting a prompt."
                ),
                "mode": plan.runtime_mode,
                "trace_id": effective_trace_id,
            }

        try:
            messages, resume_context = build_single_pass_request(context, request_payload, plan)
        except (FileNotFoundError, ValueError) as ex:
            # User-data conditions (missing roster.csv, malformed instruction
            # input) must surface as a regular error result, not as a 500: the
            # CLI and the UI both render status:error payloads verbatim.
            self._runtime.logger.warn(
                "CommandService",
                "Rejected build_schedule_prompt on user data.",
                trace_id=effective_trace_id,
                request_source=request_source,
                error_message=str(ex),
            )
            return {
                "status": "error",
                "message": str(ex),
                "trace_id": effective_trace_id,
            }
        self._runtime.logger.info(
            "CommandService",
            "Finished build_schedule_prompt.",
            trace_id=effective_trace_id,
            request_source=request_source,
            runtime_mode=plan.runtime_mode,
            message_count=len(messages),
        )
        return {
            "status": "success",
            "mode": plan.runtime_mode,
            "messages": messages,
            "prompt_text": _messages_to_prompt_text(messages),
            "resume_context": resume_context,
            "prompt_metadata": resume_context.get("prompt_metadata", {}),
            "trace_id": effective_trace_id,
        }

    def apply_schedule_completion(
        self,
        payload: Dict[str, Any],
        trace_id: str | None = None,
        request_source: str = "cli",
        stop_event=None,
    ) -> Dict[str, Any]:
        """Ingest an external-AI completion for a previously built prompt.

        Holds the schedule lock while parsing + settling the completion, then
        publishes the standard schedule notification and returns the result plus
        a fresh snapshot.
        """
        request_payload = dict(payload or {})
        completion_text = str(request_payload.get("completion", "") or "")
        resume_context = dict(request_payload.get("resume_context") or {})
        effective_trace_id = (
            trace_id
            or request_payload.get("trace_id")
            or resume_context.get("trace_id")
            or self._runtime.new_trace_id()
        )

        self._runtime.logger.info(
            "CommandService",
            "Starting apply_schedule_completion.",
            trace_id=effective_trace_id,
            request_source=request_source,
            completion_length=len(completion_text),
        )

        if not self._runtime.schedule_run_lock.acquire(blocking=False):
            self._runtime.logger.warn(
                "CommandService",
                "Rejected concurrent apply_schedule_completion request.",
                trace_id=effective_trace_id,
                request_source=request_source,
            )
            return {
                "status": "error",
                "code": "busy",
                "message": "Another schedule run is already in progress.",
                "trace_id": effective_trace_id,
            }

        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        try:
            try:
                result = apply_single_pass_completion(context, completion_text, resume_context, stop_event=stop_event)
            except (FileNotFoundError, ValueError) as ex:
                # Missing roster.csv or an unparsable/unsettled completion are
                # caller-fixable conditions; report them as a regular error
                # result instead of an HTTP 500. The lock release stays in the
                # surrounding finally.
                self._runtime.logger.warn(
                    "CommandService",
                    "Rejected apply_schedule_completion on user data.",
                    trace_id=effective_trace_id,
                    request_source=request_source,
                    error_message=str(ex),
                )
                return {
                    "status": "error",
                    "message": str(ex),
                    "trace_id": effective_trace_id,
                }
            result.setdefault("trace_id", effective_trace_id)
            self._runtime.logger.info(
                "CommandService",
                "Finished apply_schedule_completion.",
                trace_id=effective_trace_id,
                request_source=request_source,
                status=result.get("status", ""),
                selected_executor=result.get("selected_executor", ""),
            )
            self._runtime.publish_schedule_result_notification(result)
            result["snapshot"] = self._runtime.query_service.get_snapshot(
                trace_id=effective_trace_id,
                request_source=request_source,
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
        self._runtime.publish_snapshot_changed("config-updated", effective_trace_id)
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

    def update_notification_settings(
        self,
        patch_payload: Dict[str, Any],
        trace_id: str | None = None,
        request_source: str = "api",
    ) -> Dict[str, Any]:
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        self._runtime.logger.info(
            "CommandService",
            "Starting update_notification_settings.",
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
        result = patch_host_config(context, patch_payload)
        self._runtime.reload_host_config()
        self._runtime.check_due_duty_reminders()
        self._runtime.logger.info(
            "CommandService",
            "Finished update_notification_settings.",
            trace_id=effective_trace_id,
            request_source=request_source,
            notification_entry=result.get("notification_entry", ""),
            duty_reminder_enabled=str(bool(result.get("duty_reminder_enabled", False))).lower(),
        )
        return self._runtime.query_service.get_notification_settings(
            trace_id=effective_trace_id,
            request_source=request_source,
        )

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

        try:
            old_roster = load_roster_entries(context.paths["roster"])
        except (FileNotFoundError, ValueError):
            old_roster = []

        result = save_roster_entries(context, roster_payload)

        if old_roster and _is_roster_order_changed(old_roster, result):
            old_state = load_state(context.paths["state"])
            new_state = remap_state_ids(old_state, old_roster, result)
            if new_state != old_state:
                save_state(context, new_state)
            self._runtime.logger.info(
                "CommandService",
                "Roster order changed, state IDs remapped.",
                trace_id=effective_trace_id,
            )

        self._runtime.publish_snapshot_changed("roster-updated", effective_trace_id)
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
        if response["status"] == "success":
            self._runtime.publish_snapshot_changed("schedule-entry-saved", effective_trace_id)
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

    def manage_absences(
        self,
        payload: Dict[str, Any],
        trace_id: str | None = None,
        request_source: str = "api",
    ) -> Dict[str, Any]:
        """Register/clear leave-absence ranges in state (sudden-situation)."""
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        request = dict(payload or {})
        action = str(request.get("action", "add") or "add").strip().lower()

        if action == "clear":
            def _clear(current_state: dict) -> dict:
                person_raw = str(request.get("person", "") or "").strip()
                if not person_raw:
                    current_state["absences"] = []
                    return current_state
                person_id = self._resolve_person_id(context, person_raw)
                current_state["absences"] = [
                    entry for entry in (current_state.get("absences", []) or [])
                    if int(entry.get("id", -1)) != person_id
                ]
                return current_state

            updated = update_state(context.paths["state"], _clear)
            self._runtime.publish_snapshot_changed("absences-cleared", effective_trace_id)
            return {
                "status": "success",
                "message": "Absences cleared.",
                "absences": updated.get("absences", []),
            }

        person_raw = str(request.get("person", "") or "").strip()
        if not person_raw:
            raise ValueError("person (ID or name) is required.")
        person_id = self._resolve_person_id(context, person_raw)

        from_date_text = str(request.get("from_date", "") or "").strip()
        if from_date_text:
            try:
                from_day = datetime.strptime(from_date_text[:10], "%Y-%m-%d").date()
            except ValueError as ex:
                raise ValueError("from_date must be YYYY-MM-DD.") from ex
        else:
            from_day = date.today()
        to_date_text = str(request.get("to_date", "") or "").strip()
        if to_date_text:
            try:
                to_day = datetime.strptime(to_date_text[:10], "%Y-%m-%d").date()
            except ValueError as ex:
                raise ValueError("to_date must be YYYY-MM-DD.") from ex
        else:
            days = int(request.get("days", 1) or 1)
            if days <= 0:
                raise ValueError("days must be a positive integer.")
            to_day = from_day + timedelta(days=days - 1)
        if to_day < from_day:
            raise ValueError("to_date must not be before from_date.")

        addition = [{"id": person_id, "from": from_day.isoformat(), "to": to_day.isoformat()}]

        def _add(current_state: dict) -> dict:
            current_state["absences"] = merge_absence_entries(
                current_state.get("absences", []), addition
            )
            return current_state

        updated = update_state(context.paths["state"], _add)
        self._runtime.publish_snapshot_changed("absence-registered", effective_trace_id)
        self._runtime.logger.info(
            "CommandService",
            "Absence registered.",
            trace_id=effective_trace_id,
            request_source=request_source,
            person_id=person_id,
            absence_from=from_day.isoformat(),
            absence_to=to_day.isoformat(),
        )
        return {
            "status": "success",
            "message": f"Absence registered: ID {person_id} {from_day.isoformat()}~{to_day.isoformat()}.",
            "absences": updated.get("absences", []),
        }

    def manage_run_notes(
        self,
        payload: Dict[str, Any],
        trace_id: str | None = None,
        request_source: str = "api",
    ) -> Dict[str, Any]:
        """Add/clear user run notes (e.g. deep-clean reminders) in state."""
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        request = dict(payload or {})
        action = str(request.get("action", "add") or "add").strip().lower()

        if action == "clear":
            def _clear(current_state: dict) -> dict:
                current_state["user_notes"] = []
                return current_state

            updated = update_state(context.paths["state"], _clear)
            self._runtime.publish_snapshot_changed("run-notes-cleared", effective_trace_id)
            return {
                "status": "success",
                "message": "Run notes cleared.",
                "user_notes": updated.get("user_notes", []),
            }

        text = str(request.get("text", "") or "").strip()
        if not text:
            raise ValueError("text is required.")
        until_raw = str(request.get("until", "") or "").strip()
        until_iso: str | None = None
        if until_raw:
            try:
                until_iso = datetime.strptime(until_raw[:10], "%Y-%m-%d").date().isoformat()
            except ValueError as ex:
                raise ValueError("until must be YYYY-MM-DD.") from ex

        def _add(current_state: dict) -> dict:
            notes = list(current_state.get("user_notes", []) or [])
            notes.append(
                {"text": text, "until": until_iso, "created_at": datetime.now().isoformat(timespec="seconds")}
            )
            current_state["user_notes"] = notes
            return current_state

        updated = update_state(context.paths["state"], _add)
        self._runtime.publish_snapshot_changed("run-note-added", effective_trace_id)
        return {
            "status": "success",
            "message": "Run note added.",
            "user_notes": updated.get("user_notes", []),
        }

    def manage_day_overrides(
        self,
        payload: Dict[str, Any],
        trace_id: str | None = None,
        request_source: str = "api",
    ) -> Dict[str, Any]:
        """Set/clear per-day per-area headcount overrides (e.g. deep-clean day)."""
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        request = dict(payload or {})
        action = str(request.get("action", "set") or "set").strip().lower()
        date_raw = str(request.get("date", "") or "").strip()
        if not date_raw:
            raise ValueError("date is required.")
        try:
            day_iso = datetime.strptime(date_raw[:10], "%Y-%m-%d").date().isoformat()
        except ValueError as ex:
            raise ValueError("date must be YYYY-MM-DD.") from ex

        if action == "clear":
            def _clear(current_state: dict) -> dict:
                overrides = dict(current_state.get("day_overrides", {}) or {})
                area_raw = str(request.get("area", "") or "").strip()
                if area_raw and day_iso in overrides:
                    overrides[day_iso] = {
                        area: count for area, count in overrides[day_iso].items() if area != area_raw
                    }
                    if not overrides[day_iso]:
                        overrides.pop(day_iso)
                else:
                    overrides.pop(day_iso, None)
                current_state["day_overrides"] = overrides
                return current_state

            updated = update_state(context.paths["state"], _clear)
            self._runtime.publish_snapshot_changed("day-override-cleared", effective_trace_id)
            return {
                "status": "success",
                "message": f"Day override cleared for {day_iso}.",
                "day_overrides": updated.get("day_overrides", {}),
            }

        area = str(request.get("area", "") or "").strip()
        if not area:
            raise ValueError("area is required.")
        count = int(request.get("count", 0) or 0)
        if count <= 0:
            raise ValueError("count must be a positive integer.")

        def _set(current_state: dict) -> dict:
            overrides = dict(current_state.get("day_overrides", {}) or {})
            day_map = dict(overrides.get(day_iso, {}) or {})
            day_map[area] = count
            overrides[day_iso] = day_map
            current_state["day_overrides"] = overrides
            return current_state

        updated = update_state(context.paths["state"], _set)
        self._runtime.publish_snapshot_changed("day-override-set", effective_trace_id)
        return {
            "status": "success",
            "message": f"Day override set: {day_iso} {area}={count}.",
            "day_overrides": updated.get("day_overrides", {}),
        }

    def _resolve_person_id(self, context: Context, person_raw: str) -> int:
        try:
            return int(person_raw)
        except ValueError:
            pass
        name_to_id, _id_to_name, _all_ids, _id_to_active = load_roster(context.paths["roster"])
        if person_raw in name_to_id:
            return int(name_to_id[person_raw])
        raise ValueError(f"Unknown person: {person_raw}")

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
        self._runtime.publish_snapshot_changed("rollback", effective_trace_id)
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
