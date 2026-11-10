"""Regression tests: date context is FACTS + a loose bound, never a schedule.

Which days need duty (weekends included or not) is decided by the LLM from
user_instruction / user_defined_rule. This module must not filter weekends,
must not parse duty_rule, and must not produce a checklist of dates to fill.
"""

from __future__ import annotations

import sys
import unittest
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from schedule_window import DateContext, resolve_date_context  # noqa: E402

TUESDAY = date(2026, 11, 10)  # 2026-11-09 is Monday


class ResolveDateContextTests(unittest.TestCase):
    def test_plain_instruction_uses_default_window(self):
        ctx = resolve_date_context("安排今天值日", TUESDAY, default_days=7)
        self.assertFalse(ctx.anchor_recognized)
        self.assertEqual(ctx.span_start, TUESDAY)
        self.assertEqual(ctx.span_end, date(2026, 11, 16))
        self.assertEqual(ctx.date_span_text, "11-10..11-16")
        self.assertEqual(ctx.reason, "default window")

    def test_weekday_hints_are_facts_and_include_weekends(self):
        """No weekend filtering: the code must not decide weekend duty."""
        ctx = resolve_date_context("安排今天值日", TUESDAY, default_days=7)
        self.assertIn("11-10=Tue", ctx.weekday_hints)
        self.assertIn("11-14=Sat", ctx.weekday_hints)
        self.assertIn("11-15=Sun", ctx.weekday_hints)
        self.assertEqual(len(ctx.weekday_hints), 7)

    def test_no_date_checklist_is_exposed(self):
        ctx = resolve_date_context("安排下周值日", TUESDAY, default_days=7)
        self.assertIsInstance(ctx, DateContext)
        self.assertFalse(hasattr(ctx, "dates"))
        self.assertFalse(hasattr(ctx, "expected_dates"))

    def test_next_week_only_widens_the_upper_bound(self):
        ctx = resolve_date_context("安排下周值日", TUESDAY, default_days=7)
        self.assertTrue(ctx.anchor_recognized)
        self.assertEqual(ctx.span_end, date(2026, 11, 22))
        # The span is an upper bound, so intermediate days (including weekends)
        # are all present as facts rather than being filtered out.
        self.assertIn("11-21=Sat", ctx.weekday_hints)
        self.assertIn("11-22=Sun", ctx.weekday_hints)

    def test_this_week_anchor(self):
        ctx = resolve_date_context("整周值日", TUESDAY, default_days=7)
        self.assertTrue(ctx.anchor_recognized)
        # "this week" ends 11-15, but the span is never narrower than the
        # 7-day default window (11-16).
        self.assertEqual(ctx.span_end, date(2026, 11, 16))

    def test_unparsed_span_hint_relaxes_the_bound_instead_of_guessing(self):
        ctx = resolve_date_context("连续三天值日", TUESDAY, default_days=7)
        self.assertFalse(ctx.anchor_recognized)
        self.assertGreater(ctx.span_end, date(2026, 11, 16))
        self.assertEqual(ctx.span_end, date(2026, 12, 9))

    def test_span_never_narrower_than_default(self):
        # "整周" would end on 11-15, but a 30-day default window must win.
        ctx = resolve_date_context("整周值日", TUESDAY, default_days=30)
        self.assertEqual(ctx.span_end, date(2026, 12, 9))

    def test_duty_rule_text_is_not_a_parameter(self):
        """duty_rule is free-form text for the LLM; code must not parse it."""
        import inspect

        self.assertNotIn("duty_rule", inspect.signature(resolve_date_context).parameters)

    def test_skip_weekend_wording_does_not_change_the_bound(self):
        """The old heuristic inverted '跳过周末'; now it is simply not parsed."""
        a = resolve_date_context("安排下周值日", TUESDAY, 7)
        b = resolve_date_context("安排下周值日，跳过周末", TUESDAY, 7)
        self.assertEqual(a.span_end, b.span_end)
        self.assertEqual(a.weekday_hints, b.weekday_hints)


if __name__ == "__main__":
    unittest.main()
