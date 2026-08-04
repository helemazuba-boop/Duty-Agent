import contextlib
import io
import json
import os
import tempfile
import unittest
from datetime import date, timedelta
from pathlib import Path
from unittest import mock

from fastapi.testclient import TestClient

import cli
from core import app
from runtime import create_runtime
from single_pass_executor import (
    apply_single_pass_completion,
    build_single_pass_request,
    run_single_pass_schedule,
)
from execution_profiles import build_execution_plan, resolve_execution_profile
from state_ops import Context, load_config, load_state, save_config, save_roster_entries


BASE_URL = "http://testserver"


def _seed_roster(temp_dir: str) -> None:
    context = Context(Path(temp_dir))
    save_roster_entries(
        context,
        [
            {"id": 1, "name": "Alice", "active": True},
            {"id": 2, "name": "Bob", "active": True},
            {"id": 3, "name": "Carol", "active": True},
        ],
    )


def _select_plan(temp_dir: str, plan_id: str) -> None:
    """Switch the active plan preset. ``orchestration_mode`` is a derived field
    (hydrated from the selected plan's ``mode_id``), so we must persist the
    selection rather than the derived value."""
    context = Context(Path(temp_dir))
    config = load_config(context)
    config["selected_plan_id"] = plan_id
    save_config(context, config)


def _future_completion(start_date_iso: str) -> str:
    start = date.fromisoformat(start_date_iso)
    day = start + timedelta(days=3)
    return (
        "[areas]\n"
        "A = Classroom\n"
        "[schedule]\n"
        f"{day.strftime('%m-%d')} = A:1 2\n"
        "[state]\n"
        "debt = 3\n"
    )


@contextlib.contextmanager
def _cli_routed_to(client: TestClient):
    """Route cli.httpx.request through the in-process ASGI TestClient."""

    def fake_request(method, url, headers=None, json=None, timeout=None, **kwargs):
        path = url[len(BASE_URL):] if url.startswith(BASE_URL) else url
        return client.request(method, path, headers=headers, json=json)

    with mock.patch("cli.httpx.request", new=fake_request):
        yield


def _run_cli(argv: list[str]) -> tuple[int, dict]:
    buffer = io.StringIO()
    with contextlib.redirect_stdout(buffer):
        exit_code = cli.main(argv)
    stdout = buffer.getvalue().strip()
    return exit_code, (json.loads(stdout) if stdout else {})


class TestCliRedaction(unittest.TestCase):
    def test_redacts_api_key_by_default(self):
        payload = {"config": {"api_key": "secret", "model": "m"}, "presets": [{"api_key": "p"}]}
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            cli.emit(payload, pretty=False, show_secrets=False)
        result = json.loads(buffer.getvalue())
        self.assertEqual(result["config"]["api_key"], "")
        self.assertEqual(result["config"]["model"], "m")
        self.assertEqual(result["presets"][0]["api_key"], "")

    def test_show_secrets_preserves_api_key(self):
        payload = {"config": {"api_key": "secret"}}
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            cli.emit(payload, pretty=False, show_secrets=True)
        result = json.loads(buffer.getvalue())
        self.assertEqual(result["config"]["api_key"], "secret")

    def test_describe_needs_no_network(self):
        exit_code, payload = _run_cli(["describe"])
        self.assertEqual(exit_code, 0)
        self.assertEqual(payload["status"], "success")
        self.assertEqual(payload["delegation"]["supported_modes"], ["single_pass"])
        self.assertTrue(any(c["name"] == "plan-prompt" for c in payload["commands"]))


class TestCliDiscoverability(unittest.TestCase):
    """An AI's zero-prior first contact must be self-explaining and JSON-clean."""

    def test_no_command_emits_describe_catalog(self):
        # Bare invocation should behave like ``describe`` so the very first
        # contact yields the full machine-readable catalog, no network needed.
        exit_code, payload = _run_cli([])
        self.assertEqual(exit_code, 0)
        self.assertEqual(payload["status"], "success")
        self.assertTrue(any(c["name"] == "describe" for c in payload["commands"]))

    def test_bad_command_emits_json_error_on_stdout(self):
        # Usage errors must honor the stdout JSON contract (not bare argparse
        # stderr) and point the caller at ``describe``.
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            with self.assertRaises(SystemExit) as ctx:
                cli.main(["frobnicate"])
        self.assertEqual(ctx.exception.code, 2)
        payload = json.loads(buffer.getvalue().strip())
        self.assertEqual(payload["status"], "error")
        self.assertIn("describe", payload["hint"])

    def test_missing_required_arg_emits_json_error(self):
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            with self.assertRaises(SystemExit) as ctx:
                cli.main(["plan-prompt"])
        self.assertEqual(ctx.exception.code, 2)
        payload = json.loads(buffer.getvalue().strip())
        self.assertEqual(payload["status"], "error")
        self.assertIn("--instruction", payload["message"])


class TestCliLifecycle(unittest.TestCase):
    """serve/status lifecycle helpers and registration."""

    def test_pid_file_roundtrip(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            data_dir = Path(temp_dir)
            pid_path = cli._pid_file_for(data_dir)
            self.assertIsNone(cli._read_pid(pid_path))
            cli._write_pid(pid_path, 4242)
            self.assertEqual(cli._read_pid(pid_path), 4242)
            cli._remove_pid_file(pid_path)
            self.assertIsNone(cli._read_pid(pid_path))

    def test_read_pid_tolerates_garbage(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            pid_path = Path(temp_dir) / cli.PID_FILE_NAME
            pid_path.write_text("not-a-pid", encoding="utf-8")
            self.assertIsNone(cli._read_pid(pid_path))

    def test_pid_alive_current_vs_missing(self):
        self.assertTrue(cli._pid_alive(os.getpid()))
        self.assertFalse(cli._pid_alive(None))
        self.assertFalse(cli._pid_alive(0))
        # A pid that is (almost certainly) not running.
        self.assertFalse(cli._pid_alive(2_000_000_000))

    def test_describe_lists_lifecycle_commands(self):
        exit_code, payload = _run_cli(["describe"])
        self.assertEqual(exit_code, 0)
        names = {c["name"] for c in payload["commands"]}
        self.assertIn("serve", names)
        self.assertIn("status", names)
        self.assertIn("run", names)
        # describe must advertise the PowerShell-safe file flags for delegation.
        ingest = next(c for c in payload["commands"] if c["name"] == "plan-ingest")
        self.assertIn("--completion-file", ingest["args"])
        self.assertIn("--handle-file", ingest["args"])

    def test_status_unreachable_reports_not_running(self):
        # Point at a port nothing listens on; status must stay success/running:false.
        exit_code, payload = _run_cli(["status", "--port", "8799"])
        self.assertEqual(exit_code, 0)
        self.assertEqual(payload["status"], "success")
        self.assertFalse(payload["running"])
        self.assertFalse(payload["managed"])

    def test_serve_stop_without_managed_backend(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            exit_code, payload = _run_cli(["serve", "--stop", "--data-dir", temp_dir])
            self.assertEqual(exit_code, 0)
            self.assertEqual(payload["status"], "success")
            self.assertFalse(payload["stopped"])


class TestCliDelegation(unittest.TestCase):
    def _with_runtime(self, temp_dir: str):
        runtime = create_runtime(Path(temp_dir))
        app.state.runtime = runtime
        return runtime

    @staticmethod
    def _teardown_runtime(runtime, original) -> None:
        # Stop the daemon workers *before* the TemporaryDirectory is cleaned up,
        # otherwise their background file access races the rmtree on Windows
        # (WinError 145). addCleanup is too late: the temp dir is torn down when
        # the ``with`` block exits, before the test method returns.
        if runtime is not None:
            runtime.stop_auto_run_worker()
            runtime.stop_notification_workers()
        app.state.runtime = original

    def test_plan_prompt_single_pass_success_and_anonymized(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original = getattr(app.state, "runtime", None)
            _seed_roster(temp_dir)
            runtime = self._with_runtime(temp_dir)
            try:
                with TestClient(app) as client, _cli_routed_to(client):
                    exit_code, payload = _run_cli(
                        [
                            "--base-url", BASE_URL,
                            "--token", runtime.access_token,
                            "plan-prompt",
                            "--instruction", "本周排班",
                        ]
                    )
            finally:
                self._teardown_runtime(runtime, original)

        self.assertEqual(exit_code, 0)
        self.assertEqual(payload["status"], "success")
        self.assertEqual(payload["mode"], "single_pass")
        # Slim payload: prompt_text is the canonical prompt (messages dropped).
        self.assertTrue(payload["prompt_text"])
        self.assertNotIn("messages", payload)
        self.assertIn("resume_context", payload)
        # Anonymization: real names must never appear in the delegated prompt.
        self.assertNotIn("Alice", payload["prompt_text"])
        self.assertNotIn("Bob", payload["prompt_text"])

    def test_plan_prompt_rejects_non_single_pass(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original = getattr(app.state, "runtime", None)
            _seed_roster(temp_dir)
            _select_plan(temp_dir, "agents")
            runtime = self._with_runtime(temp_dir)
            try:
                with TestClient(app) as client, _cli_routed_to(client):
                    exit_code, payload = _run_cli(
                        [
                            "--base-url", BASE_URL,
                            "--token", runtime.access_token,
                            "plan-prompt",
                            "--instruction", "本周排班",
                        ]
                    )
            finally:
                self._teardown_runtime(runtime, original)

        self.assertEqual(exit_code, 1)
        self.assertEqual(payload["status"], "error")
        self.assertNotEqual(payload.get("mode"), "single_pass")

    def test_plan_ingest_lands_schedule_and_advances_ledger(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original = getattr(app.state, "runtime", None)
            _seed_roster(temp_dir)
            runtime = self._with_runtime(temp_dir)
            try:
                with TestClient(app) as client, _cli_routed_to(client):
                    _, prompt_payload = _run_cli(
                        [
                            "--base-url", BASE_URL,
                            "--token", runtime.access_token,
                            "plan-prompt",
                            "--instruction", "本周排班",
                        ]
                    )
                    resume_context = prompt_payload["resume_context"]
                    completion = _future_completion(resume_context["start_date"])

                    exit_code, payload = _run_cli(
                        [
                            "--base-url", BASE_URL,
                            "--token", runtime.access_token,
                            "plan-ingest",
                            "--completion", completion,
                            "--handle", json.dumps(resume_context),
                        ]
                    )
            finally:
                self._teardown_runtime(runtime, original)

        self.assertEqual(exit_code, 0)
        self.assertEqual(payload["status"], "success")
        schedule_pool = payload["snapshot"]["state"].get("schedule_pool", [])
        self.assertEqual(len(schedule_pool), 1)
        assignments = schedule_pool[0]["area_assignments"]
        self.assertEqual(assignments.get("Classroom"), ["Alice", "Bob"])
        # Ledger advanced: Carol (id 3) picked up the injected debt.
        self.assertEqual(payload["snapshot"]["state"]["debt_counts"].get("3"), 1)

    def test_plan_prompt_rejects_wrong_token(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original = getattr(app.state, "runtime", None)
            _seed_roster(temp_dir)
            runtime = self._with_runtime(temp_dir)
            try:
                with TestClient(app) as client, _cli_routed_to(client):
                    exit_code, payload = _run_cli(
                        [
                            "--base-url", BASE_URL,
                            "--token", "wrong-token",
                            "plan-prompt",
                            "--instruction", "本周排班",
                        ]
                    )
            finally:
                self._teardown_runtime(runtime, original)

        self.assertEqual(exit_code, 1)
        self.assertEqual(payload["status"], "error")
        self.assertIn("401", payload["message"])


class TestBuildApplyEquivalence(unittest.TestCase):
    """Regression: the build->apply split lands the same state as the
    original single-shot run_single_pass_schedule for a fixed completion."""

    def _plan_for(self, temp_dir: str):
        context = Context(Path(temp_dir))
        config = load_config(context)
        profile = resolve_execution_profile({}, config)
        return context, build_execution_plan(profile)

    def test_split_matches_single_shot(self):
        with tempfile.TemporaryDirectory() as dir_a, tempfile.TemporaryDirectory() as dir_b:
            _seed_roster(dir_a)
            _seed_roster(dir_b)

            # Discover start_date to craft a valid, date-stable completion.
            ctx_probe, plan_probe = self._plan_for(dir_a)
            _, probe_resume = build_single_pass_request(ctx_probe, {"instruction": "x"}, plan_probe)
            completion = _future_completion(probe_resume["start_date"])

            # Path 1: single-shot run with the model call mocked to the completion.
            ctx_a, plan_a = self._plan_for(dir_a)
            with mock.patch("single_pass_executor.call_llm_raw", return_value=completion):
                result_run = run_single_pass_schedule(ctx_a, {"instruction": "x"}, plan_a)

            # Path 2: explicit build -> apply with the same completion.
            ctx_b, plan_b = self._plan_for(dir_b)
            messages, resume_context = build_single_pass_request(ctx_b, {"instruction": "x"}, plan_b)
            self.assertTrue(messages)
            result_split = apply_single_pass_completion(ctx_b, completion, resume_context)

        self.assertEqual(result_run["status"], "success")
        self.assertEqual(result_split["status"], "success")

        state_a = load_state(Path(dir_a) / "state.json") if (Path(dir_a) / "state.json").exists() else load_state(ctx_a.paths["state"])
        state_b = load_state(ctx_b.paths["state"])
        self.assertEqual(state_a.get("schedule_pool"), state_b.get("schedule_pool"))
        self.assertEqual(state_a.get("debt_counts"), state_b.get("debt_counts"))
        self.assertEqual(state_a.get("last_pointer"), state_b.get("last_pointer"))


if __name__ == "__main__":
    unittest.main()
