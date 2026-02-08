#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import argparse
import csv
import json
import os
import re
import sys
import traceback
import urllib.error
import urllib.request
from datetime import datetime, timedelta, date
from pathlib import Path
from typing import Dict, List, Optional, Tuple

DEFAULT_DAYS = 5
DEFAULT_PER_DAY = 2
DAY_NAMES = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]


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
    os.replace(str(tmp_path), str(path))


def write_result(path: Path, status: str, message: str = ""):
    payload = {"status": status}
    if message:
        payload["message"] = message
    save_json_atomic(path, payload)


def load_config(ctx: Context) -> dict:
    config_path = ctx.paths["config"]
    if not config_path.exists():
        raise FileNotFoundError(f"Config file not found: {config_path}")

    with open(config_path, "r", encoding="utf-8") as f:
        config = json.load(f)

    for key in ("api_key", "base_url", "model"):
        if not str(config.get(key, "")).strip():
            raise ValueError(f"Missing config field: {key}")
    return config


def load_roster(csv_path: Path) -> Tuple[Dict[str, int], Dict[int, str], List[int]]:
    if not csv_path.exists():
        raise FileNotFoundError(f"roster.csv not found: {csv_path}")

    name_to_id: Dict[str, int] = {}
    id_to_name: Dict[int, str] = {}
    active_ids: List[int] = []

    with open(csv_path, "r", encoding="utf-8-sig", newline="") as f:
        reader = csv.DictReader(f)
        for row in reader:
            raw_id = str(row.get("id", "")).strip()
            raw_name = str(row.get("name", "")).strip()
            raw_active = str(row.get("active", "1")).strip()
            if not raw_id or not raw_name:
                continue

            pid = int(raw_id)
            active = int(raw_active) if raw_active else 1
            name_to_id[raw_name] = pid
            id_to_name[pid] = raw_name
            if active == 1:
                active_ids.append(pid)

    active_ids = sorted(set(active_ids))
    if not active_ids:
        raise ValueError("No active people in roster.csv.")
    return name_to_id, id_to_name, active_ids


def load_state(path: Path) -> dict:
    if not path.exists():
        return {"schedule_pool": []}
    with open(path, "r", encoding="utf-8") as f:
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
        for name_list in (cleaning_area_students, classroom_students, legacy_students):
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
    for name in sorted(name_to_id.keys(), key=len, reverse=True):
        result = re.sub(re.escape(name), str(name_to_id[name]), result)
    return result


def build_prompts(
    active_ids: List[int],
    last_id: int,
    instruction: str,
    days: int,
    per_day: int,
    duty_rule: str,
    start_date: date,
    skip_weekends: bool,
) -> Tuple[str, str]:
    system_prompt = (
        "You are a scheduling engine.\n"
        "Only process numeric IDs and output strict JSON.\n"
        "Do not output extra explanations.\n"
        "Output schema:\n"
        "{\n"
        '  "schedule": [\n'
        '    {"day": "Mon", "classroom_ids": [101, 102], "cleaning_area_ids": [103, 104]},\n'
        '    {"day": "Tue", "classroom_ids": [105, 106], "cleaning_area_ids": [107, 108]}\n'
        "  ]\n"
        "}\n"
        "The day field must use: Mon, Tue, Wed, Thu, Fri, Sat, Sun.\n"
        "For each day, both classroom_ids and cleaning_area_ids must be arrays."
    )

    prompt_parts = [
        f"Candidate IDs: {active_ids}",
        f"Last ID: {last_id}",
        f"Start Date: {start_date}",
        f"Skip Weekends: {skip_weekends}",
        f"Classroom students per day: {per_day}",
        f"Cleaning area students per day: {per_day}",
        f"Days to generate: {days}",
        f'Instruction: "{instruction}"',
    ]
    duty_rule = (duty_rule or "").strip()
    if duty_rule:
        prompt_parts.append(f"Rules:\n{duty_rule}")
    prompt_parts.append("Task: Generate schedule for two cleaning areas: classroom and cleaning area.")
    user_prompt = "\n".join(prompt_parts)
    return system_prompt, user_prompt


def clean_json_response(text: str) -> str:
    text = text.strip()
    text = re.sub(r"^```(?:json)?\s*", "", text)
    text = re.sub(r"\s*```$", "", text)
    m = re.search(r"\{.*\}", text, re.DOTALL)
    return m.group(0) if m else text


def call_llm(system_prompt: str, user_prompt: str, config: dict) -> dict:
    base_url = str(config["base_url"]).rstrip("/")
    url = f"{base_url}/chat/completions"
    payload = {
        "model": config["model"],
        "messages": [
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": user_prompt},
        ],
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

    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            raw = resp.read().decode("utf-8")
    except urllib.error.HTTPError as ex:
        detail = ex.read().decode("utf-8", errors="ignore")
        raise RuntimeError(f"HTTP error {ex.code}: {detail}") from ex
    except urllib.error.URLError as ex:
        raise RuntimeError(f"Network error: {ex.reason}") from ex

    response = json.loads(raw)
    choices = response.get("choices", [])
    if not choices:
        raise RuntimeError("LLM response missing choices.")
    message = choices[0].get("message", {})
    content = message.get("content") or ""
    if not content.strip():
        raise RuntimeError("LLM returned empty content.")
    return json.loads(clean_json_response(content))


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
    avoid_ids: set = None,
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
    return result, (curr_idx - 1 + len(active_ids)) % len(active_ids)


def extract_ids(entry: dict, keys: Tuple[str, ...], active_set: set, per_day: int) -> List[int]:
    result: List[int] = []
    for key in keys:
        values = entry.get(key, [])
        if not isinstance(values, list):
            continue

        for raw in values:
            try:
                pid = int(raw)
            except Exception:
                continue
            if pid not in active_set or pid in result:
                continue
            result.append(pid)
            if len(result) >= per_day:
                return result
    return result


def normalize_dual_area_schedule_ids(
    schedule_raw: list,
    active_ids: List[int],
    per_day: int,
    days: int,
    anchor_id: int,
) -> List[dict]:
    active_set = set(active_ids)
    classroom_index = active_ids.index(anchor_id) if anchor_id in active_set else len(active_ids) - 1
    cleaning_index = classroom_index
    normalized: List[dict] = []

    for day_idx in range(days):
        classroom_ids: List[int] = []
        cleaning_area_ids: List[int] = []

        if day_idx < len(schedule_raw):
            entry = schedule_raw[day_idx]
            if isinstance(entry, dict):
                classroom_ids = extract_ids(entry, ("classroom_ids", "ids"), active_set, per_day)
                cleaning_area_ids = extract_ids(
                    entry,
                    ("cleaning_area_ids", "cleaning_ids", "area_ids", "zone_ids"),
                    active_set,
                    per_day,
                )
                cleaning_area_ids = [pid for pid in cleaning_area_ids if pid not in classroom_ids]

        classroom_ids, classroom_index = fill_rotation_ids(
            classroom_ids,
            active_ids,
            classroom_index,
            per_day,
        )
        cleaning_area_ids, cleaning_index = fill_rotation_ids(
            cleaning_area_ids,
            active_ids,
            cleaning_index,
            per_day,
            avoid_ids=set(classroom_ids),
        )

        normalized.append(
            {
                "classroom_ids": classroom_ids,
                "cleaning_area_ids": cleaning_area_ids,
            }
        )
    return normalized


def restore_schedule(
    normalized_ids: List[dict],
    id_to_name: Dict[int, str],
    target_dates: List[date],
) -> List[dict]:
    restored = []
    for idx, day_entry in enumerate(normalized_ids):
        d = target_dates[idx]
        classroom_ids = day_entry.get("classroom_ids", [])
        cleaning_area_ids = day_entry.get("cleaning_area_ids", [])
        classroom_students = [id_to_name[pid] for pid in classroom_ids if pid in id_to_name]
        cleaning_area_students = [id_to_name[pid] for pid in cleaning_area_ids if pid in id_to_name]
        restored.append(
            {
                "date": d.strftime("%Y-%m-%d"),
                "day": DAY_NAMES[d.weekday()],
                "students": classroom_students,
                "classroom_students": classroom_students,
                "cleaning_area_students": cleaning_area_students,
            }
        )
    return restored


def merge_schedule_pool(state_data: dict, restored: List[dict], apply_mode: str, start_date: date) -> List[dict]:
    pool = state_data.get("schedule_pool", [])
    if apply_mode == "append":
        return pool + restored

    if apply_mode == "replace_all":
        return restored

    if apply_mode == "replace_future":
        today_str = datetime.now().strftime("%Y-%m-%d")
        kept = [x for x in pool if x.get("date", "") <= today_str]
        return kept + restored

    if apply_mode == "replace_overlap":
        start_str = start_date.strftime("%Y-%m-%d")
        end_str = restored[-1]["date"]
        kept = [x for x in pool if x.get("date", "") < start_str or x.get("date", "") > end_str]
        merged = kept + restored
        merged.sort(key=lambda x: x.get("date", ""))
        return merged

    return pool + restored


def main():
    parser = argparse.ArgumentParser(description="Duty-Agent Core")
    parser.add_argument("--data-dir", type=str, default="data")
    args = parser.parse_args()

    data_dir = Path(args.data_dir).resolve()
    data_dir.mkdir(parents=True, exist_ok=True)
    ctx = Context(data_dir)

    try:
        ctx.config = load_config(ctx)
        name_to_id, id_to_name, active_ids = load_roster(ctx.paths["roster"])
        state_data = load_state(ctx.paths["state"])

        input_data = {}
        if ctx.paths["input"].exists():
            with open(ctx.paths["input"], "r", encoding="utf-8") as f:
                input_data = json.load(f)

        instruction = str(input_data.get("instruction", ""))
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
        skip_weekends = parse_bool(
            input_data.get("skip_weekends", ctx.config.get("skip_weekends", True)),
            True,
        )
        start_from_today = parse_bool(
            input_data.get("start_from_today", ctx.config.get("start_from_today", False)),
            False,
        )
        duty_rule = str(input_data.get("duty_rule", ctx.config.get("duty_rule", ""))).strip()
        apply_mode = str(input_data.get("apply_mode", "append")).strip().lower()

        # Start date
        if start_from_today:
            start_date = datetime.now().date()
        else:
            entries = get_pool_entries_with_date(state_data)
            if entries:
                start_date = entries[-1][1] + timedelta(days=1)
            else:
                start_date = datetime.now().date() + timedelta(days=1)

        anchor_id = get_anchor_id(state_data, name_to_id, active_ids, start_date, apply_mode)
        sanitized_instruction = anonymize_instruction(instruction, name_to_id)
        system_prompt, user_prompt = build_prompts(
            active_ids,
            anchor_id,
            sanitized_instruction,
            days_to_generate,
            per_day,
            duty_rule,
            start_date,
            skip_weekends,
        )

        llm_result = call_llm(system_prompt, user_prompt, ctx.config)
        schedule_raw = llm_result.get("schedule", [])
        target_dates = generate_target_dates(start_date, days_to_generate, skip_weekends)
        normalized_ids = normalize_dual_area_schedule_ids(
            schedule_raw,
            active_ids,
            per_day,
            days_to_generate,
            anchor_id,
        )
        restored = restore_schedule(normalized_ids, id_to_name, target_dates)
        state_data["schedule_pool"] = merge_schedule_pool(state_data, restored, apply_mode, start_date)

        save_json_atomic(ctx.paths["state"], state_data)
        write_result(ctx.paths["result"], "success")
    except Exception as ex:
        traceback.print_exc()
        try:
            write_result(ctx.paths["result"], "error", str(ex))
        except Exception:
            pass
        sys.exit(1)


if __name__ == "__main__":
    main()