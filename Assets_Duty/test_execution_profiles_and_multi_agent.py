import sys
import unittest
from datetime import date, datetime
from pathlib import Path

from pydantic import ValidationError

ROOT = Path(__file__).resolve().parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from execution_profiles import build_execution_plan, resolve_execution_profile
from models.schemas import DutyRequest
from multi_agent.contracts import FrozenSnapshot
from multi_agent.validators import merge_barrier2
from state_ops import normalize_config


class TestExecutionPlanSelection(unittest.TestCase):
    def test_campus_6agent_defaults_to_parallel_multi_agent(self):
        config = normalize_config(
            {
                "selected_plan_id": "campus-6agent",
                "plan_presets": [
                    {
                        "id": "campus-6agent",
                        "name": "6Agent",
                        "mode_id": "campus_6agent",
                        "model": "campus-model",
                        "model_profile": "campus_small",
                        "multi_agent_execution_mode": "auto",
                    }
                ],
            }
        )
        profile = resolve_execution_profile({}, config)
        plan = build_execution_plan(profile)
        self.assertEqual(plan.runtime_mode, "multi_agent_parallel")

    def test_auto_edge_stays_single_pass(self):
        config = normalize_config(
            {
                "selected_plan_id": "standard",
                "plan_presets": [
                    {
                        "id": "standard",
                        "name": "Standard",
                        "mode_id": "standard",
                        "model": "edge-model",
                        "model_profile": "edge",
                    }
                ],
            }
        )
        profile = resolve_execution_profile({}, config)
        plan = build_execution_plan(profile)
        self.assertEqual(plan.runtime_mode, "single_pass")

    def test_multi_agent_serial_mode_is_respected(self):
        config = normalize_config(
            {
                "selected_plan_id": "campus-6agent",
                "plan_presets": [
                    {
                        "id": "campus-6agent",
                        "name": "6Agent",
                        "mode_id": "campus_6agent",
                        "model": "campus-model",
                        "model_profile": "campus_small",
                        "multi_agent_execution_mode": "serial",
                    }
                ],
            }
        )
        profile = resolve_execution_profile({}, config)
        plan = build_execution_plan(profile)
        self.assertEqual(plan.runtime_mode, "multi_agent_serial")

    def test_edge_tuned_strategy_uses_model_or_hint(self):
        config = normalize_config(
            {
                "selected_plan_id": "standard",
                "plan_presets": [
                    {
                        "id": "standard",
                        "name": "Standard",
                        "mode_id": "standard",
                        "provider_hint": "lab-edge-tuned",
                        "model": "duty-agent-0.8b",
                        "model_profile": "edge",
                    }
                ],
            }
        )
        profile = resolve_execution_profile({}, config)
        self.assertEqual(profile.single_pass_strategy, "edge_tuned")

    def test_incremental_mode_uses_explicit_single_pass_strategy(self):
        config = normalize_config(
            {
                "selected_plan_id": "incremental-small",
                "plan_presets": [
                    {
                        "id": "standard",
                        "name": "Standard",
                        "mode_id": "standard",
                        "model": "small-thinking",
                        "model_profile": "campus_small",
                    },
                    {
                        "id": "campus-6agent",
                        "name": "6Agent",
                        "mode_id": "campus_6agent",
                        "model": "campus-model",
                        "model_profile": "campus_small",
                        "multi_agent_execution_mode": "serial",
                    },
                    {
                        "id": "incremental-small",
                        "name": "Incremental",
                        "mode_id": "incremental_small",
                        "model": "small-thinking",
                        "model_profile": "campus_small",
                    },
                ],
            }
        )
        profile = resolve_execution_profile({}, config)
        plan = build_execution_plan(profile)
        self.assertEqual(plan.runtime_mode, "single_pass")
        self.assertEqual(profile.single_pass_strategy, "incremental_thinking")

    def test_missing_plan_presets_fall_back_to_defaults(self):
        config = normalize_config({})
        self.assertEqual(config["selected_plan_id"], "standard")
        self.assertEqual(len(config["plan_presets"]), 3)
        self.assertEqual(config["plan_presets"][0]["mode_id"], "standard")

    def test_request_overrides_are_ignored_for_execution_profile(self):
        config = normalize_config(
            {
                "selected_plan_id": "standard",
                "plan_presets": [
                    {
                        "id": "standard",
                        "name": "Standard",
                        "mode_id": "standard",
                        "model": "cloud-model",
                        "model_profile": "cloud",
                        "provider_hint": "config-provider",
                    }
                ],
            }
        )

        profile = resolve_execution_profile(
            {
                "model_profile": "edge",
                "orchestration_mode": "multi_agent",
                "provider_hint": "request-provider",
            },
            config,
        )

        self.assertEqual(profile.model_profile, "cloud")
        self.assertEqual(profile.orchestration_mode, "single_pass")
        self.assertEqual(profile.provider_hint, "config-provider")
        self.assertEqual(profile.single_pass_strategy, "cloud_standard")


class TestScheduleRequestContract(unittest.TestCase):
    def test_schedule_request_rejects_nested_config(self):
        with self.assertRaises(ValidationError):
            DutyRequest.model_validate(
                {
                    "instruction": "Generate duty schedule",
                    "apply_mode": "replace_all",
                    "config": {"model_profile": "edge"},
                }
            )

    def test_schedule_request_rejects_execution_override_fields(self):
        with self.assertRaises(ValidationError):
            DutyRequest.model_validate(
                {
                    "instruction": "Generate duty schedule",
                    "model_profile": "edge",
                }
            )


class TestBarrier2(unittest.TestCase):
    def test_barrier2_filters_pointer_overlap_and_fills_slots(self):
        snapshot = FrozenSnapshot(
            trace_id="trace",
            request_source="api",
            instruction="123 and 456",
            apply_mode="append",
            request_time=datetime(2026, 3, 14, 8, 0, 0),
            start_date=date(2026, 3, 17),
            config={},
            state={},
            name_to_id={},
            id_to_name={1: "A", 2: "B", 3: "C"},
            all_ids=[1, 2, 3],
            active_ids=[1, 2, 3],
            inactive_ids=[],
            id_to_active={1: 1, 2: 1, 3: 1},
            debt_list=[],
            credit_list=[],
            last_pointer=0,
            previous_note="",
            duty_rule="",
        )
        barrier1 = {
            "dates": ["2026-03-17"],
            "template": {"2026-03-17": {"default_area": 2}},
            "area_names": ["default_area"],
            "total_slots": 2,
            "absent_ids": [],
            "new_debt_ids": [],
            "new_credit_ids": [],
            "must_run_ids": [],
            "volunteer_ids": [],
            "supported_rules": [],
            "unsupported_rules": [],
            "warnings": [],
        }
        priority = {"priority_pool": [1], "notes": []}
        pointer = {"pointer_pool": [1, 2, 3], "pointer_after": 1, "consumed_credit_ids": [], "notes": []}

        merged = merge_barrier2(snapshot, barrier1, priority, pointer)
        self.assertEqual(merged["final_pool"], [1, 2])
        self.assertEqual(merged["pointer_pool"], [2])


if __name__ == "__main__":
    unittest.main()
