#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Wave 2 回归测试固化：突发情况处理（请假/缺席/临时调整/大扫除）。

覆盖（对应 2026-09-12 全量修复计划 F1-F5）：

* a) ``absence_ops.extract_absentees``：关键词+姓名同句判定、误报防护
     （“张三请假，李四补上”不伤李四）、时长解析（三天/到 MM-DD/明天）、
     无关键词句（大扫除）不误判。
* b) ``merge_absence_entries`` 区间合并；``absences_for_window`` 窗口重叠。
* c) ``compose_run_notes``：机器摘要 + 未过期 user_notes；过期备注被过滤。
* d) 旧 state 文件（无新字段）向后兼容；``prune_operational_state`` 清理
     过期缺席/备注/覆盖。
* e) V2 ``[state]`` 新增 ``absent = ...`` 行解析（llm_transport）。
* f) tool_loop ``parse_and_apply``：ABSENT 拒绝且不入班表；INACTIVE 拒绝
     且**不再泄漏进班表**（修复 wave2 前的既有缺陷）。
* g) ``_enforce_absence_and_counts``：缺席剔除 → 债务优先补位 → 超编裁剪
     （保护债务人）；人手不足告警。
* h) day_overrides 进入 tool_loop / orchestrator 的 required slots。
* i) multi_agent ``_check_capacity`` 人性化容量报错。
* j) CommandService.manage_absences / manage_run_notes / manage_day_overrides
     （含姓名解析与未知人员报错）。

约定：全部使用 tempfile 数据目录，绝不触碰 %LOCALAPPDATA% 下的真实数据。
"""

import json
import sys
import tempfile
import unittest
from datetime import date, datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import absence_ops
import state_ops
from state_ops import Context, load_state, save_json_atomic, update_state

TODAY = date(2026, 9, 12)
N2I = {"张三": 1001, "李四": 1002, "王五": 1003}


class AbsenceExtractionTests(unittest.TestCase):
    def test_keyword_with_name_same_day(self):
        result = absence_ops.extract_absentees("张三请假", N2I, today=TODAY)
        self.assertEqual(result, [{"id": 1001, "from": "2026-09-12", "to": "2026-09-12"}])

    def test_no_false_positive_for_replacement_clause(self):
        result = absence_ops.extract_absentees("张三请假，李四补上", N2I, today=TODAY)
        self.assertEqual([item["id"] for item in result], [1001])

    def test_enumeration_and_duration(self):
        result = absence_ops.extract_absentees("张三、李四都请假三天", N2I, today=TODAY)
        self.assertEqual(
            result,
            [
                {"id": 1001, "from": "2026-09-12", "to": "2026-09-14"},
                {"id": 1002, "from": "2026-09-12", "to": "2026-09-14"},
            ],
        )

    def test_until_date_and_tomorrow(self):
        result = absence_ops.extract_absentees(
            "王五生病到 09-20，另外张三明天请假", N2I, today=TODAY
        )
        by_id = {item["id"]: item for item in result}
        self.assertEqual(by_id[1003]["to"], "2026-09-20")
        self.assertEqual(by_id[1001]["from"], "2026-09-13")

    def test_cleaning_is_not_absence(self):
        result = absence_ops.extract_absentees("李四、王五去大扫除", N2I, today=TODAY)
        self.assertEqual(result, [])

    def test_merge_overlapping_ranges(self):
        merged = absence_ops.merge_absence_entries(
            [{"id": 1001, "from": "2026-09-12", "to": "2026-09-12"}],
            [{"id": 1001, "from": "2026-09-13", "to": "2026-09-14"}],
        )
        self.assertEqual(merged, [{"id": 1001, "from": "2026-09-12", "to": "2026-09-14"}])

    def test_window_overlap(self):
        absences = [{"id": 1005, "from": "2026-09-13", "to": "2026-09-15"}]
        self.assertEqual(
            absence_ops.absences_for_window(absences, date(2026, 9, 14), date(2026, 9, 20)),
            [1005],
        )
        self.assertEqual(
            absence_ops.absences_for_window(absences, date(2026, 9, 16), date(2026, 9, 20)),
            [],
        )


class RunNotesTests(unittest.TestCase):
    def test_compose_filters_expired(self):
        state = {
            "next_run_note": "mode=x",
            "user_notes": [
                {"text": "周三大扫除", "until": "2026-09-16"},
                {"text": "旧备注", "until": "2026-09-01"},
                {"text": "长期备注", "until": None},
            ],
        }
        composed = absence_ops.compose_run_notes(state, today=TODAY)
        self.assertIn("mode=x", composed)
        self.assertIn("周三大扫除", composed)
        self.assertIn("长期备注", composed)
        self.assertNotIn("旧备注", composed)


class StateNormalizationTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.data_dir = Path(self._tmp.name)

    def test_legacy_state_file_gets_new_fields(self):
        state_path = self.data_dir / "state.json"
        state_path.write_text(
            json.dumps({"schedule_pool": [], "debt_counts": {}, "credit_counts": {}}),
            encoding="utf-8",
        )
        loaded = load_state(state_path)
        self.assertEqual(loaded["absences"], [])
        self.assertEqual(loaded["user_notes"], [])
        self.assertEqual(loaded["day_overrides"], {})

    def test_prune_drops_expired_entries(self):
        state = {
            "absences": [
                {"id": 1005, "from": "2026-09-01", "to": "2026-09-10"},
                {"id": 1006, "from": "2026-09-13", "to": "2026-09-15"},
            ],
            "user_notes": [
                {"text": "旧", "until": "2026-09-01"},
                {"text": "新", "until": "2026-09-30"},
                {"text": "永久", "until": None},
            ],
            "day_overrides": {
                "2026-09-10": {"教室": 4},
                "2026-09-16": {"教室": 4},
            },
        }
        state_ops.prune_operational_state(state, today=TODAY)
        self.assertEqual([entry["id"] for entry in state["absences"]], [1006])
        self.assertEqual([note["text"] for note in state["user_notes"]], ["新", "永久"])
        self.assertEqual(list(state["day_overrides"]), ["2026-09-16"])


class V2ProtocolTests(unittest.TestCase):
    def test_state_absent_line_parsed(self):
        from llm_transport import parse_schedule_completion

        completion = (
            "[areas]\nA = 教室\n[schedule]\n09-13 = A:1001\n"
            "[state]\npointer = 2\nabsent = 1005 1007\n"
        )
        result, _ = parse_schedule_completion(completion, "2026-09-12")
        self.assertEqual(result["state_delta"].get("absent_ids"), [1005, 1007])

    def test_state_without_absent_line_backward_compatible(self):
        from llm_transport import parse_schedule_completion

        completion = "[areas]\nA = 教室\n[schedule]\n09-13 = A:1001\n[state]\npointer = 2\n"
        result, _ = parse_schedule_completion(completion, "2026-09-12")
        self.assertNotIn("absent_ids", result["state_delta"])

    def test_prompt_includes_absent_and_overrides(self):
        from build_prompt import build_prompt_messages

        messages = build_prompt_messages(
            all_ids=[1001, 1005],
            current_time="2026-09-12 08:00",
            id_to_active={1001: 1, 1005: 1},
            instruction="生成排班",
            duty_rule="",
            area_names=["教室"],
            area_per_day_counts={"教室": 2},
            debt_counts={},
            credit_counts={},
            start_date="2026-09-12",
            absent_ids=[1005],
            day_overrides={"2026-09-16": {"教室": 4}},
        )
        text = messages[0]["content"]
        self.assertIn("absent_ids=1005", text)
        self.assertIn("day_headcount_overrides=2026-09-16:教室=4", text)


class ToolLoopEnforcementTests(unittest.TestCase):
    def _run(self, absent_ids=None, inactive_ids=None):
        from tool_loop.ini_handler import PollingFlags, ScheduleUnit, parse_and_apply

        slots = [
            ScheduleUnit(date_iso="2026-09-14", area_name="教室", alias="A"),
            ScheduleUnit(date_iso="2026-09-14", area_name="教室", alias="A"),
        ]
        ini = "[areas]\nA = 教室\n\n[[#b1]]\n09-14 = A:1001 1005\n"
        _, ctx = parse_and_apply(
            ini,
            [1001, 1002, 1005],
            {},
            {},
            list(inactive_ids or []),
            0,
            date(2026, 9, 14),
            [],
            slots,
            PollingFlags(hints_on=False),
            1,
            absent_ids=list(absent_ids or []),
        )
        return ctx

    def test_absent_id_rejected_and_not_scheduled(self):
        ctx = self._run(absent_ids=[1005])
        self.assertTrue(any(code == "ABSENT" for _d, code, _m in ctx.rejected))
        self.assertEqual(ctx.accepted[0]["area_ids"]["教室"], [1001])

    def test_inactive_id_rejected_and_not_scheduled(self):
        # Regression: the validator used to log INACTIVE rejections while the
        # append loop still scheduled the rejected ID.
        ctx = self._run(inactive_ids=[1005])
        self.assertTrue(any(code == "INACTIVE" for _d, code, _m in ctx.rejected))
        self.assertEqual(ctx.accepted[0]["area_ids"]["教室"], [1001])

    def test_required_slots_honor_day_overrides(self):
        from tool_loop.executor import _build_required_slots

        slots = _build_required_slots(
            "",
            date(2026, 9, 14),
            {},
            [],
            ["教室"],
            {"教室": 2},
            {"2026-09-16": {"教室": 4}},
        )
        per_day = {}
        for unit in slots:
            per_day[unit.date_iso] = per_day.get(unit.date_iso, 0) + 1
        self.assertEqual(per_day["2026-09-15"], 2)
        self.assertEqual(per_day["2026-09-16"], 4)

    def test_write_state_prunes_expired_operational_entries(self):
        import tool_loop.executor as tool_executor
        from tool_loop.ini_handler import ExecutionCtx, PollingFlags

        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        state_path = Path(tmp.name) / "state.json"
        save_json_atomic(
            state_path,
            {
                "schedule_pool": [],
                "debt_counts": {},
                "credit_counts": {},
                "absences": [{"id": 1, "from": "2026-09-01", "to": "2026-09-01"}],
                "user_notes": [{"text": "旧", "until": "2026-09-01"}],
                "day_overrides": {"2026-09-01": {"教室": 4}},
            },
        )

        class _Ctx:
            paths = {"state": state_path}

        ctx_exec = ExecutionCtx(
            debt_counts={},
            credit_counts={},
            inactive_ids=set(),
            last_pointer=0,
            schedule_pool=[],
            remaining=[],
            round_num=1,
            flags=PollingFlags(hints_on=False),
            start_date=date(2026, 9, 12),
            accepted=[],
            rejected=[],
            invalid_cmds=[],
        )
        tool_executor._write_state(_Ctx(), ctx_exec, "t")
        persisted = load_state(state_path)
        self.assertEqual(persisted["absences"], [])
        self.assertEqual(persisted["user_notes"], [])
        self.assertEqual(persisted["day_overrides"], {})


class SinglePassEnforcementTests(unittest.TestCase):
    def test_strip_absent_then_fill_debt_first(self):
        from single_pass_executor import _enforce_absence_and_counts

        schedule = [{"date": "2026-09-14", "area_ids": {"教室": [1001, 1005]}, "note": ""}]
        warnings = _enforce_absence_and_counts(
            schedule,
            [1001, 1002, 1003, 1005],
            {1001: 1, 1002: 1, 1003: 1, 1005: 1},
            0,
            {"教室": 2},
            {"2026-09-14": {"教室": 3}},
            {1003: 1},
            {1005},
        )
        area_ids = schedule[0]["area_ids"]["教室"]
        self.assertNotIn(1005, area_ids)
        self.assertEqual(len(area_ids), 3)
        self.assertEqual(area_ids[1], 1003)  # debtor filled before plain rotation
        self.assertIn("剔除缺席人员 1005", " ".join(warnings))
        self.assertIn("自动修正", schedule[0]["note"])

    def test_trim_excess_protects_debtors(self):
        from single_pass_executor import _enforce_absence_and_counts

        schedule = [{"date": "2026-09-14", "area_ids": {"教室": [1003, 1001, 1002, 1004]}, "note": ""}]
        warnings = _enforce_absence_and_counts(
            schedule,
            [1001, 1002, 1003, 1004],
            {1001: 1, 1002: 1, 1003: 1, 1004: 1},
            0,
            {"教室": 2},
            {},
            {1003: 2},
            set(),
        )
        area_ids = schedule[0]["area_ids"]["教室"]
        self.assertEqual(len(area_ids), 2)
        self.assertIn(1003, area_ids)  # debtor protected from trimming
        self.assertTrue(any("超额裁剪" in warning for warning in warnings))

    def test_shortfall_with_empty_pool_warns(self):
        from single_pass_executor import _enforce_absence_and_counts

        schedule = [{"date": "2026-09-14", "area_ids": {"教室": [1001]}, "note": ""}]
        warnings = _enforce_absence_and_counts(
            schedule,
            [1001],
            {1001: 1},
            0,
            {"教室": 3},
            {},
            {},
            set(),
        )
        self.assertTrue(any("人手不足" in warning for warning in warnings))

    def test_apply_single_pass_completion_strips_absent_and_persists(self):
        from single_pass_executor import apply_single_pass_completion

        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        data_dir = Path(tmp.name)
        ctx = Context(data_dir)
        state_ops.save_roster_entries(
            ctx,
            [
                {"id": 1001, "name": "张三", "active": True},
                {"id": 1002, "name": "李四", "active": True},
                {"id": 1003, "name": "赵六", "active": True},
                {"id": 1005, "name": "王五", "active": True},
            ],
        )
        save_json_atomic(
            ctx.paths["config"],
            {"areas": ["教室"], "area_per_day_counts": {"教室": 3}},
        )
        update_state(
            ctx.paths["state"],
            lambda state: {**state, "absences": [{"id": 1005, "from": "2026-09-12", "to": "2026-09-14"}]},
        )
        completion = (
            "[areas]\nA = 教室\n[schedule]\n09-13 = A:1001 1005 1002\n"
            "[state]\npointer = 1\n"
        )
        resume_context = {"mode": "single_pass", "start_date": "2026-09-12", "trace_id": "t"}
        result = apply_single_pass_completion(ctx, completion, resume_context)
        self.assertEqual(result["status"], "success")
        self.assertTrue(any("剔除缺席人员 1005" in w for w in result["adjustment_warnings"]))
        pool = load_state(ctx.paths["state"])["schedule_pool"]
        day_entry = next(entry for entry in pool if entry["date"] == "2026-09-13")
        day_names = day_entry["area_assignments"]["教室"]
        self.assertNotIn("王五", day_names)
        self.assertEqual(day_names, ["张三", "李四", "赵六"])


class OrchestratorOverrideTests(unittest.TestCase):
    def test_required_slots_honor_day_overrides(self):
        import orchestrator.executor as orchestrator_executor

        slots = orchestrator_executor._build_required_slots(
            "",
            [date(2026, 9, 16)],
            {},
            ["教室"],
            {"教室": 2},
            day_overrides={"2026-09-16": {"教室": 4}},
        )
        self.assertEqual(len(slots), 4)


class MultiAgentCapacityTests(unittest.TestCase):
    def _snapshot(self):
        from multi_agent.contracts import FrozenSnapshot

        return FrozenSnapshot(
            trace_id="",
            request_source="",
            instruction="",
            request_time=datetime(2026, 9, 12),
            start_date=TODAY,
            config={},
            state={},
            name_to_id={},
            id_to_name={1001: "张三", 1002: "李四"},
            all_ids=[1001, 1002],
            active_ids=[1001, 1002],
            inactive_ids=[],
            id_to_active={1001: 1, 1002: 1},
            debt_list=[],
            credit_list=[],
            last_pointer=0,
            previous_note="",
            duty_rule="",
        )

    def test_capacity_shortfall_raises_friendly_error(self):
        from multi_agent.executor import _check_capacity

        snapshot = self._snapshot()
        barrier1 = {"absent_ids": [1002]}
        barrier2 = {"template": {"2026-09-14": {"教室": 3}}}
        with self.assertRaises(ValueError) as caught:
            _check_capacity(snapshot, barrier1, barrier2)
        message = str(caught.exception)
        self.assertIn("可用人员不足", message)
        self.assertIn("需要 3 人", message)
        self.assertIn("1 人可用", message)

    def test_capacity_sufficient_passes(self):
        from multi_agent.executor import _check_capacity

        snapshot = self._snapshot()
        _check_capacity(
            snapshot,
            {"absent_ids": []},
            {"template": {"2026-09-14": {"教室": 2}, "2026-09-15": {"教室": 2}}},
        )

    def test_barrier1_merges_persisted_absences(self):
        from multi_agent.validators import merge_barrier1

        snapshot = self._snapshot()
        object.__setattr__(snapshot, "absent_ids", [1002])
        anchor = {
            "dates": ["2026-09-14"],
            "template": {"2026-09-14": {"教室": 1}},
            "area_names": ["教室"],
            "total_slots": 1,
        }
        accounting = {"absent_ids": [], "new_debt_ids": [], "new_credit_ids": [], "must_run_ids": [], "volunteer_ids": [], "warnings": []}
        rules = {"supported_rules": [], "unsupported_rules": [], "warnings": []}
        merged = merge_barrier1(snapshot, anchor, accounting, rules)
        self.assertEqual(merged["absent_ids"], [1002])


class CommandServiceAdjustmentTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        data_dir = Path(self._tmp.name)

        class _Logger:
            def info(self, *a, **k):
                pass

            def warn(self, *a, **k):
                pass

            def error(self, *a, **k):
                pass

        class _Runtime:
            def __init__(self):
                self.data_dir = data_dir
                self.logger = _Logger()

            def new_trace_id(self):
                return "t-wave2"

            def publish_snapshot_changed(self, *a, **k):
                pass

        self.runtime = _Runtime()
        state_ops.save_roster_entries(
            Context(data_dir, logger=self.runtime.logger),
            [
                {"id": 1001, "name": "张三", "active": True},
                {"id": 1005, "name": "王五", "active": True},
            ],
        )

    def _service(self):
        from application.command_service import CommandService

        return CommandService(self.runtime)

    def test_absence_add_by_name_and_clear(self):
        service = self._service()
        result = service.manage_absences({"action": "add", "person": "王五", "from_date": "2026-09-12", "days": 3})
        self.assertEqual(result["absences"][0]["id"], 1005)
        self.assertEqual(result["absences"][0]["to"], "2026-09-14")
        service.manage_absences({"action": "clear", "person": "王五"})
        self.assertEqual(load_state(self.runtime.data_dir / "state.json")["absences"], [])

    def test_absence_unknown_person_raises(self):
        with self.assertRaises(ValueError):
            self._service().manage_absences({"action": "add", "person": "不存在"})

    def test_run_note_add_and_compose(self):
        service = self._service()
        service.manage_run_notes({"action": "add", "text": "周三大扫除", "until": "2026-09-30"})
        state = load_state(self.runtime.data_dir / "state.json")
        composed = absence_ops.compose_run_notes(state, today=TODAY)
        self.assertIn("周三大扫除", composed)

    def test_day_override_set_and_clear(self):
        service = self._service()
        service.manage_day_overrides({"action": "set", "date": "2026-09-16", "area": "教室", "count": 4})
        state = load_state(self.runtime.data_dir / "state.json")
        self.assertEqual(state["day_overrides"]["2026-09-16"]["教室"], 4)
        service.manage_day_overrides({"action": "clear", "date": "2026-09-16", "area": "教室"})
        state = load_state(self.runtime.data_dir / "state.json")
        self.assertNotIn("2026-09-16", state["day_overrides"])

    def test_day_override_requires_positive_count(self):
        with self.assertRaises(ValueError):
            self._service().manage_day_overrides({"action": "set", "date": "2026-09-16", "area": "教室", "count": 0})


if __name__ == "__main__":
    unittest.main()
