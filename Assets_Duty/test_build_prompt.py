import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from build_prompt import build_prompt_messages


class TestBuildPromptMessages(unittest.TestCase):
    def _build(self, single_pass_strategy: str) -> str:
        messages = build_prompt_messages(
            all_ids=[1, 2, 3],
            current_time="2026-03-24 08:00",
            id_to_active={1: 1, 2: 1, 3: 1},
            instruction="Arrange classroom and cleaning duty for several days.",
            duty_rule="Add a special cleanup area on Friday.",
            area_names=[],
            area_per_day_counts={},
            debt_counts={2: 2},
            credit_counts={3: 1},
            start_date="2026-03-24",
            previous_context="legacy note",
            model_profile="cloud",
            orchestration_mode="single_pass",
            single_pass_strategy=single_pass_strategy,
            last_pointer=5,
        )
        self.assertEqual(len(messages), 1)
        return messages[0]["content"]

    def test_cloud_prompt_uses_ini_contract(self):
        content = self._build("cloud_standard")
        self.assertIn("[areas]", content)
        self.assertIn("[schedule]", content)
        self.assertIn("[state]", content)
        self.assertIn("MM-DD = A:1001 1002 | B:1003 1004 #", content)
        self.assertIn("current_debt_counts=2*2", content)
        self.assertIn("current_credit_counts=3", content)
        self.assertIn("boundary_dates=", content)
        self.assertNotIn("_note=", content)
        self.assertNotIn("@areas", content)
        self.assertNotIn("RESET", content)
        self.assertNotIn("<csv>", content)
        self.assertNotIn("Assigned_IDs", content)

    def test_incremental_prompt_uses_same_wire_contract_without_reset(self):
        content = self._build("incremental_thinking")
        self.assertIn("[areas]", content)
        self.assertIn("[schedule]", content)
        self.assertIn("[state]", content)
        self.assertIn("Work through the dates in order internally", content)
        self.assertNotIn("_note=", content)
        self.assertNotIn("RESET", content)
        self.assertNotIn("<next_run_note>", content)


if __name__ == "__main__":
    unittest.main()
