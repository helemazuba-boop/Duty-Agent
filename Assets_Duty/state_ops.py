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
from diagnostics import mask_secret, truncate_for_log

DEFAULT_PER_DAY = 2
DEFAULT_BASE_URL = "https://integrate.api.nvidia.com/v1"
DEFAULT_MODEL = "moonshotai/kimi-k2-thinking"
DEFAULT_MODEL_PROFILE = "auto"
DEFAULT_ORCHESTRATION_MODE = "auto"
DEFAULT_MULTI_AGENT_EXECUTION_MODE = "auto"
STATE_LOCK_TIMEOUT_SECONDS = 360
STATE_LOCK_RETRY_INTERVAL_SECONDS = 0.2


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


def create_default_config() -> dict:
    return {
        "api_key": "",
        "base_url": DEFAULT_BASE_URL,
        "model": DEFAULT_MODEL,
        "model_profile": DEFAULT_MODEL_PROFILE,
        "orchestration_mode": DEFAULT_ORCHESTRATION_MODE,
        "multi_agent_execution_mode": DEFAULT_MULTI_AGENT_EXECUTION_MODE,
        "provider_hint": "",
        "per_day": DEFAULT_PER_DAY,
        "duty_rule": "",
    }


def normalize_config(config: dict | None) -> dict:
    source = dict(create_default_config())
    if isinstance(config, dict):
        source.update(config)

    normalized = create_default_config()
    normalized["api_key"] = str(source.get("api_key", "") or "").strip()
    normalized["base_url"] = str(source.get("base_url", "") or DEFAULT_BASE_URL).strip() or DEFAULT_BASE_URL
    normalized["model"] = str(source.get("model", "") or DEFAULT_MODEL).strip() or DEFAULT_MODEL
    normalized["model_profile"] = normalize_model_profile(source.get("model_profile", DEFAULT_MODEL_PROFILE))
    normalized["orchestration_mode"] = normalize_orchestration_mode(
        source.get("orchestration_mode", DEFAULT_ORCHESTRATION_MODE)
    )
    normalized["multi_agent_execution_mode"] = normalize_multi_agent_execution_mode(
        source.get("multi_agent_execution_mode", DEFAULT_MULTI_AGENT_EXECUTION_MODE)
    )
    normalized["provider_hint"] = str(source.get("provider_hint", "") or "").strip()
    normalized["per_day"] = parse_int(source.get("per_day"), DEFAULT_PER_DAY, 1, 30)
    normalized["duty_rule"] = str(source.get("duty_rule", "") or "").strip()
    return normalized


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


def acquire_state_file_lock(lock_path: Path, timeout_seconds: int = STATE_LOCK_TIMEOUT_SECONDS) -> None:
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
                raise TimeoutError(f"Timed out waiting for state lock: {lock_path}")
            time.sleep(STATE_LOCK_RETRY_INTERVAL_SECONDS)


def release_state_file_lock(lock_path: Path) -> None:
    try:
        lock_path.unlink()
    except FileNotFoundError:
        pass


def load_config(ctx: Context) -> dict:
    config_path = ctx.paths["config"]
    _log(ctx, "INFO", "ConfigStore", "Loading backend config.", config_path=str(config_path))
    if not config_path.exists():
        config = create_default_config()
        save_json_atomic(config_path, config)
        _log(ctx, "INFO", "ConfigStore", "Config file missing; created default backend config.",
             config_path=str(config_path))
        return config
    with open(config_path, "r", encoding="utf-8-sig") as file:
        raw = json.load(file)
    config = normalize_config(raw)
    if raw != config:
        save_json_atomic(config_path, config)
        _log(ctx, "INFO", "ConfigStore", "Normalized backend config and rewrote config file.",
             config_path=str(config_path),
             model=config.get("model", ""),
             model_profile=config.get("model_profile", ""),
             orchestration_mode=config.get("orchestration_mode", ""))
    else:
        _log(ctx, "INFO", "ConfigStore", "Loaded backend config.",
             config_path=str(config_path),
             model=config.get("model", ""),
             model_profile=config.get("model_profile", ""),
             orchestration_mode=config.get("orchestration_mode", ""))
    return config


def save_config(ctx: Context, config: dict) -> dict:
    normalized = normalize_config(config)
    save_json_atomic(ctx.paths["config"], normalized)
    _log(ctx, "INFO", "ConfigStore", "Saved backend config.",
         config_path=str(ctx.paths["config"]),
         base_url=normalized.get("base_url", ""),
         model=normalized.get("model", ""),
         model_profile=normalized.get("model_profile", ""),
         orchestration_mode=normalized.get("orchestration_mode", ""),
         multi_agent_execution_mode=normalized.get("multi_agent_execution_mode", ""))
    return normalized


def patch_config(ctx: Context, patch: dict) -> dict:
    patch_keys = sorted([str(key) for key, value in (patch or {}).items() if value is not None])
    _log(ctx, "INFO", "ConfigStore", "Patching backend config.",
         config_path=str(ctx.paths["config"]),
         patch_keys=patch_keys,
         api_key=mask_secret(str((patch or {}).get("api_key", "") or "")) if "api_key" in (patch or {}) else "<unchanged>",
         duty_rule=truncate_for_log(str((patch or {}).get("duty_rule", "") or ""), 160) if "duty_rule" in (patch or {}) else "<unchanged>")
    current = load_config(ctx)
    for key, value in (patch or {}).items():
        if value is None:
            continue
        current[key] = value
    return save_config(ctx, current)


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
    fallback = parse_int(fallback_per_day, DEFAULT_PER_DAY, 1, 30)
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
