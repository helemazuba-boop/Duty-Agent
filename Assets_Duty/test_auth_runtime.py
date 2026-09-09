import contextlib
import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from auth import (
    create_pbkdf2_sha256_verifier,
    extract_bearer_token,
    extract_bearer_token_from_query,
    is_public_http_path,
    is_websocket_authorized,
    verify_pbkdf2_sha256_token,
)
from core import app, main
from runtime import create_runtime
from state_ops import load_api_key_from_env


class TestAuthRuntime(unittest.TestCase):
    def test_favicon_paths_are_public(self):
        # The default-deny middleware must not 401 the browser's automatic
        # favicon probes at the host root.
        self.assertTrue(is_public_http_path("/favicon.svg"))
        self.assertTrue(is_public_http_path("/favicon.ico"))
        self.assertFalse(is_public_http_path("/favicon.png"))

    def test_pbkdf2_verifier_round_trip(self):
        verifier = create_pbkdf2_sha256_verifier("static-token")

        self.assertTrue(verify_pbkdf2_sha256_token("static-token", verifier))
        self.assertFalse(verify_pbkdf2_sha256_token("wrong-token", verifier))
        self.assertFalse(verify_pbkdf2_sha256_token("static-token", "invalid"))

    def test_runtime_static_mode_uses_verifier_from_host_config(self):
        static_token = "static-token"
        with tempfile.TemporaryDirectory() as temp_dir:
            data_dir = Path(temp_dir)
            (data_dir / "host-config.json").write_text(
                json.dumps(
                    {
                        "access_token_mode": "static",
                        "static_access_token_verifier": create_pbkdf2_sha256_verifier(static_token),
                    }
                ),
                encoding="utf-8",
            )

            runtime = create_runtime(data_dir)

        self.assertEqual(runtime.access_token_mode, "static")
        self.assertTrue(runtime.is_authorized(static_token))
        self.assertFalse(runtime.is_authorized("wrong-token"))

    def test_runtime_disable_mcp_runtime_keeps_configured_flag_but_disables_endpoint(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            data_dir = Path(temp_dir)
            (data_dir / "host-config.json").write_text(
                json.dumps({"enable_mcp": True}),
                encoding="utf-8",
            )

            runtime = create_runtime(data_dir, disable_mcp_runtime=True)

        self.assertTrue(runtime.enable_mcp_configured)
        self.assertFalse(runtime.enable_mcp)

    def test_load_api_key_from_env_prefers_mcp_env_name(self):
        with mock.patch.dict(
            os.environ,
            {"DUTY_AGENT_MCP_API_KEY": "mcp-key", "DUTY_AGENT_API_KEY": "fallback-key"},
            clear=True,
        ):
            self.assertEqual(load_api_key_from_env(), "mcp-key")

        with mock.patch.dict(os.environ, {"DUTY_AGENT_API_KEY": "fallback-key"}, clear=True):
            self.assertEqual(load_api_key_from_env(), "fallback-key")

    def test_core_server_bootstrap_prints_dynamic_mode_and_token(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            stdout_buffer = io.StringIO()
            with contextlib.redirect_stdout(stdout_buffer):
                with mock.patch.object(sys, "argv", ["core.py", "--data-dir", temp_dir, "--server", "--port", "0"]):
                    with mock.patch("core.uvicorn.run"):
                        with mock.patch("core.threading.Thread") as thread_mock:
                            thread_mock.return_value.start.return_value = None
                            main()
            app.state.runtime = original_runtime

        stdout_text = stdout_buffer.getvalue()
        self.assertIn("__DUTY_SERVER_TOKEN_MODE__:dynamic", stdout_text)
        self.assertIn("__DUTY_SERVER_TOKEN__:", stdout_text)

    def test_core_server_bootstrap_prints_static_mode_without_token(self):
        static_token = "static-token"
        with tempfile.TemporaryDirectory() as temp_dir:
            data_dir = Path(temp_dir)
            (data_dir / "host-config.json").write_text(
                json.dumps(
                    {
                        "access_token_mode": "static",
                        "static_access_token_verifier": create_pbkdf2_sha256_verifier(static_token),
                    }
                ),
                encoding="utf-8",
            )

            original_runtime = getattr(app.state, "runtime", None)
            stdout_buffer = io.StringIO()
            with contextlib.redirect_stdout(stdout_buffer):
                with mock.patch.object(sys, "argv", ["core.py", "--data-dir", temp_dir, "--server", "--port", "0"]):
                    with mock.patch("core.uvicorn.run"):
                        with mock.patch("core.threading.Thread") as thread_mock:
                            thread_mock.return_value.start.return_value = None
                            main()
            app.state.runtime = original_runtime

        stdout_text = stdout_buffer.getvalue()
        self.assertIn("__DUTY_SERVER_TOKEN_MODE__:static", stdout_text)
        self.assertNotIn("__DUTY_SERVER_TOKEN__:", stdout_text)

    def test_core_server_disable_mcp_runtime_argument_disables_mcp_effectively(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            data_dir = Path(temp_dir)
            (data_dir / "host-config.json").write_text(
                json.dumps({"enable_mcp": True}),
                encoding="utf-8",
            )

            original_runtime = getattr(app.state, "runtime", None)
            try:
                with mock.patch.object(sys, "argv", ["core.py", "--data-dir", temp_dir, "--server", "--port", "0", "--disable-mcp-runtime"]):
                    with mock.patch("core.uvicorn.run"):
                        with mock.patch("core.threading.Thread") as thread_mock:
                            thread_mock.return_value.start.return_value = None
                            main()
                self.assertTrue(app.state.runtime.enable_mcp_configured)
                self.assertFalse(app.state.runtime.enable_mcp)
            finally:
                app.state.runtime = original_runtime

    def test_extract_bearer_token_from_query(self):
        self.assertEqual(extract_bearer_token_from_query({"token": "abc123"}), "abc123")
        self.assertEqual(extract_bearer_token_from_query({"access_token": "xyz789"}), "xyz789")
        self.assertEqual(extract_bearer_token_from_query({"token": "  padded  "}), "padded")
        self.assertIsNone(extract_bearer_token_from_query({}))
        self.assertIsNone(extract_bearer_token_from_query({"other": "value"}))
        self.assertIsNone(extract_bearer_token_from_query({"token": ""}))
        self.assertIsNone(extract_bearer_token_from_query(None))

    def test_is_websocket_authorized_falls_back_to_query_params(self):
        static_token = "static-token"
        with tempfile.TemporaryDirectory() as temp_dir:
            data_dir = Path(temp_dir)
            (data_dir / "host-config.json").write_text(
                json.dumps(
                    {
                        "access_token_mode": "static",
                        "static_access_token_verifier": create_pbkdf2_sha256_verifier(static_token),
                    }
                ),
                encoding="utf-8",
            )
            runtime = create_runtime(data_dir)

        mock_websocket = mock.Mock()
        mock_websocket.headers = {}
        mock_websocket.query_params = {"token": static_token}

        self.assertTrue(is_websocket_authorized(mock_websocket, runtime))
        mock_websocket.query_params = {"token": "wrong-token"}
        self.assertFalse(is_websocket_authorized(mock_websocket, runtime))

    def test_runtime_dynamic_mode_uses_constant_time_compare(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            data_dir = Path(temp_dir)
            (data_dir / "host-config.json").write_text(
                json.dumps({"access_token_mode": "dynamic"}),
                encoding="utf-8",
            )
            runtime = create_runtime(data_dir)

        token = runtime.access_token
        self.assertTrue(runtime.is_authorized(token))
        self.assertFalse(runtime.is_authorized("wrong-token"))
        self.assertFalse(runtime.is_authorized(""))
        self.assertFalse(runtime.is_authorized(None))


if __name__ == "__main__":
    unittest.main()
