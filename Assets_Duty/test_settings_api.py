import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from fastapi.testclient import TestClient

from core import app
from runtime import create_runtime


class TestSettingsApi(unittest.TestCase):
    def test_get_settings_combines_host_and_backend(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            app.state.runtime = create_runtime(Path(temp_dir))
            try:
                with TestClient(app) as client:
                    response = client.get("/api/v1/settings", headers={"X-Duty-Request-Source": "test_suite"})
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(response.status_code, 200)
        payload = response.json()
        self.assertEqual(payload["host_version"], 1)
        self.assertEqual(payload["backend_version"], 1)
        self.assertEqual(payload["host"]["auto_run_mode"], "Off")
        self.assertEqual(payload["backend"]["selected_plan_id"], "standard")

    def test_patch_settings_host_only_increments_host_version_and_preserves_runtime_fields(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            data_dir = Path(temp_dir)
            host_path = data_dir / "host-config.json"
            host_path.write_text(
                json.dumps(
                    {
                        "version": 1,
                        "ai_consecutive_failures": 4,
                        "last_auto_run_date": "2026-03-20",
                        "duty_reminder_times": ["07:40"],
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )

            original_runtime = getattr(app.state, "runtime", None)
            app.state.runtime = create_runtime(data_dir)
            try:
                with TestClient(app) as client:
                    response = client.patch(
                        "/api/v1/settings",
                        json={
                            "expected": {"host_version": 1, "backend_version": 1},
                            "changes": {
                                "host": {
                                    "enable_mcp": True,
                                    "notification_duration_seconds": 10,
                                }
                            },
                        },
                        headers={"X-Duty-Request-Source": "test_suite"},
                    )
            finally:
                app.state.runtime = original_runtime

            saved_host = json.loads(host_path.read_text(encoding="utf-8"))

        self.assertEqual(response.status_code, 200)
        payload = response.json()
        self.assertTrue(payload["success"])
        self.assertEqual(payload["document"]["host_version"], 2)
        self.assertTrue(payload["document"]["host"]["enable_mcp"])
        self.assertEqual(saved_host["version"], 2)
        self.assertEqual(saved_host["ai_consecutive_failures"], 4)
        self.assertEqual(saved_host["last_auto_run_date"], "2026-03-20")

    def test_patch_settings_backend_only_increments_backend_version(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            app.state.runtime = create_runtime(Path(temp_dir))
            try:
                with TestClient(app) as client:
                    response = client.patch(
                        "/api/v1/settings",
                        json={
                            "expected": {"host_version": 1, "backend_version": 1},
                            "changes": {
                                "backend": {
                                    "duty_rule": "new rule",
                                }
                            },
                        },
                        headers={"X-Duty-Request-Source": "test_suite"},
                    )
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(response.status_code, 200)
        payload = response.json()
        self.assertTrue(payload["success"])
        self.assertEqual(payload["document"]["backend_version"], 2)
        self.assertEqual(payload["document"]["backend"]["duty_rule"], "new rule")

    def test_patch_settings_mixed_patch_returns_combined_document(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            app.state.runtime = create_runtime(Path(temp_dir))
            try:
                with TestClient(app) as client:
                    response = client.patch(
                        "/api/v1/settings",
                        json={
                            "expected": {"host_version": 1, "backend_version": 1},
                            "changes": {
                                "host": {
                                    "enable_webview_debug_layer": True,
                                },
                                "backend": {
                                    "selected_plan_id": "incremental-small",
                                    "plan_presets": [
                                        {
                                            "id": "standard",
                                            "name": "标准",
                                            "mode_id": "standard",
                                            "api_key": "",
                                            "base_url": "https://integrate.api.nvidia.com/v1",
                                            "model": "moonshotai/kimi-k2-thinking",
                                            "model_profile": "auto",
                                            "provider_hint": "",
                                            "multi_agent_execution_mode": "auto",
                                        },
                                        {
                                            "id": "campus-6agent",
                                            "name": "6Agent",
                                            "mode_id": "campus_6agent",
                                            "api_key": "",
                                            "base_url": "https://integrate.api.nvidia.com/v1",
                                            "model": "moonshotai/kimi-k2-thinking",
                                            "model_profile": "auto",
                                            "provider_hint": "",
                                            "multi_agent_execution_mode": "auto",
                                        },
                                        {
                                            "id": "incremental-small",
                                            "name": "增量小模型",
                                            "mode_id": "incremental_small",
                                            "api_key": "",
                                            "base_url": "https://integrate.api.nvidia.com/v1",
                                            "model": "moonshotai/kimi-k2-thinking",
                                            "model_profile": "auto",
                                            "provider_hint": "",
                                            "multi_agent_execution_mode": "auto",
                                        },
                                    ],
                                    "duty_rule": "mixed rule",
                                },
                            },
                        },
                        headers={"X-Duty-Request-Source": "test_suite"},
                    )
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(response.status_code, 200)
        payload = response.json()
        self.assertTrue(payload["success"])
        self.assertTrue(payload["document"]["host"]["enable_webview_debug_layer"])
        self.assertEqual(payload["document"]["backend"]["selected_plan_id"], "incremental-small")
        self.assertEqual(payload["document"]["backend"]["duty_rule"], "mixed rule")

    def test_patch_settings_rejects_stale_versions(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            app.state.runtime = create_runtime(Path(temp_dir))
            try:
                with TestClient(app) as client:
                    first = client.patch(
                        "/api/v1/settings",
                        json={
                            "expected": {"host_version": 1, "backend_version": 1},
                            "changes": {"backend": {"duty_rule": "rule-a"}},
                        },
                        headers={"X-Duty-Request-Source": "test_suite"},
                    )
                    second = client.patch(
                        "/api/v1/settings",
                        json={
                            "expected": {"host_version": 1, "backend_version": 1},
                            "changes": {"backend": {"duty_rule": "rule-b"}},
                        },
                        headers={"X-Duty-Request-Source": "test_suite"},
                    )
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(first.status_code, 200)
        self.assertEqual(second.status_code, 409)
        payload = second.json()
        self.assertFalse(payload["success"])
        self.assertEqual(payload["document"]["backend_version"], 2)
        self.assertIn("version mismatch", payload["message"].lower())

    def test_patch_settings_returns_failure_and_real_document_when_second_write_fails(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            app.state.runtime = create_runtime(Path(temp_dir))
            try:
                import application.settings_service as settings_service_module

                real_save_json_atomic = settings_service_module.save_json_atomic
                call_count = {"value": 0}

                def flaky_save(path, data):
                    call_count["value"] += 1
                    if call_count["value"] == 2 and Path(path).name == "host-config.json":
                        raise PermissionError("host write blocked")
                    return real_save_json_atomic(path, data)

                with mock.patch.object(settings_service_module, "save_json_atomic", side_effect=flaky_save):
                    with TestClient(app) as client:
                        warmup = client.get(
                            "/api/v1/settings",
                            headers={"X-Duty-Request-Source": "test_suite"},
                        )
                        self.assertEqual(warmup.status_code, 200)
                        call_count["value"] = 0
                        response = client.patch(
                            "/api/v1/settings",
                            json={
                                "expected": {"host_version": 1, "backend_version": 1},
                                "changes": {
                                    "host": {"enable_mcp": True},
                                    "backend": {"duty_rule": "partial-write"},
                                },
                            },
                            headers={"X-Duty-Request-Source": "test_suite"},
                        )
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(response.status_code, 500)
        payload = response.json()
        self.assertFalse(payload["success"])
        self.assertEqual(payload["document"]["backend"]["duty_rule"], "partial-write")
        self.assertFalse(payload["document"]["host"]["enable_mcp"])


if __name__ == "__main__":
    unittest.main()
