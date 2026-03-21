import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

from core import app
from runtime import create_runtime
from state_ops import Context, load_state, save_json_atomic, save_roster_entries, save_schedule_entry_edit


def _auth_headers(runtime, extra: dict | None = None) -> dict:
    headers = {"Authorization": f"Bearer {runtime.access_token}"}
    if extra:
        headers.update(extra)
    return headers


def _seed_roster(context: Context, entries: list[dict]) -> None:
    save_roster_entries(context, entries)


def _seed_state(context: Context, state: dict) -> None:
    save_json_atomic(context.paths["state"], state)


class TestScheduleEntryEditStore(unittest.TestCase):
    def test_record_mode_updates_schedule_and_ledger(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            _seed_roster(
                context,
                [
                    {"id": 1, "name": "Alice", "active": True},
                    {"id": 2, "name": "Bob", "active": True},
                    {"id": 3, "name": "Carol", "active": True},
                ],
            )
            _seed_state(
                context,
                {
                    "schedule_pool": [
                        {
                            "date": "2026-03-21",
                            "day": "周六",
                            "area_assignments": {"教室": ["Alice", "Bob"]},
                            "note": "old",
                        }
                    ],
                    "next_run_note": "keep-note",
                    "debt_list": [3],
                    "credit_list": [1],
                    "last_pointer": 9,
                },
            )

            result = save_schedule_entry_edit(
                context,
                {
                    "source_date": "2026-03-21",
                    "target_date": "2026-03-21",
                    "day": "周六",
                    "area_assignments": {"教室": ["Bob", "Carol"]},
                    "note": "updated",
                    "create_if_missing": False,
                    "ledger_mode": "record",
                },
            )
            state = load_state(context.paths["state"])

        self.assertEqual(result["status"], "success")
        self.assertTrue(result["ledger_applied"])
        self.assertEqual(state["schedule_pool"][0]["area_assignments"], {"教室": ["Bob", "Carol"]})
        self.assertEqual(state["debt_list"], [])
        self.assertEqual(state["credit_list"], [])
        self.assertEqual(state["next_run_note"], "keep-note")
        self.assertEqual(state["last_pointer"], 9)

    def test_skip_mode_updates_schedule_without_touching_ledger(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            _seed_roster(
                context,
                [
                    {"id": 1, "name": "Alice", "active": True},
                    {"id": 2, "name": "Bob", "active": True},
                    {"id": 3, "name": "Carol", "active": True},
                ],
            )
            _seed_state(
                context,
                {
                    "schedule_pool": [
                        {
                            "date": "2026-03-21",
                            "day": "周六",
                            "area_assignments": {"教室": ["Alice", "Bob"]},
                            "note": "old",
                        }
                    ],
                    "next_run_note": "keep-note",
                    "debt_list": [3],
                    "credit_list": [1],
                    "last_pointer": 9,
                },
            )

            result = save_schedule_entry_edit(
                context,
                {
                    "source_date": "2026-03-21",
                    "target_date": "2026-03-21",
                    "day": "周六",
                    "area_assignments": {"教室": ["Bob", "Carol"]},
                    "note": "updated",
                    "create_if_missing": False,
                    "ledger_mode": "skip",
                },
            )
            state = load_state(context.paths["state"])

        self.assertEqual(result["status"], "success")
        self.assertFalse(result["ledger_applied"])
        self.assertEqual(state["schedule_pool"][0]["area_assignments"], {"教室": ["Bob", "Carol"]})
        self.assertEqual(state["debt_list"], [3])
        self.assertEqual(state["credit_list"], [1])
        self.assertEqual(state["next_run_note"], "keep-note")
        self.assertEqual(state["last_pointer"], 9)

    def test_rejects_duplicate_target_date(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            _seed_state(
                context,
                {
                    "schedule_pool": [
                        {"date": "2026-03-21", "day": "周六", "area_assignments": {}, "note": ""},
                        {"date": "2026-03-22", "day": "周日", "area_assignments": {}, "note": ""},
                    ],
                    "next_run_note": "",
                    "debt_list": [],
                    "credit_list": [],
                    "last_pointer": 0,
                },
            )

            with self.assertRaisesRegex(ValueError, "already exists"):
                save_schedule_entry_edit(
                    context,
                    {
                        "source_date": "2026-03-21",
                        "target_date": "2026-03-22",
                        "area_assignments": {},
                        "create_if_missing": False,
                        "ledger_mode": "record",
                    },
                )

    def test_rejects_missing_source_date_when_create_disabled(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            _seed_state(
                context,
                {
                    "schedule_pool": [],
                    "next_run_note": "",
                    "debt_list": [],
                    "credit_list": [],
                    "last_pointer": 0,
                },
            )

            with self.assertRaisesRegex(ValueError, "not found"):
                save_schedule_entry_edit(
                    context,
                    {
                        "source_date": "2026-03-21",
                        "target_date": "2026-03-21",
                        "area_assignments": {},
                        "create_if_missing": False,
                        "ledger_mode": "record",
                    },
                )

    def test_unknown_or_inactive_names_do_not_affect_ledger(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            _seed_roster(
                context,
                [
                    {"id": 1, "name": "Alice", "active": True},
                    {"id": 2, "name": "Bob", "active": False},
                ],
            )
            _seed_state(
                context,
                {
                    "schedule_pool": [],
                    "next_run_note": "",
                    "debt_list": [2],
                    "credit_list": [],
                    "last_pointer": 0,
                },
            )

            save_schedule_entry_edit(
                context,
                {
                    "source_date": None,
                    "target_date": "2026-03-21",
                    "day": "周六",
                    "area_assignments": {"教室": ["Bob", "Carol"]},
                    "note": "",
                    "create_if_missing": True,
                    "ledger_mode": "record",
                },
            )
            state = load_state(context.paths["state"])

        self.assertEqual(state["debt_list"], [2])
        self.assertEqual(state["credit_list"], [])


class TestScheduleEntryEditApi(unittest.TestCase):
    def test_post_schedule_entry_returns_snapshot(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            runtime = create_runtime(Path(temp_dir))
            context = Context(Path(temp_dir))
            _seed_roster(
                context,
                [
                    {"id": 1, "name": "Alice", "active": True},
                    {"id": 2, "name": "Bob", "active": True},
                ],
            )
            _seed_state(
                context,
                {
                    "schedule_pool": [
                        {"date": "2026-03-21", "day": "周六", "area_assignments": {"教室": ["Alice"]}, "note": ""}
                    ],
                    "next_run_note": "",
                    "debt_list": [],
                    "credit_list": [],
                    "last_pointer": 0,
                },
            )
            app.state.runtime = runtime
            try:
                with TestClient(app) as client:
                    response = client.post(
                        "/api/v1/duty/schedule-entry",
                        json={
                            "source_date": "2026-03-21",
                            "target_date": "2026-03-21",
                            "day": "周六",
                            "area_assignments": {"教室": ["Bob"]},
                            "note": "edited",
                            "create_if_missing": False,
                            "ledger_mode": "skip",
                        },
                        headers=_auth_headers(runtime),
                    )
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(response.status_code, 200)
        payload = response.json()
        self.assertEqual(payload["status"], "success")
        self.assertEqual(payload["ledger_mode"], "skip")
        self.assertIn("snapshot", payload)
        self.assertEqual(payload["snapshot"]["state"]["schedule_pool"][0]["area_assignments"], {"教室": ["Bob"]})

    def test_duty_live_supports_schedule_entry_save_while_run_is_active(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            runtime = create_runtime(Path(temp_dir))
            context = Context(Path(temp_dir))
            _seed_roster(
                context,
                [
                    {"id": 1, "name": "Alice", "active": True},
                    {"id": 2, "name": "Bob", "active": True},
                ],
            )
            _seed_state(
                context,
                {
                    "schedule_pool": [
                        {"date": "2026-03-21", "day": "周六", "area_assignments": {"教室": ["Alice"]}, "note": ""}
                    ],
                    "next_run_note": "",
                    "debt_list": [],
                    "credit_list": [],
                    "last_pointer": 0,
                },
            )
            app.state.runtime = runtime
            try:
                def fake_run_schedule(payload, progress_callback, stop_event):
                    progress_callback("llm", "thinking", "chunk-1")
                    time.sleep(0.05)
                    return {"status": "success", "message": "ok", "ai_response": "result"}

                with mock.patch.object(runtime.command_service, "run_schedule", side_effect=fake_run_schedule):
                    with TestClient(app) as client:
                        with client.websocket_connect("/api/v1/duty/live", headers=_auth_headers(runtime)) as websocket:
                            websocket.send_json({"type": "hello"})
                            self.assertEqual(websocket.receive_json()["type"], "hello")

                            websocket.send_json(
                                {
                                    "type": "schedule_run",
                                    "client_change_id": "run-1",
                                    "instruction": "run",
                                    "apply_mode": "append",
                                }
                            )
                            websocket.send_json(
                                {
                                    "type": "schedule_entry_save",
                                    "client_change_id": "edit-1",
                                    "source_date": "2026-03-21",
                                    "target_date": "2026-03-21",
                                    "day": "周六",
                                    "area_assignments": {"教室": ["Bob"]},
                                    "note": "edited",
                                    "create_if_missing": False,
                                    "ledger_mode": "skip",
                                }
                            )

                            received_types = []
                            saved_payload = None
                            for _ in range(8):
                                message = websocket.receive_json()
                                received_types.append(message["type"])
                                if message["type"] == "schedule_entry_saved":
                                    saved_payload = message
                                if "schedule_entry_saved" in received_types and "schedule_complete" in received_types:
                                    break
            finally:
                app.state.runtime = original_runtime

        self.assertIn("accepted", received_types)
        self.assertIn("schedule_entry_saved", received_types)
        self.assertIn("schedule_complete", received_types)
        self.assertIsNotNone(saved_payload)
        self.assertEqual(saved_payload["snapshot"]["state"]["schedule_pool"][0]["area_assignments"], {"教室": ["Bob"]})

    def test_duty_live_requires_bearer_token(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            app.state.runtime = create_runtime(Path(temp_dir))
            try:
                with TestClient(app) as client:
                    with self.assertRaises(WebSocketDisconnect) as ctx:
                        with client.websocket_connect("/api/v1/duty/live") as websocket:
                            websocket.receive_json()
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(ctx.exception.code, 4401)


if __name__ == "__main__":
    unittest.main()
