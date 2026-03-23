import unittest
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from build_prompt import build_prompt_messages
from state_ops import DEFAULT_SINGLE_AREA_NAME


class TestBuildPromptMessages(unittest.TestCase):
    def _build(self, single_pass_strategy: str) -> str:
        messages = build_prompt_messages(
            all_ids=[1, 2, 3],
            current_time="2026-03-24 08:00",
            id_to_active={1: 1, 2: 1, 3: 1},
            instruction="周一到周四安排教室 2 人和清洁区 2 人。",
            duty_rule="区域由 AI 定义。",
            area_names=[],
            area_per_day_counts={},
            debt_list=[],
            credit_list=[],
            previous_context="",
            model_profile="cloud",
            orchestration_mode="single_pass",
            single_pass_strategy=single_pass_strategy,
        )
        self.assertEqual(len(messages), 1)
        return messages[0]["content"]

    def test_cloud_prompt_uses_dynamic_area_csv_contract(self):
        content = self._build("cloud_standard")
        self.assertIn("Date,&lt;Area1&gt;,&lt;Area2&gt;,...,Note", content)
        self.assertIn(DEFAULT_SINGLE_AREA_NAME, content)
        self.assertNotIn("default_area", content)
        self.assertNotIn("area_slot_counts", content)
        self.assertNotIn("Assigned_IDs must contain", content)

    def test_incremental_prompt_uses_dynamic_area_csv_contract(self):
        content = self._build("incremental_thinking")
        self.assertIn("Date,&lt;Area1&gt;,&lt;Area2&gt;,...,Note", content)
        self.assertIn(DEFAULT_SINGLE_AREA_NAME, content)
        self.assertNotIn("default_area", content)
        self.assertNotIn("area_slot_counts", content)
        self.assertNotIn("Assigned_IDs must contain", content)


if __name__ == "__main__":
    unittest.main()
