import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

from auth import create_pbkdf2_sha256_verifier
from core import app
from runtime import create_runtime


def _auth_headers(runtime, extra: dict | None = None, token: str | None = None) -> dict:
    effective_token = runtime.access_token if token is None else token
    headers = {"Authorization": f"Bearer {effective_token}"}
    if extra:
        headers.update(extra)
    return headers


class TestDutyLiveApi(unittest.TestCase):
    def test_settings_endpoints_are_removed(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            runtime = create_runtime(Path(temp_dir))
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    get_response = client.get("/api/v1/settings", headers=_auth_headers(runtime))
                    patch_response = client.patch("/api/v1/settings", json={}, headers=_auth_headers(runtime))
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(get_response.status_code, 404)
        self.assertEqual(patch_response.status_code, 404)

    def test_duty_live_supports_hello_and_schedule_run(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            runtime = create_runtime(Path(temp_dir))
            app.state.runtime = runtime
            try:
                def fake_run_schedule(payload, progress_callback, stop_event):
                    progress_callback("llm", "thinking", "chunk-1")
                    time.sleep(0.02)
                    progress_callback("parse", "done", None)
                    return {"status": "success", "message": "ok", "ai_response": "result"}

                with mock.patch.object(runtime.command_service, "run_schedule", side_effect=fake_run_schedule):
                    with TestClient(app) as client:
                        with client.websocket_connect(
                            "/api/v1/duty/live",
                            headers=_auth_headers(runtime, {"X-Duty-Request-Source": "test_suite"}),
                        ) as websocket:
                            websocket.send_json({"type": "hello", "request_source": "test_suite"})
                            hello = websocket.receive_json()
                            self.assertEqual(hello["type"], "hello")

                            websocket.send_json(
                                {
                                    "type": "schedule_run",
                                    "client_change_id": "run-1",
                                    "request_source": "test_suite",
                                    "instruction": "run",
                                }
                            )

                            received_types = []
                            received_chunks = []
                            for _ in range(5):
                                message = websocket.receive_json()
                                received_types.append(message["type"])
                                if message["type"] == "schedule_progress":
                                    received_chunks.append(message.get("stream_chunk"))
                                if "schedule_complete" in received_types:
                                    break
            finally:
                app.state.runtime = original_runtime

        self.assertIn("accepted", received_types)
        self.assertIn("schedule_progress", received_types)
        self.assertIn("schedule_complete", received_types)
        self.assertIn("chunk-1", received_chunks)

    def test_duty_live_rejects_settings_messages(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            app.state.runtime = create_runtime(Path(temp_dir))
            try:
                with TestClient(app) as client:
                    with client.websocket_connect(
                        "/api/v1/duty/live",
                        headers=_auth_headers(app.state.runtime, {"X-Duty-Request-Source": "test_suite"}),
                    ) as websocket:
                        websocket.send_json(
                            {
                                "type": "settings_patch",
                                "client_change_id": "change-1",
                                "request": {},
                            }
                        )
                        error_message = websocket.receive_json()
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(error_message["type"], "error")
        self.assertIn("Unsupported message type", error_message["message"])

    def test_duty_live_only_allows_one_owner_connection(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            runtime = create_runtime(Path(temp_dir))
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    with client.websocket_connect(
                        "/api/v1/duty/live",
                        headers=_auth_headers(runtime, {"X-Duty-Request-Source": "owner-1"}),
                    ) as owner_socket:
                        owner_socket.send_json({"type": "hello", "request_source": "owner-1"})
                        owner_hello = owner_socket.receive_json()

                        with client.websocket_connect(
                            "/api/v1/duty/live",
                            headers=_auth_headers(runtime, {"X-Duty-Request-Source": "owner-2"}),
                        ) as extra_socket:
                            busy_message = extra_socket.receive_json()
                            with self.assertRaises(WebSocketDisconnect):
                                extra_socket.receive_json()
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(owner_hello["type"], "hello")
        self.assertEqual(busy_message["type"], "error")
        self.assertIn("busy", busy_message["message"].lower())

    def test_snapshot_requires_bearer_token(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            app.state.runtime = create_runtime(Path(temp_dir))
            try:
                with TestClient(app) as client:
                    response = client.get("/api/v1/snapshot")
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(response.status_code, 401)

    def test_snapshot_accepts_static_token_and_rejects_wrong_token(self):
        static_token = "static-token"
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            runtime = create_runtime(Path(temp_dir))
            runtime.access_token_mode = "static"
            runtime.static_access_token_verifier = create_pbkdf2_sha256_verifier(static_token)
            runtime.access_token = ""
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    success = client.get("/api/v1/snapshot", headers=_auth_headers(runtime, token=static_token))
                    failure = client.get("/api/v1/snapshot", headers=_auth_headers(runtime, token="wrong-token"))
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(success.status_code, 200)
        self.assertEqual(failure.status_code, 401)


if __name__ == "__main__":
    unittest.main()
