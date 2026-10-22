"""Validation policy: one check table, three modes, four actions.

The backend used to hard-code "what to do when the model misbehaves" inside
every executor. That was written for February-era models that regularly
produced structurally broken completions; current cloud models do not, and the
only population that still needs the safety net is sub-1B local models. The
result was that a capable model's perfectly good schedule got silently
rewritten (people added, people trimmed) with no way to opt out.

This module is the single source of truth for that decision. Every check
declares, per mode, one of four actions:

* ``reject``  — raise, commit nothing. The schedule never reaches the pool.
* ``warn``    — keep the model's output byte-for-byte, append a warning.
* ``repair``  — deterministic fix, and always emit a warning describing it.
* ``enforce`` — independent of the mode; protects data integrity rather than
  guarding against model mistakes, so no mode may switch it off.

Modes (product-facing names in the UI):

* ``relaxed`` — 宽松模式, the intended default. The model is authoritative for
  scheduling decisions (headcount, window coverage, area structure). Only a
  conflict with locally-known truth is fatal.
* ``strict`` — 严格模式. Full contract: area headcount, target dates and
  business rules must all hold or the commit is refused.
* ``repair`` — 实验性智能修复. Today's historical behaviour: deterministic
  fill/trim, every change surfaced as a warning.
* ``auto``   — resolves by ``model_profile``: constrained/local profiles get
  ``repair``, everything else gets ``relaxed``. Mirrors the existing
  "auto + campus_small promotes to multi-agent" precedent.

What is deliberately NOT a mode-dependent check:

* Protocol decoding (section structure, MM-DD validity, token syntax). A
  completion that cannot be decoded is not "a model style choice" — there is
  no schedule to validate.
* Date ordering / duplicate dates. Ambiguity, not style.
* Ledger idempotence, apply_mode history preservation, roster-generation
  rollback guard, atomic commit. These protect the ledger and the pool from
  double-charging and data loss; turning them off would corrupt state, not
  trust the model.
"""

from __future__ import annotations

from typing import Dict, Iterable, List, Optional

VALIDATION_MODES = ("relaxed", "strict", "repair")

# The product default. The mechanism first landed with the historical behaviour
# ("repair") so the refactor was behaviour-neutral; this is the flip to the
# intended default - a capable model's schedule is no longer silently rewritten,
# and deviations are reported instead. `auto` keeps sub-1B/edge profiles on
# "repair" so the population that still needs the net keeps it.
DEFAULT_VALIDATION_MODE = "relaxed"

ACTION_REJECT = "reject"
ACTION_WARN = "warn"
ACTION_REPAIR = "repair"
ACTION_ENFORCE = "enforce"

VALID_ACTIONS = (ACTION_REJECT, ACTION_WARN, ACTION_REPAIR, ACTION_ENFORCE)

# Human/config-facing spellings that must round-trip. The UI shows the Chinese
# labels; config.json stores the stable English slugs.
MODE_ALIASES: Dict[str, str] = {
    "relaxed": "relaxed",
    "宽松": "relaxed",
    "宽松模式": "relaxed",
    "lenient": "relaxed",
    "loose": "relaxed",
    "strict": "strict",
    "严格": "strict",
    "严格模式": "strict",
    "repair": "repair",
    "修复": "repair",
    "智能修复": "repair",
    "实验性智能修复": "repair",
    "smart_repair": "repair",
    "experimental": "repair",
    "auto": "auto",
    "自动": "auto",
}

# Profiles whose models are small/constrained enough that an unreviewed
# completion is likely to be structurally weak. `auto` resolves to `repair`
# for these and `relaxed` for everything else. `edge` is included on purpose:
# the cost of wrongly repairing a capable model is a warning the user can
# read, while the cost of wrongly trusting a weak model is a bad schedule
# committed to the pool.
CONSTRAINED_MODEL_PROFILES = frozenset({"campus_small", "edge"})


def _row(*, relaxed: str, strict: str, repair: str) -> Dict[str, str]:
    return {"relaxed": relaxed, "strict": strict, "repair": repair}


# Checks whose disposition depends on the mode. Keys are stable identifiers —
# executors must reference them by name, never by re-deriving the behaviour.
CHECK_ACTIONS: Dict[str, Dict[str, str]] = {
    # --- conflicts with locally-known truth: fatal in every mode -----------
    # Not "model style": the roster and the leave register are authoritative
    # local facts, and scheduling a graduated or absent person is a factual
    # error no amount of LLM freedom makes acceptable.
    "unknown_person": _row(relaxed=ACTION_REJECT, strict=ACTION_REJECT, repair=ACTION_REJECT),
    "inactive_person": _row(relaxed=ACTION_REJECT, strict=ACTION_REJECT, repair=ACTION_REPAIR),
    "absent_same_day": _row(relaxed=ACTION_REJECT, strict=ACTION_REJECT, repair=ACTION_REPAIR),
    # Ambiguous assignment: the same ID twice in one area has no honest reading.
    "duplicate_in_area": _row(relaxed=ACTION_REJECT, strict=ACTION_REJECT, repair=ACTION_REPAIR),
    # --- the model's own discretion ---------------------------------------
    "headcount_short": _row(relaxed=ACTION_WARN, strict=ACTION_REJECT, repair=ACTION_REPAIR),
    "headcount_excess": _row(relaxed=ACTION_WARN, strict=ACTION_REJECT, repair=ACTION_REPAIR),
    # Window checks: whether a *fix* exists depends on the executor.
    # multi_agent can hand a short window to its deterministic fallback (which
    # emits every template date), so `repair` there means "delegate to the code
    # layer". single_pass has no window-fill capability, so the same `repair`
    # degrades to a warning there - the check is identical, the available fix is
    # not, and both executors document which one they are.
    "window_gap": _row(relaxed=ACTION_WARN, strict=ACTION_REJECT, repair=ACTION_REPAIR),
    "out_of_window": _row(relaxed=ACTION_WARN, strict=ACTION_REJECT, repair=ACTION_WARN),
    "phantom_area": _row(relaxed=ACTION_WARN, strict=ACTION_REJECT, repair=ACTION_WARN),
    "must_run_missing": _row(relaxed=ACTION_WARN, strict=ACTION_REJECT, repair=ACTION_REPAIR),
    "template_mismatch": _row(relaxed=ACTION_WARN, strict=ACTION_REJECT, repair=ACTION_WARN),
    # Cross-area same-day reuse is ALLOWED by product rule in every mode
    # (see the audit's design constraint); it is listed only so a future
    # policy change has an obvious place to land, not because it is checked.
    "cross_area_reuse": _row(relaxed=ACTION_WARN, strict=ACTION_WARN, repair=ACTION_WARN),
}

# Fatal regardless of mode: nothing to commit, or the completion is ambiguous.
ALWAYS_REJECT = (
    "empty_pool",
    "unparseable",
    "duplicate_date",
    "unordered_dates",
    "capacity_short",
    "roster_missing",
)

# Independent of mode: these guard the ledger and the pool, not the model.
ALWAYS_ENFORCE = (
    "ledger_idempotence",
    "apply_mode_history",
    "roster_generation",
    "atomic_commit",
    "finalize_gate",
)

ALL_CHECKS = tuple(CHECK_ACTIONS) + ALWAYS_REJECT + ALWAYS_ENFORCE


def normalize_validation_mode(raw_value) -> str:
    """Normalize a caller-supplied mode.

    Unknown/empty values fall back to ``DEFAULT_VALIDATION_MODE`` — never to a
    silent no-op, because "unset" must still mean *some* explicit policy.
    """
    key = str(raw_value or "").strip().lower()
    if not key:
        return DEFAULT_VALIDATION_MODE
    return MODE_ALIASES.get(key, DEFAULT_VALIDATION_MODE)


def _profile_of(config: dict) -> str:
    """The effective ``model_profile`` for a config in either shape.

    The runtime projection (``load_config``) flattens ``model_profile`` to the
    top level; the persisted ``config.json`` keeps it inside the selected plan
    preset. Both shapes must resolve identically — reading only the flattened
    key silently resolves a raw persisted config to the wrong mode.
    """
    direct = str(config.get("model_profile") or "").strip().lower()
    if direct:
        return direct
    presets = config.get("plan_presets")
    selected_id = str(config.get("selected_plan_id") or "").strip()
    if isinstance(presets, list):
        for preset in presets:
            if not isinstance(preset, dict):
                continue
            if selected_id and str(preset.get("id") or "").strip() != selected_id:
                continue
            nested = str(preset.get("model_profile") or "").strip().lower()
            if nested:
                return nested
    return ""


def resolve_validation_mode(config: Optional[dict]) -> str:
    """Resolve the effective mode for a run, expanding ``auto``.

    ``auto`` is the only value that inspects the rest of the config; every
    other value is returned verbatim (already normalized).
    """
    config = config if isinstance(config, dict) else {}
    raw = normalize_validation_mode(config.get("validation_mode"))
    if raw != "auto":
        return raw
    return ACTION_REPAIR if _profile_of(config) in CONSTRAINED_MODEL_PROFILES else "relaxed"


class ValidationPolicy:
    """Mode-bound view over :data:`CHECK_ACTIONS`.

    Executors ask ``policy.action("headcount_short")`` instead of branching on
    the mode themselves, so adding a mode or re-classifying a check is a
    one-line table edit.
    """

    __slots__ = ("mode", "_configured_mode")

    def __init__(self, mode: str, configured_mode: Optional[str] = None):
        self.mode = mode if mode in VALIDATION_MODES else DEFAULT_VALIDATION_MODE
        self._configured_mode = normalize_validation_mode(
            configured_mode if configured_mode is not None else mode
        )

    @classmethod
    def from_config(cls, config: Optional[dict]) -> "ValidationPolicy":
        config = config if isinstance(config, dict) else {}
        configured = normalize_validation_mode(config.get("validation_mode"))
        return cls(resolve_validation_mode(config), configured_mode=configured)

    @property
    def configured_mode(self) -> str:
        """The mode as written in config (``auto`` stays ``auto``)."""
        return self._configured_mode

    def action(self, check: str) -> str:
        name = str(check or "").strip()
        if name in ALWAYS_ENFORCE:
            return ACTION_ENFORCE
        if name in ALWAYS_REJECT:
            return ACTION_REJECT
        row = CHECK_ACTIONS.get(name)
        if row is None:
            # An unknown check must fail loudly rather than silently pass:
            # a typo here would quietly disable a guard.
            raise KeyError(f"unknown validation check: {name!r}")
        return row[self.mode]

    def is_fatal(self, check: str) -> bool:
        return self.action(check) == ACTION_REJECT

    def repairs(self, check: str) -> bool:
        return self.action(check) == ACTION_REPAIR

    def warns(self, check: str) -> bool:
        return self.action(check) == ACTION_WARN

    def delegates_to_code(self, check: str) -> bool:
        """True when the model's output is not acceptable as-is and the
        deterministic layer must take over.

        Covers both ``reject`` (refuse the commit) and ``repair`` (the code
        fixes it). Executors whose "repair" is *routing to a fallback* — e.g.
        multi_agent handing a short window to ``fallback_fill_schedule`` — use
        this instead of :meth:`is_fatal`, which would let a repairable deviation
        through unreviewed.
        """
        return self.action(check) in (ACTION_REJECT, ACTION_REPAIR)

    def as_dict(self) -> dict:
        return {
            "validation_mode": self.mode,
            "configured_mode": self._configured_mode,
            "actions": {name: self.action(name) for name in ALL_CHECKS},
        }


class ValidationReport:
    """Accumulates what a run's validation pass decided, for the result payload.

    ``warnings`` is what the user sees ("自动修正: ..." / "未覆盖日期 ...");
    ``repairs`` is the subset that actually changed the schedule. Keeping them
    separate lets the UI say "we changed your schedule" without making the
    user diff the pool by hand.
    """

    __slots__ = ("warnings", "repairs", "rejected")

    def __init__(self) -> None:
        self.warnings: List[str] = []
        self.repairs: List[str] = []
        self.rejected: List[str] = []

    def warn(self, check: str, message: str) -> None:
        self.warnings.append(str(message))

    def repair(self, check: str, message: str) -> None:
        text = str(message)
        self.repairs.append(text)
        self.warnings.append(text)

    def reject(self, check: str, message: str) -> None:
        self.rejected.append(str(message))

    @property
    def changed_schedule(self) -> bool:
        return bool(self.repairs)

    def merge(self, other: "ValidationReport") -> None:
        self.warnings.extend(other.warnings)
        self.repairs.extend(other.repairs)
        self.rejected.extend(other.rejected)

    def as_dict(self) -> dict:
        return {
            "warnings": list(self.warnings),
            "repairs": list(self.repairs),
            "rejected": list(self.rejected),
        }

    def __bool__(self) -> bool:  # pragma: no cover - convenience only
        return bool(self.warnings or self.repairs or self.rejected)


def describe_modes() -> List[dict]:
    """Product copy for the settings UI, so the frontend never re-derives it."""
    return [
        {
            "value": "relaxed",
            "label": "宽松模式",
            "recommended": True,
            "summary": "尊重模型输出，非致命问题只警告、不自动修改排班",
            "detail": "模型对排班决策（人数、日期覆盖、区域结构）拥有最终决定权。"
                      "仅当与本地已知事实冲突（人员不在花名册、已停用、当天请假）"
                      "或同一区域重复安排时拒绝提交。",
        },
        {
            "value": "strict",
            "label": "严格模式",
            "recommended": False,
            "summary": "完整检查区域人数、目标日期与业务规则，不满足即拒绝提交",
            "detail": "区域人数必须等于配置、必须覆盖全部目标日期、不得越界、"
                      "必须满足必排人员。任一不满足则拒绝写入，并列出具体违例。",
        },
        {
            "value": "repair",
            "label": "实验性智能修复",
            "recommended": False,
            "summary": "尝试自动补齐、裁剪和调整，但必须展示具体修改",
            "detail": "由确定性代码修正人数缺口、剔除缺席/停用人员、裁剪超员，"
                      "每一次修改都会以警告形式列出。适合 0.8B 级本地小模型。",
        },
        {
            "value": "auto",
            "label": "自动（按模型选择）",
            "recommended": False,
            "summary": "本地/受限模型用智能修复，其余用宽松模式",
            "detail": "campus_small 与 edge 档位解析为智能修复，"
                      "其它档位解析为宽松模式。",
        },
    ]
