#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unit tests for the backend auto-run worker (A4 integration).

Covers ``DutyRuntime.check_auto_run`` orchestration under the D5 claim-first
semantics: a due tick atomically claims today (``last_auto_run_date`` is
stamped at claim time, not on success), sub-limit failures retry via the
winner's in-memory attempt budget (host-config failure counters are
diagnostics only), busy (lock held *or* ``code=="busy"``) skips without
consuming an attempt, exhausting the attempt budget gives up for the day, the
target-time gate blocks normal triggers while catch-up bypasses it, and the
worker start/stop helpers are idempotent and join the thread.

The trigger helpers (``is_auto_run_triggered``/``detect_catchup_due``) live in
``auto_run`` (A1). They are imported defensively by ``runtime``; these tests
patch the module-level bindings so the orchestration can be exercised without
the real A1 module, and control ``run_schedule`` + wall clock directly.
"""

import json
import sys
import tempfile
import unittest
from datetime import datetime
from pathlib import Path
from unittest.mock import MagicMock, patch

# Ensure the module under test is importable
sys.path.insert(0, str(Path(__file__).resolve().parent))

import runtime as runtime_module
from runtime import create_runtime

FIXED_NOW = datetime(2026, 7, 11, 9, 30)
EXPECTED_TODAY = "2026-07-11"


class _FixedDateTime:
    """Stand-in for ``datetime`` so ``datetime.now()`` is deterministic."""

    @classmethod
    def now(cls, tz=None):
        return FIXED_NOW


def _trigger_true(*_args, **_kwargs):
    return True


def _trigger_false(*_args, **_kwargs):
    return False


def _catchup_true(*_args, **_kwargs):
    return True


def _catchup_false(*_args, **_kwargs):
    return False


def _write_host_config(data_dir: Path, **overrides) -> None:
    payload = {
        "auto_run_mode": "Off",
        "auto_run_parameter": "Monday",
        "auto_run_time": "00:00",
        "auto_run_retry_times": 3,
        "ai_consecutive_failures": 0,
        "last_auto_run_date": "",
    }
    payload.update(overrides)
    (data_dir / "host-config.json").write_text(json.dumps(payload), encoding="utf-8")


class _AutoRunRuntimeTestBase(unittest.TestCase):
    def _make_runtime(self, **host_overrides):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        data_dir = Path(self._tmp.name)
        _write_host_config(data_dir, **host_overrides)
        runtime = create_runtime(data_dir)
        # Kill the background workers so only our synchronous check_auto_run()
        # call mutates host-config.json during the test.
        runtime.stop_auto_run_worker()
        runtime.stop_notification_workers()
        self.addCleanup(runtime.stop_auto_run_worker)
        self.addCleanup(runtime.stop_notification_workers)
        return runtime

    def _drive(
        self,
        runtime,
        *,
        run_schedule_result,
        trigger=True,
        catchup=False,
        expect_invoked=True,
    ):
        """Run one ``check_auto_run`` tick with patched helpers + clock."""
        invoked = []

        def _fake_run_schedule(payload, progress_callback=None, stop_event=None):
            invoked.append(payload)
            return run_schedule_result

        with patch.object(runtime_module, "is_auto_run_triggered", _trigger_true if trigger else _trigger_false), \
                patch.object(runtime_module, "detect_catchup_due", _catchup_true if catchup else _catchup_false), \
                patch.object(runtime_module, "datetime", _FixedDateTime), \
                patch.object(runtime.command_service, "run_schedule", _fake_run_schedule):
            runtime.check_auto_run()

        if expect_invoked:
            self.assertTrue(invoked, "expected run_schedule to be invoked")
        else:
            self.assertFalse(invoked, "expected run_schedule NOT to be invoked")
        return invoked

    def _host_config(self, runtime) -> dict:
        return runtime._load_host_config()


class TestCheckAutoRun(_AutoRunRuntimeTestBase):
    def test_off_mode_returns_without_invoking_engine(self):
        runtime = self._make_runtime(auto_run_mode="Off")
        self._drive(runtime, run_schedule_result={"status": "success"}, expect_invoked=False)
        cfg = self._host_config(runtime)
        self.assertEqual(cfg["last_auto_run_date"], "")
        self.assertEqual(cfg["ai_consecutive_failures"], 0)

    def test_success_resets_failures_and_stamps_today(self):
        runtime = self._make_runtime(
            auto_run_mode="Weekly",
            ai_consecutive_failures=2,
            last_auto_run_date="2026-07-09",
        )
        self._drive(runtime, run_schedule_result={"status": "success", "message": "ok"})
        cfg = self._host_config(runtime)
        self.assertEqual(cfg["last_auto_run_date"], EXPECTED_TODAY)
        self.assertEqual(cfg["ai_consecutive_failures"], 0)

    def test_ok_status_alias_also_treated_as_success(self):
        runtime = self._make_runtime(auto_run_mode="Weekly", auto_run_time="00:00")
        self._drive(runtime, run_schedule_result={"status": "ok"})
        cfg = self._host_config(runtime)
        self.assertEqual(cfg["last_auto_run_date"], EXPECTED_TODAY)
        self.assertEqual(cfg["ai_consecutive_failures"], 0)

    def test_failure_below_limit_claims_today_and_retries_in_memory(self):
        # D5 claim-first：认领即把 last_auto_run_date 置为今日（不再是"失败后
        # 保持旧日期"）；跨日重试改为胜出进程的内存 attempt 预算，host-config
        # 失败计数只作诊断持久化。
        runtime = self._make_runtime(
            auto_run_mode="Weekly",
            auto_run_retry_times=3,
            ai_consecutive_failures=0,
            last_auto_run_date="2026-07-09",
        )
        self._drive(runtime, run_schedule_result={"status": "error", "message": "boom"})
        cfg = self._host_config(runtime)
        # 认领先于执行：失败后日期已是今日；首次失败把诊断计数推到 1。
        self.assertEqual(cfg["last_auto_run_date"], EXPECTED_TODAY)
        self.assertEqual(cfg["ai_consecutive_failures"], 1)

        # 内存 attempt（1/3）驱动下一个 tick 重试：trigger/catchup 均为 False
        # 也必须重跑，且不需要再次认领。
        self._drive(
            runtime,
            run_schedule_result={"status": "error", "message": "boom again"},
            trigger=False,
            catchup=False,
        )
        cfg = self._host_config(runtime)
        self.assertEqual(cfg["last_auto_run_date"], EXPECTED_TODAY)
        self.assertEqual(cfg["ai_consecutive_failures"], 2)

    def test_retry_budget_exhaustion_gives_up_for_today(self):
        # 内存 attempt 达到 auto_run_retry_times 后当日放弃：不再认领、不再
        # 执行；last_auto_run_date 保持今日（次日由认领语义自然放行）。
        runtime = self._make_runtime(
            auto_run_mode="Weekly",
            auto_run_retry_times=1,
            ai_consecutive_failures=2,
            ai_failures_date=EXPECTED_TODAY,
            last_auto_run_date="2026-07-09",
        )
        self._drive(runtime, run_schedule_result={"status": "error", "message": "boom"})
        cfg = self._host_config(runtime)
        self.assertEqual(cfg["last_auto_run_date"], EXPECTED_TODAY)
        self.assertEqual(cfg["ai_consecutive_failures"], 3)

        # 预算已耗尽（attempt 1/1）：即使处于可触发状态也不执行。
        self._drive(
            runtime,
            run_schedule_result={"status": "success"},
            trigger=False,
            catchup=False,
            expect_invoked=False,
        )

    def test_stale_failure_counter_resets_across_days(self):
        # Failures stamped on a previous day must not eat today's retry budget:
        # the pre-fix behavior carried the counter over and gave up after a
        # single attempt the next morning. Under D5 the retry budget is the
        # in-memory attempt counter, so the persisted counter only resets for
        # diagnostics while the run still retries.
        runtime = self._make_runtime(
            auto_run_mode="Weekly",
            auto_run_retry_times=3,
            ai_consecutive_failures=2,
            ai_failures_date="2026-07-09",
            last_auto_run_date="2026-07-09",
        )
        self._drive(runtime, run_schedule_result={"status": "error", "message": "boom"})
        cfg = self._host_config(runtime)
        # Fresh budget for today: claim stamps today, first failure counts as 1.
        self.assertEqual(cfg["last_auto_run_date"], EXPECTED_TODAY)
        self.assertEqual(cfg["ai_consecutive_failures"], 1)
        self.assertEqual(cfg["ai_failures_date"], EXPECTED_TODAY)

        # In-memory retry budget (1/3) is untouched by the stale counter:
        # the next tick retries without trigger/catchup.
        self._drive(
            runtime,
            run_schedule_result={"status": "error", "message": "boom again"},
            trigger=False,
            catchup=False,
        )
        cfg = self._host_config(runtime)
        self.assertEqual(cfg["ai_consecutive_failures"], 2)

    def test_retry_times_zero_gives_up_on_first_failure(self):
        runtime = self._make_runtime(
            auto_run_mode="Weekly",
            auto_run_retry_times=0,
            ai_consecutive_failures=0,
            last_auto_run_date="",
        )
        self._drive(runtime, run_schedule_result={"status": "error"})
        cfg = self._host_config(runtime)
        self.assertEqual(cfg["last_auto_run_date"], EXPECTED_TODAY)
        self.assertEqual(cfg["ai_consecutive_failures"], 1)

    def test_busy_result_code_skips_without_counting(self):
        runtime = self._make_runtime(
            auto_run_mode="Weekly",
            auto_run_retry_times=3,
            ai_consecutive_failures=1,
            last_auto_run_date="2026-07-09",
        )
        # Claim-first：busy 返回时今日已被认领（日期=今日），但失败计数原样。
        self._drive(runtime, run_schedule_result={"code": "busy"})
        cfg = self._host_config(runtime)
        self.assertEqual(cfg["last_auto_run_date"], EXPECTED_TODAY)
        self.assertEqual(cfg["ai_consecutive_failures"], 1)

        # busy 不消耗内存 attempt（仍为 0/3）：下一 tick 无需 trigger 即重试，
        # 且成功后清零诊断计数。
        self._drive(
            runtime,
            run_schedule_result={"status": "success"},
            trigger=False,
            catchup=False,
        )
        cfg = self._host_config(runtime)
        self.assertEqual(cfg["last_auto_run_date"], EXPECTED_TODAY)
        self.assertEqual(cfg["ai_consecutive_failures"], 0)

    def test_held_schedule_lock_skips_without_invoking_engine(self):
        runtime = self._make_runtime(
            auto_run_mode="Weekly",
            ai_consecutive_failures=0,
            last_auto_run_date="",
        )
        # Simulate a manual run holding the schedule lock at tick time.
        self.assertTrue(runtime.schedule_run_lock.acquire(blocking=False))
        try:
            self._drive(runtime, run_schedule_result={"status": "success"}, expect_invoked=False)
        finally:
            runtime.schedule_run_lock.release()
        cfg = self._host_config(runtime)
        self.assertEqual(cfg["last_auto_run_date"], "")
        self.assertEqual(cfg["ai_consecutive_failures"], 0)

    def test_target_time_gate_blocks_normal_trigger(self):
        # auto_run_time is in the future relative to FIXED_NOW (09:30 < 10:00),
        # and catch-up is not due -> tick must no-op.
        runtime = self._make_runtime(
            auto_run_mode="Weekly",
            auto_run_time="10:00",
            last_auto_run_date="2026-07-09",
        )
        self._drive(
            runtime,
            run_schedule_result={"status": "success"},
            trigger=True,
            catchup=False,
            expect_invoked=False,
        )
        cfg = self._host_config(runtime)
        self.assertEqual(cfg["last_auto_run_date"], "2026-07-09")

    def test_catchup_due_bypasses_target_time_gate(self):
        # Same future target time, but catch-up is due -> runs immediately.
        runtime = self._make_runtime(
            auto_run_mode="Weekly",
            auto_run_time="10:00",
            last_auto_run_date="2026-07-09",
            ai_consecutive_failures=4,
        )
        self._drive(
            runtime,
            run_schedule_result={"status": "success"},
            trigger=False,
            catchup=True,
            expect_invoked=True,
        )
        cfg = self._host_config(runtime)
        self.assertEqual(cfg["last_auto_run_date"], EXPECTED_TODAY)
        self.assertEqual(cfg["ai_consecutive_failures"], 0)

    def test_not_due_when_trigger_false_and_catchup_false(self):
        runtime = self._make_runtime(
            auto_run_mode="Weekly",
            last_auto_run_date="2026-07-09",
            ai_consecutive_failures=1,
        )
        self._drive(
            runtime,
            run_schedule_result={"status": "success"},
            trigger=False,
            catchup=False,
            expect_invoked=False,
        )
        cfg = self._host_config(runtime)
        self.assertEqual(cfg["last_auto_run_date"], "2026-07-09")
        self.assertEqual(cfg["ai_consecutive_failures"], 1)

    def test_invoke_auto_run_uses_automation_request_source(self):
        runtime = self._make_runtime(auto_run_mode="Weekly")
        invoked = self._drive(runtime, run_schedule_result={"status": "success"})
        payload = invoked[0]
        self.assertEqual(payload["request_source"], "automation")
        self.assertIn("roster.csv", payload["instruction"])


class TestAutoRunWorkerLifecycle(unittest.TestCase):
    def test_start_is_idempotent_and_stop_joins(self):
        with tempfile.TemporaryDirectory() as tmp:
            data_dir = Path(tmp)
            _write_host_config(data_dir, auto_run_mode="Off")
            runtime = create_runtime(data_dir)
            try:
                self.assertIsNotNone(runtime.auto_run_thread)
                self.assertTrue(runtime.auto_run_thread.is_alive())
                first_thread = runtime.auto_run_thread

                # Calling start again must not spawn a second thread.
                runtime.start_auto_run_worker()
                self.assertIs(runtime.auto_run_thread, first_thread)

                runtime.stop_auto_run_worker()
                self.assertFalse(runtime.auto_run_thread.is_alive())

                # Stopping again is a no-op (idempotent), must not raise.
                runtime.stop_auto_run_worker()
            finally:
                runtime.stop_auto_run_worker()
                runtime.stop_notification_workers()

    def test_check_auto_run_survives_missing_trigger_helpers(self):
        # When the A1 auto_run module is absent the worker must not raise; it
        # logs + skips. With helpers patched to None, check_auto_run bails early.
        with tempfile.TemporaryDirectory() as tmp:
            data_dir = Path(tmp)
            _write_host_config(data_dir, auto_run_mode="Weekly")
            runtime = create_runtime(data_dir)
            runtime.stop_auto_run_worker()
            runtime.stop_notification_workers()
            try:
                with patch.object(runtime_module, "is_auto_run_triggered", None), \
                        patch.object(runtime_module, "detect_catchup_due", None), \
                        patch.object(runtime_module, "datetime", _FixedDateTime), \
                        patch.object(runtime.command_service, "run_schedule") as run_mock:
                    runtime.check_auto_run()  # must not raise / must not run
                    run_mock.assert_not_called()
            finally:
                runtime.stop_auto_run_worker()
                runtime.stop_notification_workers()


if __name__ == "__main__":
    unittest.main()
