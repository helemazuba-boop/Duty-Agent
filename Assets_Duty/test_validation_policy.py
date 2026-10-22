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
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import validation_policy as vp
from state_ops import Context, patch_config, save_json_atomic

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

    def test_repair_rewrites_model_discretion(self):
        for check in ("headcount_short", "headcount_excess", "window_gap", "must_run_missing"):
            self.assertEqual(vp.CHECK_ACTIONS[check]["repair"], vp.ACTION_REPAIR, check)

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


if __name__ == "__main__":
    unittest.main()
