from __future__ import annotations

import asyncio
import json
import threading
import uuid
from typing import Any

import httpx
from fastapi import FastAPI
from starlette.testclient import TestClient
from starlette.websockets import WebSocketDisconnect


class DutyLoopbackError(RuntimeError):
    pass


class DutyLoopbackBusyError(DutyLoopbackError):
    pass


class DutyLoopbackRecoverableWebSocketError(DutyLoopbackError):
    pass


def _redact_api_keys(value: Any) -> Any:
    if isinstance(value, dict):
        redacted: dict[str, Any] = {}
        for key, item in value.items():
            if str(key) == "api_key":
                redacted[key] = ""
            else:
                redacted[key] = _redact_api_keys(item)
        return redacted
    if isinstance(value, list):
        return [_redact_api_keys(item) for item in value]
    return value


def _normalize_schedule_result(payload: dict[str, Any] | None) -> dict[str, Any]:
    result = dict(payload or {})
    result.pop("type", None)
    result.pop("client_change_id", None)
    result.pop("request_source", None)
    return dict(_redact_api_keys(result))


def _extract_response_detail(response: httpx.Response) -> str:
    try:
        payload = response.json()
    except Exception:
        return response.text.strip() or f"HTTP {response.status_code}"

    if isinstance(payload, dict):
        detail = payload.get("detail")
        if isinstance(detail, str) and detail.strip():
            return detail.strip()
        message = payload.get("message")
        if isinstance(message, str) and message.strip():
            return message.strip()
    return response.text.strip() or f"HTTP {response.status_code}"


def _is_busy_message(message: str) -> bool:
    normalized = str(message or "").strip().lower()
    return "busy" in normalized or "already connected" in normalized or "owner" in normalized


def _is_recoverable_socket_exception(ex: Exception) -> bool:
    return isinstance(ex, (WebSocketDisconnect, RuntimeError, ConnectionError, OSError))


class DutyLoopbackClient:
    def __init__(self, app: FastAPI, bearer_token: str, trace_id: str, request_source: str = "mcp"):
        self._app = app
        self._bearer_token = str(bearer_token or "").strip()
        self._trace_id = str(trace_id or "").strip()
        self._request_source = str(request_source or "").strip() or "mcp"
        if not self._bearer_token:
            raise DutyLoopbackError("Bearer token is unavailable for MCP loopback.")

    def _headers(self) -> dict[str, str]:
        return {
            "Authorization": f"Bearer {self._bearer_token}",
            "X-Duty-Trace-Id": self._trace_id,
            "X-Duty-Request-Source": self._request_source,
        }

    async def _request_json(
        self,
        method: str,
        path: str,
        payload: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        transport = httpx.ASGITransport(app=self._app)
        async with httpx.AsyncClient(transport=transport, base_url="http://duty-loopback") as client:
            response = await client.request(method.upper(), path, json=payload, headers=self._headers())

        if response.status_code >= 400:
            raise DutyLoopbackError(f"{method.upper()} {path} failed: {_extract_response_detail(response)}")

        return dict(_redact_api_keys(response.json() or {}))

    async def inspect_workspace(self) -> dict[str, Any]:
        transport = httpx.ASGITransport(app=self._app)
        async with httpx.AsyncClient(transport=transport, base_url="http://duty-loopback") as client:
            engine_info_response = await client.get("/engine/info", headers=self._headers())
            snapshot_response = await client.get("/api/v1/snapshot", headers=self._headers())

        if engine_info_response.status_code >= 400:
            raise DutyLoopbackError(
                f"GET /engine/info failed: {_extract_response_detail(engine_info_response)}"
            )
        if snapshot_response.status_code >= 400:
            raise DutyLoopbackError(
                f"GET /api/v1/snapshot failed: {_extract_response_detail(snapshot_response)}"
            )

        return {
            "engine_info": dict(engine_info_response.json() or {}),
            "snapshot": dict(_redact_api_keys(snapshot_response.json() or {})),
        }

    async def update_scheduler_config(self, payload: dict[str, Any]) -> dict[str, Any]:
        return await self._request_json("PATCH", "/api/v1/config", payload)

    async def replace_roster(self, roster: list[dict[str, Any]]) -> dict[str, Any]:
        return await self._request_json("PUT", "/api/v1/roster", {"roster": list(roster or [])})

    async def edit_schedule_entry(self, payload: dict[str, Any]) -> dict[str, Any]:
        return await self._request_json("POST", "/api/v1/duty/schedule-entry", payload)

    async def run_schedule(
        self,
        instruction: str,
        progress_callback=None,
    ) -> dict[str, Any]:
        loop = asyncio.get_running_loop()
        queue: asyncio.Queue[dict[str, Any]] = asyncio.Queue()

        def emit(item: dict[str, Any]) -> None:
            loop.call_soon_threadsafe(queue.put_nowait, item)

        def worker() -> None:
            try:
                result = self._run_schedule_via_websocket_sync(
                    instruction=instruction,
                    progress_callback=emit,
                )
                emit({"type": "done", "data": result})
            except DutyLoopbackBusyError as ex:
                emit({"type": "busy", "message": str(ex)})
            except DutyLoopbackRecoverableWebSocketError:
                emit({"type": "fallback"})
            except Exception as ex:
                emit({"type": "error", "message": str(ex)})

        threading.Thread(target=worker, daemon=True, name="McpLoopbackWs").start()

        while True:
            item = await queue.get()
            item_type = str(item.get("type") or "").strip().lower()
            if item_type == "progress":
                if progress_callback:
                    await progress_callback(item)
                continue
            if item_type == "done":
                return dict(item.get("data") or {})
            if item_type == "fallback":
                return await self._run_schedule_via_sse(instruction, progress_callback)
            if item_type == "busy":
                raise DutyLoopbackBusyError(str(item.get("message") or "Duty live channel is busy."))
            if item_type == "error":
                raise DutyLoopbackError(str(item.get("message") or "Unknown MCP loopback error."))

    def _run_schedule_via_websocket_sync(
        self,
        instruction: str,
        progress_callback,
    ) -> dict[str, Any]:
        client_change_id = uuid.uuid4().hex
        headers = self._headers()
        socket_ready = False
        client = TestClient(self._app)
        try:
            with client.websocket_connect("/api/v1/duty/live", headers=headers) as websocket:
                websocket.send_json(
                    {
                        "type": "hello",
                        "trace_id": self._trace_id,
                        "request_source": self._request_source,
                    }
                )
                hello_message = websocket.receive_json()
                hello_type = str((hello_message or {}).get("type") or "").strip().lower()
                if hello_type == "error":
                    message = str((hello_message or {}).get("message") or "Duty live channel is busy.")
                    if _is_busy_message(message):
                        raise DutyLoopbackBusyError(message)
                    raise DutyLoopbackError(message)
                if hello_type != "hello":
                    raise DutyLoopbackError("Duty live handshake returned an unexpected message.")

                socket_ready = True
                websocket.send_json(
                    {
                        "type": "schedule_run",
                        "client_change_id": client_change_id,
                        "trace_id": self._trace_id,
                        "request_source": self._request_source,
                        "instruction": str(instruction or ""),
                    }
                )

                while True:
                    message = websocket.receive_json()
                    message_type = str((message or {}).get("type") or "").strip().lower()
                    message_change_id = str((message or {}).get("client_change_id") or "").strip()
                    if message_change_id and message_change_id != client_change_id:
                        continue

                    if message_type in {"accepted", "hello"}:
                        continue
                    if message_type == "schedule_progress":
                        progress_callback(
                            {
                                "type": "progress",
                                "phase": str((message or {}).get("phase") or "").strip(),
                                "message": str((message or {}).get("message") or "").strip(),
                                "stream_chunk": (message or {}).get("stream_chunk"),
                            }
                        )
                        continue
                    if message_type == "schedule_complete":
                        return _normalize_schedule_result(message)
                    if message_type == "error":
                        error_message = str((message or {}).get("message") or "Unknown error.").strip()
                        if _is_busy_message(error_message):
                            raise DutyLoopbackBusyError(error_message)
                        raise DutyLoopbackError(error_message)
        except DutyLoopbackBusyError:
            raise
        except Exception as ex:
            if socket_ready and _is_recoverable_socket_exception(ex):
                raise DutyLoopbackRecoverableWebSocketError(str(ex)) from ex
            raise
        finally:
            client.close()

        raise DutyLoopbackError("Duty live channel closed before a final result was received.")

    async def _run_schedule_via_sse(
        self,
        instruction: str,
        progress_callback=None,
    ) -> dict[str, Any]:
        payload = {
            "instruction": str(instruction or ""),
            "trace_id": self._trace_id,
            "request_source": self._request_source,
        }
        transport = httpx.ASGITransport(app=self._app)
        async with httpx.AsyncClient(transport=transport, base_url="http://duty-loopback") as client:
            async with client.stream(
                "POST",
                "/api/v1/duty/schedule",
                json=payload,
                headers=self._headers(),
            ) as response:
                if response.status_code >= 400:
                    body_text = await response.aread()
                    detail = body_text.decode("utf-8", errors="ignore").strip() or _extract_response_detail(response)
                    raise DutyLoopbackError(f"POST /api/v1/duty/schedule failed: {detail}")

                event_name = "message"
                data_lines: list[str] = []
                async for line in response.aiter_lines():
                    if line is None:
                        continue
                    if line == "":
                        if not data_lines:
                            event_name = "message"
                            continue
                        data_text = "\n".join(data_lines)
                        data_lines.clear()
                        event_name = event_name or "message"
                        payload_obj = json.loads(data_text)
                        if event_name == "complete":
                            return dict(payload_obj or {})
                        if progress_callback:
                            await progress_callback(
                                {
                                    "type": "progress",
                                    "phase": str((payload_obj or {}).get("phase") or "").strip(),
                                    "message": str((payload_obj or {}).get("message") or "").strip(),
                                    "stream_chunk": (payload_obj or {}).get("stream_chunk"),
                                }
                            )
                        event_name = "message"
                        continue
                    if line.startswith("event:"):
                        event_name = line[6:].strip() or "message"
                        continue
                    if line.startswith("data:"):
                        data_lines.append(line[5:].strip())

        raise DutyLoopbackError("Schedule SSE stream ended without a completion event.")
