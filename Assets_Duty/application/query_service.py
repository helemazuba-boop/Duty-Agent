from __future__ import annotations

import time


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
            "current_runtime_mode": "single_pass_compat",
        }
