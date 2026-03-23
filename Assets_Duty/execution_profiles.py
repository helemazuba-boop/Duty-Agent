from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Dict, List, Tuple

SUPPORTED_MODEL_PROFILES = ("auto", "cloud", "campus_small", "edge", "custom")
SUPPORTED_ORCHESTRATION_MODES = ("auto", "single_pass", "multi_agent")
SUPPORTED_MULTI_AGENT_EXECUTION_MODES = ("auto", "parallel", "serial")
SUPPORTED_SINGLE_PASS_STRATEGIES = ("auto", "cloud_standard", "edge_tuned", "edge_generic", "incremental_thinking")

MODEL_PROFILE_ALIASES = {
    "auto": "auto",
    "cloud": "cloud",
    "cloud_general": "cloud",
    "campus": "campus_small",
    "campus_small": "campus_small",
    "school_small": "campus_small",
    "edge": "edge",
    "edge_tuned": "edge",
    "edge_finetuned": "edge",
    "custom": "custom",
}

ORCHESTRATION_ALIASES = {
    "auto": "auto",
    "single": "single_pass",
    "single_pass": "single_pass",
    "unified": "single_pass",
    "multi_agent": "multi_agent",
    "multi-agent": "multi_agent",
    "staged": "multi_agent",
}

MULTI_AGENT_EXECUTION_ALIASES = {
    "auto": "auto",
    "parallel": "parallel",
    "concurrent": "parallel",
    "serial": "serial",
    "sequential": "serial",
}


@dataclass(frozen=True)
class ExecutionProfile:
    model_profile: str
    orchestration_mode: str
    provider_hint: str
    multi_agent_ready: bool
    edge_ready: bool
    multi_agent_execution_mode: str
    single_pass_strategy: str


@dataclass(frozen=True)
class AgentTaskSpec:
    agent_id: str
    logical_stage: str
    prompt_template: str
    llm_required: bool = True


@dataclass(frozen=True)
class ExecutionPlan:
    profile: ExecutionProfile
    runtime_mode: str
    prompt_pack_strategy: str
    tasks: Tuple[AgentTaskSpec, ...]
    notes: Tuple[str, ...]

    def to_metadata(self) -> Dict[str, Any]:
        return {
            "model_profile": self.profile.model_profile,
            "orchestration_mode": self.profile.orchestration_mode,
            "provider_hint": self.profile.provider_hint,
            "multi_agent_ready": self.profile.multi_agent_ready,
            "edge_ready": self.profile.edge_ready,
            "multi_agent_execution_mode": self.profile.multi_agent_execution_mode,
            "single_pass_strategy": self.profile.single_pass_strategy,
            "runtime_mode": self.runtime_mode,
            "prompt_pack_strategy": self.prompt_pack_strategy,
            "tasks": [
                {
                    "agent_id": task.agent_id,
                    "logical_stage": task.logical_stage,
                    "prompt_template": task.prompt_template,
                    "llm_required": task.llm_required,
                }
                for task in self.tasks
            ],
            "notes": list(self.notes),
        }


def _normalize_text(value: Any, fallback: str) -> str:
    text = str(value or "").strip().lower()
    return text or fallback


def _resolve_model_profile(_input_data: Dict[str, Any], config: Dict[str, Any]) -> str:
    raw = config.get("model_profile") or "auto"
    normalized = _normalize_text(raw, "auto")
    return MODEL_PROFILE_ALIASES.get(normalized, "custom")


def _resolve_orchestration_mode(_input_data: Dict[str, Any], config: Dict[str, Any]) -> str:
    raw = config.get("orchestration_mode") or "auto"
    normalized = _normalize_text(raw, "auto")
    return ORCHESTRATION_ALIASES.get(normalized, "auto")


def _resolve_multi_agent_execution_mode(config: Dict[str, Any]) -> str:
    raw = config.get("multi_agent_execution_mode") or "auto"
    normalized = _normalize_text(raw, "auto")
    return MULTI_AGENT_EXECUTION_ALIASES.get(normalized, "auto")


def _resolve_single_pass_strategy(model_profile: str, provider_hint: str, config: Dict[str, Any]) -> str:
    explicit = _normalize_text(config.get("single_pass_strategy"), "auto")
    if explicit in SUPPORTED_SINGLE_PASS_STRATEGIES and explicit != "auto":
        return explicit

    if model_profile != "edge":
        return "cloud_standard"

    provider_text = provider_hint.lower()
    model_text = str(config.get("model", "") or "").strip().lower()
    tuned_markers = (
        "edge_tuned",
        "edge-tuned",
        "edge tuned",
        "finetune",
        "fine-tune",
        "fine_tune",
        "0.8b",
        "duty-agent",
        "duty_agent",
        "classisland",
    )
    if any(marker in provider_text or marker in model_text for marker in tuned_markers):
        return "edge_tuned"
    return "edge_generic"


def resolve_execution_profile(input_data: Dict[str, Any], config: Dict[str, Any]) -> ExecutionProfile:
    model_profile = _resolve_model_profile(input_data, config)
    orchestration_mode = _resolve_orchestration_mode(input_data, config)
    provider_hint = str(
        config.get("provider_hint")
        or config.get("base_url")
        or ""
    ).strip()
    multi_agent_execution_mode = _resolve_multi_agent_execution_mode(config)
    single_pass_strategy = _resolve_single_pass_strategy(model_profile, provider_hint, config)

    multi_agent_ready = model_profile in {"campus_small", "edge", "custom"} or orchestration_mode == "multi_agent"
    edge_ready = model_profile == "edge"

    return ExecutionProfile(
        model_profile=model_profile,
        orchestration_mode=orchestration_mode,
        provider_hint=provider_hint,
        multi_agent_ready=multi_agent_ready,
        edge_ready=edge_ready,
        multi_agent_execution_mode=multi_agent_execution_mode,
        single_pass_strategy=single_pass_strategy,
    )


def build_execution_plan(profile: ExecutionProfile) -> ExecutionPlan:
    tasks = (
        AgentTaskSpec("agent1_anchor", "stage0_anchor", "anchor"),
        AgentTaskSpec("agent2_accountant", "stage1_perception", "accounting"),
        AgentTaskSpec("agent3_rule", "stage1_perception", "rules"),
        AgentTaskSpec("agent4_priority", "stage2_prepare", "priority"),
        AgentTaskSpec("agent5_pointer", "stage2_prepare", "pointer"),
        AgentTaskSpec("agent6_assembly", "stage3_assemble", "assembly"),
    )

    notes: List[str] = []
    runtime_mode = "single_pass"
    prompt_pack_strategy = "combined"

    if profile.orchestration_mode == "single_pass":
        notes.append("Single-pass executor selected explicitly.")
    elif profile.orchestration_mode == "multi_agent":
        runtime_mode = "multi_agent_serial" if profile.multi_agent_execution_mode == "serial" else "multi_agent_parallel"
        prompt_pack_strategy = "staged_serial" if runtime_mode == "multi_agent_serial" else "staged_parallel"
        notes.append("Multi-agent executor selected explicitly.")
    else:
        if profile.model_profile == "campus_small":
            runtime_mode = "multi_agent_serial" if profile.multi_agent_execution_mode == "serial" else "multi_agent_parallel"
            prompt_pack_strategy = "staged_serial" if runtime_mode == "multi_agent_serial" else "staged_parallel"
            notes.append("Auto orchestration promoted campus_small to multi-agent executor.")
        else:
            notes.append("Auto orchestration kept single-pass executor.")

    if runtime_mode.startswith("multi_agent"):
        notes.append("Agents DAG execution enabled.")
    else:
        notes.append(f"Single-pass strategy resolved to {profile.single_pass_strategy}.")

    return ExecutionPlan(
        profile=profile,
        runtime_mode=runtime_mode,
        prompt_pack_strategy=prompt_pack_strategy,
        tasks=tasks,
        notes=tuple(notes),
    )
