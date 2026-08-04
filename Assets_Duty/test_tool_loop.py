"""
Integration tests for the tool_loop executor.

Uses Python's built-in unittest module (no pytest required).
Run with: python test_tool_loop.py
"""
from __future__ import annotations

import json
import os
import shutil
import sys
import tempfile
import unittest
from datetime import date, datetime
from pathlib import Path
from typing import Dict, List, Optional
from unittest.mock import patch

# Ensure Assets_Duty is on path
sys.path.insert(0, str(Path(__file__).parent))

from state_ops import Context, save_state
from execution_profiles import ExecutionProfile, ExecutionPlan
from tool_loop.ini_handler import (
    ExecutionCtx,
    PollingFlags,
    ScheduleUnit,
    parse_and_apply,
    build_response,
    parse_special_commands,
)


# ------------------------------------------------------------------------------
# Helpers
# ------------------------------------------------------------------------------

def make_ctx(tmp_dir: Path) -> Context:
    """Create a minimal test Context with fake data files."""
    roster_path = tmp_dir / "roster.csv"
    roster_path.write_text("id,name,active\n1001,张三,1\n1002,李四,1\n1003,王五,1\n1004,赵六,0\n", encoding="utf-8")

    state_path = tmp_dir / "state.json"
    state_path.write_text(json.dumps({
        "schedule_pool": [],
        "debt_counts": {"1002": 1},
        "credit_counts": {"1001": 1},
        "last_pointer": 0,
    }), encoding="utf-8")

    config_path = tmp_dir / "config.json"
    config_path.write_text(json.dumps({
        "version": 1,
        "selected_plan_id": "standard",
        "plan_presets": [{
            "id": "standard", "name": "标准", "mode_id": "standard",
            "api_key": "test-key", "base_url": "https://example.com/v1",
            "model": "test-model", "model_profile": "cloud",
        }],
        "polling": {"hints_on": True, "max_rounds": 10},
    }), encoding="utf-8")

    return Context(data_dir=tmp_dir, trace_id="test-trace")


def make_plan() -> ExecutionPlan:
    """Create a minimal ExecutionPlan for tool_loop."""
    profile = ExecutionProfile(
        model_profile="cloud",
        orchestration_mode="tool_loop",
        provider_hint="",
        multi_agent_ready=False,
        edge_ready=False,
        multi_agent_execution_mode="auto",
        single_pass_strategy="cloud_standard",
    )
    return ExecutionPlan(
        profile=profile,
        runtime_mode="tool_loop",
        prompt_pack_strategy="ini_tool",
        tasks=(),
        notes=(),
    )


class FixedToolLoopDateTime(datetime):
    @staticmethod
    def now():
        return datetime(2026, 4, 5)


# ------------------------------------------------------------------------------
# Tests: parse_special_commands
# ------------------------------------------------------------------------------

class TestParseSpecialCommands(unittest.TestCase):

    def test_keep_all(self):
        cmds, invalid = parse_special_commands(["@keep(#week1)"])
        self.assertEqual(len(cmds), 1)
        self.assertEqual(cmds[0].verb, "KEEP")
        self.assertEqual(cmds[0].batch_name, "week1")
        self.assertEqual(len(cmds[0].keep_keys), 0)  # keep all
        self.assertEqual(invalid, [])

    def test_keep_one_slot(self):
        cmds, invalid = parse_special_commands(["@keep(#week1 04-01 A)"])
        self.assertEqual(len(cmds), 1)
        self.assertEqual(cmds[0].verb, "KEEP")
        self.assertIn("04-01 A", cmds[0].keep_keys)
        self.assertEqual(invalid, [])

    def test_drop_batch(self):
        cmds, invalid = parse_special_commands(["@drop(#week2)"])
        self.assertEqual(len(cmds), 1)
        self.assertEqual(cmds[0].verb, "DROP")
        self.assertEqual(cmds[0].batch_name, "week2")

    def test_replace(self):
        cmds, invalid = parse_special_commands(["@replace(#week1 04-02 B=1003)"])
        self.assertEqual(len(cmds), 1)
        self.assertEqual(cmds[0].verb, "REPLACE")
        self.assertEqual(cmds[0].batch_name, "week1")
        self.assertEqual(cmds[0].replace_map["04-02 B"], 1003)

    def test_finalize(self):
        cmds, invalid = parse_special_commands(["@finalize"])
        self.assertEqual(len(cmds), 1)
        self.assertEqual(cmds[0].verb, "FINALIZE")
        self.assertEqual(invalid, [])

    def test_mixed_commands(self):
        cmds, invalid = parse_special_commands([
            "@keep(#week1 04-01 A)",
            "@drop(#week2)",
            "@finalize",
        ])
        self.assertEqual(len(cmds), 3)
        self.assertEqual(cmds[0].verb, "KEEP")
        self.assertEqual(cmds[1].verb, "DROP")
        self.assertEqual(cmds[2].verb, "FINALIZE")
        self.assertEqual(invalid, [])

    def test_invalid_line(self):
        cmds, invalid = parse_special_commands(["@unknown", "@keep(#week1)"])
        self.assertEqual(len(cmds), 1)
        self.assertIn("INVALID", invalid[0])

    def test_replace_bad_format(self):
        cmds, invalid = parse_special_commands(["@replace(#week1 04-02 B)"])
        self.assertEqual(len(cmds), 0)
        self.assertTrue(any("INVALID" in s for s in invalid))


# ------------------------------------------------------------------------------
# Tests: parse_and_apply
# ------------------------------------------------------------------------------

class TestParseAndApply(unittest.TestCase):

    def make_flags(self, hints_on=True, max_rounds=10):
        return PollingFlags(hints_on=hints_on, max_rounds=max_rounds)

    def test_basic_schedule(self):
        raw = """[[#batch1]]
[areas]
A = 教室

[schedule]
04-06 = A:1001 1002
"""
        resp, ctx = parse_and_apply(
            raw,
            all_ids=[1001, 1002, 1003],
            debt_counts={},
            credit_counts={},
            inactive_ids=[],
            last_pointer=0,
            start_date=date(2026, 4, 5),
            schedule_pool=[],
            required_slots=[],
            flags=self.make_flags(),
            round_num=1,
        )
        self.assertEqual(len(ctx.accepted), 1)
        self.assertEqual(ctx.accepted[0]["date"], "2026-04-06")
        self.assertEqual(ctx.accepted[0]["area_ids"]["教室"], [1001, 1002])
        # With empty required_slots, remaining is empty → auto-finalizes
        self.assertTrue(ctx.finalized)
        self.assertIn("[state]", resp)

    def test_inactive_id_rejected(self):
        raw = """[[#batch1]]
[areas]
A = 教室

[schedule]
04-06 = A:1004
"""
        resp, ctx = parse_and_apply(
            raw,
            all_ids=[1001, 1002, 1003, 1004],
            debt_counts={},
            credit_counts={},
            inactive_ids=[1004],
            last_pointer=0,
            start_date=date(2026, 4, 5),
            schedule_pool=[],
            required_slots=[],
            flags=self.make_flags(),
            round_num=1,
        )
        rejected = [r for r in ctx.rejected if r[1] == "INACTIVE"]
        self.assertGreater(len(rejected), 0)

    def test_finalize_terminates(self):
        raw = """[[#batch1]]
[areas]
A = 教室

[schedule]
04-06 = A:1001

@finalize
"""
        resp, ctx = parse_and_apply(
            raw,
            all_ids=[1001, 1002, 1003],
            debt_counts={},
            credit_counts={},
            inactive_ids=[],
            last_pointer=0,
            start_date=date(2026, 4, 5),
            schedule_pool=[],
            required_slots=[],
            flags=self.make_flags(),
            round_num=1,
        )
        self.assertTrue(ctx.finalized)
        self.assertIn("[state]", resp)

    def test_hints_off_ignores_keep(self):
        raw = """[[#batch1]]
[areas]
A = 教室

[schedule]
04-06 = A:1001
04-07 = A:1002

@keep(#batch1 04-06 A)
"""
        resp, ctx = parse_and_apply(
            raw,
            all_ids=[1001, 1002, 1003],
            debt_counts={},
            credit_counts={},
            inactive_ids=[],
            last_pointer=0,
            start_date=date(2026, 4, 5),
            schedule_pool=[],
            required_slots=[],
            flags=self.make_flags(hints_on=False),
            round_num=1,
        )
        # With hints_off, @keep is ignored — both slots kept
        self.assertEqual(len(ctx.accepted), 2)
        dates = {e["date"] for e in ctx.accepted}
        self.assertIn("2026-04-06", dates)
        self.assertIn("2026-04-07", dates)

    def test_debt_cleared_on_assign(self):
        raw = """[[#batch1]]
[areas]
A = 教室

[schedule]
04-06 = A:1002
"""
        resp, ctx = parse_and_apply(
            raw,
            all_ids=[1001, 1002, 1003],
            debt_counts={"1002": 1},
            credit_counts={},
            inactive_ids=[],
            last_pointer=0,
            start_date=date(2026, 4, 5),
            schedule_pool=[],
            required_slots=[],
            flags=self.make_flags(),
            round_num=1,
        )
        # 1002 had debt=1, was assigned, debt should be cleared
        self.assertEqual(ctx.debt_counts.get(1002, 0), 0)

    def test_credit_consumed_on_assign(self):
        raw = """[[#batch1]]
[areas]
A = 教室

[schedule]
04-06 = A:1001
"""
        resp, ctx = parse_and_apply(
            raw,
            all_ids=[1001, 1002, 1003],
            debt_counts={},
            credit_counts={"1001": 1},
            inactive_ids=[],
            last_pointer=0,
            start_date=date(2026, 4, 5),
            schedule_pool=[],
            required_slots=[],
            flags=self.make_flags(),
            round_num=1,
        )
        # 1001 had credit=1, was assigned, credit should be consumed
        self.assertEqual(ctx.credit_counts.get(1001, 0), 0)

    def test_pointer_advances(self):
        raw = """[[#batch1]]
[areas]
A = 教室

[schedule]
04-06 = A:1001
"""
        resp, ctx = parse_and_apply(
            raw,
            all_ids=[1001, 1002, 1003],
            debt_counts={},
            credit_counts={"1002": 1},  # credit_id passes → pointer advances
            inactive_ids=[],
            last_pointer=0,
            start_date=date(2026, 4, 5),
            schedule_pool=[],
            required_slots=[],
            flags=self.make_flags(),
            round_num=1,
        )
        # With a credit_id, pointer advances past 1001 and 1002 (credit consumed)
        self.assertGreater(ctx.last_pointer, 0)


# ------------------------------------------------------------------------------
# Tests: build_response
# ------------------------------------------------------------------------------

class TestBuildResponse(unittest.TestCase):

    def test_round_number_included(self):
        ctx = ExecutionCtx(
            debt_counts={},
            credit_counts={},
            inactive_ids=set(),
            last_pointer=0,
            schedule_pool=[],
            remaining=[],
            round_num=3,
            flags=PollingFlags(hints_on=True, max_rounds=10),
            start_date=date(2026, 4, 5),
            accepted=[],
            rejected=[],
            invalid_cmds=[],
            finalized=False,
            alias_map={"A": "教室"},
            _valid_ids=set(),
        )
        resp = build_response(ctx=ctx, all_ids=[], id_to_name={})
        self.assertIn("round = 3", resp)

    def test_state_fields_present(self):
        ctx = ExecutionCtx(
            debt_counts={"1002": 1},
            credit_counts={"1001": 2},
            inactive_ids=set(),
            last_pointer=5,
            schedule_pool=[],
            remaining=[],
            round_num=2,
            flags=PollingFlags(hints_on=True, max_rounds=10),
            start_date=date(2026, 4, 5),
            accepted=[],
            rejected=[],
            invalid_cmds=[],
            finalized=True,
            alias_map={},
            _valid_ids=set(),
        )
        resp = build_response(ctx=ctx, all_ids=[], id_to_name={})
        self.assertIn("round = 2", resp)
        self.assertIn("pointer = 5", resp)
        self.assertIn("debt = 1002", resp)
        self.assertIn("credit = 1001*2", resp)


# ------------------------------------------------------------------------------
# Tests: _build_required_slots
# ------------------------------------------------------------------------------

class TestBuildRequiredSlots(unittest.TestCase):

    def test_date_range(self):
        from tool_loop.executor import _build_required_slots, DEFAULT_AREA_NAMES

        slots = _build_required_slots(
            "4月1日到4月3日",
            date(2026, 4, 5),
            {},
            [],
            DEFAULT_AREA_NAMES,
        )
        dates = {s.date_iso for s in slots}
        self.assertIn("2026-04-01", dates)
        self.assertIn("2026-04-02", dates)
        self.assertIn("2026-04-03", dates)

    def test_mmdd_range(self):
        from tool_loop.executor import _build_required_slots, DEFAULT_AREA_NAMES

        slots = _build_required_slots(
            "04-01~04-02",
            date(2026, 4, 5),
            {},
            [],
            DEFAULT_AREA_NAMES,
        )
        dates = {s.date_iso for s in slots}
        self.assertIn("2026-04-01", dates)
        self.assertIn("2026-04-02", dates)

    def test_single_date(self):
        from tool_loop.executor import _build_required_slots, DEFAULT_AREA_NAMES

        slots = _build_required_slots(
            "4月5日",
            date(2026, 4, 1),
            {},
            [],
            DEFAULT_AREA_NAMES,
        )
        self.assertEqual(len(slots), 1)
        self.assertEqual(slots[0].date_iso, "2026-04-05")

    def test_fallback_7_days(self):
        from tool_loop.executor import _build_required_slots, DEFAULT_AREA_NAMES

        slots = _build_required_slots(
            "",
            date(2026, 4, 5),
            {},
            [],
            DEFAULT_AREA_NAMES,
        )
        dates = sorted({s.date_iso for s in slots})
        self.assertEqual(dates[0], "2026-04-06")
        self.assertEqual(dates[-1], "2026-04-12")
        self.assertEqual(len(slots), 7)

    def test_excludes_already_scheduled(self):
        from tool_loop.executor import _build_required_slots, DEFAULT_AREA_NAMES

        slots = _build_required_slots(
            "",
            date(2026, 4, 5),
            {},
            [{"date": "2026-04-06"}],  # already scheduled
            DEFAULT_AREA_NAMES,
        )
        dates = {s.date_iso for s in slots}
        self.assertNotIn("2026-04-06", dates)
        self.assertIn("2026-04-07", dates)


# ------------------------------------------------------------------------------
# E2E tests: run_tool_loop_schedule (mock LLM)
# ------------------------------------------------------------------------------

class TestToolLoopE2E(unittest.TestCase):

    def setUp(self):
        self.tmp = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_finalize_writes_state(self):
        ctx = make_ctx(Path(self.tmp))
        plan = make_plan()

        llm_response = """[[#batch1]]
[areas]
A = 教室

[schedule]
04-06 = A:1001 1002
04-07 = A:1003

@finalize
"""

        tool_call_response = json.dumps({
            "tool_calls": [{
                "function": {
                    "name": "fill_schedule",
                    "arguments": json.dumps({"schedule": llm_response}),
                }
            }]
        })

        from tool_loop import executor as tool_loop_executor

        with patch("tool_loop.executor.call_llm_raw") as mock_llm, \
             patch.object(tool_loop_executor, "datetime", FixedToolLoopDateTime):
            mock_llm.return_value = tool_call_response

            result = tool_loop_executor.run_tool_loop_schedule(
                ctx=ctx,
                input_data={"instruction": "安排4月6日到4月7日"},
                execution_plan=plan,
            )

        self.assertEqual(result["status"], "ok")
        self.assertTrue(result["tool_loop_meta"]["finalized"])

    def test_hints_off_keeps_all_slots(self):
        # Write the hints_off config via save_config so it's properly normalized
        from state_ops import save_config
        ctx = make_ctx(Path(self.tmp))
        save_config(ctx, {
            "version": 1,
            "selected_plan_id": "standard",
            "plan_presets": [{
                "id": "standard", "name": "标准", "mode_id": "standard",
                "api_key": "test-key", "base_url": "https://example.com/v1",
                "model": "test-model", "model_profile": "cloud",
            }],
            "polling": {"hints_on": False, "max_rounds": 10},
        })

        plan = make_plan()

        llm_response = """[[#batch1]]
[areas]
A = 教室

[schedule]
04-06 = A:1001
04-07 = A:1002

@keep(#batch1 04-06 A)
@finalize
"""

        tool_call_response = json.dumps({
            "tool_calls": [{
                "function": {
                    "name": "fill_schedule",
                    "arguments": json.dumps({"schedule": llm_response}),
                }
            }]
        })

        from tool_loop import executor as tool_loop_executor

        with patch("tool_loop.executor.call_llm_raw") as mock_llm, \
             patch.object(tool_loop_executor, "datetime", FixedToolLoopDateTime):
            mock_llm.return_value = tool_call_response

            result = tool_loop_executor.run_tool_loop_schedule(
                ctx=ctx,
                input_data={"instruction": "安排4月6日到4月7日"},
                execution_plan=plan,
            )

        self.assertEqual(result["status"], "ok")
        # hints_on=False: @keep is ignored — both dates covered
        dates = result["tool_loop_meta"]["dates_covered"]
        self.assertIn("2026-04-06", dates)
        self.assertIn("2026-04-07", dates)


if __name__ == "__main__":
    unittest.main(verbosity=2)
