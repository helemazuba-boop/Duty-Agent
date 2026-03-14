import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from execution_profiles import ExecutionPlan, ExecutionProfile
from llm_transport import _build_llm_target, create_llm_request
from multi_agent.executor import _freeze_snapshot
from single_pass_executor import run_single_pass_schedule
from state_ops import Context


class TestLlmTransportAllowsBlankApiKey(unittest.TestCase):
    def test_create_llm_request_omits_authorization_when_api_key_blank(self):
        request = create_llm_request(
            "http://127.0.0.1:11434/v1/chat/completions",
            {"model": "llama3", "messages": []},
            "",
        )
        self.assertIsNone(request.headers.get("Authorization"))

    def test_build_llm_target_keeps_blank_api_key(self):
        url, payload, api_key = _build_llm_target(
            {
                "base_url": "http://127.0.0.1:11434/v1",
                "model": "llama3",
                "api_key": "",
            },
            [{"role": "user", "content": "hello"}],
        )
        self.assertEqual(url, "http://127.0.0.1:11434/v1/chat/completions")
        self.assertEqual(payload["model"], "llama3")
        self.assertEqual(api_key, "")


class TestExecutorsAllowBlankApiKey(unittest.TestCase):
    def test_single_pass_schedule_does_not_reject_blank_api_key(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            plan = ExecutionPlan(
                profile=ExecutionProfile(
                    model_profile="cloud",
                    orchestration_mode="single_pass",
                    provider_hint="",
                    multi_agent_ready=False,
                    edge_ready=False,
                    multi_agent_execution_mode="auto",
                    single_pass_strategy="cloud_standard",
                ),
                runtime_mode="single_pass",
                prompt_pack_strategy="single_pass",
                tasks=(),
                notes=(),
            )

            with patch(
                "single_pass_executor.load_config",
                return_value={
                    "base_url": "http://127.0.0.1:11434/v1",
                    "model": "llama3",
                    "api_key": "",
                    "per_day": 1,
                },
            ), patch(
                "single_pass_executor.load_roster",
                return_value=({"Alice": 1}, {1: "Alice"}, [1], {1: 1}),
            ), patch(
                "single_pass_executor.load_state",
                return_value={"schedule_pool": [], "debt_list": [], "credit_list": [], "next_run_note": ""},
            ), patch(
                "single_pass_executor.load_api_key_from_env",
                return_value="",
            ), patch(
                "single_pass_executor.build_single_pass_prompt_messages",
                return_value=([{"role": "user", "content": "hello"}], {"logical_task_count": 1}),
            ), patch(
                "single_pass_executor.call_llm",
                return_value=(
                    {
                        "schedule": [{"date": "2026-03-16", "area_ids": {"default_area": [1]}, "note": ""}],
                        "next_run_note": "",
                        "new_debt_ids": [],
                        "new_credit_ids": [],
                    },
                    "ok",
                ),
            ), patch(
                "single_pass_executor.validate_llm_schedule_entries",
            ), patch(
                "single_pass_executor.normalize_multi_area_schedule_ids",
                return_value=[{"date": "2026-03-16", "area_ids": {"default_area": [1]}, "note": ""}],
            ), patch(
                "single_pass_executor.recover_missing_debts",
                return_value=[],
            ), patch(
                "single_pass_executor.reconcile_credit_list",
                return_value=[],
            ), patch(
                "single_pass_executor.restore_schedule",
                return_value=[{"date": "2026-03-16", "area_assignments": {"default_area": ["Alice"]}, "note": ""}],
            ), patch(
                "single_pass_executor.merge_schedule_pool",
                return_value=[{"date": "2026-03-16", "area_assignments": {"default_area": ["Alice"]}, "note": ""}],
            ), patch(
                "single_pass_executor.save_json_atomic",
            ):
                result = run_single_pass_schedule(
                    context,
                    {"instruction": "test", "apply_mode": "replace_all"},
                    plan,
                )

        self.assertEqual(result["status"], "success")
        self.assertEqual(context.config["api_key"], "")

    def test_multi_agent_snapshot_does_not_reject_blank_api_key(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            with patch(
                "multi_agent.executor.load_config",
                return_value={
                    "base_url": "http://127.0.0.1:11434/v1",
                    "model": "llama3",
                    "api_key": "",
                    "per_day": 1,
                },
            ), patch(
                "multi_agent.executor.load_roster",
                return_value=({"Alice": 1}, {1: "Alice"}, [1], {1: 1}),
            ), patch(
                "multi_agent.executor.load_state",
                return_value={"schedule_pool": [], "debt_list": [], "credit_list": [], "next_run_note": "", "last_pointer": 0},
            ), patch(
                "multi_agent.executor.load_api_key_from_env",
                return_value="",
            ):
                snapshot = _freeze_snapshot(context, {"instruction": "test", "apply_mode": "replace_all"})

        self.assertEqual(snapshot.config["api_key"], "")


if __name__ == "__main__":
    unittest.main()
