import json
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

from fastapi.testclient import TestClient

from auth import create_pbkdf2_sha256_verifier
from core import app
from mcp_server import MCP_SDK_IMPORT_ERROR
from runtime import create_runtime
from state_ops import Context, load_config, save_config


def _mcp_headers(runtime, extra: dict | None = None, token: str | None = None) -> dict:
    effective_token = runtime.access_token if token is None else token
    headers = {
        "Authorization": f"Bearer {effective_token}",
        "Accept": "application/json, text/event-stream",
        "Content-Type": "application/json",
    }
    if extra:
        headers.update(extra)
    return headers


def _mcp_initialize(client: TestClient, runtime):
    response = client.post(
        "/mcp/",
        json={
            "jsonrpc": "2.0",
            "id": "init-1",
            "method": "initialize",
            "params": {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "test-suite", "version": "1.0.0"},
            },
        },
        headers=_mcp_headers(runtime),
    )
    payload = response.json()
    protocol_version = payload["result"]["protocolVersion"]
    client.post(
        "/mcp/",
        json={
            "jsonrpc": "2.0",
            "method": "notifications/initialized",
            "params": {},
        },
        headers=_mcp_headers(runtime, {"mcp-protocol-version": protocol_version}),
    )
    return response, protocol_version


def _mcp_call(client: TestClient, runtime, protocol_version: str, tool_name: str, arguments: dict | None = None):
    return client.post(
        "/mcp/",
        json={
            "jsonrpc": "2.0",
            "id": f"call-{tool_name}",
            "method": "tools/call",
            "params": {
                "name": tool_name,
                "arguments": arguments or {},
            },
        },
        headers=_mcp_headers(runtime, {"mcp-protocol-version": protocol_version}),
    )


def _extract_tool_payload(response) -> tuple[bool, object]:
    payload = response.json()["result"]
    is_error = bool(payload.get("isError"))
    if "structuredContent" in payload:
        return is_error, payload["structuredContent"]

    text = "".join(item.get("text", "") for item in payload.get("content", []) if isinstance(item, dict))
    if not text.strip():
        return is_error, {}

    try:
        return is_error, json.loads(text)
    except Exception:
        return is_error, {"text": text}


@unittest.skipIf(MCP_SDK_IMPORT_ERROR is not None, f"MCP SDK unavailable in this interpreter: {MCP_SDK_IMPORT_ERROR}")
class TestMcpApi(unittest.TestCase):
    def test_mcp_disabled_returns_404(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            runtime = create_runtime(Path(temp_dir))
            runtime.enable_mcp = False
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    response = client.post("/mcp/", json={}, headers=_mcp_headers(runtime))
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(response.status_code, 404)

    def test_mcp_runtime_disable_returns_404_even_when_configured_enabled(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            runtime = create_runtime(Path(temp_dir))
            runtime.enable_mcp_configured = True
            runtime.enable_mcp = False
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    response = client.post("/mcp/", json={}, headers=_mcp_headers(runtime))
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(response.status_code, 404)

    def test_mcp_requires_bearer_token(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            runtime = create_runtime(Path(temp_dir))
            runtime.enable_mcp = True
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    response = client.post("/mcp/", json={}, headers={"Content-Type": "application/json"})
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(response.status_code, 401)

    def test_mcp_initialize_list_tools_and_call_tools(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            context = Context(Path(temp_dir))
            config = load_config(context)
            config["api_key"] = "top-level-secret"
            config["plan_presets"][0]["api_key"] = "preset-secret"
            save_config(context, config)
            runtime = create_runtime(Path(temp_dir))
            runtime.enable_mcp = True
            app.state.runtime = runtime
            try:
                def fake_run_schedule(payload, progress_callback, stop_event):
                    progress_callback("llm", "thinking", "chunk-1")
                    time.sleep(0.02)
                    progress_callback("parse", "done", None)
                    return {"status": "success", "message": "ok", "ai_response": "result", "trace_id": payload["trace_id"]}

                with mock.patch.object(runtime.command_service, "run_schedule", side_effect=fake_run_schedule):
                    with TestClient(app) as client:
                        init_response, protocol_version = _mcp_initialize(client, runtime)
                        list_response = client.post(
                            "/mcp/",
                            json={
                                "jsonrpc": "2.0",
                                "id": "tools-1",
                                "method": "tools/list",
                                "params": {},
                            },
                            headers=_mcp_headers(runtime, {"mcp-protocol-version": protocol_version}),
                        )
                        replace_roster_response = _mcp_call(
                            client,
                            runtime,
                            protocol_version,
                            "replace_roster",
                            {
                                "roster": [
                                    {"id": 1, "name": "Alice", "active": True},
                                    {"id": 2, "name": "Bob", "active": False},
                                ]
                            },
                        )
                        inspect_response = _mcp_call(client, runtime, protocol_version, "inspect_workspace")
                        run_response = _mcp_call(
                            client,
                            runtime,
                            protocol_version,
                            "run_schedule",
                            {"instruction": "run"},
                        )
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(init_response.status_code, 200)

        tool_names = [item["name"] for item in list_response.json()["result"]["tools"]]
        self.assertEqual(
            set(tool_names),
            {"inspect_workspace", "update_scheduler_config", "replace_roster", "edit_schedule_entry", "run_schedule"},
        )

        replace_is_error, replace_payload = _extract_tool_payload(replace_roster_response)
        inspect_is_error, inspect_payload = _extract_tool_payload(inspect_response)
        run_is_error, run_payload = _extract_tool_payload(run_response)

        self.assertFalse(replace_is_error)
        self.assertFalse(inspect_is_error)
        self.assertFalse(run_is_error)
        self.assertEqual((replace_payload or {}).get("roster", [])[0]["name"], "Alice")
        snapshot = (inspect_payload or {}).get("snapshot", {})
        roster = snapshot.get("roster", [])
        self.assertEqual([item["name"] for item in roster], ["Alice", "Bob"])
        self.assertEqual(snapshot.get("config", {}).get("api_key"), "")
        self.assertEqual((snapshot.get("config", {}).get("plan_presets") or [])[0].get("api_key"), "")
        self.assertEqual((run_payload or {}).get("status"), "success")
        self.assertEqual((run_payload or {}).get("ai_response"), "result")

    def test_mcp_run_schedule_returns_busy_when_duty_live_owner_exists(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            runtime = create_runtime(Path(temp_dir))
            runtime.enable_mcp = True
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    _, protocol_version = _mcp_initialize(client, runtime)
                    with client.websocket_connect(
                        "/api/v1/duty/live",
                        headers=_mcp_headers(runtime, {"X-Duty-Request-Source": "test_suite"}),
                    ) as websocket:
                        websocket.send_json({"type": "hello", "request_source": "test_suite"})
                        hello = websocket.receive_json()
                        self.assertEqual(hello["type"], "hello")

                        busy_response = _mcp_call(
                            client,
                            runtime,
                            protocol_version,
                            "run_schedule",
                            {"instruction": "run"},
                        )
            finally:
                app.state.runtime = original_runtime

        is_error, payload = _extract_tool_payload(busy_response)
        self.assertTrue(is_error)
        self.assertIn("busy", json.dumps(payload, ensure_ascii=False).lower())

    def test_mcp_accepts_static_token_and_rejects_wrong_token(self):
        static_token = "static-token"
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            runtime = create_runtime(Path(temp_dir))
            runtime.enable_mcp = True
            runtime.access_token_mode = "static"
            runtime.static_access_token_verifier = create_pbkdf2_sha256_verifier(static_token)
            runtime.access_token = ""
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    success = client.post(
                        "/mcp/",
                        json={
                            "jsonrpc": "2.0",
                            "id": "init-static",
                            "method": "initialize",
                            "params": {
                                "protocolVersion": "2025-06-18",
                                "capabilities": {},
                                "clientInfo": {"name": "test-suite", "version": "1.0.0"},
                            },
                        },
                        headers=_mcp_headers(runtime, token=static_token),
                    )
                    failure = client.post(
                        "/mcp/",
                        json={},
                        headers=_mcp_headers(runtime, token="wrong-token"),
                    )
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(success.status_code, 200)
        self.assertEqual(failure.status_code, 401)


if __name__ == "__main__":
    unittest.main()
