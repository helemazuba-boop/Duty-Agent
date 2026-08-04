#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Tests for the offline deterministic scheduler (the ``offline`` orchestration mode)."""

import sys
import tempfile
import unittest
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

# Importing engine first loads multi_agent before prompt_gateway, resolving the
# pre-existing prompt_gateway<->multi_agent circular import (single_pass_executor,
# and therefore offline_scheduler, cannot be imported standalone). This mirrors
# the production order, where engine dispatches to the offline executor.
import engine  # noqa: F401,E402

from execution_profiles import build_execution_plan, resolve_execution_profile
from llm_transport import parse_schedule_completion
from offline_scheduler import (
    build_active_rotation,
    build_ini_completion,
    build_target_dates,
    learn_area_structure,
    plan_daily_assignments,
    run_offline_schedule,
)
from readiness import evaluate_readiness
from state_ops import (
    Context,
    load_config,
    load_state,
    normalize_config,
    save_config,
    save_json_atomic,
    save_roster_entries,
)


class TestTargetDates(unittest.TestCase):
    def test_consecutive_days_without_weekend_skip(self):
        start = date(2026, 4, 20)  # Monday
        dates = build_target_dates(start, 5, skip_weekends=False)
        self.assertEqual(dates, [date(2026, 4, 20 + offset) for offset in range(5)])

    def test_weekend_skip_lands_on_weekdays_only(self):
        start = date(2026, 4, 24)  # Friday
        dates = build_target_dates(start, 3, skip_weekends=True)
        # Fri 24 -> skip Sat 25 / Sun 26 -> Mon 27 -> Tue 28
        self.assertEqual(dates, [date(2026, 4, 24), date(2026, 4, 27), date(2026, 4, 28)])
        self.assertTrue(all(day.weekday() < 5 for day in dates))

    def test_zero_days_returns_empty(self):
        self.assertEqual(build_target_dates(date(2026, 4, 20), 0, skip_weekends=False), [])


class TestLearnStructure(unittest.TestCase):
    def test_learns_areas_and_headcount_from_latest_entry(self):
        pool = [
            {"date": "2026-04-10", "area_assignments": {"甲": ["X"]}},
            {"date": "2026-04-21", "area_assignments": {"教室": ["A", "B"], "走廊": ["C"]}},
        ]
        self.assertEqual(learn_area_structure(pool), [("教室", 2), ("走廊", 1)])

    def test_empty_history_falls_back_to_single_default_area(self):
        self.assertEqual(learn_area_structure([]), [("值日", 2)])

    def test_areas_with_no_people_are_dropped(self):
        pool = [{"date": "2026-04-21", "area_assignments": {"空": [], "教室": ["A"]}}]
        self.assertEqual(learn_area_structure(pool), [("教室", 1)])


class TestRotationAndAssignment(unittest.TestCase):
    def test_rotation_starts_at_pointer_and_skips_inactive(self):
        all_ids = [1, 2, 3, 4]
        id_to_active = {1: 1, 2: 0, 3: 1, 4: 1}
        self.assertEqual(build_active_rotation(all_ids, id_to_active, last_pointer=2), [3, 4, 1])

    def test_round_robin_across_days(self):
        days = plan_daily_assignments([1, 2, 3, 4], {}, set(), slots_per_day=2, num_days=3)
        self.assertEqual(days, [[1, 2], [3, 4], [1, 2]])

    def test_credit_holders_are_skipped(self):
        days = plan_daily_assignments([1, 2, 3, 4], {}, {2}, slots_per_day=2, num_days=1)
        self.assertEqual(days, [[1, 3]])

    def test_debtors_are_prioritized(self):
        days = plan_daily_assignments([1, 2, 3, 4], {4: 1}, set(), slots_per_day=2, num_days=1)
        self.assertEqual(days[0][0], 4)

    def test_fallback_staffs_with_credit_holders_when_short(self):
        days = plan_daily_assignments([1, 2], {}, {1, 2}, slots_per_day=2, num_days=1)
        self.assertEqual(sorted(days[0]), [1, 2])


class TestIniRoundTrip(unittest.TestCase):
    def test_single_area_ini_parses_back(self):
        ini = build_ini_completion([("值日", 2)], [[1, 2], [3, 4]], [date(2026, 4, 20), date(2026, 4, 21)])
        parsed, _ = parse_schedule_completion(ini, date(2026, 4, 20))
        schedule = parsed["schedule"]
        self.assertEqual(len(schedule), 2)
        self.assertEqual(schedule[0]["date"], "2026-04-20")
        self.assertEqual(schedule[0]["area_ids"]["值日"], [1, 2])
        self.assertEqual(schedule[1]["area_ids"]["值日"], [3, 4])

    def test_multi_area_ini_distributes_by_headcount(self):
        ini = build_ini_completion([("教室", 2), ("走廊", 1)], [[1, 2, 3]], [date(2026, 4, 20)])
        parsed, _ = parse_schedule_completion(ini, date(2026, 4, 20))
        area_ids = parsed["schedule"][0]["area_ids"]
        self.assertEqual(area_ids["教室"], [1, 2])
        self.assertEqual(area_ids["走廊"], [3])


class TestRouting(unittest.TestCase):
    def test_offline_plan_resolves_to_offline_runtime_mode(self):
        config = normalize_config({"selected_plan_id": "offline"})
        self.assertEqual(config["orchestration_mode"], "offline")
        plan = build_execution_plan(resolve_execution_profile({}, config))
        self.assertEqual(plan.runtime_mode, "offline")

    def test_non_offline_modes_unchanged(self):
        for plan_id, expected in (("standard", "single_pass"), ("agents", "multi_agent_parallel")):
            config = normalize_config({"selected_plan_id": plan_id})
            plan = build_execution_plan(resolve_execution_profile({}, config))
            self.assertEqual(plan.runtime_mode, expected)


class TestOfflineConfig(unittest.TestCase):
    def test_offline_knobs_persist_and_hydrate(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            save_config(context, {"selected_plan_id": "offline", "offline_schedule_days": 10, "offline_skip_weekends": False})
            config = load_config(context)
            self.assertEqual(config["orchestration_mode"], "offline")
            self.assertEqual(config["offline_schedule_days"], 10)
            self.assertFalse(config["offline_skip_weekends"])

    def test_schedule_days_are_clamped(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            save_config(context, {"selected_plan_id": "offline", "offline_schedule_days": 999})
            self.assertEqual(load_config(context)["offline_schedule_days"], 60)

    def test_offline_preset_is_injected_into_legacy_configs(self):
        # Configs saved before offline mode existed (presets present, none offline)
        # must still surface an offline preset after normalization.
        config = normalize_config({
            "selected_plan_id": "standard",
            "plan_presets": [{"id": "standard", "name": "标准", "mode_id": "standard"}],
        })
        offline_presets = [p for p in config["plan_presets"] if p["mode_id"] == "offline"]
        self.assertEqual(len(offline_presets), 1)


class TestReadinessOffline(unittest.TestCase):
    def test_offline_mode_ready_without_model(self):
        config = {"orchestration_mode": "offline", "base_url": "", "model": "", "api_key": ""}
        report = evaluate_readiness(config, [{"id": 1}], {"schedule_pool": [{"date": "2026-04-21"}]}, probe=True)
        by_id = {check["id"]: check for check in report["checks"]}
        self.assertTrue(by_id["plan"]["ok"])
        self.assertTrue(by_id["model"]["ok"])
        self.assertTrue(report["ready"])


class TestOfflineEndToEnd(unittest.TestCase):
    def _seed(self, context, state):
        save_roster_entries(
            context,
            [
                {"id": 1, "name": "A", "active": True},
                {"id": 2, "name": "B", "active": True},
                {"id": 3, "name": "C", "active": True},
                {"id": 4, "name": "D", "active": True},
            ],
        )
        save_config(context, {"selected_plan_id": "offline", "offline_schedule_days": 5, "offline_skip_weekends": False})
        save_json_atomic(context.paths["state"], state)

    def _plan(self, context):
        config = load_config(context)
        return build_execution_plan(resolve_execution_profile({}, config))

    def test_offline_run_generates_and_settles_without_api_key(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            self._seed(context, {"schedule_pool": [], "debt_counts": {}, "credit_counts": {}, "last_pointer": 0})

            result = run_offline_schedule(context, {"trace_id": "t1"}, self._plan(context))

            self.assertEqual(result["status"], "success")
            self.assertEqual(result["selected_executor"], "offline")
            self.assertIn("离线", result["ai_response"])

            pool = load_state(context.paths["state"])["schedule_pool"]
            self.assertEqual(len(pool), 5)
            valid_names = {"A", "B", "C", "D"}
            for entry in pool:
                assignments = entry["area_assignments"]
                self.assertIn("值日", assignments)
                self.assertEqual(len(assignments["值日"]), 2)
                self.assertTrue(set(assignments["值日"]).issubset(valid_names))

    def test_offline_run_reuses_prior_area_structure(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            self._seed(
                context,
                {
                    "schedule_pool": [
                        {"date": "2026-04-21", "area_assignments": {"教室": ["A", "B"], "走廊": ["C"]}}
                    ],
                    "debt_counts": {},
                    "credit_counts": {},
                    "last_pointer": 0,
                },
            )

            result = run_offline_schedule(context, {"trace_id": "t2"}, self._plan(context))
            self.assertEqual(result["status"], "success")

            pool = load_state(context.paths["state"])["schedule_pool"]
            self.assertEqual(len(pool), 5)
            for entry in pool:
                assignments = entry["area_assignments"]
                self.assertEqual(len(assignments.get("教室", [])), 2)
                self.assertEqual(len(assignments.get("走廊", [])), 1)

    def test_offline_run_errors_when_no_active_roster(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            save_roster_entries(context, [{"id": 1, "name": "A", "active": False}])
            save_config(context, {"selected_plan_id": "offline"})
            save_json_atomic(context.paths["state"], {"schedule_pool": []})

            result = run_offline_schedule(context, {"trace_id": "t3"}, self._plan(context))
            self.assertEqual(result["status"], "error")

    def test_engine_dispatches_offline_mode_end_to_end(self):
        # Exercises the real dispatch seam: resolve profile -> build plan ->
        # engine.run_schedule routes to the offline executor (as auto-run does).
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            self._seed(context, {"schedule_pool": [], "debt_counts": {}, "credit_counts": {}, "last_pointer": 0})

            result = engine.run_schedule(context, {"trace_id": "e1", "request_source": "automation"})

            self.assertEqual(result["status"], "success")
            self.assertEqual(result["selected_executor"], "offline")
            self.assertEqual(len(load_state(context.paths["state"])["schedule_pool"]), 5)


if __name__ == "__main__":
    unittest.main()
