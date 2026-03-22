import os
import tempfile
import threading
import time
import unittest
from pathlib import Path

from runtime import DutyRuntime
from state_ops import acquire_state_file_lock, release_state_file_lock


class TestStateLocking(unittest.TestCase):
    def test_acquire_state_file_lock_clears_stale_lock_file(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            lock_path = Path(temp_dir) / "state.json.lock"
            lock_path.write_text("999999\n2000-01-01T00:00:00\n", encoding="utf-8")

            acquire_state_file_lock(lock_path, timeout_seconds=1)
            try:
                lines = lock_path.read_text(encoding="utf-8").splitlines()
                self.assertGreaterEqual(len(lines), 2)
                self.assertEqual(int(lines[0]), os.getpid())
            finally:
                release_state_file_lock(lock_path)

    def test_acquire_state_file_lock_honors_cancellation_while_waiting(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            lock_path = Path(temp_dir) / "state.json.lock"
            stop_event = threading.Event()

            acquire_state_file_lock(lock_path, timeout_seconds=1)
            try:
                stop_event.set()
                started_at = time.monotonic()
                with self.assertRaises(InterruptedError):
                    acquire_state_file_lock(lock_path, timeout_seconds=2, stop_event=stop_event)
                self.assertLess(time.monotonic() - started_at, 0.2)
            finally:
                release_state_file_lock(lock_path)


class TestCommandServiceSingleFlight(unittest.TestCase):
    def test_run_schedule_rejects_concurrent_execution(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            runtime = DutyRuntime(Path(temp_dir))
            runtime.schedule_run_lock.acquire()
            try:
                result = runtime.command_service.run_schedule({"instruction": "test"})
            finally:
                runtime.schedule_run_lock.release()

        self.assertEqual(result["status"], "error")
        self.assertIn("already in progress", result["message"].lower())


if __name__ == "__main__":
    unittest.main()
