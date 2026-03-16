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
from urllib.parse import urlparse
from typing import Any, Callable, Dict, List, Optional, Tuple

LLM_TIMEOUT_SECONDS = 120
LLM_MAX_RETRIES = 2
LLM_RETRY_BACKOFF_SECONDS = 2
LLM_STREAM_ENABLED_DEFAULT = True
LLM_PARSE_MAX_RETRIES = 1
LLM_STREAM_PROGRESS_MIN_INTERVAL_SECONDS = 0.2


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


def _extract_json_candidate(content: str) -> dict:
    text = (content or "").strip()
    if not text:
        raise ValueError("empty JSON content")

    candidates = [text]
    fenced = re.findall(r"```(?:json)?\s*(.*?)```", text, re.DOTALL | re.IGNORECASE)
    candidates.extend(fragment.strip() for fragment in fenced if fragment and fragment.strip())

    first_brace = text.find("{")
    last_brace = text.rfind("}")
    if first_brace >= 0 and last_brace > first_brace:
        candidates.append(text[first_brace:last_brace + 1].strip())

    for candidate in candidates:
        if not candidate:
            continue
        try:
            parsed = json.loads(candidate)
        except Exception:
            continue
        if isinstance(parsed, dict):
            return parsed
    raise ValueError("unable to locate valid JSON object")


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
) -> Tuple[dict, str]:
    url, payload, api_key = _build_llm_target(config, messages, transport_overrides)
    content = call_llm_raw(messages, config, progress_callback, stop_event, transport_overrides)

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
