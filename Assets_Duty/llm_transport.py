from __future__ import annotations

import csv
import io
import json
import re
import socket
import ssl
import threading
import time
import urllib.error
import urllib.request
from datetime import date, timedelta
from urllib.parse import urlparse
from typing import Any, Callable, Dict, List, Optional, Tuple

from state_ops import DEFAULT_SINGLE_AREA_NAME

LLM_TIMEOUT_SECONDS = 120
LLM_MAX_RETRIES = 2
LLM_RETRY_BACKOFF_SECONDS = 2
LLM_STREAM_ENABLED_DEFAULT = True
LLM_STREAM_PROGRESS_MIN_INTERVAL_SECONDS = 0.2
_REASONING_BLOCK_PATTERN = re.compile(
    r"<(?P<tag>think|thinking|reasoning|analysis)\b[^>]*>.*?</(?P=tag)>",
    re.DOTALL | re.IGNORECASE,
)
_SECTION_HEADER_PATTERN = re.compile(r"^\[(?P<name>areas|schedule|state)\]$", re.IGNORECASE)
_AREA_ALIAS_PATTERN = re.compile(r"^(?P<alias>[A-Z][A-Z0-9]*)\s*=\s*(?P<name>.+?)\s*$")
_SCHEDULE_DATE_PATTERN = re.compile(r"^(?P<month>\d{2})-(?P<day>\d{2})$")
_COUNT_TOKEN_PATTERN = re.compile(r"^(?P<id>\d+)(?:\*(?P<count>\d+))?$")


class StreamUnsupportedError(RuntimeError):
    pass


def _is_loopback_host(host: str) -> bool:
    normalized = (host or "").strip().lower()
    return normalized in {"localhost", "127.0.0.1", "::1", "0.0.0.0"}


def _validate_auth_context(base_url: str, api_key: str) -> None:
    if str(api_key or "").strip():
        return

    parsed = urlparse(str(base_url or ""))
    if parsed.scheme.lower() != "https":
        return

    if _is_loopback_host(parsed.hostname or ""):
        raise ValueError(
            "HTTPS local endpoint with empty API key is not supported. "
            "For Ollama use http://127.0.0.1:11434/v1."
        )

    raise ValueError(
        "API key is empty for an HTTPS endpoint. "
        "Provide an API key, or switch to local Ollama endpoint http://127.0.0.1:11434/v1."
    )


def _is_ssl_eof_error(ex: Exception) -> bool:
    error_text = str(ex)
    if "UNEXPECTED_EOF_WHILE_READING" in error_text:
        return True
    if "EOF occurred in violation of protocol" in error_text:
        return True

    reason = getattr(ex, "reason", None)
    reason_text = str(reason)
    if "UNEXPECTED_EOF_WHILE_READING" in reason_text:
        return True
    if "EOF occurred in violation of protocol" in reason_text:
        return True
    return isinstance(reason, ssl.SSLError) and "EOF" in reason_text


def create_llm_request(url: str, payload: dict, api_key: str) -> urllib.request.Request:
    data = json.dumps(payload).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    normalized_api_key = str(api_key or "").strip()
    if normalized_api_key:
        headers["Authorization"] = f"Bearer {normalized_api_key}"
    return urllib.request.Request(
        url=url,
        data=data,
        method="POST",
        headers=headers,
    )


def extract_text_content(content: Any) -> str:
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts: List[str] = []
        for item in content:
            if isinstance(item, str):
                parts.append(item)
            elif isinstance(item, dict):
                text_value = item.get("text") or item.get("content")
                if isinstance(text_value, str):
                    parts.append(text_value)
        return "".join(parts)
    return ""


def extract_text_from_non_stream_response(response: dict) -> str:
    choices = response.get("choices", [])
    if not choices:
        return ""
    choice = choices[0]
    return extract_text_content(choice.get("message", {}).get("content")) or extract_text_content(choice.get("text"))


def extract_text_from_stream_event(event_obj: dict) -> str:
    choices = event_obj.get("choices", [])
    if not choices:
        return ""
    choice = choices[0]
    return (
        extract_text_content(choice.get("delta", {}).get("content"))
        or extract_text_content(choice.get("message", {}).get("content"))
        or extract_text_content(choice.get("text"))
    )


def execute_with_retries(request_fn: Callable[[], str], mode: str) -> str:
    last_error: Optional[Exception] = None
    for attempt in range(LLM_MAX_RETRIES + 1):
        try:
            return request_fn()
        except StreamUnsupportedError:
            raise
        except urllib.error.HTTPError as ex:
            detail = ex.read().decode("utf-8", errors="ignore")
            if (ex.code == 429 or 500 <= ex.code < 600) and attempt < LLM_MAX_RETRIES:
                time.sleep(LLM_RETRY_BACKOFF_SECONDS * (attempt + 1))
                continue
            if mode == "stream" and ex.code in (400, 404, 405, 415, 422, 426, 501):
                raise StreamUnsupportedError(
                    f"Streaming request is not supported by upstream (HTTP {ex.code})."
                ) from ex
            raise RuntimeError(f"HTTP error {ex.code}: {detail}") from ex
        except (urllib.error.URLError, TimeoutError, socket.timeout, ConnectionError) as ex:
            if _is_ssl_eof_error(ex):
                raise RuntimeError(
                    "SSL handshake failed (UNEXPECTED_EOF_WHILE_READING). "
                    "If you are using Ollama, set Base URL to http://127.0.0.1:11434/v1 (not https)."
                ) from ex
            last_error = ex
            if attempt < LLM_MAX_RETRIES:
                time.sleep(LLM_RETRY_BACKOFF_SECONDS * (attempt + 1))
                continue
            raise RuntimeError(f"Network error: {ex}") from ex
    raise RuntimeError(f"LLM request failed after retries: {last_error}")


def request_llm_non_stream(url: str, payload: dict, api_key: str, stop_event: Optional[threading.Event] = None) -> str:
    if stop_event and stop_event.is_set():
        raise InterruptedError("Cancelled.")
    request = create_llm_request(url, payload, api_key)
    response = urllib.request.urlopen(request, timeout=LLM_TIMEOUT_SECONDS)
    try:
        parts: List[bytes] = []
        while True:
            if stop_event and stop_event.is_set():
                response.close()
                raise InterruptedError("Cancelled during LLM request.")
            chunk = response.read(4096)
            if not chunk:
                break
            parts.append(chunk)
        raw = b"".join(parts).decode("utf-8")
    finally:
        response.close()
    parsed = json.loads(raw)
    content = extract_text_from_non_stream_response(parsed)
    if not content.strip():
        raise RuntimeError("LLM returned empty content.")
    return content


def request_llm_stream(url: str, payload: dict, api_key: str, progress_callback=None, stop_event=None) -> str:
    stream_payload = dict(payload)
    stream_payload["stream"] = True
    request = create_llm_request(url, stream_payload, api_key)
    deadline = time.time() + (LLM_TIMEOUT_SECONDS * 3)
    chunks: List[str] = []
    buffered_for_progress: List[str] = []
    raw_lines: List[str] = []
    saw_sse_data = False
    last_progress_emit_at = time.time()

    if progress_callback:
        progress_callback("stream_start", "Streaming response opened.", "")

    with urllib.request.urlopen(request, timeout=LLM_TIMEOUT_SECONDS) as response:
        for raw_line in response:
            if stop_event and stop_event.is_set():
                raise InterruptedError("Cancelled.")
            if time.time() > deadline:
                raise TimeoutError("Total stream duration exceeded timeout budget.")

            decoded = raw_line.decode("utf-8", errors="ignore")
            raw_lines.append(decoded)
            line = decoded.strip()
            if not line or line.startswith(":") or not line.startswith("data:"):
                continue

            saw_sse_data = True
            data_text = line[5:].strip()
            if not data_text:
                continue
            if data_text == "[DONE]":
                break

            try:
                event_obj = json.loads(data_text)
            except Exception:
                continue

            text = extract_text_from_stream_event(event_obj)
            if not text:
                continue

            chunks.append(text)
            buffered_for_progress.append(text)
            now = time.time()
            if progress_callback and (now - last_progress_emit_at) >= LLM_STREAM_PROGRESS_MIN_INTERVAL_SECONDS:
                progress_callback("stream_chunk", "Receiving model stream...", "".join(buffered_for_progress))
                buffered_for_progress.clear()
                last_progress_emit_at = now

    if not saw_sse_data:
        raw_text = "".join(raw_lines).strip()
        if raw_text:
            try:
                fallback_response = json.loads(raw_text)
                content = extract_text_from_non_stream_response(fallback_response)
                if content.strip():
                    return content
            except Exception:
                pass
        raise StreamUnsupportedError("Upstream endpoint does not provide SSE stream output.")

    if buffered_for_progress and progress_callback:
        progress_callback("stream_chunk", "Receiving model stream...", "".join(buffered_for_progress))

    content = "".join(chunks)
    if not content.strip():
        raise RuntimeError("LLM stream returned empty content.")
    if progress_callback:
        progress_callback("stream_end", "Streaming response completed.", "")
    return content


def _build_llm_target(config: dict, messages: List[dict], transport_overrides: Optional[dict] = None) -> Tuple[str, dict, str]:
    base_url = str(config["base_url"]).rstrip("/")
    if base_url.lower().endswith("/chat/completions"):
        url = base_url
    elif base_url.lower().endswith("/v1"):
        url = f"{base_url}/chat/completions"
    else:
        url = f"{base_url}/v1/chat/completions"
    payload = {
        "model": config["model"],
        "messages": list(messages),
        "temperature": 0.1,
    }
    if transport_overrides:
        payload.update({key: value for key, value in transport_overrides.items() if value is not None})
    api_key = str(config.get("api_key", "")).strip()
    _validate_auth_context(base_url, api_key)
    return url, payload, api_key


def call_llm_raw(
    messages: List[dict],
    config: dict,
    progress_callback=None,
    stop_event=None,
    transport_overrides: Optional[dict] = None,
) -> str:
    url, payload, api_key = _build_llm_target(config, messages, transport_overrides)

    stream_enabled = _parse_bool(
        config.get("llm_stream", config.get("stream", LLM_STREAM_ENABLED_DEFAULT)),
        LLM_STREAM_ENABLED_DEFAULT,
    )
    content = ""
    if stream_enabled:
        try:
            content = execute_with_retries(
                lambda: request_llm_stream(url, payload, api_key, progress_callback, stop_event),
                mode="stream",
            )
        except StreamUnsupportedError:
            if progress_callback:
                progress_callback(
                    "stream_fallback",
                    "Streaming not supported by endpoint. Falling back to non-stream mode.",
                    "",
                )
    if not content:
        content = execute_with_retries(
            lambda: request_llm_non_stream(url, payload, api_key, stop_event),
            mode="non_stream",
        )
    return content


def _extract_fenced_block(content: str, language: Optional[str] = None) -> str:
    text = str(content or "")
    if not text.strip():
        return ""

    language_pattern = re.escape(language) if language else r"[a-zA-Z0-9_-]*"
    matches = re.findall(
        rf"```(?:{language_pattern})?\s*(.*?)```",
        text,
        re.DOTALL | re.IGNORECASE,
    )
    for fragment in reversed(matches):
        candidate = str(fragment or "").strip()
        if candidate:
            return candidate
    return ""


def _extract_tag_content(content: str, tag_name: str) -> str:
    text = str(content or "")
    if not text.strip():
        return ""

    matches = re.findall(
        rf"<{re.escape(tag_name)}\b[^>]*>(.*?)</{re.escape(tag_name)}>",
        text,
        re.DOTALL | re.IGNORECASE,
    )
    for fragment in reversed(matches):
        candidate = str(fragment or "").strip()
        if candidate:
            return candidate
    return ""


def _normalize_structured_output(content: str) -> str:
    text = str(content or "").replace("\ufeff", "").strip()
    if not text:
        return ""

    stripped = text
    while True:
        updated = _REASONING_BLOCK_PATTERN.sub("", stripped)
        if updated == stripped:
            break
        stripped = updated
    return stripped.strip()


def _extract_json_candidate(content: str) -> dict:
    text = _normalize_structured_output(content)
    if not text:
        raise ValueError("empty JSON content")

    candidates: List[str] = [text]
    json_block = _extract_tag_content(text, "json")
    if json_block:
        candidates.append(json_block)

    fenced_json = _extract_fenced_block(text, "json")
    if fenced_json:
        candidates.append(fenced_json)

    fenced_any = _extract_fenced_block(text)
    if fenced_any:
        candidates.append(fenced_any)

    decoder = json.JSONDecoder()
    seen_candidates = set()

    for candidate in candidates:
        if not candidate or candidate in seen_candidates:
            continue
        seen_candidates.add(candidate)
        try:
            parsed = json.loads(candidate)
        except Exception:
            continue
        if isinstance(parsed, dict):
            return parsed

    for match in re.finditer(r"{", text):
        try:
            parsed, _ = decoder.raw_decode(text, match.start())
        except Exception:
            continue
        if isinstance(parsed, dict):
            return parsed
    raise ValueError("unable to locate valid JSON object")


def _parse_schedule_csv(csv_text: str) -> List[dict]:
    reader = csv.DictReader(io.StringIO(csv_text.strip()))
    raw_fieldnames = reader.fieldnames or []
    fieldnames = [str(name or "").strip() for name in raw_fieldnames]
    if fieldnames != ["Date", "Assigned_IDs", "Note"]:
        raise ValueError("legacy CSV fallback must use Date,Assigned_IDs,Note header")

    schedule_raw: List[dict] = []
    for row in reader:
        date_str = str(row.get("Date", "")).strip()
        if not date_str:
            continue

        assigned_ids = str(row.get("Assigned_IDs", "")).strip()
        area_ids: Dict[str, str] = {}
        if assigned_ids:
            area_ids[DEFAULT_SINGLE_AREA_NAME] = assigned_ids.replace(",", " ")

        schedule_raw.append(
            {
                "date": date_str,
                "area_ids": area_ids,
                "note": str(row.get("Note", "")).strip(),
            }
        )

    return schedule_raw


def _split_sections(content: str) -> Dict[str, List[str]]:
    text = _normalize_structured_output(content)
    if not text:
        raise ValueError("empty structured output")

    blocks: List[Dict[str, List[str]]] = []
    sections: Optional[Dict[str, List[str]]] = None
    current_section: Optional[str] = None
    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line:
            continue
        if line.startswith("#") or line.startswith(";"):
            continue
        header = _SECTION_HEADER_PATTERN.fullmatch(line)
        if header:
            next_section = header.group("name").lower()
            if next_section == "areas" or sections is None:
                sections = {}
                blocks.append(sections)
            current_section = next_section
            sections.setdefault(current_section, [])
            continue
        if current_section is None:
            continue
        sections[current_section].append(raw_line.rstrip())

    selected = next(
        (block for block in reversed(blocks) if "areas" in block and "schedule" in block),
        None,
    )
    if selected is None:
        raise ValueError("missing required [areas] or [schedule] section")
    return selected


def _parse_areas_section(lines: List[str]) -> Dict[str, str]:
    alias_map: Dict[str, str] = {}
    for raw_line in lines:
        line = str(raw_line or "").strip()
        if not line or line.startswith("#") or line.startswith(";"):
            continue
        match = _AREA_ALIAS_PATTERN.fullmatch(line)
        if not match:
            raise ValueError(f"invalid [areas] line: {line}")
        alias = match.group("alias").strip()
        area_name = match.group("name").strip()
        if not area_name:
            raise ValueError(f"empty area name for alias {alias}")
        if alias.startswith("_"):
            raise ValueError(f"alias {alias} cannot start with underscore")
        alias_map[alias] = area_name
    if not alias_map:
        raise ValueError("[areas] is empty")
    return alias_map


def _resolve_mmdd(mmdd: str, start_date: date, previous_date: Optional[date]) -> date:
    match = _SCHEDULE_DATE_PATTERN.fullmatch(mmdd)
    if not match:
        raise ValueError(f"invalid MM-DD date: {mmdd}")

    month = int(match.group("month"))
    day = int(match.group("day"))
    candidate_year = previous_date.year if previous_date is not None else start_date.year

    while True:
        try:
            candidate = date(candidate_year, month, day)
        except ValueError as ex:
            raise ValueError(f"invalid MM-DD date: {mmdd}") from ex
        if previous_date is None:
            if candidate < start_date:
                candidate_year += 1
                continue
        elif candidate < previous_date:
            candidate_year += 1
            continue
        if candidate > start_date + timedelta(days=370):
            raise ValueError(f"date {mmdd} exceeds supported window")
        return candidate


def _parse_ids_space_separated(raw_value: str, *, date_text: str, alias: str) -> List[int]:
    result: List[int] = []
    seen: set[int] = set()
    for raw_token in str(raw_value or "").strip().split():
        try:
            person_id = int(raw_token)
        except ValueError as ex:
            raise ValueError(f"invalid ID '{raw_token}' on {date_text}/{alias}") from ex
        if person_id in seen:
            raise ValueError(f"duplicate ID {person_id} on {date_text}/{alias}")
        seen.add(person_id)
        result.append(person_id)
    return result


def _parse_schedule_section(lines: List[str], alias_map: Dict[str, str], start_date: date) -> List[dict]:
    schedule: List[dict] = []
    previous_date: Optional[date] = None
    seen_dates: set[date] = set()

    for raw_line in lines:
        line = str(raw_line or "").strip()
        if not line or line.startswith("#") or line.startswith(";"):
            continue
        if "=" not in line:
            raise ValueError(f"invalid [schedule] line: {line}")
        raw_mmdd, raw_body = line.split("=", 1)
        resolved_date = _resolve_mmdd(raw_mmdd.strip(), start_date, previous_date)
        if resolved_date in seen_dates:
            raise ValueError(f"duplicate date {resolved_date.isoformat()}")
        previous_date = resolved_date
        seen_dates.add(resolved_date)

        note = ""
        assignment_body = raw_body
        if "#" in raw_body:
            assignment_body, raw_note = raw_body.split("#", 1)
            note = raw_note.strip()

        area_ids: Dict[str, List[int]] = {}
        segments = [segment.strip() for segment in assignment_body.split("|") if segment.strip()]
        if not segments:
            raise ValueError(f"schedule line {raw_mmdd.strip()} has no assignments")

        for segment in segments:
            if ":" not in segment:
                raise ValueError(f"invalid assignment segment on {raw_mmdd.strip()}: {segment}")
            key, value = segment.split(":", 1)
            key = key.strip()
            value = value.strip()
            if key not in alias_map:
                raise ValueError(f"unknown area alias {key} on {raw_mmdd.strip()}")
            area_name = alias_map[key]
            area_ids[area_name] = _parse_ids_space_separated(
                value,
                date_text=resolved_date.isoformat(),
                alias=key,
            )

        schedule.append(
            {
                "date": resolved_date.isoformat(),
                "area_ids": area_ids,
                "note": note,
            }
        )

    if not schedule:
        raise ValueError("[schedule] is empty")
    return schedule


def _parse_count_tokens(raw_value: str, *, field_name: str) -> Dict[int, int]:
    counts: Dict[int, int] = {}
    if not raw_value.strip():
        return counts
    for token in raw_value.split():
        match = _COUNT_TOKEN_PATTERN.fullmatch(token)
        if not match:
            raise ValueError(f"invalid {field_name} token: {token}")
        person_id = int(match.group("id"))
        count = int(match.group("count") or "1")
        if person_id <= 0 or count <= 0:
            raise ValueError(f"invalid {field_name} token: {token}")
        counts[person_id] = counts.get(person_id, 0) + count
    return counts


def _parse_state_section(lines: List[str]) -> Dict[str, Any]:
    state_delta: Dict[str, Any] = {
        "debt_counts": {},
        "credit_counts": {},
    }
    for raw_line in lines:
        line = str(raw_line or "").strip()
        if not line or line.startswith("#") or line.startswith(";"):
            continue
        if "=" not in line:
            raise ValueError(f"invalid [state] line: {line}")
        key, value = line.split("=", 1)
        normalized_key = key.strip().lower().replace(" ", "_")
        if normalized_key in {"debt", "credit"}:
            target_key = f"{normalized_key}_counts"
            state_delta[target_key] = _parse_count_tokens(value.strip(), field_name=normalized_key)
        elif normalized_key == "pointer":
            try:
                state_delta["pointer_after"] = int(value.strip())
            except (TypeError, ValueError) as ex:
                raise ValueError(f"invalid pointer value: {value.strip()}") from ex
        elif normalized_key == "consumed_credit":
            ids: List[int] = []
            for token in value.strip().split():
                try:
                    ids.append(int(token))
                except (TypeError, ValueError) as ex:
                    raise ValueError(f"invalid consumed_credit ID: {token}") from ex
            state_delta["consumed_credit_ids"] = ids
        else:
            raise ValueError(f"unsupported [state] field: {normalized_key}")
    return state_delta


def call_llm_json(
    messages: List[dict],
    config: dict,
    validator: Optional[Callable[[dict], dict]] = None,
    stop_event=None,
    transport_overrides: Optional[dict] = None,
    max_parse_retries: int = 2,
) -> Tuple[dict, str]:
    url, payload, api_key = _build_llm_target(config, messages, transport_overrides)
    content = call_llm_raw(messages, config, None, stop_event, transport_overrides)
    original_message_count = len(payload["messages"])
    last_parse_error: Optional[Exception] = None

    for attempt in range(max_parse_retries + 1):
        try:
            parsed = _extract_json_candidate(content)
            if validator:
                parsed = validator(parsed)
            return parsed, content
        except Exception as ex:
            last_parse_error = ex
            if attempt >= max_parse_retries:
                break
            del payload["messages"][original_message_count:]
            payload["messages"].extend(
                [
                    {"role": "assistant", "content": content},
                    {
                        "role": "user",
                        "content": (
                            "Return ONLY one valid JSON object. "
                            f"Fix this error: {ex}"
                        ),
                    },
                ]
            )
            content = execute_with_retries(
                lambda: request_llm_non_stream(url, payload, api_key, stop_event),
                mode="non_stream",
            )

    raise RuntimeError(f"JSON parse failed: {last_parse_error}")


def call_llm(
    messages: List[dict],
    config: dict,
    progress_callback=None,
    stop_event=None,
    transport_overrides: Optional[dict] = None,
    *,
    start_date_value: date | str | None = None,
) -> Tuple[dict, str]:
    content = call_llm_raw(messages, config, progress_callback, stop_event, transport_overrides)
    normalized_content = _normalize_structured_output(content)

    try:
        if start_date_value is None:
            parsed_start = date.today()
        elif isinstance(start_date_value, date):
            parsed_start = start_date_value
        else:
            parsed_start = date.fromisoformat(str(start_date_value))

        sections = _split_sections(normalized_content)
        alias_map = _parse_areas_section(sections.get("areas", []))
        schedule = _parse_schedule_section(sections.get("schedule", []), alias_map, parsed_start)
        state_delta = _parse_state_section(sections.get("state", []))
        return {
            "schedule": schedule,
            "state_delta": state_delta,
        }, content
    except Exception as kv_error:
        csv_text = _extract_fenced_block(normalized_content, "csv")
        if not csv_text:
            legacy_csv = _extract_tag_content(normalized_content, "csv")
            csv_text = legacy_csv
        if csv_text:
            return {
                "schedule": _parse_schedule_csv(csv_text),
                "state_delta": {"debt_counts": {}, "credit_counts": {}},
            }, content
        raise RuntimeError(f"Parse failed: {kv_error}") from kv_error


def _parse_bool(value, default: bool) -> bool:
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
