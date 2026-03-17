import tempfile
import unittest
from pathlib import Path

from fastapi.testclient import TestClient

from core import app
from runtime import create_runtime
from state_ops import Context, load_roster_entries, save_roster_entries


class TestRosterStore(unittest.TestCase):
    def test_save_roster_entries_normalizes_duplicates_and_persists(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))

            saved = save_roster_entries(
                context,
                [
                    {"id": 0, "name": " Alice ", "active": True},
                    {"id": 1, "name": "alice", "active": False},
                    {"id": 1, "name": "Bob", "active": True},
                    {"id": -1, "name": "   ", "active": True},
                    "bad-entry",
                ],
            )
            loaded = load_roster_entries(context.paths["roster"])

        self.assertEqual(
            saved,
            [
                {"id": 1, "name": "Alice", "active": True},
                {"id": 2, "name": "alice2", "active": False},
                {"id": 3, "name": "Bob", "active": True},
            ],
        )
        self.assertEqual(loaded, saved)


class TestRosterApi(unittest.TestCase):
    def test_put_and_get_roster_round_trip(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original_runtime = getattr(app.state, "runtime", None)
            app.state.runtime = create_runtime(Path(temp_dir))
            try:
                with TestClient(app) as client:
                    put_response = client.put(
                        "/api/v1/roster",
                        json={
                            "roster": [
                                {"id": 0, "name": "Alice", "active": True},
                                {"id": 1, "name": "Alice", "active": False},
                            ]
                        },
                        headers={"X-Duty-Request-Source": "test_suite"},
                    )
                    get_response = client.get(
                        "/api/v1/roster",
                        headers={"X-Duty-Request-Source": "test_suite"},
                    )
            finally:
                app.state.runtime = original_runtime

        self.assertEqual(put_response.status_code, 200)
        self.assertEqual(get_response.status_code, 200)
        self.assertEqual(
            put_response.json(),
            {
                "roster": [
                    {"id": 1, "name": "Alice", "active": True},
                    {"id": 2, "name": "Alice2", "active": False},
                ]
            },
        )
        self.assertEqual(get_response.json(), put_response.json())


if __name__ == "__main__":
    unittest.main()
