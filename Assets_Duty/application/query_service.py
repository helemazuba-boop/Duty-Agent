from __future__ import annotations

import time

from state_ops import Context, load_config, load_roster_entries, load_state


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
            "supported_orchestration_modes": ["auto", "single_pass", "multi_agent"],
            "supported_multi_agent_execution_modes": ["auto", "parallel", "serial"],
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
