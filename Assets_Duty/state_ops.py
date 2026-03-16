from __future__ import annotations

import csv
import json
import os
import re
import sys
import time
from datetime import date, datetime
from pathlib import Path
from typing import Dict, List, Optional, Tuple

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
STATE_LOCK_TIMEOUT_SECONDS = 360
STATE_LOCK_RETRY_INTERVAL_SECONDS = 0.2
CONFIG_LOCK_TIMEOUT_SECONDS = 30


class Context:
    def __init__(self, data_dir: Path, logger=None, trace_id: str = "", request_source: str = ""):
        self.data_dir = data_dir
        self.paths = {
            "config": data_dir / "config.json",
            "roster": data_dir / "roster.csv",
            "state": data_dir / "state.json",
            "input": data_dir / "ipc_input.json",
            "result": data_dir / "ipc_result.json",
        }
        self.config: dict = {}
        self.logger = logger
        self.trace_id = trace_id
        self.request_source = request_source


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
        "selected_plan_id": DEFAULT_SELECTED_PLAN_ID,
        "plan_presets": _create_default_plan_presets(),
        "duty_rule": "",
    }


def _normalize_persisted_config(config: dict | None) -> dict:
    source = dict(config) if isinstance(config, dict) else {}
    plan_presets = _normalize_plan_presets(source.get("plan_presets"))
    selected_plan_id = normalize_selected_plan_id(source.get("selected_plan_id"), plan_presets, source)
    return {
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


def acquire_file_lock(lock_path: Path, timeout_seconds: int) -> None:
    deadline = time.time() + max(1, int(timeout_seconds))
    while True:
        try:
            fd = os.open(str(lock_path), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            with os.fdopen(fd, "w", encoding="utf-8") as lock_file:
                lock_file.write(f"{os.getpid()}\n")
                lock_file.write(f"{datetime.now().isoformat()}\n")
            return
        except FileExistsError:
            if time.time() >= deadline:
                raise TimeoutError(f"Timed out waiting for file lock: {lock_path}")
            time.sleep(STATE_LOCK_RETRY_INTERVAL_SECONDS)


def release_file_lock(lock_path: Path) -> None:
    try:
        lock_path.unlink()
    except FileNotFoundError:
        pass


def acquire_state_file_lock(lock_path: Path, timeout_seconds: int = STATE_LOCK_TIMEOUT_SECONDS) -> None:
    acquire_file_lock(lock_path, timeout_seconds)


def release_state_file_lock(lock_path: Path) -> None:
    release_file_lock(lock_path)


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


def patch_config(ctx: Context, patch: dict) -> dict:
    patch_keys = sorted([str(key) for key, value in (patch or {}).items() if value is not None])
    unsupported_keys = sorted(
        str(key)
        for key, value in (patch or {}).items()
        if value is not None and key not in {"selected_plan_id", "plan_presets", "duty_rule"}
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
        selected_plan_id=str((patch or {}).get("selected_plan_id", "") or "") if "selected_plan_id" in (patch or {}) else "<unchanged>",
        plan_preset_count=str(len((patch or {}).get("plan_presets") or [])) if "plan_presets" in (patch or {}) else "<unchanged>",
        duty_rule=truncate_for_log(str((patch or {}).get("duty_rule", "") or ""), 160) if "duty_rule" in (patch or {}) else "<unchanged>",
    )

    lock_path = _config_lock_path(ctx)
    acquire_file_lock(lock_path, CONFIG_LOCK_TIMEOUT_SECONDS)
    try:
        current, _ = _load_persisted_config_unlocked(ctx.paths["config"])
        for key, value in (patch or {}).items():
            if value is None:
                continue
            current[key] = value

        persisted = _normalize_persisted_config(current)
        save_json_atomic(ctx.paths["config"], persisted)
    finally:
        release_file_lock(lock_path)

    return _hydrate_runtime_config(persisted)


def load_api_key_from_env() -> str:
    if not sys.stdin.isatty():
        try:
            line = sys.stdin.readline()
            api_key = line.strip() if line else ""
            if api_key:
                return api_key
        except Exception:
            pass
    return os.environ.get("DUTY_AGENT_API_KEY", "").strip()


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
