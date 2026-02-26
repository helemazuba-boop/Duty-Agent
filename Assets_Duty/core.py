#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import argparse
import calendar
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
from typing import Any, Callable, Dict, List, Optional, Tuple

DEFAULT_PER_DAY = 2
DEFAULT_AREA_NAMES = ["教室", "清洁区"]
DAY_NAMES = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]
LLM_TIMEOUT_SECONDS = 120
LLM_MAX_RETRIES = 2
LLM_RETRY_BACKOFF_SECONDS = 2
AI_RESPONSE_MAX_CHARS = 20000
LLM_STREAM_ENABLED_DEFAULT = True
LLM_PROGRESS_LINE_PREFIX = "__DUTY_PROGRESS__:"
LLM_STREAM_PROGRESS_MIN_INTERVAL_SECONDS = 0.2


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
        area_assignments = entry.get("area_assignments", {})
        if isinstance(area_assignments, dict):
            for name_list in area_assignments.values():
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

WEEKDAY_CN = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]


def build_calendar_anchor(
    auto_run_mode: str,
    auto_run_parameter: str,
    per_day: int,
    active_count: int,
) -> str:
    """Build a calendar anchor string that tells the AI exactly which date range to schedule.

    Returns a multi-line string with:
      - Start Date / End Date (with weekday)
      - Cross-month boundaries if applicable
      - Hard constraint: no dates outside this range
    """
    today = datetime.now().date()
    mode = (auto_run_mode or "Off").strip().lower()

    # Determine scheduling span in days
    if mode == "weekly":
        span_days = 7
    elif mode == "monthly":
        span_days = calendar.monthrange(today.year, today.month)[1]
    elif mode == "custom":
        try:
            span_days = max(int(auto_run_parameter), 1)
        except (ValueError, TypeError):
            span_days = 14
    else:
        # For "Off" or unknown, estimate from roster size
        if per_day > 0 and active_count > 0:
            span_days = max((active_count // per_day) + 1, 7)
        else:
            span_days = 7

    start_date = today
    end_date = today + timedelta(days=span_days - 1)

    lines = [
        f"Schedule Start Date: {start_date.isoformat()} ({WEEKDAY_CN[start_date.weekday()]})",
        f"Schedule End Date:   {end_date.isoformat()} ({WEEKDAY_CN[end_date.weekday()]})",
        f"Total Days: {span_days}",
    ]

    # Cross-month boundary detection
    if start_date.month != end_date.month:
        # Find all month boundaries in the range
        cursor = start_date.replace(day=1)
        while cursor <= end_date:
            last_day_of_month = cursor.replace(
                day=calendar.monthrange(cursor.year, cursor.month)[1]
            )
            if start_date <= last_day_of_month <= end_date:
                next_month_first = last_day_of_month + timedelta(days=1)
                lines.append(
                    f"Cross-Month Boundary: {last_day_of_month.isoformat()} ({WEEKDAY_CN[last_day_of_month.weekday()]}) "
                    f"-> {next_month_first.isoformat()} ({WEEKDAY_CN[next_month_first.weekday()]})"
                )
            # Move to next month
            if cursor.month == 12:
                cursor = cursor.replace(year=cursor.year + 1, month=1)
            else:
                cursor = cursor.replace(month=cursor.month + 1)

    lines.append(
        "HARD CONSTRAINT: You MUST NOT generate any date outside this range. "
        "Use the boundaries above to verify every date you produce."
    )
    return "\n".join(lines)


def build_prompt_messages(
    id_range: Tuple[int, int],
    disabled_ids: List[int],
    last_id: int,
    current_time: str,
    instruction: str,
    duty_rule: str,
    area_names: List[str],
    debt_list: List[int],
    previous_context: str = "",
    calendar_anchor: str = "",
) -> List[dict]:
    """Build the 3-part prompt messages for the LLM."""
    
    area_schema_items = [f'"{name}": [101, 102]' for name in area_names]
    area_schema = ", ".join(area_schema_items)

    system_content = f"""# Role
You are the Duty-Agent, an intelligent scheduling assistant.
Your goal is to generate a schedule that balances **Hard Constraints** (Sick leave), **Soft Constraints** (Team training), and **Fairness** (Debt repayment).

# Input Context
- IDs are a continuous sequence (e.g., 1, 2, 3...).
- You have a `Main_Pointer` starting at `Last ID`.

# The "Two-Queue" Protocol
1. **Debt Queue**: Stores IDs skipped temporarily due to soft conflicts (e.g. Training). These MUST be cleared first.
2. **Main Pointer**: Tracks the highest ID accessed in the roster. It **ONLY increments**. It never resets or goes back.

# The "Patch" Principle (CRITICAL)
Your output acts as a JSON PATCH to an existing live scheduling database.
1. ONLY generate schedule entries for the specific dates requested by the User Instruction.
2. OVER-GENERATION IS FATAL: If the user asks for "Thursday and Friday" (2 days), you MUST output exactly 2 entries. Do NOT generate Saturday, Sunday, or next week.
3. Generating unrequested future dates will irreversibly DESTROY the user's existing future schedules.
4. When in doubt, generate FEWER days rather than more.

# Process (Chain of Thought)
For each scheduling request, perform these steps in your `thinking_trace`:
0. **Intent Parsing**: Read the User Instruction carefully. Count exactly how many days are requested. List the target dates explicitly. This is your contract — do NOT exceed it.
1. **Date & Conflict**: Calculate the date. Identify who is blocked (Sick or Team).
2. **Check Debt**: Is anyone in `Debt Queue` available today?
   - YES: Schedule them first (Backfill).
   - NO: Proceed to step 3.
3. **Advance Pointer**: If more slots are needed, increment `Main_Pointer`.
   - If New ID is Sick -> Skip permanently (do not add to Debt).
   - If New ID is Team/Soft Conflict -> Skip for today, ADD to `Debt Queue`, and increment Pointer again.
   - If Valid -> Schedule them.
4. **Flow Control**: Do not exceed daily capacity by too much. Spread debt repayment over multiple days if the queue is large ("Debt Avalanche" prevention).
5. **Final Check**: Before outputting, verify that your schedule array length matches the day count from step 0. If it doesn't, you have a bug.

# Output Schema (Strict JSON)
```json
{{
  "thinking_trace": {{
    "intent_parsing": "User requested Thursday and Friday. Target day count: 2. I will generate exactly 2 entries.",
    "step_1_analysis": "Date is 2026-02-18. IDs 5,6 are Team (blocked).",
    "step_2_pointer_logic": "Debt Queue is empty. Main Pointer moved from 10 to 12.",
    "step_3_action": "Scheduled 11. 12 is Team, added to Debt. Scheduled 13.",
    "final_check": "Output has 2 entries matching target. Safe to submit."
  }},
  "schedule": [
    {{
      "date": "YYYY-MM-DD",
      "area_ids": {{ {area_schema} }},
      "note": "Brief reason (e.g., 'Backfilled ID 6')"
    }}
  ],
  "next_run_note": "CRITICAL: Debt List [12] must be handled next run. Current Pointer at 13.",
  "new_debt_ids": [12]
}}
```
**Important**:
1. The `next_run_note` is your "Memory" for the next time you run. You MUST strictly record any remaining Debt List or important context here.
2. `new_debt_ids`: If you added anyone to the Debt Queue (or if anyone remains in it), you MUST output their IDs here as a list of integers.
"""

    duty_rule = (duty_rule or "").strip()
    if duty_rule:
        system_content += f"\n\n--- User Defined Rules ---\n{duty_rule}"

    user_parts = [
        f"ID Range: {id_range[0]}-{id_range[1]}",
        f"Disabled IDs: {disabled_ids}",
        f"Last ID: {last_id}",
    ]

    if previous_context:
        user_parts.append(f"PREVIOUS RUN MEMORY (IMPORTANT): {previous_context}")

    if debt_list:
        user_parts.append(f"CURRENT DEBT LIST (PRIORITY HIGH): {debt_list}. You MUST schedule these IDs first.")

    user_parts.append(f"Current Time: {current_time}")

    if calendar_anchor:
        user_parts.append(f"\n--- Calendar Anchor (DO NOT VIOLATE) ---\n{calendar_anchor}")

    user_parts.append(f'Instruction: "{instruction}"')
    
    user_prompt = "\n".join(user_parts)

    return [
        {"role": "system", "content": system_content},
        {"role": "user", "content": user_prompt},
    ]


def force_insert_debts(schedule_raw: List[dict], debt_list: List[int], area_names: List[str]) -> None:
    """Force insert debt IDs into the first day's schedule if not present."""
    if not debt_list or not schedule_raw or not area_names:
        return

    # 1. Identify who is arguably already scheduled?
    # Actually, we can just prepend them to the first slot and let normalization dedupe.
    # But wait, we want to ensure they are *first* in the list.
    
    target_day = schedule_raw[0]
    if "area_ids" not in target_day or not isinstance(target_day["area_ids"], dict):
        target_day["area_ids"] = {}
    
    first_area = area_names[0]
    existing = target_day["area_ids"].get(first_area, [])
    if not isinstance(existing, list):
        existing = []
    
    # Prepend debts to the first area of the first day
    # Filter out duplicates that might already be in existing to avoid double-adding?
    # Normalization handles deduplication, but we want debts at the FRONT.
    
    # We only prepend debts that are NOT already in the list to avoid duplication if LLM did its job.
    # Actually, if LLM did its job, they are somewhere. If we prepend, they are at the front.
    # If LLM put them at the end, prepending moves them to front (and dupes are removed later).
    # So prepending is safe.
    
    new_list = list(debt_list) + existing
    target_day["area_ids"][first_area] = new_list



def clean_json_response(text: str) -> str:
    text = text.strip()
    text = re.sub(r"^```(?:json)?\s*", "", text)
    text = re.sub(r"\s*```$", "", text)
    m = re.search(r"\{.*\}", text, re.DOTALL)
    return m.group(0) if m else text


def validate_llm_schedule_entries(schedule_raw) -> None:
    if not isinstance(schedule_raw, list):
        raise ValueError("LLM returned invalid schedule: `schedule` must be a list.")

    previous_date: Optional[date] = None
    for idx, entry in enumerate(schedule_raw):
        if not isinstance(entry, dict):
            raise ValueError(f"LLM schedule entry at index {idx} must be an object.")

        raw_date = str(entry.get("date", "")).strip()
        if not raw_date:
            raise ValueError(f"LLM schedule entry at index {idx} is missing `date`.")

        try:
            current_date = datetime.strptime(raw_date, "%Y-%m-%d").date()
        except ValueError:
            raise ValueError(
                f"LLM schedule entry at index {idx} has invalid date `{raw_date}`. Expected YYYY-MM-DD."
            )

        if previous_date is not None and current_date < previous_date:
            raise ValueError("LLM schedule dates must be sorted in ascending order.")

        previous_date = current_date


def is_timeout_error(ex: Exception) -> bool:
    if isinstance(ex, (TimeoutError, socket.timeout)):
        return True
    reason = getattr(ex, "reason", None)
    if isinstance(reason, (TimeoutError, socket.timeout)):
        return True
    return "timed out" in str(ex).lower()


class StreamUnsupportedError(RuntimeError):
    pass


def emit_progress_line(phase: str, message: str = "", chunk: str = "") -> None:
    phase_text = str(phase or "").strip()
    if not phase_text:
        return

    payload: Dict[str, str] = {"phase": phase_text}
    message_text = str(message or "").strip()
    if message_text:
        payload["message"] = message_text
    if chunk:
        payload["chunk"] = str(chunk)

    try:
        line = f"{LLM_PROGRESS_LINE_PREFIX}{json.dumps(payload, ensure_ascii=False)}"
        print(line, flush=True)
    except Exception:
        # Progress output should never break the scheduling flow.
        pass


def invoke_progress_callback(
    progress_callback: Optional[Callable[[str, str, str], None]],
    phase: str,
    message: str = "",
    chunk: str = "",
) -> None:
    if progress_callback is None:
        return
    try:
        progress_callback(phase, message, chunk)
    except Exception:
        # Ignore callback exceptions to avoid interrupting model calls.
        pass


def extract_text_content(content: Any) -> str:
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts: List[str] = []
        for item in content:
            if isinstance(item, str):
                parts.append(item)
                continue
            if not isinstance(item, dict):
                continue
            text_value = item.get("text")
            if isinstance(text_value, str):
                parts.append(text_value)
                continue
            nested = item.get("content")
            if isinstance(nested, str):
                parts.append(nested)
        return "".join(parts)
    return ""


def extract_text_from_non_stream_response(response: dict) -> str:
    choices = response.get("choices", [])
    if not isinstance(choices, list) or not choices:
        return ""

    choice = choices[0]
    if not isinstance(choice, dict):
        return ""

    message = choice.get("message", {})
    if isinstance(message, dict):
        content = extract_text_content(message.get("content"))
        if content:
            return content

    return extract_text_content(choice.get("text"))


def extract_text_from_stream_event(event_obj: dict) -> str:
    choices = event_obj.get("choices", [])
    if not isinstance(choices, list) or not choices:
        return ""

    choice = choices[0]
    if not isinstance(choice, dict):
        return ""

    delta = choice.get("delta", {})
    if isinstance(delta, dict):
        content = extract_text_content(delta.get("content"))
        if content:
            return content

    message = choice.get("message", {})
    if isinstance(message, dict):
        content = extract_text_content(message.get("content"))
        if content:
            return content

    return extract_text_content(choice.get("text"))


def create_llm_request(url: str, payload: dict, api_key: str) -> urllib.request.Request:
    data = json.dumps(payload).encode("utf-8")
    return urllib.request.Request(
        url=url,
        data=data,
        method="POST",
        headers={
            "Content-Type": "application/json",
            "Authorization": f"Bearer {api_key}",
        },
    )


def request_llm_non_stream(url: str, payload: dict, api_key: str) -> str:
    req = create_llm_request(url, payload, api_key)
    with urllib.request.urlopen(req, timeout=LLM_TIMEOUT_SECONDS) as resp:
        raw = resp.read().decode("utf-8")

    response = json.loads(raw)
    content = extract_text_from_non_stream_response(response)
    if not content.strip():
        raise RuntimeError("LLM returned empty content.")
    return content


def request_llm_stream(
    url: str,
    payload: dict,
    api_key: str,
    progress_callback: Optional[Callable[[str, str, str], None]] = None,
) -> str:
    stream_payload = dict(payload)
    stream_payload["stream"] = True
    req = create_llm_request(url, stream_payload, api_key)

    invoke_progress_callback(progress_callback, "stream_start", "Streaming response opened.")

    chunks: List[str] = []
    buffered_for_progress: List[str] = []
    saw_sse_data = False
    raw_lines: List[str] = []
    last_progress_emit_at = time.time()

    with urllib.request.urlopen(req, timeout=LLM_TIMEOUT_SECONDS) as resp:
        for raw_line in resp:
            decoded = raw_line.decode("utf-8", errors="ignore")
            raw_lines.append(decoded)
            line = decoded.strip()
            if not line or line.startswith(":"):
                continue
            if not line.startswith("data:"):
                continue

            saw_sse_data = True
            data_text = line[5:].strip()
            if not data_text:
                continue
            if data_text == "[DONE]":
                break

            try:
                event_obj = json.loads(data_text)
            except json.JSONDecodeError:
                continue

            text = extract_text_from_stream_event(event_obj)
            if not text:
                continue

            chunks.append(text)
            buffered_for_progress.append(text)
            now = time.time()
            if (now - last_progress_emit_at) >= LLM_STREAM_PROGRESS_MIN_INTERVAL_SECONDS:
                progress_chunk = "".join(buffered_for_progress)
                buffered_for_progress.clear()
                invoke_progress_callback(
                    progress_callback,
                    "stream_chunk",
                    "Receiving model stream...",
                    progress_chunk,
                )
                last_progress_emit_at = now

    if not saw_sse_data:
        raw_text = "".join(raw_lines).strip()
        if raw_text:
            try:
                fallback_response = json.loads(raw_text)
                content = extract_text_from_non_stream_response(fallback_response)
                if content.strip():
                    invoke_progress_callback(
                        progress_callback,
                        "stream_end",
                        "Streaming endpoint returned full response payload.",
                    )
                    return content
            except Exception:
                pass
        raise StreamUnsupportedError("Upstream endpoint does not provide SSE stream output.")

    if buffered_for_progress:
        invoke_progress_callback(
            progress_callback,
            "stream_chunk",
            "Receiving model stream...",
            "".join(buffered_for_progress),
        )

    content = "".join(chunks)
    if not content.strip():
        raise RuntimeError("LLM stream returned empty content.")

    invoke_progress_callback(progress_callback, "stream_end", "Streaming response completed.")
    return content


def execute_with_retries(
    request_fn: Callable[[], str],
    mode: str,
) -> str:
    last_error: Optional[Exception] = None
    for attempt in range(LLM_MAX_RETRIES + 1):
        try:
            return request_fn()
        except StreamUnsupportedError:
            raise
        except urllib.error.HTTPError as ex:
            detail = ex.read().decode("utf-8", errors="ignore")
            retryable = ex.code == 429 or 500 <= ex.code < 600
            if retryable and attempt < LLM_MAX_RETRIES:
                time.sleep(LLM_RETRY_BACKOFF_SECONDS * (attempt + 1))
                continue

            if mode == "stream" and ex.code in (400, 404, 405, 415, 422, 426, 501):
                raise StreamUnsupportedError(
                    f"Streaming request is not supported by upstream endpoint (HTTP {ex.code})."
                ) from ex

            raise RuntimeError(f"HTTP error {ex.code}: {detail}") from ex
        except (urllib.error.URLError, TimeoutError, socket.timeout) as ex:
            last_error = ex
            if attempt < LLM_MAX_RETRIES:
                time.sleep(LLM_RETRY_BACKOFF_SECONDS * (attempt + 1))
                continue

            if is_timeout_error(ex):
                raise RuntimeError(
                    f"Network request timed out ({LLM_TIMEOUT_SECONDS} seconds)."
                ) from ex
            reason = getattr(ex, "reason", ex)
            raise RuntimeError(f"Network error: {reason}") from ex

    raise RuntimeError(f"Network request failed: {last_error}") from last_error


def call_llm(
    messages: List[dict],
    config: dict,
    progress_callback: Optional[Callable[[str, str, str], None]] = None,
) -> Tuple[dict, str]:
    """Send prompt messages to the LLM and return parsed JSON + raw text."""
    base_url = str(config["base_url"]).rstrip("/")
    url = f"{base_url}/chat/completions"
    payload = {
        "model": config["model"],
        "messages": messages,
        "temperature": 0.1,
    }
    api_key = str(config.get("api_key", "")).strip()
    if not api_key:
        raise ValueError("Missing API key.")

    stream_enabled = parse_bool(
        config.get("llm_stream", config.get("stream", LLM_STREAM_ENABLED_DEFAULT)),
        LLM_STREAM_ENABLED_DEFAULT,
    )

    content = ""
    if stream_enabled:
        try:
            content = execute_with_retries(
                lambda: request_llm_stream(url, payload, api_key, progress_callback),
                mode="stream",
            )
        except StreamUnsupportedError:
            invoke_progress_callback(
                progress_callback,
                "stream_fallback",
                "Streaming not supported by endpoint. Falling back to non-stream mode.",
            )

    if not content:
        content = execute_with_retries(
            lambda: request_llm_non_stream(url, payload, api_key),
            mode="non_stream",
        )

    if not content.strip():
        raise RuntimeError("LLM returned empty content.")

    parsed = json.loads(clean_json_response(content))
    return parsed, content


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

    # Primary: read from area_ids / area_assignments dict keyed by area name
    for key in ("area_ids", "areas", "area_assignments"):
        area_map = entry.get(key)
        if not isinstance(area_map, dict):
            continue
        append_ids(area_map.get(area_name))
        append_ids(area_map.get(str(area_index)))

    return result


def normalize_multi_area_schedule_ids(
    schedule_raw: list,
    active_ids: List[int],
    area_names: List[str],
    area_per_day_counts: Dict[str, int],
) -> List[dict]:
    active_set = set(active_ids)
    normalized: List[dict] = []

    if not isinstance(schedule_raw, list):
        return []

    for entry in schedule_raw:
        if not isinstance(entry, dict):
            continue

        raw_date = str(entry.get("date", "")).strip()
        # Basic validation: must look like a date
        if len(raw_date) < 8:
            continue

        day_assignments: Dict[str, List[int]] = {}
        used_ids: set = set()

        # 1. Extract Predefined Configured Areas
        for area_index, area_name in enumerate(area_names):
            per_area_count = area_per_day_counts.get(area_name, DEFAULT_PER_DAY)
            
            # Extract IDs directly (no force fill)
            extracted_ids = extract_area_ids(entry, area_name, area_index, active_set, per_area_count)
            # Filter duplicates within the day
            final_ids = [pid for pid in extracted_ids if pid not in used_ids]
            
            day_assignments[area_name] = final_ids
            used_ids.update(final_ids)

        # 2. Extract Dynamic Areas created by AI
        raw_area_ids = entry.get("area_ids") or entry.get("areas") or entry.get("area_assignments") or {}
        if isinstance(raw_area_ids, dict):
            for dynamic_area_name, raw_value in raw_area_ids.items():
                dynamic_area_name = str(dynamic_area_name).strip()
                if not dynamic_area_name or dynamic_area_name in day_assignments:
                    continue
                extracted_ids = extract_ids_from_value(raw_value, active_set, 999)
                final_ids = [pid for pid in extracted_ids if pid not in used_ids]
                if final_ids:
                    day_assignments[dynamic_area_name] = final_ids
                    used_ids.update(final_ids)

        note = str(entry.get("note", "")).strip()
        normalized.append({
            "date": raw_date,
            "area_ids": day_assignments,
            "note": note
        })

    return normalized


def restore_schedule(
    normalized_ids: List[dict],
    id_to_name: Dict[int, str],
    area_names: List[str],
    existing_notes: Optional[Dict[str, str]] = None,
) -> List[dict]:
    restored = []
    if existing_notes is None:
        existing_notes = {}

    for day_entry in normalized_ids:
        date_str = day_entry.get("date", "")
        # Parse date to get day name
        try:
            d = datetime.strptime(date_str, "%Y-%m-%d").date()
        except ValueError:
            continue # Skip invalid dates
            
        area_ids_map = day_entry.get("area_ids", {})
        if not isinstance(area_ids_map, dict):
            area_ids_map = {}

        area_assignments: Dict[str, List[str]] = {}
        # 1. Predefined Configured Areas
        for area_name in area_names:
            area_ids = area_ids_map.get(area_name, [])
            students = [id_to_name[pid] for pid in area_ids if pid in id_to_name]
            area_assignments[area_name] = students

        # 2. Dynamic Areas
        for dynamic_area_name, area_ids in area_ids_map.items():
            dynamic_area_name = str(dynamic_area_name).strip()
            if not dynamic_area_name or dynamic_area_name in area_assignments:
                continue
            if isinstance(area_ids, list):
                students = [id_to_name[pid] for pid in area_ids if pid in id_to_name]
                if students:
                    area_assignments[dynamic_area_name] = students

        # Note logic: Use new note from AI if present, else fallback to existing note
        note = str(day_entry.get("note", "")).strip()
        if not note:
            note = existing_notes.get(date_str, "")

        restored.append(
            {
                "date": date_str,
                "day": DAY_NAMES[d.weekday()],
                "area_assignments": area_assignments,
                "note": note,
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
        if not restored:
            return dedupe_pool_by_date(pool)
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
        ctx.config["llm_stream"] = parse_bool(
            input_data.get(
                "llm_stream",
                input_data.get("stream", ctx.config.get("llm_stream", ctx.config.get("stream", LLM_STREAM_ENABLED_DEFAULT))),
            ),
            LLM_STREAM_ENABLED_DEFAULT,
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
        duty_rule = str(input_data.get("duty_rule", ctx.config.get("duty_rule", ""))).strip()
        apply_mode = str(input_data.get("apply_mode", "append")).strip().lower()
        existing_notes = input_data.get("existing_notes", {})
        if not isinstance(existing_notes, dict):
            existing_notes = {}

        # Start date
        today_date = datetime.now().date()
        entries = get_pool_entries_with_date(state_data)
        if apply_mode == "append":
            if entries:
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
        
        # Load previous context (AI Memory)
        previous_context = str(state_data.get("next_run_note", "")).strip()
        debt_list = extract_ids_from_value(state_data.get("debt_list", []), set(active_ids), 9999)
        
        # Build calendar anchor for AI date context
        auto_run_mode = str(input_data.get("auto_run_mode", ctx.config.get("auto_run_mode", "Off"))).strip()
        auto_run_parameter = str(input_data.get("auto_run_parameter", ctx.config.get("auto_run_parameter", ""))).strip()
        cal_anchor = build_calendar_anchor(auto_run_mode, auto_run_parameter, per_day, len(active_ids))

        messages = build_prompt_messages(
            id_range,
            disabled_ids,
            anchor_id,
            current_time,
            sanitized_instruction,
            duty_rule,
            area_names,
            previous_context=previous_context,
            debt_list=debt_list,
            calendar_anchor=cal_anchor,
        )

        llm_result, llm_response_text = call_llm(
            messages,
            ctx.config,
            progress_callback=emit_progress_line,
        )
        schedule_raw = llm_result.get("schedule", [])
        validate_llm_schedule_entries(schedule_raw)
        
        # Persist next_run_note for future runs
        next_run_note = str(llm_result.get("next_run_note", "")).strip()
        state_data["next_run_note"] = next_run_note
        
        # --- SCHEME 1: Structured Debt Enforcement ---
        # 1. Force insert existing debts
        force_insert_debts(schedule_raw, debt_list, area_names)
        
        # 2. Update debt list based on output `new_debt_ids`
        # Note: We trust the LLM to tell us who is STILL in debt (or newly added).
        # But we also should verify if our forced debts actually got scheduled?
        # If we force insert, they WILL be scheduled (unless active_ids filter drops them, but we checked that).
        
        raw_new_debts = llm_result.get("new_debt_ids", [])
        new_debt_list = extract_ids_from_value(raw_new_debts, set(active_ids), 9999)
        state_data["debt_list"] = new_debt_list
        # ---------------------------------------------
        
        normalized_ids = normalize_multi_area_schedule_ids(
            schedule_raw,
            active_ids,
            area_names,
            area_per_day_counts,
        )
        
        # Verify if force insertion worked?
        # If normalization dropped them (due to per_day limit), they should logically remain in debt.
        # But `new_debt_list` comes from LLM. LLM might not know we force inserted.
        # If we force insert, we are overriding LLM.
        # So we should recalculate debt?
        # logic: final_debt = (old_debt + new_debt) - scheduled_ids
        
        scheduled_set = set()
        for entry in normalized_ids:
            for ids in entry.get("area_ids", {}).values():
                scheduled_set.update(ids)
                
        # Recalculate strict debt list
        # Start with what LLM thinks is debt
        final_debt_set = set(new_debt_list)
        # Add original debts that failed to schedule (if any)
        # We check against keys in normalized_ids to see who ACTUALLY got scheduled
        scheduled_set = set()
        for entry in normalized_ids:
            for ids in entry.get("area_ids", {}).values():
                scheduled_set.update(ids)

        for old_pid in debt_list:
            if old_pid not in scheduled_set:
                final_debt_set.add(old_pid)
        
        # Remove anyone who IS scheduled (just in case LLM listed them as debt but also scheduled them)
        final_debt_set = final_debt_set - scheduled_set
        
        state_data["debt_list"] = sorted(list(final_debt_set))

        restored = restore_schedule(
            normalized_ids,
            id_to_name,
            area_names,
            existing_notes,
        )
        if not restored:
            raise ValueError("LLM returned no valid schedule entries.")
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
