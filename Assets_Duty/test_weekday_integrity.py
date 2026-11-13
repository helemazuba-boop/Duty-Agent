"""Regression tests for the weekday/time chain (2026-11-11 audit).

`schedule_pool[].day` used to be a caller-controlled free-text field: any
non-empty string was stored verbatim, so an AI editing "today's duty" with a
stale/hand-computed weekday silently produced an entry whose weekday disagreed
with its date. It is now a DERIVED field, with one shared label set.
"""

from __future__ import annotations

import sys
import tempfile
import unittest
from datetime import date, datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from postprocess import DAY_NAMES, restore_schedule  # noqa: E402
from state_ops import (  # noqa: E402
    WEEKDAY_LABELS,
    Context,
    _clone_schedule_entry_payload,
    _normalize_schedule_entry_day,
    compute_current_duty_date,
    load_state,
    normalize_pool_weekday,
    save_json_atomic,
    save_roster_entries,
    save_schedule_entry_edit,
    weekday_label,
)

WEDNESDAY = date(2026, 11, 11)


class DerivedWeekdayTests(unittest.TestCase):
    def test_single_label_source(self):
        self.assertEqual(tuple(DAY_NAMES), WEEKDAY_LABELS)
        self.assertEqual(weekday_label(WEDNESDAY), "周三")
        self.assertEqual(weekday_label(date(2026, 11, 14)), "周六")

    def test_supplied_label_is_ignored(self):
        for bogus in (None, "", "周六", "Mon", "星期九", "周三"):
            self.assertEqual(
                _normalize_schedule_entry_day(bogus, WEDNESDAY), "周三",
                f"bogus={bogus!r} must not be stored",
            )

    def test_legacy_english_labels_are_normalized_on_read(self):
        entry = {"date": "2026-11-11", "day": "Wed", "area_assignments": {}, "note": ""}
        self.assertEqual(_clone_schedule_entry_payload(entry)["day"], "周三")
        self.assertEqual(normalize_pool_weekday("Wed", "2026-11-11"), "周三")
        self.assertEqual(normalize_pool_weekday("garbage", "not-a-date"), "")

    def test_restore_schedule_uses_the_shared_label(self):
        restored = restore_schedule(
            [{"date": "2026-11-11", "area_ids": {"教室": [1]}, "note": ""}],
            {1: "Alice"}, ["教室"],
        )
        self.assertEqual(restored[0]["day"], "周三")


class SaveEntryWeekdayTests(unittest.TestCase):
    def _ctx(self, tmp: Path) -> Context:
        ctx = Context(tmp)
        save_roster_entries(ctx, [
            {"id": 1, "name": "Alice", "active": True},
            {"id": 2, "name": "Bob", "active": True},
        ])
        save_json_atomic(ctx.paths["state"], {
            "schedule_pool": [
                {"date": "2026-11-11", "day": "Wed", "area_assignments": {"教室": ["Alice"]}, "note": "old"},
            ],
            "debt_counts": {}, "credit_counts": {}, "last_pointer": 0,
            "next_run_note": "", "absences": [], "day_overrides": {},
        })
        return ctx

    def test_load_state_normalizes_legacy_labels(self):
        with tempfile.TemporaryDirectory() as tmp:
            ctx = self._ctx(Path(tmp))  # seeded with the legacy "Wed" label
            entry = load_state(ctx.paths["state"])["schedule_pool"][0]
            self.assertEqual(entry["day"], "周三")

    def test_stale_day_from_ai_is_not_persisted(self):
        with tempfile.TemporaryDirectory() as tmp:
            ctx = self._ctx(Path(tmp))
            result = save_schedule_entry_edit(ctx, {
                "source_date": "2026-11-11",
                "target_date": "2026-11-11",
                "day": "周日",  # deliberately wrong / stale
                "area_assignments": {"教室": ["Bob"]},
                "note": "updated",
                "confirm_overwrite": True,
                "ledger_mode": "record",
            })
            self.assertEqual(result["status"], "success")
            entry = load_state(ctx.paths["state"])["schedule_pool"][0]
            self.assertEqual(entry["day"], "周三")
            self.assertEqual(entry["area_assignments"], {"教室": ["Bob"]})

    def test_moving_an_entry_recomputes_weekday_for_the_new_date(self):
        with tempfile.TemporaryDirectory() as tmp:
            ctx = self._ctx(Path(tmp))
            result = save_schedule_entry_edit(ctx, {
                "source_date": "2026-11-11",
                "target_date": "2026-11-14",  # Saturday
                "day": "周三",  # stale label from the source date
                "area_assignments": {"教室": ["Alice"]},
                "note": "",
                "confirm_overwrite": True,
                "ledger_mode": "record",
            })
            self.assertEqual(result["status"], "success")
            pool = load_state(ctx.paths["state"])["schedule_pool"]
            moved = next(e for e in pool if e["date"] == "2026-11-14")
            self.assertEqual(moved["day"], "周六")
            self.assertFalse(any(e["date"] == "2026-11-11" for e in pool))


class DutyDateDefinitionTests(unittest.TestCase):
    """All three layers must define "today" as the device calendar date.

    Regression for "客户端有概率认错时间": the old rule advanced the duty date
    by one day after component_refresh_time, so the homepage and the ClassIsland
    component showed *tomorrow's* duty for most of the day. Vue only recomputed
    when its workspace ref changed and C# cached the backend date string until
    state.json moved, so the jump happened at arbitrary moments.
    """

    ROOT = Path(__file__).resolve().parent.parent

    def test_backend_never_advances_the_date(self):
        configs = (
            {},
            {"component_refresh_time": "08:00"},
            {"component_refresh_time": "00:00"},
            {"component_refresh_time": "23:59"},
            {"component_refresh_time": "not-a-time"},
        )
        for cfg in configs:
            for hour in (0, 7, 8, 12, 23):
                self.assertEqual(
                    compute_current_duty_date(cfg, datetime(2026, 11, 11, hour, 59)),
                    "2026-11-11",
                    msg=f"cfg={cfg} hour={hour}",
                )
        # Crossing midnight follows the clock, and only the clock.
        self.assertEqual(
            compute_current_duty_date({"component_refresh_time": "08:00"}, datetime(2026, 11, 12, 0, 1)),
            "2026-11-12",
        )

    def test_csharp_uses_device_date_verbatim(self):
        source = (
            self.ROOT / "Duty-Agent-ClassIsland-Bridge" / "Services" / "DutyStateCache.cs"
        ).read_text(encoding="utf-8")
        self.assertIn("public DateTime Today => DateTime.Now.Date;", source)
        self.assertNotIn("AddDays(1)", source)
        self.assertNotIn("AddDays(-1)", source)
        # No stale cached backend date, no ClassIsland offset clock injection.
        self.assertNotIn("_currentDutyDate", source)
        self.assertNotIn("using ClassIsland.Core.Abstractions.Services;", source)
        self.assertNotIn("private readonly IExactTimeService", source)

    def test_vue_has_no_date_shift_and_is_clock_reactive(self):
        source = (self.ROOT / "duty-agent-ui" / "src" / "composables" / "useToday.ts").read_text(
            encoding="utf-8"
        )
        self.assertNotIn("ws?.component_refresh_time", source)
        self.assertNotIn("setWorkspace", source)
        self.assertNotIn("getDate() + 1", source)
        self.assertIn("setInterval(tick, 60_000)", source)
        self.assertIn("return { dutyToday: today }", source)

    def test_settings_ui_no_longer_exposes_the_date_switch(self):
        source = (self.ROOT / "duty-agent-ui" / "src" / "pages" / "SettingsPage.vue").read_text(
            encoding="utf-8"
        )
        self.assertNotIn("值日日期切换时间", source)
        self.assertNotIn("componentRefreshTime", source)


if __name__ == "__main__":
    unittest.main()
