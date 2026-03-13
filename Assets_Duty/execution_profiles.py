from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Dict, List, Tuple

SUPPORTED_MODEL_PROFILES = ("auto", "cloud", "campus_small", "edge", "custom")
SUPPORTED_ORCHESTRATION_MODES = ("auto", "single_pass", "multi_agent")


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


@dataclass(frozen=True)
class ExecutionProfile:
    model_profile: str
    orchestration_mode: str
    provider_hint: str
    multi_agent_ready: bool
    edge_ready: bool


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


def _resolve_model_profile(input_data: Dict[str, Any], config: Dict[str, Any]) -> str:
    raw = (
        input_data.get("model_profile")
        or config.get("model_profile")
        or "auto"
    )
    normalized = _normalize_text(raw, "auto")
    return MODEL_PROFILE_ALIASES.get(normalized, "custom")


def _resolve_orchestration_mode(input_data: Dict[str, Any], config: Dict[str, Any]) -> str:
    raw = (
        input_data.get("orchestration_mode")
        or config.get("orchestration_mode")
        or "auto"
    )
    normalized = _normalize_text(raw, "auto")
    return ORCHESTRATION_ALIASES.get(normalized, "auto")


def resolve_execution_profile(input_data: Dict[str, Any], config: Dict[str, Any]) -> ExecutionProfile:
    model_profile = _resolve_model_profile(input_data, config)
    orchestration_mode = _resolve_orchestration_mode(input_data, config)
    provider_hint = str(
        input_data.get("provider_hint")
        or config.get("provider_hint")
        or config.get("base_url")
        or ""
    ).strip()

    multi_agent_ready = model_profile in {"campus_small", "edge", "custom"} or orchestration_mode == "multi_agent"
    edge_ready = model_profile == "edge"

    return ExecutionProfile(
        model_profile=model_profile,
        orchestration_mode=orchestration_mode,
        provider_hint=provider_hint,
        multi_agent_ready=multi_agent_ready,
        edge_ready=edge_ready,
    )


def build_execution_plan(profile: ExecutionProfile) -> ExecutionPlan:
    tasks = (
        AgentTaskSpec("agent1_anchor", "stage0_anchor", "anchor"),
        AgentTaskSpec("agent2_accountant", "stage1_perception", "accounting"),
        AgentTaskSpec("agent3_rule", "stage1_perception", "rules"),
        AgentTaskSpec("agent4_priority", "stage2_prepare", "priority", llm_required=False),
        AgentTaskSpec("agent5_pointer", "stage2_prepare", "pointer", llm_required=False),
        AgentTaskSpec("agent6_assembly", "stage3_assemble", "assembly"),
    )

    notes: List[str] = []
    prompt_pack_strategy = "combined"

    if profile.multi_agent_ready:
        prompt_pack_strategy = "staged"
        notes.append("Logical task graph reserved for multi-agent execution.")

    if profile.orchestration_mode == "multi_agent":
        notes.append("Runtime remains on single-pass compatibility path for now.")

    runtime_mode = "single_pass_compat"

    return ExecutionPlan(
        profile=profile,
        runtime_mode=runtime_mode,
        prompt_pack_strategy=prompt_pack_strategy,
        tasks=tasks,
        notes=tuple(notes),
    )
