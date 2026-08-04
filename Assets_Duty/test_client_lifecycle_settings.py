#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Tests for the standalone-client lifecycle switches stored in host-config
(client_auto_start / client_close_action) and their settings API round-trip."""

import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from fastapi.testclient import TestClient

from core import app
from runtime import create_runtime
from state_ops import Context, _normalize_persisted_host_config, load_host_config, patch_host_config


def _auth_headers(runtime) -> dict:
    return {"Authorization": f"Bearer {runtime.access_token}"}


class TestClientLifecycleNormalization(unittest.TestCase):
    def test_defaults(self):
        normalized = _normalize_persisted_host_config({})
        self.assertTrue(normalized["client_auto_start"])
        self.assertEqual(normalized["client_close_action"], "ask")

    def test_close_action_values_and_fallback(self):
        for value, expected in (("tray", "tray"), ("EXIT", "exit"), ("Ask", "ask"), ("garbage", "ask"), (None, "ask")):
            normalized = _normalize_persisted_host_config({"client_close_action": value})
            self.assertEqual(normalized["client_close_action"], expected, f"value={value!r}")

    def test_patch_host_config_round_trip(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            patched = patch_host_config(context, {"client_auto_start": False, "client_close_action": "tray"})
            self.assertFalse(patched["client_auto_start"])
            self.assertEqual(patched["client_close_action"], "tray")
            reloaded = load_host_config(context)
            self.assertFalse(reloaded["client_auto_start"])
            self.assertEqual(reloaded["client_close_action"], "tray")


class TestClientLifecycleSettingsApi(unittest.TestCase):
    def test_settings_api_exposes_and_patches_client_switches(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            runtime = create_runtime(Path(temp_dir))
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    initial = client.get("/api/v1/notifications/settings", headers=_auth_headers(runtime))
                    self.assertEqual(initial.status_code, 200)
                    payload = initial.json()
                    self.assertTrue(payload["client_auto_start"])
                    self.assertEqual(payload["client_close_action"], "ask")

                    patched = client.patch(
                        "/api/v1/notifications/settings",
                        json={"client_auto_start": False, "client_close_action": "exit"},
                        headers=_auth_headers(runtime),
                    )
                    self.assertEqual(patched.status_code, 200)
                    body = patched.json()
                    self.assertFalse(body["client_auto_start"])
                    self.assertEqual(body["client_close_action"], "exit")

                    invalid = client.patch(
                        "/api/v1/notifications/settings",
                        json={"client_close_action": "banana"},
                        headers=_auth_headers(runtime),
                    )
                    self.assertEqual(invalid.status_code, 422)
            finally:
                app.state.runtime = original_runtime


if __name__ == "__main__":
    unittest.main()
