#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import argparse
import csv
import json
import os
import re
import socket
import sys
import time
import traceback
import urllib.error
import urllib.request
from datetime import datetime, timedelta, date
from pathlib import Path
from typing import Dict, List, Optional, Set, Tuple

DEFAULT_DAYS = 5
DEFAULT_PER_DAY = 2
DEFAULT_AREA_NAMES = ["教室", "清洁区"]
DAY_NAMES = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]
LLM_TIMEOUT_SECONDS = 120
LLM_MAX_RETRIES = 2
LLM_RETRY_BACKOFF_SECONDS = 2
AI_RESPONSE_MAX_CHARS = 20000


class Context:
    def __init__(self, data_dir: Path):
        self.paths = {
            "config": data_dir / "config.json",
            "roster": data_dir / "roster.csv",
            "state": data_dir / "state.json",
            "input": data_dir / "ipc_input.json",
            "result": data_dir / "ipc_result.json",
        }
        self.config: dict = {}


def save_json_atomic(path: Path, data: dict):
    tmp_path = path.with_suffix(path.suffix + ".tmp")
    with open(tmp_path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.flush()
        os.fsync(f.fileno())
    for attempt in range(3):
        try:
            os.replace(str(tmp_path), str(path))
            return
        except PermissionError:
            if attempt < 2:
                time.sleep(0.1 * (attempt + 1))
            else:
                raise


def write_result(path: Path, status: str, message: str = "", extra: Optional[dict] = None):
    payload = {"status": status}
    if message:
        payload["message"] = message
    if isinstance(extra, dict):
        payload.update(extra)
    save_json_atomic(path, payload)


def load_config(ctx: Context) -> dict:
    config_path = ctx.paths["config"]
    if not config_path.exists():
        raise FileNotFoundError(f"Config file not found: {config_path}")

    # Accept both UTF-8 and UTF-8 BOM to avoid host-side encoding differences.
    with open(config_path, "r", encoding="utf-8-sig") as f:
        config = json.load(f)

    for key in ("base_url", "model"):
        if not str(config.get(key, "")).strip():
            raise ValueError(f"Missing config field: {key}")
    return config


def load_api_key_from_env() -> str:
    """Load API key from stdin pipe (preferred) or environment variable (fallback).

    The C# host writes the key to stdin then immediately closes the pipe,
    so readline() returns promptly.  When run interactively (isatty), stdin
    is skipped entirely to avoid blocking.
    """
    if not sys.stdin.isatty():
        try:
            line = sys.stdin.readline()
            api_key = line.strip() if line else ""
            if api_key:
                return api_key
        except Exception:
            pass
    api_key = os.environ.get("DUTY_AGENT_API_KEY", "").strip()
    if not api_key:
        raise ValueError("Missing API key: not provided via stdin or DUTY_AGENT_API_KEY.")
    return api_key


def to_unique_name(raw_name: str, seen_counts: Dict[str, int]) -> str:
    base_name = raw_name.strip()
    if not base_name:
        return ""

    next_count = seen_counts.get(base_name, 0) + 1
    seen_counts[base_name] = next_count
    if next_count == 1:
        return base_name
    return f"{base_name}{next_count}"


def load_roster(csv_path: Path) -> Tuple[Dict[str, int], Dict[int, str], List[int], List[int]]:
    """Load roster from CSV.

    Returns:
        name_to_id, id_to_name, active_ids, all_ids
    """
    if not csv_path.exists():
        raise FileNotFoundError(f"roster.csv not found: {csv_path}")

    name_to_id: Dict[str, int] = {}
    id_to_name: Dict[int, str] = {}
    active_ids: List[int] = []
    all_ids: List[int] = []
    seen_name_counts: Dict[str, int] = {}

    with open(csv_path, "r", encoding="utf-8-sig", newline="") as f:
        reader = csv.DictReader(f)
        for row in reader:
            raw_id = str(row.get("id", "")).strip()
            raw_name = str(row.get("name", "")).strip()
            raw_active = str(row.get("active", "1")).strip()
            if not raw_id or not raw_name:
                continue

            try:
                pid = int(raw_id)
            except (TypeError, ValueError):
                continue
            if pid <= 0:
                continue

            try:
                active = int(raw_active) if raw_active else 1
            except (TypeError, ValueError):
                active = 1

            unique_name = to_unique_name(raw_name, seen_name_counts)
            if not unique_name:
                continue

            name_to_id[unique_name] = pid
            id_to_name[pid] = unique_name
            all_ids.append(pid)
            if active == 1:
                active_ids.append(pid)

    all_ids = sorted(set(all_ids))
    active_ids = sorted(set(active_ids))
    if not active_ids:
        raise ValueError("No active people in roster.csv.")
    return name_to_id, id_to_name, active_ids, all_ids


def load_state(path: Path) -> dict:
    if not path.exists():
        return {"schedule_pool": []}
    with open(path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)
    if "schedule_pool" not in data or not isinstance(data["schedule_pool"], list):
        data["schedule_pool"] = []
    return data


def parse_bool(value, default: bool) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    s = str(value).lower()
    if s in ("true", "1", "yes", "on"):
        return True
    if s in ("false", "0", "no", "off"):
        return False
    return default


def parse_int(value, default: int, minimum: int = 1, maximum: int = 365) -> int:
    try:
        parsed = int(value)
    except (ValueError, TypeError):
        return default
    if parsed < minimum:
        return minimum
    if parsed > maximum:
        return maximum
    return parsed


def normalize_area_names(raw_area_names) -> List[str]:
    seen = set()
    areas: List[str] = []
    if isinstance(raw_area_names, list):
        candidates = raw_area_names
    else:
        candidates = []

    for raw in candidates:
        name = str(raw).strip()
        if not name or name in seen:
            continue
        seen.add(name)
        areas.append(name)

    if not areas:
        return DEFAULT_AREA_NAMES[:]
    return areas


def normalize_area_per_day_counts(
    area_names: List[str],
    raw_counts,
    fallback_per_day: int,
) -> Dict[str, int]:
    fallback = parse_int(fallback_per_day, DEFAULT_PER_DAY, 1, 30)
    source: Dict[str, int] = {}
    if isinstance(raw_counts, dict):
        for key, value in raw_counts.items():
            area = str(key).strip()
            if not area:
                continue
            source[area] = parse_int(value, fallback, 1, 30)

    normalized: Dict[str, int] = {}
    for area in area_names:
        normalized[area] = source.get(area, fallback)
    return normalized


def get_pool_entries_with_date(state_data: dict) -> List[Tuple[dict, date]]:
    pool = state_data.get("schedule_pool", [])
    result = []
    for entry in pool:
        d_str = entry.get("date", "")
        try:
            d = datetime.strptime(d_str, "%Y-%m-%d").date()
            result.append((entry, d))
        except ValueError:
            continue
    result.sort(key=lambda x: x[1])
    return result


def get_anchor_id(
    state_data: dict,
    name_to_id: Dict[str, int],
    active_ids: List[int],
    start_date: date,
    apply_mode: str,
) -> int:
    entries = get_pool_entries_with_date(state_data)

    if apply_mode == "append":
        candidates = entries
    else:
        candidates = [x for x in entries if x[1] < start_date]

    for entry, _ in reversed(candidates):
        classroom_students = entry.get("classroom_students", [])
        cleaning_area_students = entry.get("cleaning_area_students", [])
        legacy_students = entry.get("students", [])
        area_assignments = entry.get("area_assignments", {})
        area_assignment_lists = area_assignments.values() if isinstance(area_assignments, dict) else []
        for name_list in list(area_assignment_lists) + [cleaning_area_students, classroom_students, legacy_students]:
            if not isinstance(name_list, list) or not name_list:
                continue
            last_name = str(name_list[-1]).strip()
            if last_name in name_to_id:
                anchor_id = name_to_id[last_name]
                if anchor_id in active_ids:
                    return anchor_id

    return active_ids[-1]


def anonymize_instruction(text: str, name_to_id: Dict[str, int]) -> str:
    if not text:
        return text
    result = text
    placeholder_map: Dict[str, str] = {}
    for idx, name in enumerate(sorted(name_to_id.keys(), key=len, reverse=True)):
        placeholder = f"\x00PLACEHOLDER_{idx}\x00"
        placeholder_map[placeholder] = str(name_to_id[name])
        result = re.sub(re.escape(name), placeholder, result)
    for placeholder, pid_str in placeholder_map.items():
        result = result.replace(placeholder, pid_str)
    return result


def build_prompt_messages(
    id_range: Tuple[int, int],
    disabled_ids: List[int],
    last_id: int,
    current_time: str,
    instruction: str,
    duty_rule: str,
    area_names: List[str],
) -> List[dict]:
    """Build the 3-part prompt messages for the LLM.

    Structure:
        - System: base engine rules + output schema + long-term rules
        - User: parameters + single-use instruction
    """
    area_schema = ", ".join([f'"{name}": [101, 102]' for name in area_names])
    system_parts = [
        "You are a scheduling engine.",
        "Only process numeric IDs and output strict JSON.",
        "Do not output extra explanations.",
        "Output schema:",
        "{",
        '  "schedule": [',
        f'    {{"day": "Mon", "area_ids": {{{area_schema}}}}},',
        f'    {{"day": "Tue", "area_ids": {{{area_schema}}}}}',
        "  ]",
        "}",
        "The day field must use: Mon, Tue, Wed, Thu, Fri, Sat, Sun.",
        "For each day, area_ids must be an object where each area name maps to an array of IDs.",
        "Legacy keys such as classroom_ids and cleaning_area_ids are accepted for compatibility.",
    ]
    duty_rule = (duty_rule or "").strip()
    if duty_rule:
        system_parts.append("")
        system_parts.append("--- Rules ---")
        system_parts.append(duty_rule)
    system_prompt = "\n".join(system_parts)

    user_parts = [
        f"ID Range: {id_range[0]}-{id_range[1]}",
    ]
    if disabled_ids:
        user_parts.append(f"Disabled IDs: {disabled_ids}")
    user_parts.append(f"Last ID: {last_id}")
    user_parts.append(f"Current Time: {current_time}")
    user_parts.append(f'Instruction: "{instruction}"')
    user_prompt = "\n".join(user_parts)

    return [
        {"role": "system", "content": system_prompt},
        {"role": "user", "content": user_prompt},
    ]


def clean_json_response(text: str) -> str:
    text = text.strip()
    text = re.sub(r"^```(?:json)?\s*", "", text)
    text = re.sub(r"\s*```$", "", text)
    m = re.search(r"\{.*\}", text, re.DOTALL)
    return m.group(0) if m else text


def is_timeout_error(ex: Exception) -> bool:
    if isinstance(ex, (TimeoutError, socket.timeout)):
        return True
    reason = getattr(ex, "reason", None)
    if isinstance(reason, (TimeoutError, socket.timeout)):
        return True
    return "timed out" in str(ex).lower()


def call_llm(messages: List[dict], config: dict) -> Tuple[dict, str]:
    """Send prompt messages to the LLM and return parsed JSON + raw text."""
    base_url = str(config["base_url"]).rstrip("/")
    url = f"{base_url}/chat/completions"
    payload = {
        "model": config["model"],
        "messages": messages,
        "temperature": 0.1,
    }
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url=url,
        data=data,
        method="POST",
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {config['api_key']}",
        },
    )

    last_error: Optional[Exception] = None
    for attempt in range(LLM_MAX_RETRIES + 1):
        try:
            with urllib.request.urlopen(req, timeout=LLM_TIMEOUT_SECONDS) as resp:
                raw = resp.read().decode("utf-8")
            break
        except urllib.error.HTTPError as ex:
            detail = ex.read().decode("utf-8", errors="ignore")
            retryable = ex.code == 429 or 500 <= ex.code < 600
            if retryable and attempt < LLM_MAX_RETRIES:
                time.sleep(LLM_RETRY_BACKOFF_SECONDS * (attempt + 1))
                continue
            raise RuntimeError(f"HTTP 错误 {ex.code}: {detail}") from ex
        except (urllib.error.URLError, TimeoutError, socket.timeout) as ex:
            last_error = ex
            if attempt < LLM_MAX_RETRIES:
                time.sleep(LLM_RETRY_BACKOFF_SECONDS * (attempt + 1))
                continue
            if is_timeout_error(ex):
                raise RuntimeError(
                    f"网络请求超时（{LLM_TIMEOUT_SECONDS}秒），请检查网络连接或稍后重试。"
                ) from ex
            reason = getattr(ex, "reason", ex)
            raise RuntimeError(f"网络错误: {reason}") from ex
    else:
        raise RuntimeError(f"网络请求失败: {last_error}") from last_error

    response = json.loads(raw)
    choices = response.get("choices", [])
    if not choices:
        raise RuntimeError("LLM response missing choices.")
    message = choices[0].get("message", {})
    content = message.get("content") or ""
    if not content.strip():
        raise RuntimeError("LLM returned empty content.")
    return json.loads(clean_json_response(content)), content


def generate_target_dates(start_date: date, days: int, skip_weekends: bool) -> List[date]:
    target_dates = []
    curr = start_date
    while len(target_dates) < days:
        if skip_weekends and curr.weekday() >= 5:
            curr += timedelta(days=1)
            continue
        target_dates.append(curr)
        curr += timedelta(days=1)
    return target_dates


def fill_rotation_ids(
    initial_ids: List[int],
    active_ids: List[int],
    last_index: int,
    per_day: int,
    avoid_ids: Optional[Set[int]] = None,
) -> Tuple[List[int], int]:
    result = []
    for pid in initial_ids:
        if pid in active_ids and (avoid_ids is None or pid not in avoid_ids):
            result.append(pid)
            if len(result) >= per_day:
                break

    curr_idx = (last_index + 1) % len(active_ids)
    start_idx = curr_idx
    while len(result) < per_day:
        pid = active_ids[curr_idx]
        if pid not in result and (avoid_ids is None or pid not in avoid_ids):
            result.append(pid)
        curr_idx = (curr_idx + 1) % len(active_ids)
        if curr_idx == start_idx:
            break

    # Fallback: if avoid_ids blocked everyone, ignore avoid_ids and retry
    if not result and avoid_ids:
        curr_idx = (last_index + 1) % len(active_ids)
        start_idx = curr_idx
        while len(result) < per_day:
            pid = active_ids[curr_idx]
            if pid not in result:
                result.append(pid)
            curr_idx = (curr_idx + 1) % len(active_ids)
            if curr_idx == start_idx:
                break

    return result, (curr_idx - 1 + len(active_ids)) % len(active_ids)


def extract_ids_from_value(value, active_set: set, limit: int) -> List[int]:
    if not isinstance(value, list):
        return []

    result: List[int] = []
    for raw in value:
        try:
            pid = int(raw)
        except Exception:
            continue
        if pid not in active_set or pid in result:
            continue
        result.append(pid)
        if len(result) >= limit:
            break
    return result


def extract_area_ids(
    entry: dict,
    area_name: str,
    area_index: int,
    active_set: set,
    per_area_count: int,
) -> List[int]:
    result: List[int] = []

    def append_ids(value):
        nonlocal result
        if len(result) >= per_area_count:
            return
        for pid in extract_ids_from_value(value, active_set, per_area_count):
            if pid in result:
                continue
            result.append(pid)
            if len(result) >= per_area_count:
                break

    for key in ("area_ids", "areas", "area_assignments"):
        area_map = entry.get(key)
        if not isinstance(area_map, dict):
            continue
        append_ids(area_map.get(area_name))
        append_ids(area_map.get(str(area_index)))
        append_ids(area_map.get(str(area_index + 1)))

    normalized_name_key = re.sub(r"\s+", "_", area_name.strip().lower())
    append_ids(entry.get(area_name))
    append_ids(entry.get(f"{area_name}_ids"))
    append_ids(entry.get(normalized_name_key))
    append_ids(entry.get(f"{normalized_name_key}_ids"))

    if area_index == 0:
        for key in ("classroom_ids", "ids"):
            append_ids(entry.get(key))
    elif area_index == 1:
        for key in ("cleaning_area_ids", "cleaning_ids", "area_ids", "zone_ids"):
            value = entry.get(key)
            if key == "area_ids" and isinstance(value, dict):
                continue
            append_ids(value)
    else:
        append_ids(entry.get(f"area_{area_index + 1}_ids"))
        append_ids(entry.get(f"zone_{area_index + 1}_ids"))

    return result


def normalize_multi_area_schedule_ids(
    schedule_raw: list,
    active_ids: List[int],
    area_names: List[str],
    area_per_day_counts: Dict[str, int],
    days: int,
    anchor_id: int,
) -> List[dict]:
    active_set = set(active_ids)
    anchor_index = active_ids.index(anchor_id) if anchor_id in active_set else len(active_ids) - 1
    area_indexes: Dict[str, int] = {name: anchor_index for name in area_names}
    normalized: List[dict] = []

    for day_idx in range(days):
        day_assignments: Dict[str, List[int]] = {}
        used_ids: set = set()
        entry = schedule_raw[day_idx] if day_idx < len(schedule_raw) else {}
        if not isinstance(entry, dict):
            entry = {}

        for area_index, area_name in enumerate(area_names):
            per_area_count = area_per_day_counts.get(area_name, DEFAULT_PER_DAY)
            initial_ids = extract_area_ids(entry, area_name, area_index, active_set, per_area_count)
            initial_ids = [pid for pid in initial_ids if pid not in used_ids]

            filled_ids, area_indexes[area_name] = fill_rotation_ids(
                initial_ids,
                active_ids,
                area_indexes[area_name],
                per_area_count,
                avoid_ids=used_ids,
            )

            day_assignments[area_name] = filled_ids
            used_ids.update(filled_ids)

        normalized.append({"area_ids": day_assignments})

    return normalized


def restore_schedule(
    normalized_ids: List[dict],
    id_to_name: Dict[int, str],
    target_dates: List[date],
    area_names: List[str],
) -> List[dict]:
    restored = []
    fallback_first_area = area_names[0] if area_names else DEFAULT_AREA_NAMES[0]
    fallback_second_area = area_names[1] if len(area_names) > 1 else fallback_first_area

    for idx, day_entry in enumerate(normalized_ids):
        d = target_dates[idx]
        area_ids_map = day_entry.get("area_ids", {})
        if not isinstance(area_ids_map, dict):
            area_ids_map = {}

        area_assignments: Dict[str, List[str]] = {}
        for area_name in area_names:
            area_ids = area_ids_map.get(area_name, [])
            students = [id_to_name[pid] for pid in area_ids if pid in id_to_name]
            area_assignments[area_name] = students

        classroom_students = area_assignments.get(fallback_first_area, [])
        cleaning_area_students = area_assignments.get(fallback_second_area, [])
        restored.append(
            {
                "date": d.strftime("%Y-%m-%d"),
                "day": DAY_NAMES[d.weekday()],
                "students": classroom_students,
                "classroom_students": classroom_students,
                "cleaning_area_students": cleaning_area_students,
                "area_assignments": area_assignments,
            }
        )
    return restored


def dedupe_pool_by_date(entries: List[dict]) -> List[dict]:
    by_date: Dict[str, dict] = {}
    for entry in entries:
        if not isinstance(entry, dict):
            continue
        date_key = str(entry.get("date", "")).strip()
        if not date_key:
            continue
        by_date[date_key] = entry
    return [by_date[d] for d in sorted(by_date.keys())]


def merge_schedule_pool(state_data: dict, restored: List[dict], apply_mode: str, start_date: date) -> List[dict]:
    pool = state_data.get("schedule_pool", [])
    if not isinstance(pool, list):
        pool = []

    if apply_mode == "append":
        return dedupe_pool_by_date(pool + restored)

    if apply_mode == "replace_all":
        return dedupe_pool_by_date(restored)

    if apply_mode == "replace_future":
        start_str = start_date.strftime("%Y-%m-%d")
        kept = [x for x in pool if x.get("date", "") < start_str]
        return dedupe_pool_by_date(kept + restored)

    if apply_mode == "replace_overlap":
        start_str = start_date.strftime("%Y-%m-%d")
        end_str = restored[-1]["date"]
        kept = [x for x in pool if x.get("date", "") < start_str or x.get("date", "") > end_str]
        return dedupe_pool_by_date(kept + restored)

    return dedupe_pool_by_date(pool + restored)

def merge_input_config(input_data: dict) -> dict:
    if not isinstance(input_data, dict):
        return {}
    if "config" in input_data and isinstance(input_data["config"], dict):
        # Merge config into root, preserving root keys if they exist (though typically they won't)
        # We want config overrides to apply, so we merge config INTO root.
        # But wait, if root has "instruction" and config has others, we want union.
        merged = input_data.copy()
        merged.update(input_data["config"])
        # Ensure instruction is preserved if it was in root (it usually is)
        if "instruction" in input_data:
            merged["instruction"] = input_data["instruction"]
        return merged
    return input_data


def main():
    parser = argparse.ArgumentParser(description="Duty-Agent Core")
    parser.add_argument("--data-dir", type=str, default="data")
    args = parser.parse_args()

    data_dir = Path(args.data_dir).resolve()
    data_dir.mkdir(parents=True, exist_ok=True)
    ctx = Context(data_dir)

    try:
        ctx.config = load_config(ctx)
        name_to_id, id_to_name, active_ids, all_ids = load_roster(ctx.paths["roster"])
        state_data = load_state(ctx.paths["state"])

        input_data = {}
        if ctx.paths["input"].exists():
            with open(ctx.paths["input"], "r", encoding="utf-8-sig") as f:
                input_data = json.load(f)

        input_data = merge_input_config(input_data)

        instruction = str(input_data.get("instruction", "")).strip()
        if not instruction:
            instruction = "按照要求排班"
        base_url = str(input_data.get("base_url", ctx.config.get("base_url", ""))).strip()
        model = str(input_data.get("model", ctx.config.get("model", ""))).strip()
        if not base_url or not model:
            raise ValueError("Missing config field: base_url/model.")
        ctx.config["base_url"] = base_url
        ctx.config["model"] = model
        ctx.config["api_key"] = load_api_key_from_env()
        days_to_generate = parse_int(
            input_data.get("days_to_generate", ctx.config.get("auto_run_coverage_days", DEFAULT_DAYS)),
            DEFAULT_DAYS,
            1,
            30,
        )
        per_day = parse_int(
            input_data.get("per_day", ctx.config.get("per_day", DEFAULT_PER_DAY)),
            DEFAULT_PER_DAY,
            1,
            30,
        )
        area_names = normalize_area_names(input_data.get("area_names", ctx.config.get("area_names", DEFAULT_AREA_NAMES)))
        area_per_day_counts = normalize_area_per_day_counts(
            area_names,
            input_data.get("area_per_day_counts", ctx.config.get("area_per_day_counts", {})),
            per_day,
        )
        skip_weekends = parse_bool(
            input_data.get("skip_weekends", ctx.config.get("skip_weekends", True)),
            True,
        )
        duty_rule = str(input_data.get("duty_rule", ctx.config.get("duty_rule", ""))).strip()
        apply_mode = str(input_data.get("apply_mode", "append")).strip().lower()
        start_from_today = parse_bool(
            input_data.get("start_from_today", ctx.config.get("start_from_today", False)),
            False,
        )

        # Start date
        today_date = datetime.now().date()
        entries = get_pool_entries_with_date(state_data)
        if apply_mode == "append":
            if start_from_today:
                start_date = today_date
            elif entries:
                start_date = entries[-1][1] + timedelta(days=1)
            else:
                start_date = today_date
        else:
            start_date = today_date

        anchor_id = get_anchor_id(state_data, name_to_id, active_ids, start_date, apply_mode)
        sanitized_instruction = anonymize_instruction(instruction, name_to_id)

        # Build prompt with ID range + disabled IDs
        id_range = (min(all_ids), max(all_ids))
        disabled_ids = sorted(set(all_ids) - set(active_ids))
        current_time = datetime.now().strftime("%Y-%m-%d %H:%M")
        messages = build_prompt_messages(
            id_range,
            disabled_ids,
            anchor_id,
            current_time,
            sanitized_instruction,
            duty_rule,
            area_names,
        )

        llm_result, llm_response_text = call_llm(messages, ctx.config)
        schedule_raw = llm_result.get("schedule", [])
        target_dates = generate_target_dates(start_date, days_to_generate, skip_weekends)
        normalized_ids = normalize_multi_area_schedule_ids(
            schedule_raw,
            active_ids,
            area_names,
            area_per_day_counts,
            days_to_generate,
            anchor_id,
        )
        restored = restore_schedule(normalized_ids, id_to_name, target_dates, area_names)
        state_data["schedule_pool"] = merge_schedule_pool(state_data, restored, apply_mode, start_date)

        save_json_atomic(ctx.paths["state"], state_data)
        write_result(
            ctx.paths["result"],
            "success",
            extra={
                "ai_response": (llm_response_text or "")[:AI_RESPONSE_MAX_CHARS],
            },
        )
    except Exception as ex:
        traceback.print_exc()
        try:
            write_result(ctx.paths["result"], "error", str(ex))
        except Exception:
            pass
        sys.exit(1)


if __name__ == "__main__":
    main()
