import tempfile
import unittest
from pathlib import Path

from fastapi.testclient import TestClient

from core import app
from mcp_server import MCP_SDK_IMPORT_ERROR
from runtime import create_runtime


def _mcp_headers(runtime, extra: dict | None = None) -> dict:
    headers = {
        "Authorization": f"Bearer {runtime.access_token}",
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
    return response


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
            runtime = create_runtime(Path(temp_dir))
            runtime.enable_mcp = True
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    init_response = _mcp_initialize(client, runtime)
                    init_payload = init_response.json()
                    protocol_version = init_payload["result"]["protocolVersion"]

                    initialized_response = client.post(
                        "/mcp/",
                        json={
                            "jsonrpc": "2.0",
                            "method": "notifications/initialized",
                            "params": {},
                        },
                        headers=_mcp_headers(runtime, {"mcp-protocol-version": protocol_version}),
                    )

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

                    put_roster_response = client.post(
                        "/mcp/",
                        json={
                            "jsonrpc": "2.0",
                            "id": "tools-2",
                            "method": "tools/call",
                            "params": {
                                "name": "put_roster",
                                "arguments": {
                                    "roster": [
                                        {"id": 1, "name": "Alice", "active": True},
                                        {"id": 2, "name": "Bob", "active": False},
                                    ]
                                },
                            },
                        },
                        headers=_mcp_headers(runtime, {"mcp-protocol-version": protocol_version}),
                    )

                    get_roster_response = client.post(
                        "/mcp/",
                        json={
                            "jsonrpc": "2.0",
                            "id": "tools-3",
                            "method": "tools/call",
                            "params": {
                                "name": "get_roster",
                                "arguments": {},
                            },
                        },
                        headers=_mcp_headers(runtime, {"mcp-protocol-version": protocol_version}),
                    )
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(init_response.status_code, 200)
        self.assertIn(initialized_response.status_code, {200, 202, 204})

        tool_names = [item["name"] for item in list_response.json()["result"]["tools"]]
        self.assertIn("get_engine_info", tool_names)
        self.assertIn("run_schedule", tool_names)

        put_roster_result = put_roster_response.json()["result"]
        self.assertFalse(put_roster_result["isError"])

        get_roster_result = get_roster_response.json()["result"]
        self.assertFalse(get_roster_result["isError"])
        roster_text = "".join(item.get("text", "") for item in get_roster_result["content"])
        self.assertIn("Alice", roster_text)
        self.assertIn("Bob", roster_text)


if __name__ == "__main__":
    unittest.main()
