import tempfile
import unittest
from pathlib import Path

from fastapi.testclient import TestClient

from core import app
from runtime import create_runtime
from state_ops import Context, save_roster_entries
from readiness import evaluate_readiness, probe_model


def _auth_headers(runtime) -> dict:
    return {"Authorization": f"Bearer {runtime.access_token}"}


class TestEvaluateReadinessPure(unittest.TestCase):
    """The readiness assembly is pure (no I/O) and drives both entry points."""

    def _config(self, **over):
        base = {"base_url": "http://localhost:1234/v1", "model": "m", "api_key": ""}
        base.update(over)
        return base

    def test_empty_is_not_ready_and_lists_next_steps(self):
        report = evaluate_readiness({"base_url": "", "model": "", "api_key": ""}, [], {}, probe=False)
        self.assertFalse(report["ready"])
        ids = {c["id"]: c for c in report["checks"]}
        self.assertFalse(ids["roster"]["ok"])
        self.assertFalse(ids["plan"]["ok"])
        self.assertFalse(ids["first_run"]["ok"])
        self.assertTrue(report["next_steps"])

    def test_local_plan_with_roster_and_history_is_ready(self):
        report = evaluate_readiness(
            self._config(),
            [{"id": 1, "name": "A", "active": True}],
            {"schedule_pool": [{"date": "2026-07-23"}]},
            probe=False,
        )
        self.assertTrue(report["ready"], report)

    def test_cloud_without_key_warns_but_stays_ok(self):
        report = evaluate_readiness(
            self._config(base_url="https://api.example.com/v1", api_key=""),
            [{"id": 1, "name": "A", "active": True}],
            {"schedule_pool": [{"date": "d"}]},
            probe=False,
        )
        plan = next(c for c in report["checks"] if c["id"] == "plan")
        self.assertTrue(plan["ok"])
        self.assertTrue(plan.get("warn"))

    def test_probe_adds_model_check(self):
        report = evaluate_readiness(self._config(base_url="", model=""), [], {}, probe=True)
        self.assertIn("model", {c["id"] for c in report["checks"]})


class TestProbeModel(unittest.TestCase):
    def test_unconfigured_returns_actionable(self):
        result = probe_model("", "", "")
        self.assertFalse(result["ok"])
        self.assertEqual(result["status"], "unconfigured")

    def test_unreachable_port_is_translated(self):
        # Nothing listens here; expect a translated failure, not an exception.
        result = probe_model("http://127.0.0.1:5999", "m", "")
        self.assertFalse(result["ok"])
        self.assertIn(result["status"], {"unreachable", "timeout"})
        self.assertTrue(result["fix"])


class TestReadinessApi(unittest.TestCase):
    def _with_runtime(self, temp_dir: str):
        runtime = create_runtime(Path(temp_dir))
        runtime.stop_auto_run_worker()
        runtime.stop_notification_workers()
        return runtime

    def test_readiness_endpoint_without_probe(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original = getattr(app.state, "runtime", None)
            runtime = self._with_runtime(temp_dir)
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    resp = client.get("/api/v1/readiness", headers=_auth_headers(runtime))
            finally:
                app.state.runtime = original

        self.assertEqual(resp.status_code, 200)
        body = resp.json()
        self.assertIn("ready", body)
        self.assertIn("checks", body)
        # No roster seeded -> not ready, and no model check without probe.
        self.assertFalse(body["ready"])
        self.assertNotIn("model", {c["id"] for c in body["checks"]})

    def test_readiness_ready_after_seeding(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original = getattr(app.state, "runtime", None)
            context = Context(Path(temp_dir))
            save_roster_entries(context, [{"id": 1, "name": "Alice", "active": True}])
            runtime = self._with_runtime(temp_dir)
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    # Point the default plan at a local URL + model, and stamp a run.
                    client.patch(
                        "/api/v1/config",
                        json={"selected_plan_id": "standard", "plan_presets": [
                            {"id": "standard", "name": "标准", "mode_id": "standard", "api_key": "",
                             "base_url": "http://localhost:1234/v1", "model": "m",
                             "model_profile": "auto", "provider_hint": "", "multi_agent_execution_mode": "auto"},
                        ]},
                        headers=_auth_headers(runtime),
                    )
                    resp = client.get("/api/v1/readiness", headers=_auth_headers(runtime))
            finally:
                app.state.runtime = original

        body = resp.json()
        ids = {c["id"]: c for c in body["checks"]}
        self.assertTrue(ids["roster"]["ok"])
        self.assertTrue(ids["plan"]["ok"])

    def test_model_probe_endpoint_dead_port(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original = getattr(app.state, "runtime", None)
            runtime = self._with_runtime(temp_dir)
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    resp = client.post(
                        "/api/v1/duty/model-probe",
                        json={"base_url": "http://127.0.0.1:5999", "model": "m", "api_key": ""},
                        headers=_auth_headers(runtime),
                    )
            finally:
                app.state.runtime = original

        self.assertEqual(resp.status_code, 200)
        body = resp.json()
        self.assertFalse(body["ok"])
        self.assertTrue(body["fix"])


if __name__ == "__main__":
    unittest.main()
