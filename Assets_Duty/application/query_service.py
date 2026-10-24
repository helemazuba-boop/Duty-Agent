from __future__ import annotations

import time

from state_ops import Context, compute_current_duty_date, list_reset_archives, load_config, load_host_config, load_roster_entries, load_state

try:
    from readiness import evaluate_readiness, probe_model
    # 别名导入：本类也有一个 list_models 方法，裸名 `list_models(...)` 在方法体内
    # 解析的是模块全局，之前没导入就直接 NameError（/duty/model-list 一直 500）。
    from readiness import list_models as fetch_provider_models
except ImportError:  # pragma: no cover - import path fallback
    from ..readiness import evaluate_readiness, probe_model
    from ..readiness import list_models as fetch_provider_models


class QueryService:
    def __init__(self, runtime):
        self._runtime = runtime

    def health(self) -> dict:
        return {
            "status": "ok",
            "version": self._runtime.version,
            "uptime_seconds": round(time.monotonic() - self._runtime.started_at, 3),
            "data_dir": str(self._runtime.data_dir),
            # 任务2：让"鉴权被绕过"从 /health 即可观测（SKIP_AUTH_BYPASS 环境）。
            "auth_bypassed": bool(getattr(self._runtime, "skip_auth_bypass", False)),
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
            "auth_bypassed": bool(getattr(self._runtime, "skip_auth_bypass", False)),
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
            # 默认 False：自启是用户明确选择的结果，不是安装的副作用（商店合规）。
            "client_auto_start": bool(host_config.get("client_auto_start", False)),
            "client_close_action": str(host_config.get("client_close_action", "ask") or "ask"),
            "component_refresh_time": str(host_config.get("component_refresh_time", "08:00") or "08:00"),
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

    def list_models(self, base_url: str, api_key: str) -> dict:
        """Return the model IDs exposed by an OpenAI-compatible provider."""
        return fetch_provider_models(base_url, self._resolve_api_key(base_url, api_key))

    def _resolve_api_key(self, base_url: str, api_key: str) -> str:
        """请求里没带 key 时，回落到配置里给**同一个端点**配的 key。

        以前这里只用请求体里的 key：调用方（CLI / 向导）只要没把 key 一起发过来，
        就算配置里明明填了 key，列模型也会退化成匿名请求，云端端点直接 401。
        所以"有 key 就要带上"。

        只做同端点匹配（忽略大小写与结尾斜杠），绝不把云端 key 发往用户在表单里
        随手填的其它地址——那等于把密钥泄露给任意 URL。
        """
        resolved = str(api_key or "").strip()
        if resolved:
            return resolved

        target = str(base_url or "").strip().rstrip("/").lower()
        if not target:
            return ""

        try:
            config = load_config(Context(self._runtime.data_dir))
        except Exception:  # noqa: BLE001 - 读配置失败不该让列模型整个崩掉
            return ""

        candidates = [config] + list(config.get("plan_presets") or [])
        for item in candidates:
            if not isinstance(item, dict):
                continue
            stored = str(item.get("api_key") or "").strip()
            if not stored:
                continue
            if str(item.get("base_url") or "").strip().rstrip("/").lower() == target:
                return stored
        return ""

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

        host_config = load_host_config(context)
        snapshot = {
            "config": load_config(context),
            "roster": roster,
            "state": load_state(context.paths["state"]),
            "current_duty_date": compute_current_duty_date(host_config),
            "component_refresh_time": str(host_config.get("component_refresh_time", "08:00") or "08:00"),
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

    def get_reset_archives(self, trace_id: str | None = None, request_source: str = "api") -> dict:
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        archives = list_reset_archives(self._runtime.data_dir)
        self._runtime.logger.info(
            "QueryService",
            "Finished get_reset_archives.",
            trace_id=effective_trace_id,
            request_source=request_source,
            archive_count=len(archives),
        )
        return {
            "data_dir": str(self._runtime.data_dir),
            "archives": archives,
        }
