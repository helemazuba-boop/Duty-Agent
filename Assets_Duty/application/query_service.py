from __future__ import annotations

import time

from state_ops import Context, load_config, load_host_config, load_roster_entries, load_state

try:
    from readiness import evaluate_readiness, probe_model
except ImportError:  # pragma: no cover - import path fallback
    from ..readiness import evaluate_readiness, probe_model


class QueryService:
    def __init__(self, runtime):
        self._runtime = runtime

    def health(self) -> dict:
        return {
            "status": "ok",
            "version": self._runtime.version,
            "uptime_seconds": round(time.monotonic() - self._runtime.started_at, 3),
            "data_dir": str(self._runtime.data_dir),
        }

    def engine_info(self) -> dict:
        return {
            "engine": "Duty-Agent Unified Scheduling Engine",
            "version": self._runtime.version,
            "supported_model_profiles": ["auto", "cloud", "campus_small", "edge", "custom"],
            "supported_orchestration_modes": ["auto", "single_pass", "multi_agent", "offline"],
            "supported_multi_agent_execution_modes": ["auto", "parallel", "serial"],
            "supported_single_pass_strategies": ["auto", "cloud_standard", "edge_tuned", "edge_generic", "incremental_thinking"],
            "supported_plan_profiles": ["standard", "agents", "incremental_small", "offline"],
            "current_runtime_mode": "dynamic_dispatch",
        }

    def get_config(self, trace_id: str | None = None, request_source: str = "api") -> dict:
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        self._runtime.logger.info(
            "QueryService",
            "Starting get_config.",
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        config = load_config(context)
        self._runtime.logger.info(
            "QueryService",
            "Finished get_config.",
            trace_id=effective_trace_id,
            request_source=request_source,
            model=config.get("model", ""),
            model_profile=config.get("model_profile", ""),
            orchestration_mode=config.get("orchestration_mode", ""),
        )
        return config

    def get_notification_settings(self, trace_id: str | None = None, request_source: str = "api") -> dict:
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        self._runtime.logger.info(
            "QueryService",
            "Starting get_notification_settings.",
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        host_config = load_host_config(context)
        settings = {
            "version": int(host_config.get("version", 1) or 1),
            "notification_entry": host_config.get("notification_entry", "system"),
            "system_notifications_enabled": bool(host_config.get("system_notifications_enabled", True)),
            "schedule_completion_notification_enabled": bool(
                host_config.get("schedule_completion_notification_enabled", True)
            ),
            "auto_run_trigger_notification_enabled": bool(
                host_config.get("auto_run_trigger_notification_enabled", True)
            ),
            "duty_reminder_enabled": bool(host_config.get("duty_reminder_enabled", False)),
            "duty_reminder_times": list(host_config.get("duty_reminder_times", []) or []),
            "notification_duration_seconds": int(host_config.get("notification_duration_seconds", 8) or 8),
            "auto_run_mode": str(host_config.get("auto_run_mode", "Off") or "Off"),
            "auto_run_parameter": str(host_config.get("auto_run_parameter", "") or ""),
            "auto_run_time": str(host_config.get("auto_run_time", "08:00") or "08:00"),
            "auto_run_retry_times": int(host_config.get("auto_run_retry_times", 3) or 0),
            "client_auto_start": bool(host_config.get("client_auto_start", True)),
            "client_close_action": str(host_config.get("client_close_action", "ask") or "ask"),
        }
        self._runtime.logger.info(
            "QueryService",
            "Finished get_notification_settings.",
            trace_id=effective_trace_id,
            request_source=request_source,
            notification_entry=settings["notification_entry"],
            duty_reminder_enabled=str(settings["duty_reminder_enabled"]).lower(),
        )
        return settings

    def get_roster(self, trace_id: str | None = None, request_source: str = "api") -> list[dict]:
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        self._runtime.logger.info(
            "QueryService",
            "Starting get_roster.",
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        try:
            roster = load_roster_entries(context.paths["roster"])
        except (FileNotFoundError, ValueError):
            roster = []

        self._runtime.logger.info(
            "QueryService",
            "Finished get_roster.",
            trace_id=effective_trace_id,
            request_source=request_source,
            roster_count=len(roster),
        )
        return roster

    def get_readiness(self, probe: bool = False, trace_id: str | None = None, request_source: str = "api") -> dict:
        """First-run readiness report. ``probe`` performs a real model call and is
        opt-in because it is slow / network-bound."""
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        config = load_config(context)
        try:
            roster = load_roster_entries(context.paths["roster"])
        except (FileNotFoundError, ValueError):
            roster = []
        state = load_state(context.paths["state"])
        report = evaluate_readiness(config, roster, state, probe=probe)
        self._runtime.logger.info(
            "QueryService",
            "Computed readiness.",
            trace_id=effective_trace_id,
            request_source=request_source,
            ready=str(report["ready"]).lower(),
            probe=str(bool(probe)).lower(),
        )
        return report

    def probe_model_connectivity(self, base_url: str, model: str, api_key: str) -> dict:
        """Probe an arbitrary (candidate) model config, for the wizard / plan
        card 'test connection' button."""
        return probe_model(base_url, model, api_key)

    def get_snapshot(self, trace_id: str | None = None, request_source: str = "api") -> dict:
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        self._runtime.logger.info(
            "QueryService",
            "Starting get_snapshot.",
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        roster = []
        try:
            roster = load_roster_entries(context.paths["roster"])
        except (FileNotFoundError, ValueError):
            roster = []

        snapshot = {
            "config": load_config(context),
            "roster": roster,
            "state": load_state(context.paths["state"]),
        }
        self._runtime.logger.info(
            "QueryService",
            "Finished get_snapshot.",
            trace_id=effective_trace_id,
            request_source=request_source,
            roster_count=len(snapshot["roster"]),
            schedule_count=len(snapshot["state"].get("schedule_pool", [])),
        )
        return snapshot
