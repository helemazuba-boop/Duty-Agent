import os
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest import mock

import state_ops
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

    def test_is_process_alive_handles_systemerror_from_os_kill(self):
        with mock.patch.object(state_ops.os, "name", "posix"):
            with mock.patch.object(state_ops.os, "kill", side_effect=SystemError("kill failed")):
                self.assertFalse(state_ops._is_process_alive(12345))


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


class TestUpdateHostRuntimeFieldsConcurrency(unittest.TestCase):
    """update_host_runtime_fields must do load-modify-write under one lock so
    that concurrent writers of different fields never clobber each other."""

    def test_concurrent_runtime_field_updates_persist_without_loss_or_deadlock(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            ctx = state_ops.Context(Path(temp_dir))
            # Seed a complete default host-config (last_auto_run_date="", ai_consecutive_failures=0).
            state_ops.save_host_config(ctx, {})

            dates = [f"2026-01-{day:02d}" for day in range(1, 6)]  # 5 distinct non-default dates
            failures = [10, 20, 30, 40, 50]  # 5 distinct non-default counts

            errors = []

            def write_date(value):
                try:
                    state_ops.update_host_runtime_fields(ctx, {"last_auto_run_date": value})
                except BaseException as ex:  # noqa: BLE001 — surface any failure (incl. lock timeout)
                    errors.append(ex)

            def write_failures(value):
                try:
                    state_ops.update_host_runtime_fields(ctx, {"ai_consecutive_failures": value})
                except BaseException as ex:  # noqa: BLE001
                    errors.append(ex)

            threads = [threading.Thread(target=write_date, args=(d,)) for d in dates]
            threads += [threading.Thread(target=write_failures, args=(f,)) for f in failures]

            for t in threads:
                t.start()
            for t in threads:
                t.join(timeout=30)

            self.assertEqual(errors, [], "concurrent update_host_runtime_fields calls must not raise")
            self.assertFalse(any(t.is_alive() for t in threads), "threads must not deadlock")

            # Fresh read from disk — proves the values actually persisted.
            final = state_ops.load_host_config(ctx)
            self.assertIn(
                final["last_auto_run_date"],
                set(dates),
                "last_auto_run_date must survive concurrent writes by other-field writers",
            )
            self.assertIn(
                final["ai_consecutive_failures"],
                set(failures),
                "ai_consecutive_failures must survive concurrent writes by other-field writers",
            )


if __name__ == "__main__":
    unittest.main()
