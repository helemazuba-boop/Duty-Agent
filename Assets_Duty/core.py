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
import io
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, HTTPServer
import threading
import signal
from typing import List, Dict, Any, Optional, Union, Callable

# Add current directory to path for local module imports
sys.path.append(os.path.dirname(os.path.abspath(__file__)))

try:
    import prompt_config
except ImportError:
    from . import prompt_config

try:
    import build_prompt
except ImportError:
    from . import build_prompt
from build_prompt import build_prompt_messages
from datetime import datetime, timedelta, date
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Tuple

DEFAULT_PER_DAY = 2
DAY_NAMES = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]
LLM_TIMEOUT_SECONDS = 120
LLM_MAX_RETRIES = 2
LLM_RETRY_BACKOFF_SECONDS = 2
AI_RESPONSE_MAX_CHARS = 20000
LLM_STREAM_ENABLED_DEFAULT = True
LLM_PARSE_MAX_RETRIES = 1
LLM_PROGRESS_LINE_PREFIX = "__DUTY_PROGRESS__:"
LLM_STREAM_PROGRESS_MIN_INTERVAL_SECONDS = 0.2
STATE_LOCK_TIMEOUT_SECONDS = 360
STATE_LOCK_RETRY_INTERVAL_SECONDS = 0.2


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


def load_roster(csv_path: Path) -> Tuple[Dict[str, int], Dict[int, str], List[int], Dict[int, int]]:
    """Load roster from CSV.

    Returns:
        name_to_id, id_to_name, all_ids, id_to_active
    """
    if not csv_path.exists():
        raise FileNotFoundError(f"roster.csv not found: {csv_path}")

    name_to_id: Dict[str, int] = {}
    id_to_name: Dict[int, str] = {}
    all_ids: List[int] = []
    id_to_active: Dict[int, int] = {}

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

            if raw_name in name_to_id:
                raise ValueError(f"检测到重名学生: {raw_name}，请修改名单后重试。")

            name_to_id[raw_name] = pid
            id_to_name[pid] = raw_name
            all_ids.append(pid)
            id_to_active[pid] = active

    all_ids = sorted(set(all_ids))
    if not all_ids:
        raise ValueError("No people in roster.csv.")
    return name_to_id, id_to_name, all_ids, id_to_active


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
    all_ids: List[int],
    id_to_active: Dict[int, int],
    current_time: str,
    instruction: str,
    duty_rule: str,
    area_names: List[str],
    debt_list: List[int],
    credit_list: List[int],
    previous_context: str = "",
    prompt_mode: str = "Regular",
) -> List[dict]:
    # implementation moved to build_prompt.py
    from build_prompt import build_prompt_messages as rpm
    return rpm(
        all_ids=all_ids,
        current_time=current_time,
        id_to_active=id_to_active,
        instruction=instruction,
        duty_rule=duty_rule,
        area_names=area_names,
        debt_list=debt_list,
        credit_list=credit_list,
        previous_context=previous_context,
        prompt_mode=prompt_mode
    )


def recover_missing_debts(
    original_debt_list: List[int],
    new_debt_ids_from_llm: List[int],
    normalized_schedule: List[dict],
) -> List[int]:
    """Audit post-normalization schedule to recover debts the LLM missed.

    Returns a deduplicated, sorted list of IDs that should remain in debt.
    """
    # Collect all IDs that actually made it into the final schedule
    scheduled_set: set = set()
    for entry in normalized_schedule:
        if not isinstance(entry, dict):
            continue
        for ids in entry.get("area_ids", {}).values():
            if isinstance(ids, list):
                scheduled_set.update(ids)

    # Start with what the LLM reported as still in debt
    final_debt_set = set(new_debt_ids_from_llm)

    # Add any original debts that the LLM failed to schedule
    for pid in original_debt_list:
        if pid not in scheduled_set:
            final_debt_set.add(pid)

    # Remove anyone who IS scheduled (LLM might list them as debt AND schedule them)
    final_debt_set -= scheduled_set

    return sorted(final_debt_set)


def reconcile_credit_list(
    original_credit_list: List[int],
    new_credit_ids_from_llm: List[int],
    normalized_schedule: List[dict],
    valid_ids: set,
    debt_list: Optional[List[int]] = None,
    has_llm_field: bool = True,
) -> List[int]:
    """Reconcile credit list with runtime semantics.

    Rule:
    - Prefer LLM's explicit remaining credit list when the field is present.
    - Fall back to original list when the field is missing (compat mode).
    """
    # Kept for signature compatibility and potential future audits.
    _ = normalized_schedule

    if not has_llm_field:
        next_credit_set = set(original_credit_list)
    else:
        next_credit_set = set(new_credit_ids_from_llm)

    next_credit_set = {cid for cid in next_credit_set if cid in valid_ids}
    if debt_list:
        next_credit_set -= set(debt_list)

    return sorted(next_credit_set)



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
    seen_dates: set = set()
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
        if current_date in seen_dates:
            raise ValueError(f"LLM schedule has duplicate date `{raw_date}` at index {idx}.")

        previous_date = current_date
        seen_dates.add(current_date)


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
    deadline = time.time() + (LLM_TIMEOUT_SECONDS * 3)

    invoke_progress_callback(progress_callback, "stream_start", "Streaming response opened.")

    chunks: List[str] = []
    buffered_for_progress: List[str] = []
    saw_sse_data = False
    raw_lines: List[str] = []
    last_progress_emit_at = time.time()

    with urllib.request.urlopen(req, timeout=LLM_TIMEOUT_SECONDS) as resp:
        for raw_line in resp:
            if time.time() > deadline:
                raise TimeoutError("Total stream duration exceeded timeout budget.")
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

    if last_error:
        raise RuntimeError(f"LLM request failed after retries: {last_error}") from last_error
    else:
        raise RuntimeError("LLM request failed after retries with no captured error.")


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
        "messages": list(messages),
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

    # Parse XML/CSV with application-level retry (re-call LLM on format hallucination)
    last_parse_error: Optional[BaseException] = None
    original_msg_count = len(payload["messages"])
    for parse_attempt in range(LLM_PARSE_MAX_RETRIES + 1):
        try:
            # 1. Apply RESET logic to handle mid-generation corrections
            if "RESET" in content:
                content = content.split("RESET")[-1].strip()
            
            # 2. Extract <csv>
            csv_match = re.search(r'<csv>(.*?)</csv>', content, re.DOTALL)
            if not csv_match:
                raise ValueError("LLM returned malformed structure: missing <csv> tags.")
            csv_text = csv_match.group(1).strip()
            
            # 3. Parse CSV to list of dicts
            schedule_raw = []
            if csv_text:
                reader = csv.DictReader(io.StringIO(csv_text))
                for row in reader:
                    date_str = str(row.get("Date", "")).strip()
                    assigned_ids = str(row.get("Assigned_IDs", "")).strip()
                    note = str(row.get("Note", "")).strip()
                    if date_str and assigned_ids:
                        schedule_raw.append({
                            "date": date_str,
                            "area_ids": {"默认区域": assigned_ids},
                            "note": note
                        })
            
            # 4. Extract State Tags
            next_run_note = ""
            nrn_match = re.search(r'<next_run_note>(.*?)</next_run_note>', content, re.DOTALL)
            if nrn_match:
                next_run_note = nrn_match.group(1).strip()
                
            new_debt_ids = ""
            nd_match = re.search(r'<new_debt_ids>(.*?)</new_debt_ids>', content, re.DOTALL)
            if nd_match:
                new_debt_ids = nd_match.group(1).strip()
                
            new_credit_ids = ""
            nc_match = re.search(r'<new_credit_ids>(.*?)</new_credit_ids>', content, re.DOTALL)
            if nc_match:
                new_credit_ids = nc_match.group(1).strip()
            
            parsed = {
                "schedule": schedule_raw,
                "next_run_note": next_run_note,
                "new_debt_ids": new_debt_ids,
                "new_credit_ids": new_credit_ids
            }
            return parsed, content
            
        except Exception as e:
            last_parse_error = e
            if parse_attempt < LLM_PARSE_MAX_RETRIES:
                invoke_progress_callback(
                    progress_callback,
                    "parse_retry",
                    f"AI output format error, retrying ({parse_attempt + 1}/{LLM_PARSE_MAX_RETRIES})...",
                )
                
                # Revert context bloat
                del payload["messages"][original_msg_count:]
                
                # Append failed output + error context so the LLM can self-correct
                payload["messages"].append({"role": "assistant", "content": content})
                payload["messages"].append({
                    "role": "user",
                    "content": f"你的上一次输出无法解析为规范数据。报错信息：{str(e)}。请检查是否遗漏了<csv>标签，确保严格输出。"
                })
                # Re-call LLM to get a corrected response
                content = execute_with_retries(
                    lambda: request_llm_non_stream(url, payload, api_key),
                    mode="non_stream",
                )
    err_msg = str(last_parse_error) if last_parse_error else "Unknown parse error"
    if last_parse_error:
        raise RuntimeError(f"LLM returned malformed XML/CSV after {LLM_PARSE_MAX_RETRIES + 1} attempts: {err_msg}") from last_parse_error
    else:
        raise RuntimeError(f"LLM returned malformed XML/CSV after {LLM_PARSE_MAX_RETRIES + 1} attempts: {err_msg}")


def extract_ids_from_value(value, active_set: set, limit: Optional[int] = None) -> List[int]:
    if isinstance(value, str):
        value = value.strip(" []")
        if not value:
            return []
        items = [part.strip() for part in value.split(",")]
    elif isinstance(value, list):
        items = value
    elif isinstance(value, (int, float)):
        items = [value]
    else:
        return []

    result: List[int] = []
    for raw in items:
        try:
            pid = int(raw)
        except Exception:
            continue
        if pid not in active_set or pid in result:
            continue
        result.append(pid)
        if limit is not None and len(result) >= limit:
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
                extracted_ids = extract_ids_from_value(raw_value, active_set, None)
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


def try_parse_iso_date(value: Any) -> Optional[date]:
    text = str(value or "").strip()
    if not text:
        return None
    try:
        return datetime.strptime(text, "%Y-%m-%d").date()
    except ValueError:
        return None


def merge_schedule_pool(state_data: dict, restored: List[dict], apply_mode: str, start_date: date) -> List[dict]:
    pool = state_data.get("schedule_pool", [])
    if not isinstance(pool, list):
        pool = []
    if not isinstance(restored, list):
        restored = []

    normalized_pool = [x for x in pool if isinstance(x, dict)]
    normalized_restored = [x for x in restored if isinstance(x, dict)]

    if apply_mode == "append":
        return dedupe_pool_by_date(normalized_pool + normalized_restored)

    if apply_mode == "replace_all":
        return dedupe_pool_by_date(normalized_restored)

    if apply_mode == "replace_future":
        kept = []
        for entry in normalized_pool:
            entry_date = try_parse_iso_date(entry.get("date"))
            # Keep invalid-date legacy entries to avoid destructive loss.
            if entry_date is None or entry_date < start_date:
                kept.append(entry)
        return dedupe_pool_by_date(kept + normalized_restored)

    if apply_mode == "replace_overlap":
        if not normalized_restored:
            return dedupe_pool_by_date(normalized_pool)

        restored_dates = [
            try_parse_iso_date(entry.get("date"))
            for entry in normalized_restored
        ]
        restored_dates = [d for d in restored_dates if d is not None]
        if not restored_dates:
            return dedupe_pool_by_date(normalized_pool + normalized_restored)

        end_date = max(restored_dates)
        kept = []
        for entry in normalized_pool:
            entry_date = try_parse_iso_date(entry.get("date"))
            if entry_date is None or entry_date < start_date or entry_date > end_date:
                kept.append(entry)
        return dedupe_pool_by_date(kept + normalized_restored)

    return dedupe_pool_by_date(normalized_pool + normalized_restored)

def merge_input_config(input_data: dict) -> dict:
    if not isinstance(input_data, dict):
        return {}
    config_sub = input_data.get("config", {})
    if isinstance(config_sub, dict):
        # Treat config as defaults, root-level fields as explicit overrides.
        merged = dict(config_sub)
        merged.update(input_data)
        return merged
    return input_data



def run_duty_agent(ctx, input_data, emit_progress_fn=None):
    state_lock_path = ctx.paths["state"].with_suffix(ctx.paths["state"].suffix + ".lock")
    state_lock_acquired = False
    
    try:
        run_now = datetime.now()
        ctx.config = load_config(ctx)
        name_to_id, id_to_name, all_ids, id_to_active = load_roster(ctx.paths["roster"])
        acquire_state_file_lock(state_lock_path)
        state_lock_acquired = True
        state_data = load_state(ctx.paths["state"])

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
        area_names = normalize_area_names(input_data.get("area_names", ctx.config.get("area_names", [])))
        area_per_day_counts = normalize_area_per_day_counts(
            area_names,
            input_data.get("area_per_day_counts", ctx.config.get("area_per_day_counts", {})),
            per_day,
        )
        duty_rule = str(input_data.get("duty_rule", ctx.config.get("duty_rule", ""))).strip()
        apply_mode = str(input_data.get("apply_mode", "append")).strip().lower()
        prompt_mode = str(input_data.get("prompt_mode", "Regular")).strip()
        existing_notes = input_data.get("existing_notes", {})
        if not isinstance(existing_notes, dict):
            existing_notes = {}

        # Start date
        today_date = run_now.date()
        entries = get_pool_entries_with_date(state_data)
        if apply_mode == "append":
            if entries:
                start_date = entries[-1][1] + timedelta(days=1)
            else:
                start_date = today_date
        else:
            start_date = today_date

        sanitized_instruction = anonymize_instruction(instruction, name_to_id)
        duty_rule = anonymize_instruction(duty_rule, name_to_id)

        current_time = run_now.strftime("%Y-%m-%d %H:%M")
        
        # Load previous context (AI Memory)
        previous_context = str(state_data.get("next_run_note", "")).strip()
        debt_list = extract_ids_from_value(state_data.get("debt_list", []), set(all_ids), 9999)
        credit_list = extract_ids_from_value(state_data.get("credit_list", []), set(all_ids), 9999)

        messages = build_prompt_messages(
            all_ids=all_ids,
            id_to_active=id_to_active,
            current_time=current_time,
            instruction=sanitized_instruction,
            duty_rule=duty_rule,
            area_names=area_names,
            previous_context=previous_context,
            debt_list=debt_list,
            credit_list=credit_list,
            prompt_mode=prompt_mode,
        )

        llm_result, llm_response_text = call_llm(
            messages,
            ctx.config,
            progress_callback=emit_progress_fn if emit_progress_fn else emit_progress_line,
        )
        schedule_raw = llm_result.get("schedule", [])
        validate_llm_schedule_entries(schedule_raw)
        
        # Persist next_run_note for future runs
        next_run_note = str(llm_result.get("next_run_note", "")).strip()
        state_data["next_run_note"] = next_run_note

        normalized_ids = normalize_multi_area_schedule_ids(
            schedule_raw,
            all_ids,
            area_names,
            area_per_day_counts,
        )

        # --- Debt Recovery Audit ---
        raw_new_debts = llm_result.get("new_debt_ids", [])
        new_debt_list = extract_ids_from_value(raw_new_debts, set(all_ids), 9999)
        state_data["debt_list"] = recover_missing_debts(
            original_debt_list=debt_list,
            new_debt_ids_from_llm=new_debt_list,
            normalized_schedule=normalized_ids,
        )

        # --- Credit List Persistence ---
        raw_new_credits = llm_result.get("new_credit_ids", [])
        new_credit_list = extract_ids_from_value(raw_new_credits, set(all_ids), 9999)
        state_data["credit_list"] = reconcile_credit_list(
            original_credit_list=credit_list,
            new_credit_ids_from_llm=new_credit_list,
            normalized_schedule=normalized_ids,
            valid_ids=set(all_ids),
            debt_list=state_data.get("debt_list", []),
            has_llm_field="new_credit_ids" in llm_result,
        )

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
        
        return {
            "status": "success",
            "message": "",
            "ai_response": (llm_response_text or "")[:AI_RESPONSE_MAX_CHARS]
        }
    except Exception as ex:
        traceback.print_exc()
        return {
            "status": "error",
            "message": str(ex)
        }
    finally:
        if state_lock_acquired:
            try:
                release_state_file_lock(state_lock_path)
            except Exception:
                pass

from fastapi import FastAPI, BackgroundTasks
from fastapi.middleware.cors import CORSMiddleware
from routers import duty
import uvicorn
import signal

app = FastAPI(title="Duty-Agent IPC Engine", version="0.40.0")

# Enable CORS for future-proofing (Web UI support)
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

# Register modular routers
app.include_router(duty.router)

@app.get("/")
async def root():
    return {"status": "running", "engine": "Duty-Agent FastAPI", "version": "0.40.0"}

@app.post("/shutdown")
async def shutdown(background_tasks: BackgroundTasks):
    def exit_process():
        time.sleep(0.5)
        os.kill(os.getpid(), signal.SIGTERM)
    
    background_tasks.add_task(exit_process)
    return {"status": "shutting down"}

def main():
    parser = argparse.ArgumentParser(description="Duty-Agent Core")
    parser.add_argument("--data-dir", type=str, default="data")
    parser.add_argument("--server", action="store_true", help="Run in HTTP server mode")
    parser.add_argument("--port", type=int, default=0, help="Port to listen on (0 for random)")
    args = parser.parse_args()

    data_dir = Path(args.data_dir).resolve()
    data_dir.mkdir(parents=True, exist_ok=True)
    
    if args.server:
        app.state.data_dir = data_dir
        # Config uvicorn to print the port to stdout
        config = uvicorn.Config(app, host="127.0.0.1", port=args.port, log_level="info")
        server = uvicorn.Server(config)
        
        # We need a small hack to print the port in the same format as before for C# capture
        # Uvicorn starts the loop in run(), so we'll use a lifespan or similar if needed.
        # But we can also just let it print its own logs if C# can parse them,
        # or manually print before serve.
        
        # Let's find an available port if args.port is 0 manually to be sure
        actual_port = args.port
        if actual_port == 0:
            import socket
            sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            sock.bind(('127.0.0.1', 0))
            actual_port = sock.getsockname()[1]
            sock.close()
        
        print(f"__DUTY_SERVER_PORT__:{actual_port}", flush=True)
        uvicorn.run(app, host="127.0.0.1", port=actual_port, log_level="warning")
    else:
        ctx = Context(data_dir)
        input_data = {}
        if ctx.paths["input"].exists():
            with open(ctx.paths["input"], "r", encoding="utf-8-sig") as f:
                input_data = json.load(f)
                
        result = run_duty_agent(ctx, input_data)
        
        extra = {}
        if result and "ai_response" in result:
            extra["ai_response"] = result["ai_response"]
        
        if result:
            write_result(
                ctx.paths["result"],
                result.get("status", "error"),
                result.get("message", "No message"),
                extra=extra
            )
        else:
            write_result(
                ctx.paths["result"],
                "error",
                "Internal error: No result generated.",
                extra=extra
            )

if __name__ == "__main__":
    main()
