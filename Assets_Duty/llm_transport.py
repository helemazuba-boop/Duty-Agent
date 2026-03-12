from __future__ import annotations

import csv
import io
import json
import re
import socket
import threading
import time
import urllib.error
import urllib.request
from typing import Any, Callable, Dict, List, Optional, Tuple

LLM_TIMEOUT_SECONDS = 120
LLM_MAX_RETRIES = 2
LLM_RETRY_BACKOFF_SECONDS = 2
LLM_STREAM_ENABLED_DEFAULT = True
LLM_PARSE_MAX_RETRIES = 1
LLM_STREAM_PROGRESS_MIN_INTERVAL_SECONDS = 0.2


class StreamUnsupportedError(RuntimeError):
    pass


def create_llm_request(url: str, payload: dict, api_key: str) -> urllib.request.Request:
    data = json.dumps(payload).encode("utf-8")
    return urllib.request.Request(
        url=url,
        data=data,
        method="POST",
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {api_key}"},
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
    with urllib.request.urlopen(request, timeout=LLM_TIMEOUT_SECONDS) as response:
        raw = response.read().decode("utf-8")
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


def call_llm(messages: List[dict], config: dict, progress_callback=None, stop_event=None) -> Tuple[dict, str]:
    base_url = str(config["base_url"]).rstrip("/")
    url = f"{base_url}/chat/completions"
    payload = {"model": config["model"], "messages": list(messages), "temperature": 0.1}
    api_key = str(config.get("api_key", "")).strip()
    if not api_key:
        raise ValueError("Missing API key.")

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

    last_parse_error = None
    original_message_count = len(payload["messages"])
    for attempt in range(LLM_PARSE_MAX_RETRIES + 1):
        try:
            if "RESET" in content:
                content = content.split("RESET")[-1].strip()
            csv_match = re.search(r"<csv>(.*?)</csv>", content, re.DOTALL)
            if not csv_match:
                raise ValueError("missing <csv> tags.")

            schedule_raw = []
            reader = csv.DictReader(io.StringIO(csv_match.group(1).strip()))
            for row in reader:
                date_str = str(row.get("Date", "")).strip()
                assigned_ids = str(row.get("Assigned_IDs", "")).strip()
                if date_str and assigned_ids:
                    schedule_raw.append(
                        {
                            "date": date_str,
                            "area_ids": {"default_area": assigned_ids},
                            "note": str(row.get("Note", "")).strip(),
                        }
                    )

            def extract_tag(tag_name: str) -> str:
                match = re.search(f"<{tag_name}>(.*?)</{tag_name}>", content, re.DOTALL)
                return match.group(1).strip() if match else ""

            return {
                "schedule": schedule_raw,
                "next_run_note": extract_tag("next_run_note"),
                "new_debt_ids": extract_tag("new_debt_ids"),
                "new_credit_ids": extract_tag("new_credit_ids"),
            }, content
        except Exception as ex:
            last_parse_error = ex
            if attempt < LLM_PARSE_MAX_RETRIES:
                if progress_callback:
                    progress_callback("parse_retry", f"Retry {attempt + 1}...", "")
                del payload["messages"][original_message_count:]
                payload["messages"].extend(
                    [
                        {"role": "assistant", "content": content},
                        {"role": "user", "content": f"Fix error: {ex}"},
                    ]
                )
                content = execute_with_retries(
                    lambda: request_llm_non_stream(url, payload, api_key),
                    mode="non_stream",
                )
    raise RuntimeError(f"Parse failed: {last_parse_error}")


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
