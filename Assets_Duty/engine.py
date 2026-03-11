#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import json
import os
import re
import csv
import io
import time
import sys
import socket
import threading
import traceback
import urllib.request
import urllib.error
from datetime import datetime, timedelta, date
from pathlib import Path
from typing import List, Dict, Any, Optional, Union, Tuple, Callable

# Constants
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
        self.data_dir = data_dir
        self.paths = {
            "config": data_dir / "config.json",
            "roster": data_dir / "roster.csv",
            "state": data_dir / "state.json",
            "input": data_dir / "ipc_input.json",
            "result": data_dir / "ipc_result.json",
        }
        self.config: dict = {}

class StreamUnsupportedError(RuntimeError): pass

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

def load_config(ctx: Context) -> dict:
    config_path = ctx.paths["config"]
    if not config_path.exists():
        raise FileNotFoundError(f"Config file not found: {config_path}")
    with open(config_path, "r", encoding="utf-8-sig") as f:
        config = json.load(f)
    for key in ("base_url", "model"):
        if not str(config.get(key, "")).strip():
            raise ValueError(f"Missing config field: {key}")
    return config

def load_api_key_from_env() -> str:
    if not sys.stdin.isatty():
        try:
            line = sys.stdin.readline()
            api_key = line.strip() if line else ""
            if api_key: return api_key
        except Exception: pass
    api_key = os.environ.get("DUTY_AGENT_API_KEY", "").strip()
    return api_key

def load_roster(csv_path: Path) -> Tuple[Dict[str, int], Dict[int, str], List[int], Dict[int, int]]:
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
            if not raw_id or not raw_name: continue
            try: pid = int(raw_id)
            except (TypeError, ValueError): continue
            if pid <= 0: continue
            try: active = int(raw_active) if raw_active else 1
            except (TypeError, ValueError): active = 1
            if raw_name in name_to_id:
                raise ValueError(f"检测到重名学生: {raw_name}，请修改名单后重试。")
            name_to_id[raw_name] = pid
            id_to_name[pid] = raw_name
            all_ids.append(pid)
            id_to_active[pid] = active
    all_ids = sorted(set(all_ids))
    if not all_ids: raise ValueError("No people in roster.csv.")
    return name_to_id, id_to_name, all_ids, id_to_active

def load_state(path: Path) -> dict:
    if not path.exists(): return {"schedule_pool": []}
    with open(path, "r", encoding="utf-8-sig") as f:
        data = json.load(f)
    if "schedule_pool" not in data or not isinstance(data["schedule_pool"], list):
        data["schedule_pool"] = []
    return data

def parse_bool(value, default: bool) -> bool:
    if value is None: return default
    if isinstance(value, bool): return value
    s = str(value).lower()
    if s in ("true", "1", "yes", "on"): return True
    if s in ("false", "0", "no", "off"): return False
    return default

def parse_int(value, default: int, minimum: int = 1, maximum: int = 365) -> int:
    try: parsed = int(value)
    except (ValueError, TypeError): return default
    return max(minimum, min(maximum, parsed))

def normalize_area_names(raw_area_names) -> List[str]:
    seen = set(); areas: List[str] = []
    if not isinstance(raw_area_names, list): return areas
    for raw in raw_area_names:
        name = str(raw).strip()
        if name and name not in seen:
            seen.add(name); areas.append(name)
    return areas

def normalize_area_per_day_counts(area_names: List[str], raw_counts, fallback_per_day: int) -> Dict[str, int]:
    fallback = parse_int(fallback_per_day, DEFAULT_PER_DAY, 1, 30)
    source: Dict[str, int] = {}
    if isinstance(raw_counts, dict):
        for key, value in raw_counts.items():
            area = str(key).strip()
            if area: source[area] = parse_int(value, fallback, 1, 30)
    return {area: source.get(area, fallback) for area in area_names}

def get_pool_entries_with_date(state_data: dict) -> List[Tuple[dict, date]]:
    pool = state_data.get("schedule_pool", [])
    result = []
    for entry in pool:
        try:
            d = datetime.strptime(entry.get("date", ""), "%Y-%m-%d").date()
            result.append((entry, d))
        except (ValueError, TypeError): continue
    return sorted(result, key=lambda x: x[1])

def anonymize_instruction(text: str, name_to_id: Dict[str, int]) -> str:
    if not text: return text
    result = text; placeholder_map: Dict[str, str] = {}
    for idx, name in enumerate(sorted(name_to_id.keys(), key=len, reverse=True)):
        placeholder = f"\x00PLACEHOLDER_{idx}\x00"
        placeholder_map[placeholder] = str(name_to_id[name])
        result = re.sub(re.escape(name), placeholder, result)
    for placeholder, pid_str in placeholder_map.items():
        result = result.replace(placeholder, pid_str)
    return result

def extract_ids_from_value(value, active_set: set, limit: Optional[int] = None) -> List[int]:
    if isinstance(value, str):
        value = value.strip(" []")
        items = [part.strip() for part in value.split(",")] if value else []
    elif isinstance(value, list): items = value
    elif isinstance(value, (int, float)): items = [value]
    else: return []
    result: List[int] = []
    for raw in items:
        try: pid = int(raw)
        except: continue
        if pid in active_set and pid not in result:
            result.append(pid)
            if limit is not None and len(result) >= limit: break
    return result

def recover_missing_debts(original_debt_list: List[int], new_debt_ids_from_llm: List[int], normalized_schedule: List[dict]) -> List[int]:
    scheduled_set: set = set()
    for entry in normalized_schedule:
        if not isinstance(entry, dict):
            continue
        area_map = entry.get("area_ids", {})
        if not isinstance(area_map, dict):
            continue
        for ids in area_map.values():
            if isinstance(ids, list): scheduled_set.update(ids)
    final_debt_set = set(new_debt_ids_from_llm)
    for pid in original_debt_list:
        if pid not in scheduled_set: final_debt_set.add(pid)
    final_debt_set -= scheduled_set
    return sorted(final_debt_set)

def reconcile_credit_list(original_credit_list: List[int], new_credit_ids_from_llm: List[int], normalized_schedule: List[dict], valid_ids: set, debt_list: List[int], has_llm_field: bool) -> List[int]:
    next_credit_set = set(new_credit_ids_from_llm) if has_llm_field else set(original_credit_list)
    next_credit_set = {cid for cid in next_credit_set if cid in valid_ids}
    next_credit_set -= set(debt_list)
    return sorted(next_credit_set)

def create_llm_request(url: str, payload: dict, api_key: str) -> urllib.request.Request:
    data = json.dumps(payload).encode("utf-8")
    return urllib.request.Request(url=url, data=data, method="POST", headers={"Content-Type": "application/json", "Authorization": f"Bearer {api_key}"})

def extract_text_content(content: Any) -> str:
    if isinstance(content, str): return content
    if isinstance(content, list):
        parts: List[str] = []
        for item in content:
            if isinstance(item, str): parts.append(item)
            elif isinstance(item, dict):
                text_value = item.get("text") or item.get("content")
                if isinstance(text_value, str): parts.append(text_value)
        return "".join(parts)
    return ""

def extract_text_from_non_stream_response(response: dict) -> str:
    choices = response.get("choices", [])
    if not choices: return ""
    choice = choices[0]
    return extract_text_content(choice.get("message", {}).get("content")) or extract_text_content(choice.get("text"))

def extract_text_from_stream_event(event_obj: dict) -> str:
    choices = event_obj.get("choices", [])
    if not choices: return ""
    choice = choices[0]
    return extract_text_content(choice.get("delta", {}).get("content")) or extract_text_content(choice.get("message", {}).get("content")) or extract_text_content(choice.get("text"))

def execute_with_retries(request_fn: Callable[[], str], mode: str) -> str:
    last_error: Optional[Exception] = None
    for attempt in range(LLM_MAX_RETRIES + 1):
        try: return request_fn()
        except StreamUnsupportedError: raise
        except urllib.error.HTTPError as ex:
            detail = ex.read().decode("utf-8", errors="ignore")
            if (ex.code == 429 or 500 <= ex.code < 600) and attempt < LLM_MAX_RETRIES:
                time.sleep(LLM_RETRY_BACKOFF_SECONDS * (attempt + 1)); continue
            if mode == "stream" and ex.code in (400, 404, 405, 415, 422, 426, 501):
                raise StreamUnsupportedError(f"Streaming request is not supported by upstream (HTTP {ex.code}).") from ex
            raise RuntimeError(f"HTTP error {ex.code}: {detail}") from ex
        except (urllib.error.URLError, TimeoutError, socket.timeout, ConnectionError) as ex:
            last_error = ex
            if attempt < LLM_MAX_RETRIES:
                time.sleep(LLM_RETRY_BACKOFF_SECONDS * (attempt + 1)); continue
            raise RuntimeError(f"Network error: {ex}") from ex
    raise RuntimeError(f"LLM request failed after retries: {last_error}")

def request_llm_non_stream(url: str, payload: dict, api_key: str, stop_event: Optional[threading.Event] = None) -> str:
    if stop_event and stop_event.is_set(): raise InterruptedError("Cancelled.")
    req = create_llm_request(url, payload, api_key)
    with urllib.request.urlopen(req, timeout=LLM_TIMEOUT_SECONDS) as resp:
        raw = resp.read().decode("utf-8")
    response = json.loads(raw)
    content = extract_text_from_non_stream_response(response)
    if not content.strip(): raise RuntimeError("LLM returned empty content.")
    return content

def request_llm_stream(url: str, payload: dict, api_key: str, progress_callback=None, stop_event=None) -> str:
    stream_payload = dict(payload); stream_payload["stream"] = True
    req = create_llm_request(url, stream_payload, api_key)
    deadline = time.time() + (LLM_TIMEOUT_SECONDS * 3); chunks: List[str] = []
    buffered_for_progress: List[str] = []; saw_sse_data = False; raw_lines: List[str] = []
    last_progress_emit_at = time.time()
    
    if progress_callback: progress_callback("stream_start", "Streaming response opened.", "")

    with urllib.request.urlopen(req, timeout=LLM_TIMEOUT_SECONDS) as resp:
        for raw_line in resp:
            if stop_event and stop_event.is_set(): raise InterruptedError("Cancelled.")
            if time.time() > deadline: raise TimeoutError("Total stream duration exceeded timeout budget.")
            decoded = raw_line.decode("utf-8", errors="ignore")
            raw_lines.append(decoded); line = decoded.strip()
            if not line or line.startswith(":") or not line.startswith("data:"): continue
            saw_sse_data = True; data_text = line[5:].strip()
            if not data_text: continue
            if data_text == "[DONE]": break
            try: event_obj = json.loads(data_text)
            except: continue
            text = extract_text_from_stream_event(event_obj)
            if not text: continue
            chunks.append(text); buffered_for_progress.append(text)
            now = time.time()
            if progress_callback and (now - last_progress_emit_at) >= LLM_STREAM_PROGRESS_MIN_INTERVAL_SECONDS:
                progress_callback("stream_chunk", "Receiving model stream...", "".join(buffered_for_progress))
                buffered_for_progress.clear(); last_progress_emit_at = now
    if not saw_sse_data:
        raw_text = "".join(raw_lines).strip()
        if raw_text:
            try:
                fallback_response = json.loads(raw_text)
                content = extract_text_from_non_stream_response(fallback_response)
                if content.strip(): return content
            except: pass
        raise StreamUnsupportedError("Upstream endpoint does not provide SSE stream output.")
    if buffered_for_progress and progress_callback:
        progress_callback("stream_chunk", "Receiving model stream...", "".join(buffered_for_progress))
    content = "".join(chunks)
    if not content.strip(): raise RuntimeError("LLM stream returned empty content.")
    if progress_callback: progress_callback("stream_end", "Streaming response completed.", "")
    return content

def call_llm(messages: List[dict], config: dict, progress_callback=None, stop_event=None) -> Tuple[dict, str]:
    base_url = str(config["base_url"]).rstrip("/")
    url = f"{base_url}/chat/completions"
    payload = {"model": config["model"], "messages": list(messages), "temperature": 0.1}
    api_key = str(config.get("api_key", "")).strip()
    if not api_key: raise ValueError("Missing API key.")
    stream_enabled = parse_bool(config.get("llm_stream", config.get("stream", LLM_STREAM_ENABLED_DEFAULT)), LLM_STREAM_ENABLED_DEFAULT)
    content = ""
    if stream_enabled:
        try:
            content = execute_with_retries(lambda: request_llm_stream(url, payload, api_key, progress_callback, stop_event), mode="stream")
        except StreamUnsupportedError:
            if progress_callback: progress_callback("stream_fallback", "Streaming not supported by endpoint. Falling back to non-stream mode.", "")
    if not content:
        content = execute_with_retries(lambda: request_llm_non_stream(url, payload, api_key, stop_event), mode="non_stream")
    
    last_parse_error = None; original_msg_count = len(payload["messages"])
    for attempt in range(LLM_PARSE_MAX_RETRIES + 1):
        try:
            if "RESET" in content: content = content.split("RESET")[-1].strip()
            csv_match = re.search(r'<csv>(.*?)</csv>', content, re.DOTALL)
            if not csv_match: raise ValueError("missing <csv> tags.")
            schedule_raw = []
            reader = csv.DictReader(io.StringIO(csv_match.group(1).strip()))
            for row in reader:
                d_str = str(row.get("Date", "")).strip()
                ids = str(row.get("Assigned_IDs", "")).strip()
                if d_str and ids: schedule_raw.append({"date": d_str, "area_ids": {"默认区域": ids}, "note": str(row.get("Note", "")).strip()})
            
            def tag(t):
                m = re.search(f'<{t}>(.*?)</{t}>', content, re.DOTALL)
                return m.group(1).strip() if m else ""
            
            return {"schedule": schedule_raw, "next_run_note": tag("next_run_note"), "new_debt_ids": tag("new_debt_ids"), "new_credit_ids": tag("new_credit_ids")}, content
        except Exception as e:
            last_parse_error = e
            if attempt < LLM_PARSE_MAX_RETRIES:
                if progress_callback: progress_callback("parse_retry", f"Retry {attempt+1}...", "")
                del payload["messages"][original_msg_count:]
                payload["messages"].extend([{"role": "assistant", "content": content}, {"role": "user", "content": f"Fix error: {e}"}])
                content = execute_with_retries(lambda: request_llm_non_stream(url, payload, api_key), mode="non_stream")
    raise RuntimeError(f"Parse failed: {last_parse_error}")

def normalize_multi_area_schedule_ids(schedule_raw: list, active_ids: List[int], area_names: List[str], area_per_day_counts: Dict[str, int]) -> List[dict]:
    active_set = set(active_ids); normalized: List[dict] = []
    if not isinstance(schedule_raw, list): return []
    for entry in schedule_raw:
        if not isinstance(entry, dict): continue
        raw_date = str(entry.get("date", "")).strip()
        if len(raw_date) < 8: continue
        day_assignments: Dict[str, List[int]] = {}; used_ids: set = set()
        for area_index, area_name in enumerate(area_names):
            per_area_count = area_per_day_counts.get(area_name, DEFAULT_PER_DAY)
            extracted_ids = extract_area_ids(entry, area_name, area_index, active_set, per_area_count)
            final_ids = [pid for pid in extracted_ids if pid not in used_ids]
            day_assignments[area_name] = final_ids; used_ids.update(final_ids)
        raw_area_ids = entry.get("area_ids") or entry.get("areas") or entry.get("area_assignments") or {}
        if isinstance(raw_area_ids, dict):
            for dynamic_area_name, raw_value in raw_area_ids.items():
                name = str(dynamic_area_name).strip()
                if not name or name in day_assignments: continue
                extracted = extract_ids_from_value(raw_value, active_set, None)
                final = [pid for pid in extracted if pid not in used_ids]
                if final: day_assignments[name] = final; used_ids.update(final)
        normalized.append({"date": raw_date, "area_ids": day_assignments, "note": str(entry.get("note", "")).strip()})
    return normalized

def extract_area_ids(entry, area_name, area_index, active_set, per_area_count):
    result = []
    def append_ids(value):
        nonlocal result
        if len(result) >= per_area_count: return
        for pid in extract_ids_from_value(value, active_set, per_area_count):
            if pid not in result:
                result.append(pid)
                if len(result) >= per_area_count: break
    for key in ("area_ids", "areas", "area_assignments"):
        m = entry.get(key)
        if isinstance(m, dict): append_ids(m.get(area_name)); append_ids(m.get(str(area_index)))
    return result

def restore_schedule(normalized_ids: List[dict], id_to_name: Dict[int, str], area_names: List[str], existing_notes: Optional[Dict[str, str]] = None) -> List[dict]:
    restored = []; existing_notes = existing_notes or {}
    for day_entry in normalized_ids:
        date_str = day_entry.get("date", "")
        try: d = datetime.strptime(date_str, "%Y-%m-%d").date()
        except: continue
        area_ids_map = day_entry.get("area_ids", {})
        area_assignments: Dict[str, List[str]] = {}
        for name in area_names:
            area_assignments[name] = [id_to_name[pid] for pid in area_ids_map.get(name, []) if pid in id_to_name]
        for name, ids in area_ids_map.items():
            if name not in area_assignments:
                students = [id_to_name[pid] for pid in ids if pid in id_to_name]
                if students: area_assignments[name] = students
        note = str(day_entry.get("note", "")).strip() or existing_notes.get(date_str, "")
        restored.append({"date": date_str, "day": DAY_NAMES[d.weekday()], "area_assignments": area_assignments, "note": note})
    return restored

def merge_schedule_pool(state_data: dict, restored: List[dict], apply_mode: str, start_date: date) -> List[dict]:
    pool = [x for x in state_data.get("schedule_pool", []) if isinstance(x, dict)]
    restored = [x for x in restored if isinstance(x, dict)]
    if apply_mode == "append": return dedupe_pool_by_date(pool + restored)
    if apply_mode == "replace_all": return dedupe_pool_by_date(restored)
    if apply_mode == "replace_future":
        kept = [e for e in pool if (d:=try_parse_iso_date(e.get("date"))) is None or d < start_date]
        return dedupe_pool_by_date(kept + restored)
    if apply_mode == "replace_overlap":
        dates = [d for e in restored if (d:=try_parse_iso_date(e.get("date"))) is not None]
        if not dates: return dedupe_pool_by_date(pool + restored)
        end_date = max(dates)
        kept = [e for e in pool if (d:=try_parse_iso_date(e.get("date"))) is None or d < start_date or d > end_date]
        return dedupe_pool_by_date(kept + restored)
    return dedupe_pool_by_date(pool + restored)

def dedupe_pool_by_date(entries):
    by_date = {str(e.get("date", "")).strip(): e for e in entries if str(e.get("date", "")).strip()}
    return [by_date[d] for d in sorted(by_date.keys())]

def try_parse_iso_date(v):
    try: return datetime.strptime(str(v or "").strip(), "%Y-%m-%d").date()
    except: return None

def validate_llm_schedule_entries(schedule_raw):
    if not isinstance(schedule_raw, list): raise ValueError("schedule must be a list.")
    prev_date = None; seen = set()
    for idx, e in enumerate(schedule_raw):
        d_str = str(e.get("date", "")).strip()
        if not d_str: raise ValueError(f"entry {idx} missing date.")
        try: curr = datetime.strptime(d_str, "%Y-%m-%d").date()
        except: raise ValueError(f"entry {idx} invalid date {d_str}.")
        if prev_date and curr < prev_date: raise ValueError("dates must be sorted.")
        if curr in seen: raise ValueError(f"duplicate date {d_str} at {idx}.")
        prev_date = curr; seen.add(curr)

def merge_input_config(input_data: dict) -> dict:
    if not isinstance(input_data, dict): return {}
    merged = dict(input_data.get("config", {})); merged.update(input_data); return merged

def run_duty_agent(ctx: Context, input_data: dict, emit_progress_fn=None, stop_event=None):
    state_lock_path = ctx.paths["state"].with_suffix(ctx.paths["state"].suffix + ".lock")
    state_lock_acquired = False
    try:
        if stop_event and stop_event.is_set(): raise InterruptedError("Cancelled.")
        run_now = datetime.now(); ctx.config = load_config(ctx)
        name_to_id, id_to_name, all_ids, id_to_active = load_roster(ctx.paths["roster"])
        acquire_state_file_lock(state_lock_path); state_lock_acquired = True
        state_data = load_state(ctx.paths["state"])
        input_data = merge_input_config(input_data)
        instruction = str(input_data.get("instruction", "按照要求排班")).strip()
        
        base_url = str(input_data.get("base_url", ctx.config.get("base_url", ""))).strip()
        model = str(input_data.get("model", ctx.config.get("model", ""))).strip()
        api_key = str(input_data.get("api_key", "")).strip() or str(ctx.config.get("api_key", "")).strip() or load_api_key_from_env()
        if not base_url or not model or not api_key: raise ValueError("Missing config/api_key.")
        
        ctx.config.update({"base_url": base_url, "model": model, "api_key": api_key})
        ctx.config["llm_stream"] = parse_bool(input_data.get("llm_stream", ctx.config.get("llm_stream", LLM_STREAM_ENABLED_DEFAULT)), LLM_STREAM_ENABLED_DEFAULT)
        
        area_names = normalize_area_names(input_data.get("area_names", ctx.config.get("area_names", [])))
        area_per_day_counts = normalize_area_per_day_counts(area_names, input_data.get("area_per_day_counts", ctx.config.get("area_per_day_counts", {})), parse_int(input_data.get("per_day"), DEFAULT_PER_DAY))
        
        apply_mode = str(input_data.get("apply_mode", "append")).lower(); prompt_mode = str(input_data.get("prompt_mode", "Regular"))
        
        entries = get_pool_entries_with_date(state_data)
        start_date = (entries[-1][1] + timedelta(days=1)) if apply_mode == "append" and entries else run_now.date()
        
        from build_prompt import build_prompt_messages
        messages = build_prompt_messages(all_ids=all_ids, id_to_active=id_to_active, current_time=run_now.strftime("%Y-%m-%d %H:%M"), instruction=anonymize_instruction(instruction, name_to_id), duty_rule=anonymize_instruction(str(input_data.get("duty_rule", "")), name_to_id), area_names=area_names, debt_list=extract_ids_from_value(state_data.get("debt_list", []), set(all_ids)), credit_list=extract_ids_from_value(state_data.get("credit_list", []), set(all_ids)), previous_context=str(state_data.get("next_run_note", "")).strip(), prompt_mode=prompt_mode)
        
        llm_result, llm_text = call_llm(messages, ctx.config, progress_callback=emit_progress_fn, stop_event=stop_event)
        validate_llm_schedule_entries(llm_result.get("schedule", []))
        
        state_data["next_run_note"] = str(llm_result.get("next_run_note", "")).strip()
        normalized_ids = normalize_multi_area_schedule_ids(llm_result.get("schedule", []), all_ids, area_names, area_per_day_counts)
        state_data["debt_list"] = recover_missing_debts(extract_ids_from_value(state_data.get("debt_list", []), set(all_ids)), extract_ids_from_value(llm_result.get("new_debt_ids", []), set(all_ids)), normalized_ids)
        state_data["credit_list"] = reconcile_credit_list(extract_ids_from_value(state_data.get("credit_list", []), set(all_ids)), extract_ids_from_value(llm_result.get("new_credit_ids", []), set(all_ids)), normalized_ids, set(all_ids), state_data["debt_list"], "new_credit_ids" in llm_result)
        
        restored = restore_schedule(normalized_ids, id_to_name, area_names, input_data.get("existing_notes", {}))
        if not restored: raise ValueError("No valid schedule entries.")
        state_data["schedule_pool"] = merge_schedule_pool(state_data, restored, apply_mode, start_date)
        save_json_atomic(ctx.paths["state"], state_data)
        return {"status": "success", "ai_response": llm_text[:AI_RESPONSE_MAX_CHARS]}
    except Exception as e:
        traceback.print_exc(); return {"status": "error", "message": str(e)}
    finally:
        if state_lock_acquired: release_state_file_lock(state_lock_path)
