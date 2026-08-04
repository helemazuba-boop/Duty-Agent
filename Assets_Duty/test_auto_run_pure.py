#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unit tests for the pure auto-run trigger helpers (A1, ``auto_run.py``).

Known calendar facts used below: 2026-07-13 is a Monday, 2026-07-12 a Sunday,
2026-07-11 a Saturday; June 2026 has 30 days, July 31.
"""

import sys
import unittest
from datetime import date, datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from auto_run import detect_catchup_due, is_auto_run_triggered


def _at(day: date) -> datetime:
    return datetime(day.year, day.month, day.day, 9, 30)


class TestIsAutoRunTriggered(unittest.TestCase):
    def test_off_and_unknown_modes_never_trigger(self):
        monday = date(2026, 7, 13)
        self.assertFalse(is_auto_run_triggered("Off", "Monday", "", _at(monday)))
        self.assertFalse(is_auto_run_triggered("", "Monday", "", _at(monday)))
        self.assertFalse(is_auto_run_triggered("bogus", "Monday", "", _at(monday)))

    def test_weekly_matches_only_the_configured_weekday(self):
        self.assertTrue(is_auto_run_triggered("Weekly", "Monday", "", _at(date(2026, 7, 13))))
        self.assertFalse(is_auto_run_triggered("Weekly", "Monday", "", _at(date(2026, 7, 14))))

    def test_weekly_accepts_settings_page_aliases(self):
        monday = _at(date(2026, 7, 13))
        sunday = _at(date(2026, 7, 12))
        for alias in ("monday", "mon", "1"):
            self.assertTrue(is_auto_run_triggered("weekly", alias, "", monday), alias)
        for alias in ("Sunday", "sun", "7", "0"):
            self.assertTrue(is_auto_run_triggered("Weekly", alias, "", sunday), alias)
        # Unknown parameter falls back to Monday (settings-page default).
        self.assertTrue(is_auto_run_triggered("Weekly", "??", "", monday))

    def test_monthly_day_and_last_day(self):
        self.assertTrue(is_auto_run_triggered("Monthly", "15", "", _at(date(2026, 7, 15))))
        self.assertFalse(is_auto_run_triggered("Monthly", "15", "", _at(date(2026, 7, 16))))
        self.assertTrue(is_auto_run_triggered("Monthly", "L", "", _at(date(2026, 7, 31))))
        # Day 31 clamps to June's 30-day month end.
        self.assertTrue(is_auto_run_triggered("Monthly", "31", "", _at(date(2026, 6, 30))))

    def test_custom_interval_since_last_run(self):
        base = date(2026, 7, 1)
        self.assertFalse(is_auto_run_triggered("Custom", "14", base.isoformat(), _at(date(2026, 7, 14))))
        self.assertTrue(is_auto_run_triggered("Custom", "14", base.isoformat(), _at(date(2026, 7, 15))))
        # Never ran -> due immediately; garbage interval falls back to 14.
        self.assertTrue(is_auto_run_triggered("Custom", "14", "", _at(date(2026, 7, 2))))
        self.assertTrue(is_auto_run_triggered("Custom", "abc", base.isoformat(), _at(date(2026, 7, 15))))


class TestDetectCatchupDue(unittest.TestCase):
    def test_missed_weekly_day_between_last_and_today(self):
        # Monday 07-13 was missed: last ran Friday 07-10, today is Tuesday 07-14.
        self.assertTrue(detect_catchup_due("Weekly", "Monday", "2026-07-10", date(2026, 7, 14)))

    def test_no_catchup_when_last_run_covered_the_scheduled_day(self):
        self.assertFalse(detect_catchup_due("Weekly", "Monday", "2026-07-13", date(2026, 7, 14)))

    def test_today_being_the_scheduled_day_is_not_catchup(self):
        # Today (Monday) is governed by the normal trigger + time gate.
        self.assertFalse(detect_catchup_due("Weekly", "Monday", "2026-07-10", date(2026, 7, 13)))

    def test_no_baseline_or_future_last_means_no_catchup(self):
        self.assertFalse(detect_catchup_due("Weekly", "Monday", "", date(2026, 7, 14)))
        self.assertFalse(detect_catchup_due("Weekly", "Monday", "2026-07-14", date(2026, 7, 14)))
        self.assertFalse(detect_catchup_due("Weekly", "Monday", "2026-07-20", date(2026, 7, 14)))

    def test_monthly_missed_day(self):
        self.assertTrue(detect_catchup_due("Monthly", "15", "2026-07-10", date(2026, 7, 20)))
        self.assertFalse(detect_catchup_due("Monthly", "15", "2026-07-16", date(2026, 7, 20)))

    def test_custom_overdue_only_when_past_the_scheduled_day(self):
        self.assertFalse(detect_catchup_due("Custom", "7", "2026-07-01", date(2026, 7, 8)))
        self.assertTrue(detect_catchup_due("Custom", "7", "2026-07-01", date(2026, 7, 9)))

    def test_off_mode_never_catches_up(self):
        self.assertFalse(detect_catchup_due("Off", "Monday", "2026-07-01", date(2026, 7, 14)))

    def test_lookback_window_is_bounded(self):
        # A Monday exists within the 31-day lookback, so this is due even with
        # a very old baseline; the scan stops at the window rather than walking
        # months of history.
        self.assertTrue(detect_catchup_due("Weekly", "Monday", "2026-01-01", date(2026, 7, 20)))


if __name__ == "__main__":
    unittest.main()
