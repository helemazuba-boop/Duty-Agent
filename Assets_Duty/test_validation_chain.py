#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""统一校验链回归测试（2026-10-22 后端校验审计 · Phase A）。

这些用例对应审计确认的 P0 缺陷，**先于修复编写**，在当前实现上必须失败：

1. ``ScheduleParseError``：解析失败必须抛"调用方可修复"的异常——同时是
   ``ValueError``（``command_service`` 既有 except 子句）与 ``RuntimeError``
   （``cli`` 既有 except 子句），使 plan-ingest / CLI 拿到结构化 error JSON
   而不是 HTTP 500 纯文本。
2. 请假预处理：``build_single_pass_request`` 曾引用未定义的 ``stop_event``，
   任何含请假关键词的指令 100% NameError（排班主路径报 error、plan-prompt
   路径直接 500）。
3. 手工编辑入口：``save_schedule_entry`` 的坏输入必须返回 ``status:error``，
   而不是把 ValueError 抛成 500（同文件的 adjustment 端点一直是 400）。
4. 默认区域注入：未配置区域时，后端不得把「值日」注入/补齐进最终排班；
   只有模型声明的区域才是真实区域（CSV 兜底 / 离线无历史除外）。
5. 历史保留：``merge_schedule_pool`` 必须按 ``apply_mode`` 合并（默认
   ``append``），不得无条件整池替换——``origin/main`` 上本就有四模式实现。
6. tool_loop 完整性：``compute_remaining_units`` 必须按 (日期, 区域, 人数)
   计算；空 ID 槽位必须拒绝；不完整结果（含模型 ``@finalize`` 但欠员、
   或跑满 max_rounds）不得提交 state、不得返回 ok。

设计约束（不得违反，见审计交接报告）：
* 同日跨区域复用人员**允许**；只禁止同区域同日重复 ID。
* 确定性兜底完整成功仍然是 success（标注 fallback 来源），不强制 partial。

约定：全部使用 tempfile 数据目录；unittest；不引 pytest。
"""

import json
import sys
import tempfile
import unittest
from datetime import date, datetime
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))

from state_ops import Context, load_state, save_json_atomic, save_config

BASE_CONFIG = {
    "version": 1,
    "selected_plan_id": "standard",
    "plan_presets": [{
        "id": "standard", "name": "标准", "mode_id": "standard",
        "api_key": "test-key", "base_url": "https://example.com/v1",
        "model": "test-model", "model_profile": "cloud",
    }],
    "polling": {"hints_on": True, "max_rounds": 10},
}

ROSTER = "id,name,active\n1001,张三,1\n1002,李四,1\n1003,王五,1\n1004,赵六,1\n"


def make_ctx(tmp: Path, *, config=None, roster=ROSTER, state=None) -> Context:
    (tmp / "roster.csv").write_text(roster, encoding="utf-8")
    save_json_atomic(
        tmp / "state.json",
        state if state is not None else {
            "schedule_pool": [], "debt_counts": {}, "credit_counts": {}, "last_pointer": 0,
        },
    )
    save_json_atomic(tmp / "config.json", config if config is not None else dict(BASE_CONFIG))
    return Context(data_dir=tmp, trace_id="test-trace")


def single_pass_plan(ctx: Context):
    from execution_profiles import build_execution_plan, resolve_execution_profile
    from state_ops import load_config

    return build_execution_plan(resolve_execution_profile({}, load_config(ctx)))


class _StubLogger:
    def info(self, *args, **kwargs):
        pass

    def warn(self, *args, **kwargs):
        pass

    def error(self, *args, **kwargs):
        pass


class _StubLock:
    def __init__(self):
        self._held = False

    def acquire(self, blocking=False):
        if self._held:
            return False
        self._held = True
        return True

    def release(self):
        self._held = False


class _StubQueryService:
    def get_snapshot(self, **kwargs):
        return {"config": {}, "roster": [], "state": {}}


class _StubRuntime:
    """Minimal CommandService runtime (no notification bus, no locking)."""

    def __init__(self, data_dir: Path):
        self.data_dir = data_dir
        self.logger = _StubLogger()
        self.schedule_run_lock = _StubLock()
        self.query_service = _StubQueryService()
        self.notifications = []

    def new_trace_id(self):
        return "test-trace"

    def publish_schedule_result_notification(self, result):
        self.notifications.append(result)

    def publish_snapshot_changed(self, *args, **kwargs):
        pass


class ParseErrorContractTests(unittest.TestCase):
    """解析失败必须是"调用方可修复"的结构化错误，不是 500。"""

    def _tmp(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        return Path(tmp)

    def test_parse_failure_raises_caller_fixable_error(self):
        from llm_transport import ScheduleParseError, parse_schedule_completion

        bad = "[areas]\nA = 教室\n[schedule]\n03-24 = A:1001\nHope this helps!\n"
        with self.assertRaises(ScheduleParseError):
            parse_schedule_completion(bad, "2026-03-24")
        # 两类既有 except 子句都必须能接住它
        self.assertTrue(issubclass(ScheduleParseError, ValueError))
        self.assertTrue(issubclass(ScheduleParseError, RuntimeError))

    def test_apply_completion_parse_failure_returns_error_result(self):
        from application.command_service import CommandService
        from single_pass_executor import apply_single_pass_completion

        tmp = self._tmp()
        ctx = make_ctx(tmp)
        service = CommandService(_StubRuntime(tmp))
        resume_context = {"mode": "single_pass", "start_date": "2026-03-24", "trace_id": "t"}
        bad = "[areas]\nA = 教室\n[schedule]\n03-24 = A:1001\nHope this helps!\n"

        # 底层抛的是 caller-fixable 异常
        with self.assertRaises(ValueError):
            apply_single_pass_completion(ctx, bad, dict(resume_context))

        # service 层必须转成 error 结果（而不是让 RuntimeError 穿成 500）
        result = service.apply_schedule_completion(
            {"completion": bad, "resume_context": resume_context}, "t", "cli",
        )
        self.assertEqual(result["status"], "error")
        self.assertIn("Parse failed", result["message"])

    def test_parse_failure_does_not_commit_state(self):
        from single_pass_executor import apply_single_pass_completion

        tmp = self._tmp()
        ctx = make_ctx(tmp)
        bad = "[areas]\nA = 教室\n[schedule]\n03-24 = A:1001\n Hope this helps!\n"
        with self.assertRaises(ValueError):
            apply_single_pass_completion(
                ctx, bad, {"mode": "single_pass", "start_date": "2026-03-24", "trace_id": "t"},
            )
        self.assertEqual(load_state(ctx.paths["state"])["schedule_pool"], [])


class AbsencePreprocessingTests(unittest.TestCase):
    """请假关键词指令不得因 stop_event 未定义而整体失败。"""

    def _tmp(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        return Path(tmp)

    def test_build_request_with_absence_keyword(self):
        from single_pass_executor import build_single_pass_request

        tmp = self._tmp()
        ctx = make_ctx(tmp)
        plan = single_pass_plan(ctx)
        messages, resume_context = build_single_pass_request(
            ctx, {"instruction": "张三请假，安排下周值日"}, plan,
        )
        self.assertTrue(messages)
        # 请假意图在匿名化之前落盘（plan-prompt / provider run 共用）
        absences = load_state(ctx.paths["state"]).get("absences", [])
        self.assertTrue(absences)
        self.assertEqual(absences[0]["id"], 1001)

    def test_service_build_prompt_with_absence_keyword(self):
        from application.command_service import CommandService

        tmp = self._tmp()
        make_ctx(tmp)
        service = CommandService(_StubRuntime(tmp))
        result = service.build_schedule_prompt(
            {"instruction": "张三请假，安排下周值日"}, "t", "cli",
        )
        self.assertEqual(result["status"], "success")
        self.assertTrue(result.get("messages"))


class ScheduleEntryErrorContractTests(unittest.TestCase):
    """手工编辑入口的坏输入返回 error 结果，不是 500。"""

    def _tmp(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        return Path(tmp)

    def test_invalid_target_date_returns_error_result(self):
        from application.command_service import CommandService

        tmp = self._tmp()
        make_ctx(tmp)
        service = CommandService(_StubRuntime(tmp))
        result = service.save_schedule_entry(
            {"target_date": "21/03/2026", "area_assignments": {"教室": ["张三"]}},
            "t",
            "api",
        )
        self.assertEqual(result["status"], "error")
        self.assertTrue(result.get("message"))

    def test_missing_entry_returns_error_result(self):
        from application.command_service import CommandService

        tmp = self._tmp()
        make_ctx(tmp)
        service = CommandService(_StubRuntime(tmp))
        result = service.save_schedule_entry(
            {"source_date": "2026-03-24", "target_date": "2026-03-25",
             "area_assignments": {"教室": ["张三"]}},
            "t",
            "api",
        )
        self.assertEqual(result["status"], "error")


class DefaultAreaInjectionTests(unittest.TestCase):
    """未配置区域时后端不得注入/补齐「值日」幻影区域。"""

    def _tmp(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        return Path(tmp)

    def test_unconfigured_areas_do_not_inject_default_area(self):
        from single_pass_executor import apply_single_pass_completion

        tmp = self._tmp()
        ctx = make_ctx(tmp)  # config 无 areas
        completion = (
            "[areas]\nA = 教室\nB = 走廊\n"
            "[schedule]\n10-22 = A:1001 1002 | B:1003\n"
        )
        result = apply_single_pass_completion(
            ctx, completion, {"mode": "single_pass", "start_date": "2026-10-22", "trace_id": "t"},
        )
        self.assertEqual(result["status"], "success")
        entry = load_state(ctx.paths["state"])["schedule_pool"][-1]
        self.assertEqual(set(entry["area_assignments"]), {"教室", "走廊"})
        self.assertNotIn("值日", entry["area_assignments"])

    def test_configured_areas_still_enforced(self):
        """回归保护：配置了区域时，缺额补齐/超额裁剪仍然生效。"""
        from single_pass_executor import apply_single_pass_completion

        tmp = self._tmp()
        ctx = make_ctx(tmp, config={**BASE_CONFIG, "areas": ["教室"], "area_per_day_counts": {"教室": 3}})
        completion = "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001\n"
        result = apply_single_pass_completion(
            ctx, completion, {"mode": "single_pass", "start_date": "2026-10-22", "trace_id": "t"},
        )
        self.assertEqual(result["status"], "success")
        entry = load_state(ctx.paths["state"])["schedule_pool"][-1]
        self.assertEqual(len(entry["area_assignments"]["教室"]), 3)


class HistoryPreservationTests(unittest.TestCase):
    """merge_schedule_pool 必须按 apply_mode 保留历史（默认 append）。"""

    def _tmp(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        return Path(tmp)

    def _apply(self, ctx, completion, start_date, apply_mode=None):
        from single_pass_executor import apply_single_pass_completion

        resume_context = {"mode": "single_pass", "start_date": start_date, "trace_id": "t"}
        if apply_mode:
            resume_context["apply_mode"] = apply_mode
        return apply_single_pass_completion(ctx, completion, resume_context)

    def _pool_dates(self, ctx):
        return [entry["date"] for entry in load_state(ctx.paths["state"])["schedule_pool"]]

    def test_second_run_preserves_history(self):
        tmp = self._tmp()
        ctx = make_ctx(tmp)
        self._apply(ctx, "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n", "2026-10-22")
        self._apply(ctx, "[areas]\nA = 教室\n[schedule]\n10-23 = A:1003 1004\n", "2026-10-23")
        self.assertEqual(self._pool_dates(ctx), ["2026-10-22", "2026-10-23"])

    def test_replace_future_keeps_past_entries(self):
        tmp = self._tmp()
        ctx = make_ctx(tmp)
        self._apply(ctx, "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n", "2026-10-22")
        self._apply(
            ctx, "[areas]\nA = 教室\n[schedule]\n10-23 = A:1003 1004\n",
            "2026-10-23", apply_mode="replace_future",
        )
        self.assertEqual(self._pool_dates(ctx), ["2026-10-22", "2026-10-23"])

    def test_explicit_replace_all_drops_history(self):
        tmp = self._tmp()
        ctx = make_ctx(tmp)
        self._apply(ctx, "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n", "2026-10-22")
        self._apply(
            ctx, "[areas]\nA = 教室\n[schedule]\n10-23 = A:1003 1004\n",
            "2026-10-23", apply_mode="replace_all",
        )
        self.assertEqual(self._pool_dates(ctx), ["2026-10-23"])

    def test_manual_edit_outside_window_survives_rerun(self):
        tmp = self._tmp()
        ctx = make_ctx(tmp)
        self._apply(ctx, "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n", "2026-10-22")
        # 用户在窗口外手工编辑了一天
        from state_ops import update_state

        def _add(state):
            state["schedule_pool"] = state["schedule_pool"] + [{
                "date": "2026-12-25", "day": "星期五",
                "area_assignments": {"教室": ["张三", "李四"]}, "note": "手工编辑",
            }]
            return state

        update_state(ctx.paths["state"], _add)
        # 重排 10-22 窗口，手工编辑的 12-25 必须保留
        self._apply(ctx, "[areas]\nA = 教室\n[schedule]\n10-22 = A:1003 1004\n", "2026-10-22")
        dates = self._pool_dates(ctx)
        self.assertIn("2026-12-25", dates)
        self.assertIn("2026-10-22", dates)


class ToolLoopCompletenessTests(unittest.TestCase):
    """tool_loop：remaining 按 (日期, 区域, 人数) 计算；不完整不得提交。"""

    def test_remaining_counts_people_per_area(self):
        from tool_loop.ini_handler import ScheduleUnit, compute_remaining_units

        required = [
            ScheduleUnit("2026-04-06", "教室", "A"),
            ScheduleUnit("2026-04-06", "教室", "A"),
        ]
        accepted = [{"date": "2026-04-06", "area_ids": {"教室": [1001]}}]
        remaining = compute_remaining_units(required, accepted)
        self.assertEqual(len(remaining), 1)  # 缺 1 人

    def test_remaining_detects_missing_area(self):
        from tool_loop.ini_handler import ScheduleUnit, compute_remaining_units

        required = [
            ScheduleUnit("2026-04-06", "教室", "A"),
            ScheduleUnit("2026-04-06", "清洁区", "B"),
        ]
        accepted = [{"date": "2026-04-06", "area_ids": {"教室": [1001]}}]
        remaining = compute_remaining_units(required, accepted)
        self.assertEqual(len(remaining), 1)  # 清洁区整个缺失
        self.assertEqual(remaining[0].area_name, "清洁区")

    def test_remaining_detects_missing_date(self):
        from tool_loop.ini_handler import ScheduleUnit, compute_remaining_units

        required = [
            ScheduleUnit("2026-04-06", "教室", "A"),
            ScheduleUnit("2026-04-07", "教室", "A"),
        ]
        accepted = [{"date": "2026-04-06", "area_ids": {"教室": [1001]}}]
        remaining = compute_remaining_units(required, accepted)
        self.assertEqual([unit.date_iso for unit in remaining], ["2026-04-07"])

    def test_remaining_full_coverage_is_empty(self):
        from tool_loop.ini_handler import ScheduleUnit, compute_remaining_units

        required = [
            ScheduleUnit("2026-04-06", "教室", "A"),
            ScheduleUnit("2026-04-06", "教室", "A"),
            ScheduleUnit("2026-04-06", "清洁区", "B"),
        ]
        accepted = [{"date": "2026-04-06", "area_ids": {"教室": [1001, 1002], "清洁区": [1003]}}]
        self.assertEqual(compute_remaining_units(required, accepted), [])

    def test_remaining_date_total_fallback_when_areas_renamed(self):
        """模型整体改名时退回按日期总人数比较（无可靠别名映射）。"""
        from tool_loop.ini_handler import ScheduleUnit, compute_remaining_units

        required = [ScheduleUnit("2026-04-06", "值日", "")]
        accepted = [{"date": "2026-04-06", "area_ids": {"教室": [1001]}}]
        self.assertEqual(compute_remaining_units(required, accepted), [])

        accepted_short = [{"date": "2026-04-06", "area_ids": {"教室": []}}]
        self.assertEqual(len(compute_remaining_units(required, accepted_short)), 1)

    def test_empty_id_slot_rejected(self):
        """``A:`` 空 ID 必须整槽拒绝，不得产生空条目伪装成已覆盖。"""
        from tool_loop.ini_handler import PollingFlags, parse_and_apply

        raw = "[[#batch1]]\n[areas]\nA = 教室\n\n[schedule]\n04-06 = A:\n"
        _resp, ctx = parse_and_apply(
            raw,
            all_ids=[1001, 1002, 1003],
            debt_counts={},
            credit_counts={},
            inactive_ids=[],
            last_pointer=0,
            start_date=date(2026, 4, 5),
            schedule_pool=[],
            required_slots=[],
            flags=PollingFlags(hints_on=True, max_rounds=10),
            round_num=1,
        )
        codes = [rejection[1] for rejection in ctx.rejected]
        self.assertIn("EMPTY_SLOT", codes)
        self.assertEqual(ctx.accepted, [])


# ------------------------------------------------------------------------------
# tool_loop E2E（mock LLM）：不完整不得提交 state、不得返回 ok
# ------------------------------------------------------------------------------

class _FixedToolLoopDateTime(datetime):
    @staticmethod
    def now():
        return datetime(2026, 4, 5)


def _tool_loop_ctx(tmp: Path) -> Context:
    config = {
        **BASE_CONFIG,
        "areas": ["教室"],
        "area_per_day_counts": {"教室": 2},
    }
    ctx = make_ctx(tmp, config=config)
    save_config(ctx, config)
    return ctx


def _tool_loop_plan():
    from execution_profiles import ExecutionPlan, ExecutionProfile

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


def _tool_call_response(ini: str) -> str:
    return json.dumps({
        "tool_calls": [{
            "function": {
                "name": "fill_schedule",
                "arguments": json.dumps({"schedule": ini}),
            }
        }]
    })


class ToolLoopE2ECompletenessTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())

    def tearDown(self):
        import shutil

        shutil.rmtree(self.tmp, ignore_errors=True)

    def _run(self, ini: str):
        from tool_loop import executor as tool_loop_executor

        with patch("tool_loop.executor.call_llm_raw") as mock_llm, \
                patch.object(tool_loop_executor, "datetime", _FixedToolLoopDateTime):
            mock_llm.return_value = _tool_call_response(ini)
            return tool_loop_executor.run_tool_loop_schedule(
                ctx=_tool_loop_ctx(self.tmp),
                input_data={"instruction": "安排4月6日到4月7日"},
                execution_plan=_tool_loop_plan(),
            )

    def test_understaffed_area_not_committed(self):
        ini = (
            "[[#batch1]]\n[areas]\nA = 教室\n\n[schedule]\n"
            "04-06 = A:1001 1002\n04-07 = A:1003\n"  # 04-07 缺 1 人
        )
        result = self._run(ini)
        self.assertEqual(result["status"], "partial")
        # 不完整结果不得提交为正式排班
        self.assertEqual(load_state(self.tmp / "state.json")["schedule_pool"], [])
        self.assertTrue(result.get("remaining"))

    def test_finalize_with_understaffed_slot_not_committed(self):
        """@finalize 是模型的提交请求，不是完成证明。"""
        ini = (
            "[[#batch1]]\n[areas]\nA = 教室\n\n[schedule]\n"
            "04-06 = A:1001 1002\n04-07 = A:1003\n\n@finalize\n"
        )
        result = self._run(ini)
        self.assertEqual(result["status"], "partial")
        self.assertTrue(result["tool_loop_meta"]["finalized"])
        self.assertEqual(load_state(self.tmp / "state.json")["schedule_pool"], [])

    def test_complete_run_still_commits(self):
        """回归保护：完整排班照常提交并返回 ok。"""
        ini = (
            "[[#batch1]]\n[areas]\nA = 教室\n\n[schedule]\n"
            "04-06 = A:1001 1002\n04-07 = A:1003 1004\n\n@finalize\n"
        )
        result = self._run(ini)
        self.assertEqual(result["status"], "ok")
        pool = load_state(self.tmp / "state.json")["schedule_pool"]
        self.assertEqual(len(pool), 2)


if __name__ == "__main__":
    unittest.main(verbosity=2)
