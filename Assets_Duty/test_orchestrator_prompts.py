"""
Tests for the context engineering of the orchestrator executor:
prompt contracts (direct vs poll), context formatting, and required slot
computation. Regression coverage for the id_to_area mis-mapping and the
discarded-INI contract in poll mode.

Uses Python's built-in unittest module (no pytest required).
Run with: python test_orchestrator_prompts.py
"""
from __future__ import annotations

import sys
import unittest
from datetime import date, datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from orchestrator.context import OrchestratorContext, TimeWindow
from orchestrator.prompt import (
    build_orchestrator_round_prompt,
    build_orchestrator_system_prompt,
    build_timewindow_system_prompt,
)
from orchestrator.executor import _build_required_slots


def _make_ctx() -> OrchestratorContext:
    return OrchestratorContext(
        trace_id="t",
        request_source="test",
        instruction="生成排班",
        request_time=datetime(2026, 4, 5, 8, 0),
        start_date=date(2026, 4, 5),
        config={},
        state={},
        id_to_name={1001: "张三", 1002: "李四", 1003: "王五"},
        all_ids=[1001, 1002, 1003],
        active_ids=[1001, 1002, 1003],
        inactive_ids=[],
        id_to_active={1001: 1, 1002: 1, 1003: 1},
        debt_list=[1002, 1002],
        credit_list=[1003],
        last_pointer=2,
        previous_note="上周备注：周三班级活动",
        duty_rule="每周每人最多一次",
        all_areas=["教室", "清洁区"],
        area_per_day_counts={"教室": 2, "清洁区": 1},
    )


class TestContextFormatting(unittest.TestCase):

    def test_debt_list_uses_count_format(self):
        ctx = _make_ctx()
        # [1002, 1002] must render as one entry with *2, not the name twice
        self.assertEqual(ctx.format_debt_list(), "李四(ID=1002)*2")

    def test_person_pool_has_no_bogus_area_tag(self):
        ctx = _make_ctx()
        pool = ctx.format_person_pool()
        # Regression: id_to_area used to map id -> active flag (0/1)
        self.assertNotIn("[1]", pool)
        self.assertNotIn("[0]", pool)
        self.assertIn("张三 (ID=1001)", pool)


class TestSystemPromptModes(unittest.TestCase):

    def test_direct_mode_requires_full_ini(self):
        ctx = _make_ctx()
        prompt = build_orchestrator_system_prompt(ctx, [date(2026, 4, 5)], mode="direct")
        self.assertIn("[state]", prompt)
        self.assertIn("[remaining]", prompt)
        self.assertIn("@finalize", prompt)
        # Roster must not be listed twice in the same prompt
        self.assertEqual(prompt.count("张三 (ID=1001)"), 1)

    def test_poll_mode_requires_window_directives_only(self):
        ctx = _make_ctx()
        prompt = build_orchestrator_system_prompt(ctx, [date(2026, 4, 5)], mode="poll")
        self.assertIn("[[#phase1]]", prompt)
        self.assertIn("指令块", prompt)
        # The discarded-output contract must be gone
        self.assertNotIn("@finalize", prompt)
        self.assertNotIn("[remaining]", prompt)
        self.assertNotIn("生成完整排班方案", prompt)

    def test_previous_note_rendered_when_present(self):
        ctx = _make_ctx()
        prompt = build_orchestrator_system_prompt(ctx, [date(2026, 4, 5)], mode="direct")
        self.assertIn("上一轮备注", prompt)
        self.assertIn("周三班级活动", prompt)

    def test_previous_note_omitted_when_empty(self):
        ctx = _make_ctx()
        ctx.previous_note = ""
        prompt = build_orchestrator_system_prompt(ctx, [date(2026, 4, 5)], mode="direct")
        self.assertNotIn("上一轮备注", prompt)


class TestRoundPromptModes(unittest.TestCase):

    def test_direct_round_prompt_includes_authoritative_ini(self):
        ctx = _make_ctx()
        prompt = build_orchestrator_round_prompt(
            ctx, 2, [], python_response_ini="[state]\nround = 2", mode="direct",
        )
        self.assertIn("round = 2", prompt)
        self.assertIn("@finalize", prompt)

    def test_poll_round_prompt_is_compact(self):
        ctx = _make_ctx()
        huge_ini = "[state]\n" + "x" * 5000
        prompt = build_orchestrator_round_prompt(
            ctx, 2, [], python_response_ini=huge_ini, mode="poll",
        )
        self.assertIn("第 2 轮 Python 反馈", prompt)
        self.assertNotIn(huge_ini, prompt)
        self.assertNotIn("[[#" + "x", prompt)


class TestTimeWindowPrompt(unittest.TestCase):

    def test_window_debt_uses_count_format(self):
        ctx = _make_ctx()
        window = TimeWindow(
            index=0,
            name="phase1",
            start_date=date(2026, 4, 5),
            end_date=date(2026, 4, 7),
            dates=[date(2026, 4, 5), date(2026, 4, 6), date(2026, 4, 7)],
            area_names=["教室"],
            area_per_day={"教室": 2},
            available_ids=[1001, 1002],
            debt_ids=[1002, 1002],
            credit_ids=[],
        )
        prompt = build_timewindow_system_prompt(window, ctx)
        self.assertIn("ID=1002*2", prompt)


class TestRequiredSlotsCounts(unittest.TestCase):

    def test_configured_headcount_expands_units(self):
        dates = [date(2026, 4, 6), date(2026, 4, 7), date(2026, 4, 8)]
        slots = _build_required_slots(
            "4月6日到4月8日",
            dates,
            {},
            ["教室", "清洁区"],
            {"教室": 2, "清洁区": 1},
        )
        # 3 days x (2 + 1) units
        self.assertEqual(len(slots), 9)
        classroom = [s for s in slots if s.area_name == "教室"]
        self.assertEqual(len(classroom), 6)

    def test_default_headcount_is_two(self):
        slots = _build_required_slots(
            "4月6日",
            [date(2026, 4, 6)],
            {},
            ["教室"],
        )
        self.assertEqual(len(slots), 2)


if __name__ == "__main__":
    unittest.main(verbosity=2)
