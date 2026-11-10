"""Regression tests for natural-language schedule windows (audit P1)."""

from __future__ import annotations

import sys
import unittest
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from schedule_window import resolve_schedule_dates  # noqa: E402

SATURDAY = date(2026, 11, 7)  # 2026-11-01 is a Sunday
RULE_WEEKDAYS_PLUS_SAT = "周一至周五分为教室清洁区两个区域，每个区域2人 周六安排教室2人"


class ResolveScheduleDatesTests(unittest.TestCase):
    def test_plain_instruction_keeps_legacy_calendar_window(self):
        dates = resolve_schedule_dates("随便排一下", SATURDAY, default_days=7, duty_rule="")
        self.assertEqual(len(dates), 7)
        self.assertEqual(dates[0], "2026-11-07")
        self.assertEqual(dates[-1], "2026-11-13")

    def test_today_and_next_week_matches_the_reported_incident(self):
        """The 40k-CoT case: today (Sat 11-07) plus next week, no Sunday duty."""
        dates = resolve_schedule_dates(
            "从今天19#，20#开始，安排今天及下周值日",
            SATURDAY,
            default_days=7,
            duty_rule=RULE_WEEKDAYS_PLUS_SAT,
        )
        self.assertEqual(
            dates,
            [
                "2026-11-07",  # today, Saturday
                "2026-11-09",
                "2026-11-10",
                "2026-11-11",
                "2026-11-12",
                "2026-11-13",
                "2026-11-14",  # next Saturday - previously out of window
            ],
        )
        self.assertNotIn("2026-11-08", dates)  # unrequested Sunday
        self.assertNotIn("2026-11-15", dates)

    def test_next_week_only_starts_on_monday(self):
        dates = resolve_schedule_dates(
            "安排下周值日", SATURDAY, default_days=7, duty_rule=RULE_WEEKDAYS_PLUS_SAT
        )
        self.assertEqual(dates[0], "2026-11-09")
        self.assertEqual(dates[-1], "2026-11-14")

    def test_weekend_rule_includes_sunday(self):
        dates = resolve_schedule_dates(
            "今天及下周值日", SATURDAY, default_days=7, duty_rule="周末也要值日"
        )
        self.assertIn("2026-11-08", dates)
        self.assertIn("2026-11-15", dates)

    def test_empty_instruction_uses_legacy_window(self):
        self.assertEqual(
            resolve_schedule_dates("", SATURDAY, default_days=3, duty_rule=""),
            ["2026-11-07", "2026-11-08", "2026-11-09"],
        )


if __name__ == "__main__":
    unittest.main()
