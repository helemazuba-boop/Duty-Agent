from __future__ import annotations

import csv
import json
import os
import re
import time
from datetime import date, datetime
from pathlib import Path
from typing import Callable, Dict, List, Optional, Tuple

from diagnostics import truncate_for_log

DEFAULT_ASSIGNMENTS_PER_AREA = 2
DEFAULT_BASE_URL = "https://integrate.api.nvidia.com/v1"
DEFAULT_MODEL = "moonshotai/kimi-k2-thinking"
DEFAULT_MODEL_PROFILE = "auto"
DEFAULT_ORCHESTRATION_MODE = "auto"
DEFAULT_MULTI_AGENT_EXECUTION_MODE = "auto"
DEFAULT_SINGLE_PASS_STRATEGY = "auto"
INCREMENTAL_SINGLE_PASS_STRATEGY = "incremental_thinking"
DEFAULT_SELECTED_PLAN_ID = "standard"
DEFAULT_CONFIG_VERSION = 1
DEFAULT_HOST_CONFIG_VERSION = 1
DEFAULT_PYTHON_PATH = r".\Assets_Duty\python-embed\python.exe"
DEFAULT_AUTO_RUN_MODE = "Off"
DEFAULT_AUTO_RUN_PARAMETER = "Monday"
DEFAULT_AUTO_RUN_TIME = "08:00"
DEFAULT_ACCESS_TOKEN_MODE = "dynamic"
DEFAULT_COMPONENT_REFRESH_TIME = "08:00"
DEFAULT_NOTIFICATION_DURATION_SECONDS = 8
DEFAULT_DUTY_REMINDER_TIME = "07:40"
STATE_LOCK_TIMEOUT_SECONDS = 20
STATE_LOCK_RETRY_INTERVAL_SECONDS = 0.2
STATE_LOCK_STALE_SECONDS = 120
CONFIG_LOCK_TIMEOUT_SECONDS = 30


class Context:
    def __init__(self, data_dir: Path, logger=None, trace_id: str = "", request_source: str = ""):
        self.data_dir = data_dir
        self.paths = {
            "config": data_dir / "config.json",
            "host_config": data_dir / "host-config.json",
            "roster": data_dir / "roster.csv",
            "state": data_dir / "state.json",
            "input": data_dir / "ipc_input.json",
            "result": data_dir / "ipc_result.json",
        }
        self.config: dict = {}
        self.logger = logger
        self.trace_id = trace_id
        self.request_source = request_source


class ConfigVersionConflictError(ValueError):
    pass


def _log(ctx: Context, level: str, scope: str, message: str, **data) -> None:
    logger = getattr(ctx, "logger", None)
    if logger is None:
        return
    if level == "INFO":
        logger.info(scope, message, trace_id=ctx.trace_id, request_source=ctx.request_source, **data)
    elif level == "WARN":
        logger.warn(scope, message, trace_id=ctx.trace_id, request_source=ctx.request_source, **data)
    else:
        logger.error(scope, message, trace_id=ctx.trace_id, request_source=ctx.request_source, **data)


def normalize_model_profile(value) -> str:
    normalized = str(value or "").strip().lower()
    return {
        "cloud_general": "cloud",
        "campus": "campus_small",
        "school_small": "campus_small",
        "edge_tuned": "edge",
        "edge_finetuned": "edge",
    }.get(normalized, normalized if normalized in {"auto", "cloud", "campus_small", "edge", "custom"} else DEFAULT_MODEL_PROFILE)


def normalize_orchestration_mode(value) -> str:
    normalized = str(value or "").strip().lower()
    return {
        "single": "single_pass",
        "unified": "single_pass",
        "multi-agent": "multi_agent",
        "staged": "multi_agent",
    }.get(normalized, normalized if normalized in {"auto", "single_pass", "multi_agent"} else DEFAULT_ORCHESTRATION_MODE)


def normalize_multi_agent_execution_mode(value) -> str:
    normalized = str(value or "").strip().lower()
    return {
        "parallel": "parallel",
        "concurrent": "parallel",
        "serial": "serial",
        "sequential": "serial",
    }.get(normalized, normalized if normalized in {"auto", "parallel", "serial"} else DEFAULT_MULTI_AGENT_EXECUTION_MODE)


def normalize_single_pass_strategy(value, fallback: str = DEFAULT_SINGLE_PASS_STRATEGY) -> str:
    normalized = str(value or "").strip().lower()
    return {
        "edge": "auto",
        "cloud": "cloud_standard",
        "standard": "cloud_standard",
        "incremental": INCREMENTAL_SINGLE_PASS_STRATEGY,
        "incremental_small": INCREMENTAL_SINGLE_PASS_STRATEGY,
    }.get(
        normalized,
        normalized
        if normalized in {"auto", "cloud_standard", "edge_tuned", "edge_generic", INCREMENTAL_SINGLE_PASS_STRATEGY}
        else fallback,
    )


def normalize_plan_mode_id(value) -> str:
    normalized = str(value or "").strip().lower()
    return {
        "standard": "standard",
        "default": "standard",
        "campus_6agent": "campus_6agent",
        "campus6agent": "campus_6agent",
        "6agent": "campus_6agent",
        "multi_agent": "campus_6agent",
        "incremental_small": "incremental_small",
        "incremental": "incremental_small",
        "small_incremental": "incremental_small",
    }.get(normalized, DEFAULT_SELECTED_PLAN_ID)


def _normalize_plan_id(value: object, fallback: str) -> str:
    raw = str(value or "").strip().lower()
    parts = []
    previous_dash = False
    for char in raw:
        if char.isalnum():
            parts.append(char)
            previous_dash = False
        elif parts and not previous_dash:
            parts.append("-")
            previous_dash = True
    normalized = "".join(parts).strip("-")
    return normalized or fallback


def _ensure_unique_plan_id(base_id: str, used_ids: set[str]) -> str:
    candidate = base_id
    suffix = 2
    while candidate in used_ids:
        candidate = f"{base_id}-{suffix}"
        suffix += 1
    used_ids.add(candidate)
    return candidate


def _normalize_plan_name(name: object, mode_id: str, index: int, model: str = "") -> str:
    normalized = str(name or "").strip()
    if normalized:
        return normalized
    if index <= 3:
        return {
            "standard": "标准",
            "campus_6agent": "6Agent",
            "incremental_small": "增量小模型",
        }.get(mode_id, "标准")
    return model or f"方案预设 {index}"


def _create_default_plan_presets() -> List[dict]:
    return [
        {
            "id": "standard",
            "name": "标准",
            "mode_id": "standard",
            "api_key": "",
            "base_url": DEFAULT_BASE_URL,
            "model": DEFAULT_MODEL,
            "model_profile": DEFAULT_MODEL_PROFILE,
            "provider_hint": "",
            "multi_agent_execution_mode": "auto",
        },
        {
            "id": "campus-6agent",
            "name": "6Agent",
            "mode_id": "campus_6agent",
            "api_key": "",
            "base_url": DEFAULT_BASE_URL,
            "model": DEFAULT_MODEL,
            "model_profile": DEFAULT_MODEL_PROFILE,
            "provider_hint": "",
            "multi_agent_execution_mode": "auto",
        },
        {
            "id": "incremental-small",
            "name": "增量小模型",
            "mode_id": "incremental_small",
            "api_key": "",
            "base_url": DEFAULT_BASE_URL,
            "model": DEFAULT_MODEL,
            "model_profile": DEFAULT_MODEL_PROFILE,
            "provider_hint": "",
            "multi_agent_execution_mode": "auto",
        },
    ]


def _normalize_plan_presets(raw_plan_presets) -> List[dict]:
    candidates = raw_plan_presets if isinstance(raw_plan_presets, list) else []
    if not candidates:
        candidates = _create_default_plan_presets()

    normalized: List[dict] = []
    used_ids: set[str] = set()
    for index, candidate in enumerate(candidates, start=1):
        if not isinstance(candidate, dict):
            continue
        mode_id = normalize_plan_mode_id(candidate.get("mode_id"))
        model = str(candidate.get("model", "") or DEFAULT_MODEL).strip() or DEFAULT_MODEL
        plan_id = _ensure_unique_plan_id(
            _normalize_plan_id(candidate.get("id"), mode_id if index <= 3 else f"plan-{index}"),
            used_ids,
        )
        normalized.append(
            {
                "id": plan_id,
                "name": _normalize_plan_name(candidate.get("name"), mode_id, index, model),
                "mode_id": mode_id,
                "api_key": str(candidate.get("api_key", "") or "").strip(),
                "base_url": str(candidate.get("base_url", "") or DEFAULT_BASE_URL).strip() or DEFAULT_BASE_URL,
                "model": model,
                "model_profile": normalize_model_profile(candidate.get("model_profile", DEFAULT_MODEL_PROFILE)),
                "provider_hint": str(candidate.get("provider_hint", "") or "").strip(),
                "multi_agent_execution_mode": normalize_multi_agent_execution_mode(
                    candidate.get("multi_agent_execution_mode", DEFAULT_MULTI_AGENT_EXECUTION_MODE)
                )
                if mode_id == "campus_6agent"
                else "auto",
            }
        )

    return normalized or _create_default_plan_presets()


def _get_plan_by_id(plan_presets: List[dict], selected_plan_id: str) -> dict:
    normalized_id = str(selected_plan_id or "").strip()
    for plan in plan_presets:
        if str(plan.get("id", "")).strip() == normalized_id:
            return dict(plan)
    return dict(plan_presets[0]) if plan_presets else _create_default_plan_presets()[0]


def normalize_selected_plan_id(value, plan_presets: List[dict], source: Optional[dict] = None) -> str:
    raw_value = str(value or "").strip()
    if raw_value:
        candidate = _normalize_plan_id(raw_value, "")
        if candidate:
            for plan in plan_presets:
                if str(plan.get("id", "")).strip() == candidate:
                    return candidate

        mode_id = normalize_plan_mode_id(raw_value)
        for plan in plan_presets:
            if plan.get("mode_id") == mode_id:
                return str(plan.get("id", "")).strip()

    return str(plan_presets[0].get("id", DEFAULT_SELECTED_PLAN_ID)).strip() if plan_presets else DEFAULT_SELECTED_PLAN_ID


def _create_default_persisted_config() -> dict:
    return {
        "version": DEFAULT_CONFIG_VERSION,
        "selected_plan_id": DEFAULT_SELECTED_PLAN_ID,
        "plan_presets": _create_default_plan_presets(),
        "duty_rule": "",
    }


def _normalize_config_version(value: object) -> int:
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return DEFAULT_CONFIG_VERSION
    return max(DEFAULT_CONFIG_VERSION, parsed)


def _normalize_persisted_config(config: dict | None) -> dict:
    source = dict(config) if isinstance(config, dict) else {}
    plan_presets = _normalize_plan_presets(source.get("plan_presets"))
    selected_plan_id = normalize_selected_plan_id(source.get("selected_plan_id"), plan_presets, source)
    return {
        "version": _normalize_config_version(source.get("version", DEFAULT_CONFIG_VERSION)),
        "selected_plan_id": selected_plan_id,
        "plan_presets": plan_presets,
        "duty_rule": str(source.get("duty_rule", "") or "").strip(),
    }


def _hydrate_runtime_config(persisted: dict) -> dict:
    normalized = _normalize_persisted_config(persisted)
    plan_presets = normalized["plan_presets"]
    selected_plan_id = normalized["selected_plan_id"]
    selected_plan = _get_plan_by_id(plan_presets, selected_plan_id)
    selected_mode_id = normalize_plan_mode_id(selected_plan.get("mode_id"))

    return {
        "version": normalized["version"],
        "api_key": selected_plan["api_key"],
        "base_url": selected_plan["base_url"],
        "model": selected_plan["model"],
        "model_profile": selected_plan["model_profile"],
        "orchestration_mode": "multi_agent" if selected_mode_id == "campus_6agent" else "single_pass",
        "multi_agent_execution_mode": normalize_multi_agent_execution_mode(
            selected_plan.get("multi_agent_execution_mode", DEFAULT_MULTI_AGENT_EXECUTION_MODE)
        )
        if selected_mode_id == "campus_6agent"
        else "auto",
        "single_pass_strategy": INCREMENTAL_SINGLE_PASS_STRATEGY if selected_mode_id == "incremental_small" else "auto",
        "provider_hint": selected_plan["provider_hint"],
        "selected_plan_id": selected_plan_id,
        "plan_presets": plan_presets,
        "duty_rule": normalized["duty_rule"],
    }


def create_default_config() -> dict:
    return _hydrate_runtime_config(_create_default_persisted_config())


def normalize_config(config: dict | None) -> dict:
    return _hydrate_runtime_config(_normalize_persisted_config(config))


def save_json_atomic(path: Path, data: dict):
    tmp_path = path.with_suffix(path.suffix + ".tmp")
    with open(tmp_path, "w", encoding="utf-8") as file:
        json.dump(data, file, ensure_ascii=False, indent=2)
        file.flush()
        os.fsync(file.fileno())
    for attempt in range(3):
        try:
            os.replace(str(tmp_path), str(path))
            return
        except PermissionError:
            if attempt < 2:
                time.sleep(0.1 * (attempt + 1))
            else:
                raise


def _read_lock_metadata(lock_path: Path) -> tuple[Optional[int], Optional[datetime]]:
    try:
        lines = lock_path.read_text(encoding="utf-8").splitlines()
    except OSError:
        return None, None

    pid = None
    created_at = None
    if lines:
        try:
            pid = int(lines[0].strip())
        except (TypeError, ValueError):
            pid = None
    if len(lines) > 1:
        try:
            created_at = datetime.fromisoformat(lines[1].strip())
        except ValueError:
            created_at = None
    return pid, created_at


def _is_process_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return False
    return True


def _clear_stale_lock_if_needed(lock_path: Path, stale_after_seconds: int) -> bool:
    pid, created_at = _read_lock_metadata(lock_path)
    now = datetime.now()
    age_seconds: Optional[float] = None
    if created_at is not None:
        age_seconds = max(0.0, (now - created_at).total_seconds())
    else:
        try:
            age_seconds = max(0.0, now.timestamp() - lock_path.stat().st_mtime)
        except OSError:
            age_seconds = None

    pid_stale = pid is not None and not _is_process_alive(pid)
    age_stale = age_seconds is not None and age_seconds >= max(1, stale_after_seconds)
    if not pid_stale and not age_stale:
        return False

    try:
        lock_path.unlink()
        return True
    except FileNotFoundError:
        return True
    except OSError:
        return False


def acquire_file_lock(
    lock_path: Path,
    timeout_seconds: int,
    *,
    stop_event=None,
    stale_after_seconds: Optional[int] = None,
) -> None:
    deadline = time.monotonic() + max(1, int(timeout_seconds))
    stale_after = max(1, int(stale_after_seconds if stale_after_seconds is not None else max(timeout_seconds, 30)))
    while True:
        if stop_event and stop_event.is_set():
            raise InterruptedError(f"Cancelled while waiting for file lock: {lock_path}")
        try:
            fd = os.open(str(lock_path), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            with os.fdopen(fd, "w", encoding="utf-8") as lock_file:
                lock_file.write(f"{os.getpid()}\n")
                lock_file.write(f"{datetime.now().isoformat()}\n")
            return
        except FileExistsError:
            if _clear_stale_lock_if_needed(lock_path, stale_after):
                continue
            if time.monotonic() >= deadline:
                raise TimeoutError(f"Timed out waiting for file lock: {lock_path}")
            time.sleep(STATE_LOCK_RETRY_INTERVAL_SECONDS)


def release_file_lock(lock_path: Path) -> None:
    try:
        lock_path.unlink()
    except FileNotFoundError:
        pass


def acquire_state_file_lock(lock_path: Path, timeout_seconds: int = STATE_LOCK_TIMEOUT_SECONDS, stop_event=None) -> None:
    acquire_file_lock(
        lock_path,
        timeout_seconds,
        stop_event=stop_event,
        stale_after_seconds=STATE_LOCK_STALE_SECONDS,
    )


def release_state_file_lock(lock_path: Path) -> None:
    release_file_lock(lock_path)


def update_state(
    path: Path,
    updater: Callable[[dict], dict | None],
    *,
    timeout_seconds: int = STATE_LOCK_TIMEOUT_SECONDS,
    stop_event=None,
) -> dict:
    lock_path = path.with_suffix(path.suffix + ".lock")
    acquire_state_file_lock(lock_path, timeout_seconds=timeout_seconds, stop_event=stop_event)
    try:
        current = load_state(path)
        updated = updater(current)
        next_state = updated if isinstance(updated, dict) else current
        save_json_atomic(path, next_state)
        return next_state
    finally:
        release_state_file_lock(lock_path)


def _load_persisted_config_unlocked(config_path: Path) -> tuple[dict, bool]:
    if not config_path.exists():
        return _create_default_persisted_config(), True

    with open(config_path, "r", encoding="utf-8-sig") as file:
        raw = json.load(file)

    normalized = _normalize_persisted_config(raw)
    return normalized, raw != normalized


def _config_lock_path(ctx: Context) -> Path:
    return ctx.paths["config"].with_suffix(ctx.paths["config"].suffix + ".lock")


def load_config(ctx: Context) -> dict:
    config_path = ctx.paths["config"]
    _log(ctx, "INFO", "ConfigStore", "Loading backend config.", config_path=str(config_path))
    lock_path = _config_lock_path(ctx)
    acquire_file_lock(lock_path, CONFIG_LOCK_TIMEOUT_SECONDS)
    try:
        persisted, changed = _load_persisted_config_unlocked(config_path)
        if changed:
            save_json_atomic(config_path, persisted)
    finally:
        release_file_lock(lock_path)

    config = _hydrate_runtime_config(persisted)
    if changed:
        _log(
            ctx,
            "INFO",
            "ConfigStore",
            "Normalized backend config and rewrote config file.",
            config_path=str(config_path),
            model=config.get("model", ""),
            model_profile=config.get("model_profile", ""),
            orchestration_mode=config.get("orchestration_mode", ""),
            selected_plan_id=config.get("selected_plan_id", ""),
        )
    else:
        _log(
            ctx,
            "INFO",
            "ConfigStore",
            "Loaded backend config.",
            config_path=str(config_path),
            model=config.get("model", ""),
            model_profile=config.get("model_profile", ""),
            orchestration_mode=config.get("orchestration_mode", ""),
            selected_plan_id=config.get("selected_plan_id", ""),
        )
    return config


def save_config(ctx: Context, config: dict) -> dict:
    persisted = _normalize_persisted_config(config)
    lock_path = _config_lock_path(ctx)
    acquire_file_lock(lock_path, CONFIG_LOCK_TIMEOUT_SECONDS)
    try:
        save_json_atomic(ctx.paths["config"], persisted)
    finally:
        release_file_lock(lock_path)

    normalized = _hydrate_runtime_config(persisted)
    _log(
        ctx,
        "INFO",
        "ConfigStore",
        "Saved backend config.",
        config_path=str(ctx.paths["config"]),
        base_url=normalized.get("base_url", ""),
        model=normalized.get("model", ""),
        model_profile=normalized.get("model_profile", ""),
        orchestration_mode=normalized.get("orchestration_mode", ""),
        multi_agent_execution_mode=normalized.get("multi_agent_execution_mode", ""),
        selected_plan_id=normalized.get("selected_plan_id", ""),
    )
    return normalized


def _persisted_config_body(config: dict | None) -> dict:
    normalized = _normalize_persisted_config(config)
    return {
        "selected_plan_id": normalized["selected_plan_id"],
        "plan_presets": normalized["plan_presets"],
        "duty_rule": normalized["duty_rule"],
    }


def patch_config(ctx: Context, patch: dict) -> dict:
    patch_keys = sorted([str(key) for key, value in (patch or {}).items() if value is not None])
    unsupported_keys = sorted(
        str(key)
        for key, value in (patch or {}).items()
        if value is not None and key not in {"expected_version", "selected_plan_id", "plan_presets", "duty_rule"}
    )
    if unsupported_keys:
        raise ValueError(f"Unsupported config patch keys: {', '.join(unsupported_keys)}")

    _log(
        ctx,
        "INFO",
        "ConfigStore",
        "Patching backend config.",
        config_path=str(ctx.paths["config"]),
        patch_keys=patch_keys,
        expected_version=str((patch or {}).get("expected_version", "") or "") if "expected_version" in (patch or {}) else "<unchanged>",
        selected_plan_id=str((patch or {}).get("selected_plan_id", "") or "") if "selected_plan_id" in (patch or {}) else "<unchanged>",
        plan_preset_count=str(len((patch or {}).get("plan_presets") or [])) if "plan_presets" in (patch or {}) else "<unchanged>",
        duty_rule=truncate_for_log(str((patch or {}).get("duty_rule", "") or ""), 160) if "duty_rule" in (patch or {}) else "<unchanged>",
    )

    lock_path = _config_lock_path(ctx)
    acquire_file_lock(lock_path, CONFIG_LOCK_TIMEOUT_SECONDS)
    try:
        current, _ = _load_persisted_config_unlocked(ctx.paths["config"])
        current_version = _normalize_config_version(current.get("version", DEFAULT_CONFIG_VERSION))
        expected_version = None
        if "expected_version" in (patch or {}) and (patch or {}).get("expected_version") is not None:
            expected_version = _normalize_config_version((patch or {}).get("expected_version"))
            if expected_version != current_version:
                raise ConfigVersionConflictError(
                    f"Config version mismatch: expected {expected_version}, current {current_version}"
                )

        candidate = dict(current)
        for key, value in (patch or {}).items():
            if key == "expected_version" or value is None:
                continue
            candidate[key] = value

        persisted = _normalize_persisted_config(candidate)
        if _persisted_config_body(persisted) != _persisted_config_body(current):
            persisted["version"] = current_version + 1
        else:
            persisted["version"] = current_version
        save_json_atomic(ctx.paths["config"], persisted)
    finally:
        release_file_lock(lock_path)

    return _hydrate_runtime_config(persisted)


def _normalize_time_string(value: object, default: str) -> str:
    text = str(value or "").strip()
    if not text:
        return default
    for fmt in ("%H:%M", "%H:%M:%S"):
        try:
            parsed = datetime.strptime(text, fmt)
            return parsed.strftime("%H:%M")
        except ValueError:
            continue
    return default


def _parse_int_range(value: object, default: int, minimum: int, maximum: int) -> int:
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return default
    return max(minimum, min(maximum, parsed))


def _normalize_duty_reminder_times(raw_times: object) -> List[str]:
    normalized: List[str] = []
    seen: set[str] = set()
    candidates = raw_times if isinstance(raw_times, list) else [raw_times]
    for raw in candidates:
        text = str(raw or "").strip()
        if not text:
            continue
        for token in re.split(r"[,;\r\n]+", text):
            value = _normalize_time_string(token, "")
            if value and value not in seen:
                seen.add(value)
                normalized.append(value)
    return normalized or [DEFAULT_DUTY_REMINDER_TIME]


def _create_default_persisted_host_config() -> dict:
    return {
        "version": DEFAULT_HOST_CONFIG_VERSION,
        "python_path": DEFAULT_PYTHON_PATH,
        "auto_run_mode": DEFAULT_AUTO_RUN_MODE,
        "auto_run_parameter": DEFAULT_AUTO_RUN_PARAMETER,
        "access_token_mode": DEFAULT_ACCESS_TOKEN_MODE,
        "static_access_token_verifier": "",
        "enable_mcp": False,
        "enable_webview_debug_layer": False,
        "auto_run_time": DEFAULT_AUTO_RUN_TIME,
        "auto_run_trigger_notification_enabled": True,
        "auto_run_retry_times": 3,
        "ai_consecutive_failures": 0,
        "last_auto_run_date": "",
        "component_refresh_time": DEFAULT_COMPONENT_REFRESH_TIME,
        "notification_duration_seconds": DEFAULT_NOTIFICATION_DURATION_SECONDS,
        "duty_reminder_enabled": False,
        "duty_reminder_times": [DEFAULT_DUTY_REMINDER_TIME],
    }


def _normalize_host_config_version(value: object) -> int:
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return DEFAULT_HOST_CONFIG_VERSION
    return max(DEFAULT_HOST_CONFIG_VERSION, parsed)


def _normalize_persisted_host_config(config: dict | None) -> dict:
    source = dict(config) if isinstance(config, dict) else {}
    return {
        "version": _normalize_host_config_version(source.get("version", DEFAULT_HOST_CONFIG_VERSION)),
        "python_path": str(source.get("python_path", DEFAULT_PYTHON_PATH) or DEFAULT_PYTHON_PATH).strip() or DEFAULT_PYTHON_PATH,
        "auto_run_mode": {
            "weekly": "Weekly",
            "monthly": "Monthly",
            "custom": "Custom",
        }.get(str(source.get("auto_run_mode", DEFAULT_AUTO_RUN_MODE) or DEFAULT_AUTO_RUN_MODE).strip().lower(), "Off"),
        "auto_run_parameter": str(source.get("auto_run_parameter", DEFAULT_AUTO_RUN_PARAMETER) or DEFAULT_AUTO_RUN_PARAMETER).strip()
        or DEFAULT_AUTO_RUN_PARAMETER,
        "access_token_mode": "static"
        if str(source.get("access_token_mode", DEFAULT_ACCESS_TOKEN_MODE) or DEFAULT_ACCESS_TOKEN_MODE).strip().lower() == "static"
        else DEFAULT_ACCESS_TOKEN_MODE,
        "static_access_token_verifier": str(source.get("static_access_token_verifier", "") or "").strip(),
        "enable_mcp": parse_bool(source.get("enable_mcp"), False),
        "enable_webview_debug_layer": parse_bool(source.get("enable_webview_debug_layer"), False),
        "auto_run_time": _normalize_time_string(source.get("auto_run_time"), DEFAULT_AUTO_RUN_TIME),
        "auto_run_trigger_notification_enabled": parse_bool(source.get("auto_run_trigger_notification_enabled"), True),
        "auto_run_retry_times": _parse_int_range(source.get("auto_run_retry_times"), 3, 0, 20),
        "ai_consecutive_failures": _parse_int_range(source.get("ai_consecutive_failures"), 0, 0, 1000),
        "last_auto_run_date": str(source.get("last_auto_run_date", "") or "").strip(),
        "component_refresh_time": _normalize_time_string(source.get("component_refresh_time"), DEFAULT_COMPONENT_REFRESH_TIME),
        "notification_duration_seconds": _parse_int_range(
            source.get("notification_duration_seconds"),
            DEFAULT_NOTIFICATION_DURATION_SECONDS,
            3,
            15,
        ),
        "duty_reminder_enabled": parse_bool(source.get("duty_reminder_enabled"), False),
        "duty_reminder_times": _normalize_duty_reminder_times(source.get("duty_reminder_times", [DEFAULT_DUTY_REMINDER_TIME])),
    }


def _load_persisted_host_config_unlocked(config_path: Path) -> tuple[dict, bool]:
    if not config_path.exists():
        return _create_default_persisted_host_config(), True

    with open(config_path, "r", encoding="utf-8-sig") as file:
        raw = json.load(file)

    normalized = _normalize_persisted_host_config(raw)
    return normalized, raw != normalized


def _persisted_host_config_body(config: dict | None) -> dict:
    normalized = _normalize_persisted_host_config(config)
    return {
        "python_path": normalized["python_path"],
        "auto_run_mode": normalized["auto_run_mode"],
        "auto_run_parameter": normalized["auto_run_parameter"],
        "access_token_mode": normalized["access_token_mode"],
        "static_access_token_verifier": normalized["static_access_token_verifier"],
        "enable_mcp": normalized["enable_mcp"],
        "enable_webview_debug_layer": normalized["enable_webview_debug_layer"],
        "auto_run_time": normalized["auto_run_time"],
        "auto_run_trigger_notification_enabled": normalized["auto_run_trigger_notification_enabled"],
        "auto_run_retry_times": normalized["auto_run_retry_times"],
        "ai_consecutive_failures": normalized["ai_consecutive_failures"],
        "last_auto_run_date": normalized["last_auto_run_date"],
        "component_refresh_time": normalized["component_refresh_time"],
        "notification_duration_seconds": normalized["notification_duration_seconds"],
        "duty_reminder_enabled": normalized["duty_reminder_enabled"],
        "duty_reminder_times": normalized["duty_reminder_times"],
    }


def _host_config_lock_path(ctx: Context) -> Path:
    return ctx.paths["host_config"].with_suffix(ctx.paths["host_config"].suffix + ".lock")


def load_host_config(ctx: Context) -> dict:
    config_path = ctx.paths["host_config"]
    _log(ctx, "INFO", "HostConfigStore", "Loading host config.", config_path=str(config_path))
    lock_path = _host_config_lock_path(ctx)
    acquire_file_lock(lock_path, CONFIG_LOCK_TIMEOUT_SECONDS)
    try:
        persisted, changed = _load_persisted_host_config_unlocked(config_path)
        if changed:
            save_json_atomic(config_path, persisted)
    finally:
        release_file_lock(lock_path)

    _log(
        ctx,
        "INFO",
        "HostConfigStore",
        "Loaded host config.",
        config_path=str(config_path),
        auto_run_mode=persisted.get("auto_run_mode", ""),
        access_token_mode=persisted.get("access_token_mode", DEFAULT_ACCESS_TOKEN_MODE),
        enable_mcp=str(persisted.get("enable_mcp", False)).lower(),
        enable_webview_debug_layer=str(persisted.get("enable_webview_debug_layer", False)).lower(),
        static_access_token_configured=str(bool(persisted.get("static_access_token_verifier"))).lower(),
        version=str(persisted.get("version", DEFAULT_HOST_CONFIG_VERSION)),
    )
    return persisted


def save_host_config(ctx: Context, config: dict) -> dict:
    persisted = _normalize_persisted_host_config(config)
    lock_path = _host_config_lock_path(ctx)
    acquire_file_lock(lock_path, CONFIG_LOCK_TIMEOUT_SECONDS)
    try:
        save_json_atomic(ctx.paths["host_config"], persisted)
    finally:
        release_file_lock(lock_path)

    _log(
        ctx,
        "INFO",
        "HostConfigStore",
        "Saved host config.",
        config_path=str(ctx.paths["host_config"]),
        auto_run_mode=persisted.get("auto_run_mode", ""),
        access_token_mode=persisted.get("access_token_mode", DEFAULT_ACCESS_TOKEN_MODE),
        enable_mcp=str(persisted.get("enable_mcp", False)).lower(),
        enable_webview_debug_layer=str(persisted.get("enable_webview_debug_layer", False)).lower(),
        static_access_token_configured=str(bool(persisted.get("static_access_token_verifier"))).lower(),
        version=str(persisted.get("version", DEFAULT_HOST_CONFIG_VERSION)),
    )
    return persisted


def load_api_key_from_env() -> str:
    # Never read stdin here: MCP stdio transports also use stdin, and consuming it
    # would corrupt the protocol stream. Prefer dedicated MCP env first, then the
    # legacy shared env name for existing host integrations.
    return (
        os.environ.get("DUTY_AGENT_MCP_API_KEY", "").strip()
        or os.environ.get("DUTY_AGENT_API_KEY", "").strip()
    )


def load_roster(csv_path: Path) -> Tuple[Dict[str, int], Dict[int, str], List[int], Dict[int, int]]:
    if not csv_path.exists():
        raise FileNotFoundError(f"roster.csv not found: {csv_path}")

    name_to_id: Dict[str, int] = {}
    id_to_name: Dict[int, str] = {}
    all_ids: List[int] = []
    id_to_active: Dict[int, int] = {}

    with open(csv_path, "r", encoding="utf-8-sig", newline="") as file:
        reader = csv.DictReader(file)
        for row in reader:
            raw_id = str(row.get("id", "")).strip()
            raw_name = str(row.get("name", "")).strip()
            raw_active = str(row.get("active", "1")).strip()
            if not raw_id or not raw_name:
                continue
            try:
                person_id = int(raw_id)
            except (TypeError, ValueError):
                continue
            if person_id <= 0:
                continue
            try:
                active = int(raw_active) if raw_active else 1
            except (TypeError, ValueError):
                active = 1
            if raw_name in name_to_id:
                raise ValueError(f"Duplicate student name detected: {raw_name}")
            name_to_id[raw_name] = person_id
            id_to_name[person_id] = raw_name
            all_ids.append(person_id)
            id_to_active[person_id] = active

    all_ids = sorted(set(all_ids))
    if not all_ids:
        raise ValueError("No people in roster.csv.")
    return name_to_id, id_to_name, all_ids, id_to_active


def load_roster_entries(csv_path: Path) -> List[dict]:
    _, id_to_name, all_ids, id_to_active = load_roster(csv_path)
    return [
        {
            "id": person_id,
            "name": id_to_name.get(person_id, ""),
            "active": id_to_active.get(person_id, 1) != 0,
        }
        for person_id in all_ids
    ]


def _roster_lock_path(ctx: Context) -> Path:
    return ctx.paths["roster"].with_suffix(ctx.paths["roster"].suffix + ".lock")


def _normalize_roster_entry_id(value: object) -> int:
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return 0
    return parsed


def _to_unique_roster_name(base_name: str, name_counts: Dict[str, int]) -> str:
    key = base_name.casefold()
    if key not in name_counts:
        name_counts[key] = 1
        return base_name

    next_count = name_counts[key] + 1
    name_counts[key] = next_count
    return f"{base_name}{next_count}"


def normalize_roster_entries(entries: object) -> List[dict]:
    candidates = entries if isinstance(entries, list) else []
    normalized: List[dict] = []
    name_counts: Dict[str, int] = {}
    used_ids: set[int] = set()
    next_generated_id = 1

    for candidate in candidates:
        if not isinstance(candidate, dict):
            continue

        base_name = str(candidate.get("name", "") or "").strip()
        if not base_name:
            continue

        person_id = _normalize_roster_entry_id(candidate.get("id"))
        if person_id <= 0 or person_id in used_ids:
            while next_generated_id in used_ids:
                next_generated_id += 1
            person_id = next_generated_id
            used_ids.add(person_id)
        else:
            used_ids.add(person_id)

        if person_id >= next_generated_id:
            next_generated_id = person_id + 1

        normalized.append(
            {
                "id": person_id,
                "name": _to_unique_roster_name(base_name, name_counts),
                "active": bool(candidate.get("active", True)),
            }
        )

    normalized.sort(key=lambda item: item["id"])
    return normalized


def save_roster_atomic(path: Path, roster_entries: List[dict]) -> None:
    tmp_path = path.with_suffix(path.suffix + ".tmp")
    with open(tmp_path, "w", encoding="utf-8-sig", newline="") as file:
        writer = csv.DictWriter(file, fieldnames=["id", "name", "active"])
        writer.writeheader()
        for item in roster_entries:
            writer.writerow(
                {
                    "id": int(item.get("id", 0) or 0),
                    "name": str(item.get("name", "") or "").strip(),
                    "active": "1" if bool(item.get("active", True)) else "0",
                }
            )
        file.flush()
        os.fsync(file.fileno())
    for attempt in range(3):
        try:
            os.replace(str(tmp_path), str(path))
            return
        except PermissionError:
            if attempt < 2:
                time.sleep(0.1 * (attempt + 1))
            else:
                raise


def save_roster_entries(ctx: Context, roster_entries: object) -> List[dict]:
    normalized = normalize_roster_entries(roster_entries)
    ctx.paths["roster"].parent.mkdir(parents=True, exist_ok=True)
    lock_path = _roster_lock_path(ctx)
    acquire_file_lock(lock_path, CONFIG_LOCK_TIMEOUT_SECONDS)
    try:
        save_roster_atomic(ctx.paths["roster"], normalized)
    finally:
        release_file_lock(lock_path)

    _log(
        ctx,
        "INFO",
        "RosterStore",
        "Saved roster entries.",
        roster_path=str(ctx.paths["roster"]),
        roster_count=len(normalized),
        active_count=sum(1 for item in normalized if item.get("active")),
    )
    return normalized


def load_state(path: Path) -> dict:
    if not path.exists():
        return {
            "schedule_pool": [],
            "next_run_note": "",
            "debt_list": [],
            "credit_list": [],
            "last_pointer": 0,
        }
    with open(path, "r", encoding="utf-8-sig") as file:
        data = json.load(file)
    if "schedule_pool" not in data or not isinstance(data["schedule_pool"], list):
        data["schedule_pool"] = []
    if "next_run_note" not in data or not isinstance(data["next_run_note"], str):
        data["next_run_note"] = ""
    if "debt_list" not in data or not isinstance(data["debt_list"], list):
        data["debt_list"] = []
    if "credit_list" not in data or not isinstance(data["credit_list"], list):
        data["credit_list"] = []
    data["last_pointer"] = parse_int(data.get("last_pointer"), 0, 0, 1_000_000_000)
    return data


def _normalize_schedule_entry_day(value: object, parsed_date: date) -> str:
    text = str(value or "").strip()
    if text:
        return text
    return {
        0: "周一",
        1: "周二",
        2: "周三",
        3: "周四",
        4: "周五",
        5: "周六",
        6: "周日",
    }.get(parsed_date.weekday(), "")


def _normalize_schedule_entry_area_assignments(raw_assignments: object) -> Dict[str, List[str]]:
    normalized: Dict[str, List[str]] = {}
    if not isinstance(raw_assignments, dict):
        return normalized

    for raw_area, raw_students in raw_assignments.items():
        area = str(raw_area or "").strip()
        if not area:
            continue

        students: List[str] = []
        seen: set[str] = set()
        for raw_student in raw_students if isinstance(raw_students, list) else []:
            student = str(raw_student or "").strip()
            if not student or student in seen:
                continue
            seen.add(student)
            students.append(student)
        normalized[area] = students

    return normalized


def _collect_assignment_names(area_assignments: object) -> set[str]:
    names: set[str] = set()
    if not isinstance(area_assignments, dict):
        return names

    for raw_students in area_assignments.values():
        if not isinstance(raw_students, list):
            continue
        for raw_student in raw_students:
            student = str(raw_student or "").strip()
            if student:
                names.add(student)
    return names


def _load_active_name_to_id(roster_path: Path) -> Dict[str, int]:
    try:
        _, id_to_name, all_ids, id_to_active = load_roster(roster_path)
    except (FileNotFoundError, ValueError):
        return {}

    return {
        id_to_name[person_id].strip(): person_id
        for person_id in all_ids
        if id_to_active.get(person_id, 0) != 0 and id_to_name.get(person_id, "").strip()
    }


def _normalize_existing_id_list(raw_values: object) -> set[int]:
    normalized: set[int] = set()
    for raw_value in raw_values if isinstance(raw_values, list) else []:
        try:
            person_id = int(raw_value)
        except (TypeError, ValueError):
            continue
        if person_id > 0:
            normalized.add(person_id)
    return normalized


def save_schedule_entry_edit(ctx: Context, payload: dict) -> dict:
    request = dict(payload or {})
    ledger_mode = str(request.get("ledger_mode", "record") or "record").strip().lower()
    if ledger_mode not in {"record", "skip"}:
        raise ValueError("ledger_mode must be either 'record' or 'skip'.")

    source_date = str(request.get("source_date", "") or "").strip()
    target_date_raw = str(request.get("target_date", "") or "").strip()
    if not target_date_raw:
        raise ValueError("target_date is required.")

    try:
        parsed_target_date = datetime.strptime(target_date_raw, "%Y-%m-%d").date()
    except ValueError:
        raise ValueError("target_date must be in yyyy-MM-dd format.") from None

    normalized_target_date = parsed_target_date.strftime("%Y-%m-%d")
    normalized_day = _normalize_schedule_entry_day(request.get("day"), parsed_target_date)
    normalized_assignments = _normalize_schedule_entry_area_assignments(request.get("area_assignments"))
    normalized_note = str(request.get("note", "") or "").strip()
    create_if_missing = bool(request.get("create_if_missing", False))
    active_name_to_id = _load_active_name_to_id(ctx.paths["roster"])

    ledger_applied = False

    def _apply_state_update(current_state: dict) -> dict:
        nonlocal ledger_applied
        next_state = dict(current_state)
        schedule_pool = [dict(entry) for entry in next_state.get("schedule_pool", []) if isinstance(entry, dict)]

        item = None
        if source_date:
            item = next((entry for entry in schedule_pool if str(entry.get("date", "")).strip() == source_date), None)

        duplicate = next((entry for entry in schedule_pool if str(entry.get("date", "")).strip() == normalized_target_date), None)
        if item is None:
            if not create_if_missing:
                raise ValueError("Schedule entry not found.")
            if duplicate is not None:
                raise ValueError("Schedule entry already exists for target date.")
            item = {}
            schedule_pool.append(item)
        elif item is not duplicate and duplicate is not None:
            raise ValueError("Schedule entry already exists for target date.")

        old_names = _collect_assignment_names(item.get("area_assignments"))

        item["date"] = normalized_target_date
        item["day"] = normalized_day
        item["area_assignments"] = normalized_assignments
        item["note"] = normalized_note

        if ledger_mode == "record":
            new_names = _collect_assignment_names(normalized_assignments)
            removed_names = old_names - new_names
            added_names = new_names - old_names

            debt_set = _normalize_existing_id_list(next_state.get("debt_list", []))
            credit_set = _normalize_existing_id_list(next_state.get("credit_list", []))
            original_debt = set(debt_set)
            original_credit = set(credit_set)

            for name in removed_names:
                person_id = active_name_to_id.get(name)
                if person_id is None:
                    continue
                if person_id in credit_set:
                    credit_set.remove(person_id)
                else:
                    debt_set.add(person_id)

            for name in added_names:
                person_id = active_name_to_id.get(name)
                if person_id is None:
                    continue
                if person_id in debt_set:
                    debt_set.remove(person_id)
                else:
                    credit_set.add(person_id)

            ledger_applied = debt_set != original_debt or credit_set != original_credit
            next_state["debt_list"] = sorted(debt_set)
            next_state["credit_list"] = sorted(credit_set)

        next_state["schedule_pool"] = sorted(
            schedule_pool,
            key=lambda entry: str(entry.get("date", "")).strip(),
        )
        return next_state

    updated_state = update_state(ctx.paths["state"], _apply_state_update)

    if create_if_missing and not source_date:
        message = "Schedule created."
    else:
        message = "Schedule updated."
    if ledger_mode == "skip":
        message = f"{message} Ledger changes skipped."
    elif ledger_applied:
        message = f"{message} Ledger updated."
    else:
        message = f"{message} Ledger unchanged."

    return {
        "status": "success",
        "message": message,
        "ledger_mode": ledger_mode,
        "ledger_applied": ledger_applied,
        "state": updated_state,
    }


def parse_bool(value, default: bool) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    normalized = str(value).strip().lower()
    if normalized in ("true", "1", "yes", "on"):
        return True
    if normalized in ("false", "0", "no", "off"):
        return False
    return default


def parse_int(value, default: int, minimum: int = 1, maximum: int = 365) -> int:
    try:
        parsed = int(value)
    except (ValueError, TypeError):
        return default
    return max(minimum, min(maximum, parsed))


def normalize_area_names(raw_area_names) -> List[str]:
    seen = set()
    areas: List[str] = []
    if not isinstance(raw_area_names, list):
        return areas
    for raw in raw_area_names:
        name = str(raw).strip()
        if name and name not in seen:
            seen.add(name)
            areas.append(name)
    return areas


def normalize_area_per_day_counts(area_names: List[str], raw_counts, fallback_per_day: int) -> Dict[str, int]:
    fallback = parse_int(fallback_per_day, DEFAULT_ASSIGNMENTS_PER_AREA, 1, 30)
    source: Dict[str, int] = {}
    if isinstance(raw_counts, dict):
        for key, value in raw_counts.items():
            area = str(key).strip()
            if area:
                source[area] = parse_int(value, fallback, 1, 30)
    return {area: source.get(area, fallback) for area in area_names}


def get_pool_entries_with_date(state_data: dict) -> List[Tuple[dict, date]]:
    pool = state_data.get("schedule_pool", [])
    result = []
    for entry in pool:
        try:
            entry_date = datetime.strptime(entry.get("date", ""), "%Y-%m-%d").date()
            result.append((entry, entry_date))
        except (ValueError, TypeError):
            continue
    return sorted(result, key=lambda item: item[1])


def anonymize_instruction(text: str, name_to_id: Dict[str, int]) -> str:
    if not text:
        return text
    result = text
    placeholder_map: Dict[str, str] = {}
    for index, name in enumerate(sorted(name_to_id.keys(), key=len, reverse=True)):
        placeholder = f"\x00PLACEHOLDER_{index}\x00"
        placeholder_map[placeholder] = str(name_to_id[name])
        result = re.sub(re.escape(name), placeholder, result)
    for placeholder, person_id in placeholder_map.items():
        result = result.replace(placeholder, person_id)
    return result


def extract_ids_from_value(value, active_set: set, limit: Optional[int] = None) -> List[int]:
    if isinstance(value, str):
        value = value.strip(" []")
        items = [part.strip() for part in value.split(",")] if value else []
    elif isinstance(value, list):
        items = value
    elif isinstance(value, (int, float)):
        items = [value]
    else:
        return []

    result: List[int] = []
    for raw in items:
        try:
            person_id = int(raw)
        except Exception:
            continue
        if person_id in active_set and person_id not in result:
            result.append(person_id)
            if limit is not None and len(result) >= limit:
                break
    return result
