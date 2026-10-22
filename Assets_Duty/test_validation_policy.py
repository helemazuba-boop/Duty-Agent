#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""校验策略（validation_policy）回归测试。

背景：后端原有的 LLM 结果校验是 2026 年 2 月按"模型会乱写"的前提堆的，每个
执行模式各写一套。现在的云端模型基本不犯这类错误，只有 0.8B 级本地模型还需要
兜底，结果是"能干的模型的排班被静默改写且无法关闭"。

产品目标是三档：宽松（默认，非致命只警告）/ 严格（不满足即拒绝）/ 实验性智能
修复（自动修且必须展示修改）。本文件钉死的是**策略表本身**与**配置链路**，
执行器如何按表分派在 test_validation_chain.py 与各模式自己的测试里。

第一批落地时刻意让默认值等于历史行为（``repair``），使重构本身零行为变化；
默认值翻到 ``relaxed`` 是后续单独的提交，届时本文件的 DEFAULT 断言要一起改。

设计约束（不得违反）：
* 同日跨区域复用允许——任何档位都不得因此拒绝。
* 与本地已知事实冲突（未知名/停用/当天请假）在任何档位都是拒绝或修复，
  不得降级为"静默保留"。
* 账本幂等、apply_mode 历史保留、名单代际守卫、原子提交与档位无关，
  任何模式都不得关掉它们——它们防的是数据损坏，不是模型犯错。
"""

import json
import sys
import tempfile
import unittest
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import state_ops
import validation_policy as vp
from state_ops import Context, load_state, patch_config, save_json_atomic

BASE_PLAN = {
    "id": "standard", "name": "标准", "mode_id": "standard",
    "api_key": "test-key", "base_url": "https://example.com/v1",
    "model": "test-model", "model_profile": "cloud",
}


def config_with(profile: str = "cloud", **overrides) -> dict:
    plan = dict(BASE_PLAN)
    plan["model_profile"] = profile
    payload = {
        "version": 1,
        "selected_plan_id": "standard",
        "plan_presets": [plan],
        "polling": {"hints_on": True, "max_rounds": 10},
    }
    payload.update(overrides)
    return payload


class ModeNormalizationTests(unittest.TestCase):
    def test_aliases_round_trip(self):
        for raw in ("relaxed", "宽松", "宽松模式", "lenient", "loose", "RELAXED"):
            self.assertEqual(vp.normalize_validation_mode(raw), "relaxed", raw)
        for raw in ("strict", "严格", "严格模式"):
            self.assertEqual(vp.normalize_validation_mode(raw), "strict", raw)
        for raw in ("repair", "智能修复", "实验性智能修复", "smart_repair"):
            self.assertEqual(vp.normalize_validation_mode(raw), "repair", raw)
        for raw in ("auto", "自动"):
            self.assertEqual(vp.normalize_validation_mode(raw), "auto", raw)

    def test_unknown_and_empty_fall_back_to_default(self):
        # "unset" must still mean *some* explicit policy, never a silent no-op.
        self.assertEqual(vp.normalize_validation_mode(""), vp.DEFAULT_VALIDATION_MODE)
        self.assertEqual(vp.normalize_validation_mode(None), vp.DEFAULT_VALIDATION_MODE)
        self.assertEqual(vp.normalize_validation_mode("garbage"), vp.DEFAULT_VALIDATION_MODE)
        self.assertEqual(vp.normalize_validation_mode("  宽松  "), "relaxed")

    def test_default_is_a_real_mode(self):
        self.assertIn(vp.DEFAULT_VALIDATION_MODE, vp.VALIDATION_MODES)


class AutoResolutionTests(unittest.TestCase):
    def test_constrained_profiles_resolve_to_repair(self):
        # Sub-1B / edge models are the population that still needs the net.
        for profile in sorted(vp.CONSTRAINED_MODEL_PROFILES):
            resolved = vp.resolve_validation_mode(config_with(profile, validation_mode="auto"))
            self.assertEqual(resolved, "repair", profile)

    def test_capable_profiles_resolve_to_relaxed(self):
        for profile in ("cloud", "custom", "auto", ""):
            resolved = vp.resolve_validation_mode(config_with(profile, validation_mode="auto"))
            self.assertEqual(resolved, "relaxed", profile)

    def test_persisted_and_runtime_shapes_resolve_identically(self):
        # The persisted config keeps model_profile inside plan_presets; the
        # runtime projection flattens it. Reading only the flattened key made
        # a raw persisted config resolve to the wrong mode.
        persisted = config_with("campus_small", validation_mode="auto")
        flattened = {**persisted, "model_profile": "campus_small"}
        self.assertEqual(
            vp.resolve_validation_mode(persisted), vp.resolve_validation_mode(flattened)
        )
        self.assertEqual(vp.resolve_validation_mode(persisted), "repair")

    def test_explicit_modes_are_not_overridden_by_profile(self):
        for mode in ("relaxed", "strict", "repair"):
            resolved = vp.resolve_validation_mode(config_with("campus_small", validation_mode=mode))
            self.assertEqual(resolved, mode)

    def test_missing_config_is_safe(self):
        self.assertEqual(vp.resolve_validation_mode(None), vp.DEFAULT_VALIDATION_MODE)
        self.assertEqual(vp.resolve_validation_mode({}), vp.DEFAULT_VALIDATION_MODE)


class CheckMatrixTests(unittest.TestCase):
    """The whole table is pinned: re-classifying a check must be deliberate."""

    def test_every_check_has_all_three_modes(self):
        for name, row in vp.CHECK_ACTIONS.items():
            self.assertEqual(sorted(row), ["relaxed", "repair", "strict"], name)
            for mode, action in row.items():
                self.assertIn(action, vp.VALID_ACTIONS, f"{name}/{mode}")

    def test_local_truth_conflicts_are_never_downgraded_to_silent_keep(self):
        # unknown / duplicate-in-area stay fatal everywhere; inactive and
        # same-day absence may be *repaired* but never silently tolerated.
        for check in ("unknown_person", "inactive_person", "absent_same_day", "duplicate_in_area"):
            for mode in vp.VALIDATION_MODES:
                action = vp.CHECK_ACTIONS[check][mode]
                self.assertIn(action, (vp.ACTION_REJECT, vp.ACTION_REPAIR), f"{check}/{mode}")

    def test_relaxed_only_warns_on_model_discretion(self):
        discretion = (
            "headcount_short", "headcount_excess", "window_gap",
            "out_of_window", "phantom_area", "must_run_missing", "template_mismatch",
        )
        for check in discretion:
            self.assertEqual(vp.CHECK_ACTIONS[check]["relaxed"], vp.ACTION_WARN, check)

    def test_strict_rejects_model_discretion(self):
        discretion = (
            "headcount_short", "headcount_excess", "window_gap",
            "out_of_window", "phantom_area", "must_run_missing", "template_mismatch",
        )
        for check in discretion:
            self.assertEqual(vp.CHECK_ACTIONS[check]["strict"], vp.ACTION_REJECT, check)

    def test_repair_delegates_only_where_a_fix_exists(self):
        # `repair` must mean "the deterministic layer fixes it", never a claim
        # with nothing behind it. Headcount has a real fix in every executor;
        # window_gap's fix exists only in multi_agent (its fallback emits every
        # template date), so single_pass's window audit degrades it to a warning
        # and documents why.
        for check in ("headcount_short", "headcount_excess", "must_run_missing", "window_gap"):
            self.assertEqual(vp.CHECK_ACTIONS[check]["repair"], vp.ACTION_REPAIR, check)
        for check in ("out_of_window", "phantom_area", "template_mismatch"):
            self.assertEqual(vp.CHECK_ACTIONS[check]["repair"], vp.ACTION_WARN, check)

    def test_delegates_to_code_covers_reject_and_repair_only(self):
        strict = vp.ValidationPolicy("strict")
        self.assertTrue(strict.delegates_to_code("window_gap"))
        self.assertTrue(strict.delegates_to_code("headcount_short"))
        self.assertFalse(strict.delegates_to_code("cross_area_reuse"))
        relaxed = vp.ValidationPolicy("relaxed")
        self.assertFalse(relaxed.delegates_to_code("window_gap"))
        repair = vp.ValidationPolicy("repair")
        self.assertTrue(repair.delegates_to_code("window_gap"))
        self.assertTrue(repair.delegates_to_code("must_run_missing"))
        self.assertFalse(repair.delegates_to_code("out_of_window"))

    def test_cross_area_reuse_is_allowed_in_every_mode(self):
        # Product rule: same person, different areas, same day is fine. No
        # mode may reject it, and none may silently strip it either.
        for mode in vp.VALIDATION_MODES:
            self.assertNotEqual(vp.CHECK_ACTIONS["cross_area_reuse"][mode], vp.ACTION_REJECT, mode)

    def test_mode_independent_checks_cannot_be_switched_off(self):
        for mode in vp.VALIDATION_MODES:
            policy = vp.ValidationPolicy(mode)
            for check in vp.ALWAYS_ENFORCE:
                self.assertEqual(policy.action(check), vp.ACTION_ENFORCE, f"{check}/{mode}")
            for check in vp.ALWAYS_REJECT:
                self.assertEqual(policy.action(check), vp.ACTION_REJECT, f"{check}/{mode}")

    def test_unknown_check_raises_instead_of_silently_passing(self):
        policy = vp.ValidationPolicy("relaxed")
        with self.assertRaises(KeyError):
            policy.action("headcount_shortt")  # typo must not disable a guard

    def test_policy_helpers(self):
        strict = vp.ValidationPolicy("strict")
        self.assertTrue(strict.is_fatal("headcount_short"))
        self.assertTrue(strict.warns("cross_area_reuse"))
        self.assertFalse(strict.repairs("headcount_short"))
        relaxed = vp.ValidationPolicy("relaxed")
        self.assertFalse(relaxed.is_fatal("headcount_short"))
        self.assertTrue(relaxed.warns("headcount_short"))

    def test_from_config_keeps_configured_mode_visible(self):
        policy = vp.ValidationPolicy.from_config(config_with("campus_small", validation_mode="auto"))
        self.assertEqual(policy.configured_mode, "auto")
        self.assertEqual(policy.mode, "repair")

    def test_as_dict_is_json_serializable(self):
        payload = vp.ValidationPolicy("strict").as_dict()
        json.dumps(payload)
        self.assertEqual(payload["validation_mode"], "strict")
        self.assertEqual(set(payload["actions"]), set(vp.ALL_CHECKS))


class ValidationReportTests(unittest.TestCase):
    def test_repairs_are_also_warnings_but_not_vice_versa(self):
        report = vp.ValidationReport()
        report.warn("window_gap", "未覆盖日期 10-25")
        report.repair("headcount_short", "自动修正: 教室 补齐 1 人")
        self.assertEqual(report.warnings, ["未覆盖日期 10-25", "自动修正: 教室 补齐 1 人"])
        self.assertEqual(report.repairs, ["自动修正: 教室 补齐 1 人"])
        self.assertTrue(report.changed_schedule)

    def test_warning_only_report_did_not_change_the_schedule(self):
        report = vp.ValidationReport()
        report.warn("window_gap", "未覆盖日期 10-25")
        self.assertFalse(report.changed_schedule)

    def test_merge_combines_all_buckets(self):
        left, right = vp.ValidationReport(), vp.ValidationReport()
        left.warn("window_gap", "a")
        right.repair("headcount_short", "b")
        left.merge(right)
        self.assertEqual(left.warnings, ["a", "b"])
        self.assertEqual(left.repairs, ["b"])


class DescribeModesTests(unittest.TestCase):
    def test_four_modes_with_unique_values(self):
        modes = vp.describe_modes()
        self.assertEqual(len(modes), 4)
        values = [entry["value"] for entry in modes]
        self.assertEqual(len(set(values)), len(values))
        self.assertEqual(set(values), {"relaxed", "strict", "repair", "auto"})

    def test_exactly_one_mode_is_recommended(self):
        recommended = [entry for entry in vp.describe_modes() if entry.get("recommended")]
        self.assertEqual(len(recommended), 1)
        self.assertEqual(recommended[0]["value"], "relaxed")

    def test_every_mode_has_user_facing_copy(self):
        for entry in vp.describe_modes():
            for field in ("label", "summary", "detail"):
                self.assertTrue(str(entry.get(field, "")).strip(), f"{entry['value']}.{field}")


class ConfigPlumbingTests(unittest.TestCase):
    """A config key must survive three gates or it silently disappears.

    ``state_ops._normalize_persisted_config`` rebuilds the dict from a fixed
    key set, so an unregistered key is dropped without a word (that is exactly
    how ``tool_loop_mode`` vanished). ``patch_config`` has its own allowlist and
    ``DutyBackendConfigPatch`` is ``extra="forbid"``.
    """

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)

    def _ctx(self, config=None) -> Context:
        save_json_atomic(self.tmp / "config.json", config or config_with())
        return Context(data_dir=self.tmp, trace_id="test-trace")

    def test_default_config_carries_the_mode(self):
        from state_ops import create_default_config

        config = create_default_config()
        self.assertEqual(config["validation_mode"], vp.DEFAULT_VALIDATION_MODE)
        self.assertEqual(config["effective_validation_mode"], vp.DEFAULT_VALIDATION_MODE)

    def test_patch_config_accepts_validation_mode(self):
        ctx = self._ctx()
        result = patch_config(ctx, {"validation_mode": "strict"})
        self.assertEqual(result["validation_mode"], "strict")
        self.assertEqual(result["effective_validation_mode"], "strict")
        on_disk = json.loads((self.tmp / "config.json").read_text(encoding="utf-8-sig"))
        self.assertEqual(on_disk["validation_mode"], "strict")

    def test_patch_config_still_rejects_unknown_keys(self):
        ctx = self._ctx()
        with self.assertRaises(ValueError):
            patch_config(ctx, {"tool_loop_mode": "on"})

    def test_changing_only_the_mode_bumps_the_version(self):
        # _persisted_config_body decides the version bump; a key missing there
        # lets two concurrent policy PATCHes race without a 409.
        ctx = self._ctx()
        result = patch_config(ctx, {"validation_mode": "relaxed"})
        self.assertEqual(result["version"], 2)

    def test_reapplying_the_same_mode_does_not_bump_the_version(self):
        ctx = self._ctx()
        patch_config(ctx, {"validation_mode": "relaxed"})
        result = patch_config(ctx, {"validation_mode": "relaxed"})
        self.assertEqual(result["version"], 2)

    def test_legacy_config_without_the_key_is_migrated_on_load(self):
        legacy = config_with()
        legacy.pop("validation_mode", None)
        ctx = self._ctx(legacy)
        from state_ops import load_config

        config = load_config(ctx)
        self.assertEqual(config["validation_mode"], vp.DEFAULT_VALIDATION_MODE)
        on_disk = json.loads((self.tmp / "config.json").read_text(encoding="utf-8-sig"))
        self.assertIn("validation_mode", on_disk)

    def test_auto_is_persisted_verbatim_but_resolved_for_the_engine(self):
        ctx = self._ctx(config_with("campus_small", validation_mode="auto"))
        result = patch_config(ctx, {"validation_mode": "auto"})
        self.assertEqual(result["validation_mode"], "auto")
        self.assertEqual(result["effective_validation_mode"], "repair")

    def test_chinese_label_round_trips_through_config(self):
        ctx = self._ctx()
        result = patch_config(ctx, {"validation_mode": "宽松"})
        self.assertEqual(result["validation_mode"], "relaxed")


class PatchSchemaTests(unittest.TestCase):
    def test_patch_model_accepts_validation_mode(self):
        from models.schemas import DutyBackendConfigPatch

        model = DutyBackendConfigPatch.model_validate({"validation_mode": "relaxed"})
        self.assertEqual(model.validation_mode, "relaxed")

    def test_patch_model_still_forbids_unknown_keys(self):
        from models.schemas import DutyBackendConfigPatch
        from pydantic import ValidationError

        with self.assertRaises(ValidationError):
            DutyBackendConfigPatch.model_validate({"validation_mode": "relaxed", "nope": 1})

    def test_response_model_carries_both_mode_fields(self):
        # GET /api/v1/snapshot nests this model with extra="forbid"; a runtime
        # config key that is not declared here breaks the bridge's main data
        # path with a ResponseValidationError, not a missing field.
        from models.schemas import DutyBackendConfigModel

        model = DutyBackendConfigModel.model_validate({})
        self.assertEqual(model.validation_mode, vp.DEFAULT_VALIDATION_MODE)
        self.assertEqual(model.effective_validation_mode, vp.DEFAULT_VALIDATION_MODE)
        model = DutyBackendConfigModel.model_validate(
            {"validation_mode": "auto", "effective_validation_mode": "repair"}
        )
        self.assertEqual(model.validation_mode, "auto")
        self.assertEqual(model.effective_validation_mode, "repair")


class EnforcementDispatchTests(unittest.TestCase):
    """The executors must ask the table, not decide for themselves.

    These drive ``_enforce_absence_and_counts`` directly so a regression in the
    dispatch (rather than in the repair maths, which test_repair_wave2 covers)
    is caught here.
    """

    RELAXED = None  # built in setUp to keep the assertions readable

    def _run(self, schedule, *, mode, area_counts, absences=None, day_overrides=None):
        from single_pass_executor import _enforce_absence_and_counts

        return _enforce_absence_and_counts(
            schedule,
            [1001, 1002, 1003, 1004],
            {1001: 1, 1002: 1, 1003: 1, 1004: 1},
            0,
            area_counts,
            day_overrides or {},
            {},
            set(),
            absences=absences,
            policy=vp.ValidationPolicy(mode),
        )

    def test_relaxed_does_not_fill_a_shortfall(self):
        schedule = [{"date": "2026-09-14", "area_ids": {"教室": [1001]}, "note": ""}]
        warnings = self._run(schedule, mode="relaxed", area_counts={"教室": 3})
        self.assertEqual(schedule[0]["area_ids"]["教室"], [1001])  # untouched
        self.assertTrue(any("宽松模式未修改" in w for w in warnings))
        self.assertNotIn("自动修正", schedule[0]["note"])

    def test_relaxed_does_not_trim_an_excess(self):
        schedule = [{"date": "2026-09-14", "area_ids": {"教室": [1001, 1002, 1003]}, "note": ""}]
        warnings = self._run(schedule, mode="relaxed", area_counts={"教室": 2})
        self.assertEqual(schedule[0]["area_ids"]["教室"], [1001, 1002, 1003])
        self.assertTrue(any("宽松模式未修改" in w for w in warnings))
        self.assertNotIn("自动修正", schedule[0]["note"])

    def test_relaxed_still_refuses_an_inactive_person(self):
        from single_pass_executor import _enforce_absence_and_counts

        schedule = [{"date": "2026-09-14", "area_ids": {"教室": [1001, 1004]}, "note": ""}]
        with self.assertRaises(ValueError) as caught:
            _enforce_absence_and_counts(
                schedule, [1001, 1002, 1003, 1004],
                {1001: 1, 1002: 1, 1003: 1, 1004: 0},  # 1004 deactivated
                0, {"教室": 2}, {}, {}, set(),
                policy=vp.ValidationPolicy("relaxed"),
            )
        self.assertIn("已停用", str(caught.exception))

    def test_relaxed_still_refuses_someone_on_leave(self):
        schedule = [{"date": "2026-09-14", "area_ids": {"教室": [1001, 1002]}, "note": ""}]
        with self.assertRaises(ValueError) as caught:
            self._run(
                schedule, mode="relaxed", area_counts={"教室": 2},
                absences=[{"id": 1002, "from": "2026-09-14", "to": "2026-09-14"}],
            )
        self.assertIn("请假", str(caught.exception))

    def test_strict_refuses_a_shortfall(self):
        schedule = [{"date": "2026-09-14", "area_ids": {"教室": [1001]}, "note": ""}]
        with self.assertRaises(ValueError) as caught:
            self._run(schedule, mode="strict", area_counts={"教室": 3})
        self.assertIn("少于所需", str(caught.exception))

    def test_strict_refuses_an_excess(self):
        schedule = [{"date": "2026-09-14", "area_ids": {"教室": [1001, 1002, 1003]}, "note": ""}]
        with self.assertRaises(ValueError) as caught:
            self._run(schedule, mode="strict", area_counts={"教室": 2})
        self.assertIn("超过所需", str(caught.exception))

    def test_repair_still_fills_and_stamps_the_note(self):
        # Historical behaviour, now behind the table rather than hard-coded.
        schedule = [{"date": "2026-09-14", "area_ids": {"教室": [1001]}, "note": ""}]
        warnings = self._run(schedule, mode="repair", area_counts={"教室": 2})
        self.assertEqual(len(schedule[0]["area_ids"]["教室"]), 2)
        self.assertTrue(any("补齐" in w for w in warnings))
        self.assertIn("自动修正", schedule[0]["note"])

    def test_relaxed_allows_same_day_cross_area_reuse(self):
        # Product constraint: one person in two areas on the same day is legal
        # in every mode. Neither mode may strip it or reject the run.
        schedule = [
            {
                "date": "2026-09-14",
                "area_ids": {"教室": [1001, 1002], "楼道": [1001, 1003]},
                "note": "",
            }
        ]
        warnings = self._run(schedule, mode="relaxed", area_counts={"教室": 2, "楼道": 2})
        self.assertEqual(schedule[0]["area_ids"]["教室"], [1001, 1002])
        self.assertEqual(schedule[0]["area_ids"]["楼道"], [1001, 1003])
        self.assertEqual(warnings, [])

    def test_strict_window_gap_refuses(self):
        from single_pass_executor import _audit_schedule_window

        schedule = [{"date": "2026-09-14", "area_ids": {"教室": [1001]}, "note": ""}]
        with self.assertRaises(ValueError) as caught:
            _audit_schedule_window(
                schedule, ["2026-09-13", "2026-09-14"], vp.ValidationPolicy("strict")
            )
        self.assertIn("未覆盖日期", str(caught.exception))

    def test_relaxed_window_gap_only_warns(self):
        from single_pass_executor import _audit_schedule_window

        schedule = [{"date": "2026-09-14", "area_ids": {"教室": [1001]}, "note": ""}]
        warnings = _audit_schedule_window(
            schedule, ["2026-09-13", "2026-09-14"], vp.ValidationPolicy("relaxed")
        )
        self.assertTrue(any("未覆盖日期" in w for w in warnings))
        self.assertEqual(len(schedule), 1)  # never rewrites the date set


class EndToEndModeTests(unittest.TestCase):
    """The mode must survive the whole settle path, not just one function."""

    def setUp(self):
        import tempfile

        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.data_dir = Path(self._tmp.name)
        self.ctx = Context(data_dir=self.data_dir)
        state_ops.save_roster_entries(
            self.ctx,
            [
                {"id": 1001, "name": "张三", "active": True},
                {"id": 1002, "name": "李四", "active": True},
                {"id": 1003, "name": "赵六", "active": True},
            ],
        )

    def _write_config(self, **overrides):
        payload = {"areas": ["教室"], "area_per_day_counts": {"教室": 3}}
        payload.update(overrides)
        save_json_atomic(self.ctx.paths["config"], payload)

    COMPLETION = (
        "[areas]\nA = 教室\n[schedule]\n09-14 = A:1001 1002\n"
        "[state]\npointer = 1\n"
    )
    RESUME = {"mode": "single_pass", "start_date": "2026-09-14", "trace_id": "t"}

    def test_relaxed_commits_the_models_short_schedule_verbatim(self):
        from single_pass_executor import apply_single_pass_completion

        self._write_config(validation_mode="relaxed")
        result = apply_single_pass_completion(self.ctx, self.COMPLETION, self.RESUME)

        self.assertEqual(result["status"], "success")
        self.assertTrue(any("人手不足" in w for w in result["adjustment_warnings"]))
        pool = load_state(self.ctx.paths["state"])["schedule_pool"]
        entry = next(item for item in pool if item["date"] == "2026-09-14")
        # Exactly what the model wrote - no third person conjured by the code.
        self.assertEqual(entry["area_assignments"]["教室"], ["张三", "李四"])
        self.assertNotIn("自动修正", entry["note"])

    def test_strict_refuses_the_same_completion(self):
        from single_pass_executor import apply_single_pass_completion

        self._write_config(validation_mode="strict")
        with self.assertRaises(ValueError):
            apply_single_pass_completion(self.ctx, self.COMPLETION, self.RESUME)
        # Nothing may be persisted when strict refuses.
        self.assertEqual(load_state(self.ctx.paths["state"]).get("schedule_pool", []), [])

    def test_repair_fills_it_and_says_so(self):
        from single_pass_executor import apply_single_pass_completion

        self._write_config(validation_mode="repair")
        result = apply_single_pass_completion(self.ctx, self.COMPLETION, self.RESUME)

        self.assertEqual(result["status"], "success")
        pool = load_state(self.ctx.paths["state"])["schedule_pool"]
        entry = next(item for item in pool if item["date"] == "2026-09-14")
        self.assertEqual(len(entry["area_assignments"]["教室"]), 3)
        self.assertIn("自动修正", entry["note"])

    def test_auto_with_a_constrained_profile_behaves_like_repair(self):
        from single_pass_executor import apply_single_pass_completion

        self._write_config(
            validation_mode="auto",
            plan_presets=[{
                "id": "p", "name": "p", "mode_id": "standard", "api_key": "k",
                "base_url": "https://example.com/v1", "model": "m",
                "model_profile": "campus_small",
            }],
            selected_plan_id="p",
        )
        result = apply_single_pass_completion(self.ctx, self.COMPLETION, self.RESUME)
        self.assertEqual(result["status"], "success")
        pool = load_state(self.ctx.paths["state"])["schedule_pool"]
        entry = next(item for item in pool if item["date"] == "2026-09-14")
        self.assertEqual(len(entry["area_assignments"]["教室"]), 3)


class MultiAgentPolicyTests(unittest.TestCase):
    """multi_agent's "repair" IS raising: the caller catches the ValueError and
    re-runs through ``fallback_fill_schedule``. So the executor must use
    ``delegates_to_code``, not ``is_fatal``."""

    def _barrier2(self, dates, must_run_ids=None):
        template = {day: {"教室": 1} for day in dates}
        return {
            "dates": list(dates),
            "template": template,
            "final_pool": [1001],
            "area_names": ["教室"],
            "must_run_ids": list(must_run_ids or []),
            "supported_rules": [],
            "warnings": [],
            "unsupported_rules": [],
            # Keys build_next_run_note and the ledger settlement read.
            "priority_pool": [1001],
            "pointer_pool": [1001],
            "pointer_after": 0,
            "consumed_credit_ids": [],
        }

    def _schedule(self, dates):
        return [{"date": day, "area_ids": {"教室": [1001]}, "note": ""} for day in dates]

    def test_repair_still_refuses_a_short_window_so_the_fallback_runs(self):
        from multi_agent.validators import validate_final_schedule

        barrier2 = self._barrier2(["2026-09-13", "2026-09-14"])
        with self.assertRaises(ValueError):
            validate_final_schedule(
                self._schedule(["2026-09-13"]), barrier2, vp.ValidationPolicy("repair")
            )

    def test_relaxed_commits_a_short_window_with_a_warning(self):
        from multi_agent.validators import validate_final_schedule

        barrier2 = self._barrier2(["2026-09-13", "2026-09-14"])
        warnings = validate_final_schedule(
            self._schedule(["2026-09-13"]), barrier2, vp.ValidationPolicy("relaxed")
        )
        self.assertTrue(any("宽松模式未修改" in w for w in warnings))

    def test_strict_refuses_a_short_window(self):
        from multi_agent.validators import validate_final_schedule

        barrier2 = self._barrier2(["2026-09-13", "2026-09-14"])
        with self.assertRaises(ValueError):
            validate_final_schedule(
                self._schedule(["2026-09-13"]), barrier2, vp.ValidationPolicy("strict")
            )

    def test_relaxed_reports_a_missing_must_run_instead_of_refusing(self):
        from multi_agent.validators import validate_final_schedule

        barrier2 = self._barrier2(["2026-09-13"], must_run_ids=[1002])
        warnings = validate_final_schedule(
            self._schedule(["2026-09-13"]), barrier2, vp.ValidationPolicy("relaxed")
        )
        self.assertTrue(any("must_run" in w for w in warnings))

    def test_repair_still_refuses_a_missing_must_run(self):
        from multi_agent.validators import validate_final_schedule

        barrier2 = self._barrier2(["2026-09-13"], must_run_ids=[1002])
        with self.assertRaises(ValueError):
            validate_final_schedule(
                self._schedule(["2026-09-13"]), barrier2, vp.ValidationPolicy("repair")
            )

    def test_structural_problems_raise_in_every_mode(self):
        # Ambiguity, not discretion: no mode may wave a duplicate-in-area or an
        # out-of-pool ID through.
        from multi_agent.validators import validate_final_schedule

        for mode in vp.VALIDATION_MODES:
            barrier2 = self._barrier2(["2026-09-13"])
            schedule = [{"date": "2026-09-13", "area_ids": {"教室": [1001, 1001]}, "note": ""}]
            with self.assertRaises(ValueError, msg=mode):
                validate_final_schedule(schedule, barrier2, vp.ValidationPolicy(mode))

    def test_settlement_appends_policy_warnings_to_agent_summary(self):
        # runtime.collect_result_warnings reads agent_summary.warnings, which is
        # barrier2["warnings"] - so the deviation has to land there or the user
        # never sees it.
        from multi_agent.settlement import finalize_multi_agent_run

        class _Snapshot:
            trace_id = "t"
            start_date = date(2026, 9, 13)
            config = {"validation_mode": "relaxed"}
            id_to_name = {1001: "张三"}
            all_ids = [1001]
            id_to_active = {1001: 1}

        barrier2 = self._barrier2(["2026-09-13", "2026-09-14"])
        barrier1 = {"new_debt_ids": [], "new_credit_ids": []}
        with tempfile.TemporaryDirectory() as tmp:
            ctx = Context(data_dir=Path(tmp))
            state_ops.save_roster_entries(ctx, [{"id": 1001, "name": "张三", "active": True}])
            settlement = finalize_multi_agent_run(
                ctx, _Snapshot(), barrier1, barrier2, self._schedule(["2026-09-13"])
            )
        self.assertTrue(settlement["policy_warnings"])
        self.assertTrue(barrier2["warnings"])


if __name__ == "__main__":
    unittest.main()
