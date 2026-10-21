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
from datetime import date, datetime, timedelta
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))

from state_ops import Context, load_roster_entries, load_state, save_json_atomic, save_config

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


class InactivePersonnelTests(unittest.TestCase):
    """停用人员不得进班表（single_pass 曾传 all_ids 放行停用者）。"""

    def _tmp(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        return Path(tmp)

    def test_inactive_person_stripped_with_warning(self):
        from single_pass_executor import apply_single_pass_completion

        roster = "id,name,active\n1001,张三,1\n1002,李四,1\n1003,王五,1\n1004,赵六,0\n"
        tmp = self._tmp()
        ctx = make_ctx(
            tmp,
            config={**BASE_CONFIG, "areas": ["教室"], "area_per_day_counts": {"教室": 2}},
            roster=roster,
        )
        completion = "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1004\n"
        result = apply_single_pass_completion(
            ctx, completion, {"mode": "single_pass", "start_date": "2026-10-22", "trace_id": "t"},
        )
        self.assertEqual(result["status"], "success")
        names = load_state(ctx.paths["state"])["schedule_pool"][-1]["area_assignments"]["教室"]
        self.assertNotIn("赵六", names)  # 停用者被剔除
        self.assertEqual(len(names), 2)  # 缺额由在岗人员补齐
        self.assertTrue(any("停用" in warning for warning in result["adjustment_warnings"]))

    def test_only_inactive_person_still_filled_by_active_rotation(self):
        from single_pass_executor import apply_single_pass_completion

        roster = "id,name,active\n1001,张三,0\n1002,李四,1\n1003,王五,1\n"
        tmp = self._tmp()
        ctx = make_ctx(
            tmp,
            config={**BASE_CONFIG, "areas": ["教室"], "area_per_day_counts": {"教室": 2}},
            roster=roster,
        )
        completion = "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001\n"
        result = apply_single_pass_completion(
            ctx, completion, {"mode": "single_pass", "start_date": "2026-10-22", "trace_id": "t"},
        )
        self.assertEqual(result["status"], "success")
        names = load_state(ctx.paths["state"])["schedule_pool"][-1]["area_assignments"]["教室"]
        self.assertEqual(names, ["李四", "王五"])


class PerDayAbsenceTests(unittest.TestCase):
    """缺席必须逐日判定：请一天假不得误伤整个排班窗口。"""

    def _tmp(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        return Path(tmp)

    def test_single_day_absence_does_not_affect_other_days(self):
        from single_pass_executor import apply_single_pass_completion
        from state_ops import update_state

        tmp = self._tmp()
        ctx = make_ctx(tmp)  # 未配置区域 → 动态区域，不触发自动补齐，便于观察剔除
        update_state(
            ctx.paths["state"],
            lambda state: {**state, "absences": [{"id": 1001, "from": "2026-10-23", "to": "2026-10-23"}]},
        )
        completion = (
            "[areas]\nA = 教室\n[schedule]\n"
            "10-22 = A:1001 1002\n10-23 = A:1001 1003\n10-24 = A:1001 1002\n"
        )
        result = apply_single_pass_completion(
            ctx, completion, {"mode": "single_pass", "start_date": "2026-10-22", "trace_id": "t"},
        )
        self.assertEqual(result["status"], "success")
        by_date = {
            entry["date"]: entry["area_assignments"]["教室"]
            for entry in load_state(ctx.paths["state"])["schedule_pool"]
        }
        self.assertEqual(by_date["2026-10-22"], ["张三", "李四"])
        self.assertEqual(by_date["2026-10-23"], ["王五"])  # 仅当天剔除
        self.assertEqual(by_date["2026-10-24"], ["张三", "李四"])

    def test_absence_span_covers_every_day_in_range(self):
        from single_pass_executor import apply_single_pass_completion
        from state_ops import update_state

        tmp = self._tmp()
        ctx = make_ctx(tmp)
        update_state(
            ctx.paths["state"],
            lambda state: {**state, "absences": [{"id": 1001, "from": "2026-10-22", "to": "2026-10-24"}]},
        )
        completion = (
            "[areas]\nA = 教室\n[schedule]\n"
            "10-22 = A:1001 1002\n10-23 = A:1001 1003\n10-24 = A:1001 1002\n"
        )
        result = apply_single_pass_completion(
            ctx, completion, {"mode": "single_pass", "start_date": "2026-10-22", "trace_id": "t"},
        )
        self.assertEqual(result["status"], "success")
        for entry in load_state(ctx.paths["state"])["schedule_pool"]:
            self.assertNotIn("张三", entry["area_assignments"]["教室"])


class WindowCoverageTests(unittest.TestCase):
    """排班窗口覆盖不再静默：缺日/越界日期进 adjustment_warnings（不阻断）。"""

    def _tmp(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        return Path(tmp)

    def _apply(self, ctx, completion, start_date="2026-10-22"):
        from single_pass_executor import apply_single_pass_completion

        return apply_single_pass_completion(
            ctx, completion, {"mode": "single_pass", "start_date": start_date, "trace_id": "t"},
        )

    def test_partial_window_warns_but_succeeds(self):
        tmp = self._tmp()
        ctx = make_ctx(tmp)
        result = self._apply(ctx, "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n10-23 = A:1001 1003\n")
        self.assertEqual(result["status"], "success")
        warnings = result["adjustment_warnings"]
        self.assertTrue(any("未覆盖日期" in warning for warning in warnings))
        self.assertTrue(any("10-28" in warning for warning in warnings))

    def test_full_window_has_no_coverage_warning(self):
        tmp = self._tmp()
        ctx = make_ctx(tmp)
        lines = "\n".join(
            f"10-{day:02d} = A:1001 1002" for day in range(22, 29)
        )
        result = self._apply(ctx, f"[areas]\nA = 教室\n[schedule]\n{lines}\n")
        self.assertEqual(result["status"], "success")
        self.assertFalse(any("未覆盖日期" in warning for warning in result["adjustment_warnings"]))

    def test_out_of_window_dates_warn(self):
        tmp = self._tmp()
        ctx = make_ctx(tmp)
        result = self._apply(ctx, "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n11-30 = A:1001 1002\n")
        self.assertEqual(result["status"], "success")
        self.assertTrue(any("超出排班窗口" in warning for warning in result["adjustment_warnings"]))


def _snapshot(active_ids):
    from datetime import datetime as _dt

    from multi_agent.contracts import FrozenSnapshot

    return FrozenSnapshot(
        trace_id="", request_source="", instruction="",
        request_time=_dt(2026, 3, 16), start_date=date(2026, 3, 16),
        config={}, state={}, name_to_id={}, id_to_name={},
        all_ids=list(active_ids), active_ids=list(active_ids), inactive_ids=[],
        id_to_active={}, debt_list=[], credit_list=[], last_pointer=0,
        previous_note="", duty_rule="",
    )


class MultiAgentValidatorStrengthTests(unittest.TestCase):
    """multi_agent：agent6 结构校验 + 容量口径（复用允许同日跨区）。"""

    def _barrier2(self, template=None, final_pool=None, dates=None):
        return {
            "template": template or {"2026-03-16": {"A": 2, "B": 2}},
            "dates": dates or ["2026-03-16"],
            "final_pool": final_pool if final_pool is not None else [1, 2, 3],
            "supported_rules": [],
            "warnings": [],
            "unsupported_rules": [],
            "priority_pool": [],
            "pointer_pool": [],
            "pointer_after": 0,
            "consumed_credit_ids": [],
            "slots": [],
        }

    def test_agent6_rejects_missing_date(self):
        from multi_agent.validators import validate_agent6_output

        barrier2 = self._barrier2(dates=["2026-03-16", "2026-03-17"])
        raw = {"schedule": [{"date": "2026-03-16", "area_ids": {"A": [1, 2], "B": [3, 1]}}]}
        with self.assertRaises(ValueError):
            validate_agent6_output(raw, barrier2)

    def test_agent6_rejects_wrong_area_set(self):
        from multi_agent.validators import validate_agent6_output

        barrier2 = self._barrier2()
        raw = {"schedule": [{"date": "2026-03-16", "area_ids": {"A": [1, 2]}}]}
        with self.assertRaises(ValueError):
            validate_agent6_output(raw, barrier2)

    def test_agent6_rejects_understaffed_area(self):
        from multi_agent.validators import validate_agent6_output

        barrier2 = self._barrier2()
        raw = {"schedule": [{"date": "2026-03-16", "area_ids": {"A": [1], "B": [2, 3]}}]}
        with self.assertRaises(ValueError):
            validate_agent6_output(raw, barrier2)

    def test_agent6_accepts_valid_schedule_with_cross_area_reuse(self):
        from multi_agent.validators import validate_agent6_output

        barrier2 = self._barrier2(final_pool=[1, 2, 3])
        raw = {"schedule": [{"date": "2026-03-16", "area_ids": {"A": [1, 2], "B": [2, 3]}}]}
        result = validate_agent6_output(raw, barrier2)
        self.assertEqual(len(result["schedule"]), 1)

    def test_capacity_allows_same_day_cross_area_reuse(self):
        """3 人、最忙日 A:2+B:2：跨区复用下可行，不得判"人手不足"。"""
        from multi_agent.executor import _check_capacity
        from multi_agent.contracts import FrozenSnapshot
        from datetime import datetime

        snapshot = _snapshot(active_ids=[1, 2, 3])
        barrier1 = {"absent_ids": [], "template": {"2026-03-16": {"A": 2, "B": 2}}}
        barrier2 = self._barrier2(final_pool=[1, 2, 3])
        _check_capacity(snapshot, barrier1, barrier2)  # 不得抛错

    def test_capacity_rejects_when_largest_area_cannot_be_staffed(self):
        from multi_agent.executor import _check_capacity
        from multi_agent.contracts import FrozenSnapshot
        from datetime import datetime

        snapshot = _snapshot(active_ids=[1])
        barrier1 = {"absent_ids": [], "template": {"2026-03-16": {"A": 2}}}
        barrier2 = self._barrier2(final_pool=[1])
        with self.assertRaises(ValueError):
            _check_capacity(snapshot, barrier1, barrier2)

    def test_capacity_rejects_when_final_pool_too_small(self):
        """final_pool 小于最忙日最大区域人数 → agent6/兜底都排不出，须前置报错。"""
        from multi_agent.executor import _check_capacity
        from multi_agent.contracts import FrozenSnapshot
        from datetime import datetime

        snapshot = _snapshot(active_ids=[1, 2, 3])
        barrier1 = {"absent_ids": [], "template": {"2026-03-16": {"A": 2}}}
        barrier2 = self._barrier2(template={"2026-03-16": {"A": 2}}, final_pool=[1])
        with self.assertRaises(ValueError):
            _check_capacity(snapshot, barrier1, barrier2)


class OfflineAreaSourceTests(unittest.TestCase):
    """离线排班：config 配置了区域时以 config 为准，不再学历史结构。"""

    def _tmp(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        return Path(tmp)

    def test_configured_areas_win_over_learned_structure(self):
        from offline_scheduler import run_offline_schedule
        from execution_profiles import build_execution_plan, resolve_execution_profile
        from state_ops import load_config

        tmp = self._tmp()
        config = {**BASE_CONFIG, "areas": ["操场"], "area_per_day_counts": {"操场": 2}}
        ctx = make_ctx(
            tmp,
            config=config,
            state={
                "schedule_pool": [
                    {"date": "2026-04-21", "area_assignments": {"教室": ["张三", "李四"], "走廊": ["王五"]}}
                ],
                "debt_counts": {}, "credit_counts": {}, "last_pointer": 0,
            },
        )
        plan = build_execution_plan(resolve_execution_profile({}, load_config(ctx)))
        result = run_offline_schedule(ctx, {"trace_id": "t"}, plan)
        self.assertEqual(result["status"], "success")
        pool = load_state(ctx.paths["state"])["schedule_pool"]
        new_entries = [entry for entry in pool if entry["date"] != "2026-04-21"]
        self.assertTrue(new_entries)
        for entry in new_entries:
            self.assertEqual(set(entry["area_assignments"]), {"操场"})


class DateResolutionTests(unittest.TestCase):
    """日期解析：乱序/过去日期必须显式报错，合法跨年滚动必须保留。"""

    def test_out_of_order_dates_rejected(self):
        from llm_transport import parse_schedule_completion

        completion = "[areas]\nA = X\n[schedule]\n03-25 = A:1\n03-24 = A:2\n"
        with self.assertRaises(ValueError) as caught:
            parse_schedule_completion(completion, "2026-03-24")
        message = str(caught.exception)
        self.assertIn("03-24", message)
        self.assertIn("03-25", message)

    def test_past_first_date_rejected(self):
        from llm_transport import parse_schedule_completion

        completion = "[areas]\nA = X\n[schedule]\n03-23 = A:1\n"
        with self.assertRaises(ValueError) as caught:
            parse_schedule_completion(completion, "2026-03-24")
        self.assertIn("before the schedule start", str(caught.exception))

    def test_cross_year_boundary_still_allowed(self):
        """回归保护：12-31 -> 01-01 这类合法跨年滚动不得被误杀。"""
        from llm_transport import parse_schedule_completion

        completion = "[areas]\nA = X\n[schedule]\n12-31 = A:1\n01-02 = A:2\n"
        parsed, _ = parse_schedule_completion(completion, "2026-12-31")
        self.assertEqual(
            [entry["date"] for entry in parsed["schedule"]],
            ["2026-12-31", "2027-01-02"],
        )

    def test_tool_loop_mmdd_out_of_order_rejected(self):
        from tool_loop.ini_handler import _parse_mmdd_to_date

        with self.assertRaises(ValueError) as caught:
            _parse_mmdd_to_date("04-06", date(2026, 4, 5), date(2026, 4, 7))
        self.assertIn("ascending", str(caught.exception))

    def test_tool_loop_mmdd_past_start_rejected(self):
        from tool_loop.ini_handler import _parse_mmdd_to_date

        with self.assertRaises(ValueError):
            _parse_mmdd_to_date("04-01", date(2026, 4, 5), None)

    def test_tool_loop_start_date_follows_instruction(self):
        """指令点名了更早的日期范围时，解析锚点必须跟随指令（否则过去的
        指令日期全被滚到明年，循环永远填不满）。"""
        from tool_loop.executor import _resolve_loop_start_date

        self.assertEqual(
            _resolve_loop_start_date("安排4月1日到4月7日", date(2026, 4, 5)),
            date(2026, 4, 1),
        )
        self.assertEqual(
            _resolve_loop_start_date("从 2026-04-01 到 2026-04-07", date(2026, 4, 5)),
            date(2026, 4, 1),
        )
        # 不得把人名/数量误判成日期
        self.assertEqual(_resolve_loop_start_date("教室2-3人值日", date(2026, 4, 5)), date(2026, 4, 5))
        self.assertEqual(_resolve_loop_start_date("排下周日", date(2026, 4, 5)), date(2026, 4, 5))

    def test_tool_loop_past_instruction_date_parses_to_this_year(self):
        from tool_loop.ini_handler import _parse_mmdd_to_date

        resolved = _parse_mmdd_to_date("04-01", date(2026, 4, 1), None)
        self.assertEqual(resolved, date(2026, 4, 1))


class OrchestratorDateTests(unittest.TestCase):
    """orchestrator 日期解析：M/D 范围不再必崩，ISO 范围不再被静默忽略。"""

    def test_compute_dates_handles_md_range(self):
        from orchestrator.executor import _compute_dates

        dates = _compute_dates("4月6日到4月8日", date(2026, 10, 21), 7)
        self.assertEqual(dates, [date(2026, 4, 6), date(2026, 4, 7), date(2026, 4, 8)])

    def test_compute_dates_handles_iso_range(self):
        from orchestrator.executor import _compute_dates

        dates = _compute_dates("从 2026-04-01 到 2026-04-03", date(2026, 10, 21), 7)
        self.assertEqual(dates, [date(2026, 4, 1), date(2026, 4, 2), date(2026, 4, 3)])

    def test_compute_dates_falls_back_to_default_window(self):
        from orchestrator.executor import _compute_dates

        dates = _compute_dates("排下周日", date(2026, 10, 21), 7)
        self.assertEqual(dates, [date(2026, 10, 21) + timedelta(days=i) for i in range(7)])

    def test_window_fragments_concatenate_in_window_order(self):
        """窗口片段必须按窗口序拼接：MM-DD 的年份推断跨窗共享 previous_date，
        按网络完成顺序拼接会把日期推到下一年（同输入两次运行结果不同）。"""
        from tool_loop.ini_handler import _parse_mmdd_to_date

        class _Window:
            def __init__(self, index, name):
                self.index = index
                self.name = name

        windows = [_Window(0, "p1"), _Window(1, "p2")]
        fragments = list(reversed(windows))  # 网络完成顺序：p2 先回
        ordered = sorted(fragments, key=lambda item: item.index)
        self.assertEqual([window.name for window in ordered], ["p1", "p2"])

        previous = None
        resolved = []
        for mmdd in ("04-05", "04-06", "04-07", "04-08"):
            day = _parse_mmdd_to_date(mmdd, date(2026, 4, 5), previous)
            resolved.append(day)
            previous = day
        self.assertEqual(resolved[0], date(2026, 4, 5))
        self.assertEqual(resolved[-1], date(2026, 4, 8))


class AbsenceUntilDateTests(unittest.TestCase):
    """"请假到 MM-DD"：同日不得滚到下一年，过去日期不得造出一整年缺席。"""

    TODAY = date(2026, 10, 21)

    def _extract(self, clause):
        from absence_ops import extract_absentees

        return extract_absentees(clause, {"张三": 1001}, today=self.TODAY)

    def test_until_today_is_single_day(self):
        result = self._extract("张三请假到10-21")
        self.assertEqual(result, [{"id": 1001, "from": "2026-10-21", "to": "2026-10-21"}])

    def test_until_past_date_does_not_create_year_long_absence(self):
        result = self._extract("张三请假到10-20")
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["from"], "2026-10-21")
        self.assertEqual(result[0]["to"], "2026-10-21")

    def test_until_future_date_keeps_range(self):
        result = self._extract("张三请假到10-25")
        self.assertEqual(result, [{"id": 1001, "from": "2026-10-21", "to": "2026-10-25"}])


class FrozenModeTests(unittest.TestCase):
    """tool_loop / orchestrator 已冻结：引擎必须拒绝调度，而不是跑半成品代码。"""

    def test_engine_refuses_frozen_modes(self):
        import engine
        from execution_profiles import ExecutionPlan, ExecutionProfile
        from unittest.mock import patch

        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        ctx = make_ctx(Path(tmp))

        for frozen_mode in ("tool_loop", "orchestrator"):
            profile = ExecutionProfile(
                model_profile="cloud",
                orchestration_mode=frozen_mode,
                provider_hint="",
                multi_agent_ready=False,
                edge_ready=False,
                multi_agent_execution_mode="auto",
                single_pass_strategy="cloud_standard",
            )
            plan = ExecutionPlan(
                profile=profile,
                runtime_mode=frozen_mode,
                prompt_pack_strategy="ini_tool",
                tasks=(),
                notes=(),
            )
            with patch.object(engine, "build_execution_plan", return_value=plan):
                result = engine.run_schedule(ctx, {"instruction": "x"})
            self.assertEqual(result["status"], "error")
            self.assertEqual(result["code"], "mode_frozen")

    def test_frozen_modes_are_not_selectable_from_config(self):
        """config 白名单丢弃 tool_loop_mode / orchestration_mode。"""
        from state_ops import load_config

        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        ctx = make_ctx(Path(tmp))
        save_json_atomic(
            ctx.paths["config"],
            {**BASE_CONFIG, "tool_loop_mode": "force", "orchestration_mode": "tool_loop"},
        )
        config = load_config(ctx)
        # 持久化白名单丢弃这两个键；运行时投影即使派生 orchestration_mode，
        # 也只会落在可选择的模式上（冻结模式不可达）。
        self.assertNotIn("tool_loop_mode", config)
        self.assertNotIn(config.get("orchestration_mode"), ("tool_loop", "orchestrator"))
        from engine import FROZEN_RUNTIME_MODES

        self.assertNotIn(config.get("orchestration_mode"), FROZEN_RUNTIME_MODES)


class LedgerIdempotenceTests(unittest.TestCase):
    """账本幂等：重复 ingest 同一份排班不得二次扣债/二次烧积分。"""

    def _tmp(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        return Path(tmp)

    def _apply(self, ctx, completion, start_date="2026-10-22"):
        from single_pass_executor import apply_single_pass_completion

        return apply_single_pass_completion(
            ctx, completion, {"mode": "single_pass", "start_date": start_date, "trace_id": "t"},
        )

    def _state(self, ctx):
        return load_state(ctx.paths["state"])

    def _counts(self, ctx, key):
        return {
            int(person_id): value
            for person_id, value in (load_state(ctx.paths["state"]).get(key) or {}).items()
        }

    def test_replayed_completion_does_not_double_decrement_debt(self):
        tmp = self._tmp()
        ctx = make_ctx(tmp, state={
            "schedule_pool": [], "debt_counts": {"1001": 2}, "credit_counts": {}, "last_pointer": 0,
        })
        completion = "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n"
        self._apply(ctx, completion)
        self.assertEqual(self._counts(ctx, "debt_counts"), {1001: 1})
        # 同一份 completion 再 ingest 一次：不得再扣一次
        self._apply(ctx, completion)
        self.assertEqual(self._counts(ctx, "debt_counts"), {1001: 1})

    def test_modified_date_settles_again(self):
        """被真正改写的日期要重新结算（不能为了幂等把该扣的也吞了）。"""
        tmp = self._tmp()
        ctx = make_ctx(tmp, state={
            "schedule_pool": [], "debt_counts": {"1001": 2}, "credit_counts": {}, "last_pointer": 0,
        })
        self._apply(ctx, "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001\n")
        self.assertEqual(self._counts(ctx, "debt_counts"), {1001: 1})
        self._apply(ctx, "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n")
        self.assertEqual(self._counts(ctx, "debt_counts"), {})

    def test_note_only_change_does_not_resettle(self):
        tmp = self._tmp()
        ctx = make_ctx(tmp, state={
            "schedule_pool": [], "debt_counts": {"1001": 2}, "credit_counts": {}, "last_pointer": 0,
        })
        self._apply(ctx, "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n")
        self.assertEqual(self._counts(ctx, "debt_counts"), {1001: 1})
        # 只改备注：人员没变，不得重新结算
        self._apply(ctx, "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002 # 换备注\n")
        self.assertEqual(self._counts(ctx, "debt_counts"), {1001: 1})


class ModelLedgerInjectionTests(unittest.TestCase):
    """模型通过 [state] 声明的账本变更是不可信输入。"""

    def _tmp(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        return Path(tmp)

    def _apply(self, ctx, completion, start_date="2026-10-22"):
        from single_pass_executor import apply_single_pass_completion

        return apply_single_pass_completion(
            ctx, completion, {"mode": "single_pass", "start_date": start_date, "trace_id": "t"},
        )

    def _counts(self, ctx, key):
        return {
            int(person_id): value
            for person_id, value in (load_state(ctx.paths["state"]).get(key) or {}).items()
        }

    def test_hallucinated_debt_id_dropped_with_warning(self):
        tmp = self._tmp()
        ctx = make_ctx(tmp)
        result = self._apply(
            ctx,
            "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n[state]\ndebt = 999*3\n",
        )
        self.assertEqual(result["status"], "success")
        self.assertNotIn(999, self._counts(ctx, "debt_counts"))
        self.assertTrue(any("999" in warning for warning in result["adjustment_warnings"]))

    def test_duplicate_consumed_credit_burns_once(self):
        tmp = self._tmp()
        ctx = make_ctx(tmp, state={
            "schedule_pool": [], "debt_counts": {}, "credit_counts": {"1003": 2}, "last_pointer": 0,
        })
        result = self._apply(
            ctx,
            "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n"
            "[state]\npointer = 1\nconsumed_credit = 1003 1003\n",
        )
        self.assertEqual(result["status"], "success")
        self.assertEqual(self._counts(ctx, "credit_counts"), {1003: 1})

    def test_pointer_moving_backwards_warns(self):
        tmp = self._tmp()
        ctx = make_ctx(tmp, state={
            "schedule_pool": [], "debt_counts": {}, "credit_counts": {}, "last_pointer": 3,
        })
        result = self._apply(
            ctx,
            "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n[state]\npointer = 0\n",
        )
        self.assertEqual(result["status"], "success")
        self.assertTrue(any("指针" in warning for warning in result["adjustment_warnings"]))


class RollbackSemanticsTests(unittest.TestCase):
    """回滚语义：运营类写入不得消耗回滚槽位；跨名单代的回滚必须拒绝。"""

    def _tmp(self):
        tmp = tempfile.mkdtemp()
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        return Path(tmp)

    def _run_once(self, ctx, completion="[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n"):
        from single_pass_executor import apply_single_pass_completion

        return apply_single_pass_completion(
            ctx, completion, {"mode": "single_pass", "start_date": "2026-10-22", "trace_id": "t"},
        )

    def test_operational_write_does_not_consume_rollback_slot(self):
        """清请假这类运营写入不得刷新 .prev：回滚必须是"撤销上一次排班"，
        而不是把排班成果原样奉还（旧行为让回滚看起来毫无效果）。"""
        from application.command_service import CommandService
        from state_ops import rollback_state

        tmp = self._tmp()
        ctx = make_ctx(tmp)
        self._run_once(ctx)
        self.assertEqual(len(load_state(ctx.paths["state"])["schedule_pool"]), 1)

        service = CommandService(_StubRuntime(tmp))
        service.manage_absences({"action": "clear"}, "t", "api")

        restored = rollback_state(ctx.paths["state"])
        self.assertEqual(restored["schedule_pool"], [])

    def test_rollback_allowed_when_roster_unchanged(self):
        from single_pass_executor import apply_single_pass_completion  # noqa: F401 (import order)
        from state_ops import rollback_state

        tmp = self._tmp()
        ctx = make_ctx(tmp)
        self._run_once(ctx)
        restored = rollback_state(
            ctx.paths["state"], roster_entries=load_roster_entries(ctx.paths["roster"])
        )
        self.assertEqual(restored["schedule_pool"], [])

    def test_roster_change_blocks_rollback(self):
        """名单重编号后回滚会把旧 ID 的账本写到新名单上（A 的欠账记到 B 头上），
        必须显式拒绝而不是静默错账。"""
        from application.command_service import CommandService
        from state_ops import rollback_state

        tmp = self._tmp()
        ctx = make_ctx(tmp, state={
            "schedule_pool": [], "debt_counts": {"1001": 2}, "credit_counts": {}, "last_pointer": 0,
        })
        self._run_once(ctx)
        self.assertEqual(len(load_state(ctx.paths["state"])["schedule_pool"]), 1)

        service = CommandService(_StubRuntime(tmp))
        service.update_roster(
            [
                {"id": 1002, "name": "李四", "active": True},
                {"id": 1001, "name": "张三", "active": True},
                {"id": 1003, "name": "王五", "active": True},
                {"id": 1004, "name": "赵六", "active": True},
            ],
            "t",
            "api",
        )

        with self.assertRaises(ValueError) as caught:
            rollback_state(
                ctx.paths["state"], roster_entries=load_roster_entries(ctx.paths["roster"])
            )
        self.assertIn("花名册", str(caught.exception))

    def test_legacy_snapshot_without_fingerprint_still_rolls_back(self):
        """兼容：旧快照没有名单指纹时不得挡住回滚（无法证明分歧就放行）。"""
        from state_ops import rollback_state, save_json_atomic

        tmp = self._tmp()
        ctx = make_ctx(tmp)
        save_json_atomic(ctx.paths["state"], {"schedule_pool": [], "debt_counts": {}})
        save_json_atomic(
            ctx.paths["state"].with_suffix(".prev" + ctx.paths["state"].suffix),
            {"schedule_pool": [], "debt_counts": {}, "credit_counts": {}, "last_pointer": 0},
        )
        restored = rollback_state(
            ctx.paths["state"], roster_entries=load_roster_entries(ctx.paths["roster"])
        )
        self.assertEqual(restored["schedule_pool"], [])


if __name__ == "__main__":
    unittest.main(verbosity=2)
