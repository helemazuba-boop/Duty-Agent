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
    compute_display_duty_date,
    load_state,
    normalize_pool_weekday,
    resolve_display_duty_date,
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
    """Two dates, never mixed.

    FACT:   current_duty_date = device calendar date, never configurable.
    DISPLAY: display_duty_date = FACT + 1 once the clock reaches the configured
            boundary. This only selects which schedule_pool entry to show; it
            must be recomputed from the device clock on every render.

    Regression for "客户端有概率认错时间": the old code folded the +1 into the
    FACT date, Vue recomputed only when its workspace ref changed, and C# cached
    the backend date string until state.json moved — so the jump happened at
    arbitrary moments and the page still said "今日".
    """

    ROOT = Path(__file__).resolve().parent.parent

    def test_backend_never_advances_the_fact_date(self):
        configs = (
            {},
            {"component_refresh_time": "08:00"},
            {"component_refresh_time": "00:00"},
            {"component_refresh_time": "23:59"},
            {"component_refresh_time": "not-a-time"},
            {"duty_preview_enabled": True},
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

    def test_display_date_boundary_table(self):
        cases = (
            # (now, boundary, enabled, expected display date)
            (datetime(2026, 11, 11, 7, 59), "08:00", True, "2026-11-11"),
            (datetime(2026, 11, 11, 8, 0), "08:00", True, "2026-11-12"),
            (datetime(2026, 11, 11, 23, 59), "08:00", True, "2026-11-12"),
            # 跨午夜连续：同一日期只是从「明日」变成「今日」，内容不跳。
            (datetime(2026, 11, 12, 0, 0), "08:00", True, "2026-11-12"),
            (datetime(2026, 11, 12, 0, 1), "08:00", True, "2026-11-12"),
            (datetime(2026, 11, 12, 7, 59), "08:00", True, "2026-11-12"),
            (datetime(2026, 11, 12, 8, 0), "08:00", True, "2026-11-13"),
            (datetime(2026, 11, 11, 12, 0), "08:00", False, "2026-11-11"),
            (datetime(2026, 11, 11, 0, 0), "00:00", True, "2026-11-12"),
            (datetime(2026, 11, 11, 23, 58), "23:59", True, "2026-11-11"),
            (datetime(2026, 11, 11, 23, 59), "23:59", True, "2026-11-12"),
            (datetime(2026, 11, 11, 12, 0), "not-a-time", True, "2026-11-12"),
            (datetime(2026, 11, 11, 12, 0), "99:99", True, "2026-11-12"),
        )
        for now, boundary, enabled, expected in cases:
            with self.subTest(now=now, boundary=boundary, enabled=enabled):
                self.assertEqual(resolve_display_duty_date(now, boundary, enabled), expected)
                self.assertEqual(
                    compute_display_duty_date(
                        {"component_refresh_time": boundary, "duty_preview_enabled": enabled}, now
                    ),
                    expected,
                )

    def test_each_date_is_displayed_for_a_full_24h_window(self):
        cfg = {"component_refresh_time": "08:00", "duty_preview_enabled": True}
        # 11/11 08:00 .. 11/12 07:59 都展示 11/12 的值日。
        for day, hours in ((11, range(8, 24)), (12, range(0, 8))):
            for hour in hours:
                self.assertEqual(
                    compute_display_duty_date(cfg, datetime(2026, 11, day, hour, 30)),
                    "2026-11-12",
                )

    def test_csharp_keeps_today_and_adds_a_separate_display_date(self):
        source = (
            self.ROOT / "Duty-Agent-ClassIsland-Bridge" / "Services" / "DutyStateCache.cs"
        ).read_text(encoding="utf-8")
        self.assertIn("public DateTime Today => DateTime.Now.Date;", source)
        self.assertIn("ResolveDisplayDutyDate", source)
        self.assertIn("AddDays(1)", source)
        self.assertIn("ArmDisplayBoundaryTimer", source)
        self.assertIn("GetTodayItem()", source)
        self.assertIn("GetDisplayItem()", source)
        # No stale cached backend DATE, no ClassIsland offset clock injection.
        self.assertNotIn("_currentDutyDate", source)
        self.assertNotIn("AddDays(-1)", source)
        self.assertNotIn("using ClassIsland.Core.Abstractions.Services;", source)
        self.assertNotIn("private readonly IExactTimeService", source)

    def test_vue_display_date_is_clock_reactive_and_fact_date_is_untouched(self):
        source = (self.ROOT / "duty-agent-ui" / "src" / "composables" / "useToday.ts").read_text(
            encoding="utf-8"
        )
        self.assertIn("setInterval(tick, 60_000)", source)
        self.assertIn("return { dutyToday: today }", source)
        self.assertIn("useDutyDisplayDate", source)
        self.assertIn("resolveDisplayDutyDate(now.value", source)
        self.assertNotIn("ws?.component_refresh_time", source)
        self.assertNotIn("setWorkspace", source)

    def test_settings_ui_exposes_the_preview_rule(self):
        source = (self.ROOT / "duty-agent-ui" / "src" / "pages" / "SettingsPage.vue").read_text(
            encoding="utf-8"
        )
        self.assertIn("componentRefreshTime", source)
        self.assertIn("dutyPreviewEnabled", source)
        self.assertIn("值日显示", source)
        self.assertNotIn("值日日期切换时间", source)

    def test_dashboard_labels_the_preview_and_calendar_keeps_fact_date(self):
        dashboard = (self.ROOT / "duty-agent-ui" / "src" / "pages" / "Dashboard.vue").read_text(
            encoding="utf-8"
        )
        self.assertIn("isPreview ? '明日值日' : '今日值班'", dashboard)
        self.assertIn("displayDutyDate.value", dashboard)
        calendar = (
            self.ROOT / "duty-agent-ui" / "src" / "components" / "schedule" / "ScheduleCalendar.vue"
        ).read_text(encoding="utf-8")
        self.assertIn("dutyToday.value", calendar)
        self.assertNotIn("displayDutyDate", calendar)


if __name__ == "__main__":
    unittest.main()
