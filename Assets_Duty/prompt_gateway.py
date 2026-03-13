from __future__ import annotations

from typing import Any, Dict, List, Tuple

from build_prompt import build_prompt_messages
from execution_profiles import ExecutionPlan
from multi_agent.prompts import build_agent_prompt_messages


def build_single_pass_prompt_messages(plan: ExecutionPlan, **prompt_kwargs: Any) -> Tuple[List[Dict[str, str]], Dict[str, Any]]:
    messages = build_prompt_messages(
        **prompt_kwargs,
        model_profile=plan.profile.model_profile,
        orchestration_mode=plan.profile.orchestration_mode,
        single_pass_strategy=plan.profile.single_pass_strategy,
    )
    metadata = {
        "runtime_mode": plan.runtime_mode,
        "prompt_pack_strategy": plan.prompt_pack_strategy,
        "logical_task_count": len(plan.tasks),
        "logical_task_ids": [task.agent_id for task in plan.tasks],
        "model_profile": plan.profile.model_profile,
        "orchestration_mode": plan.profile.orchestration_mode,
        "single_pass_strategy": plan.profile.single_pass_strategy,
    }
    return messages, metadata


def build_schedule_prompt_messages(plan: ExecutionPlan, **prompt_kwargs: Any) -> Tuple[List[Dict[str, str]], Dict[str, Any]]:
    return build_single_pass_prompt_messages(plan, **prompt_kwargs)


def build_agent_prompt(agent_id: str, agent_context: Dict[str, Any], plan: ExecutionPlan) -> Tuple[List[Dict[str, str]], Dict[str, Any]]:
    messages = build_agent_prompt_messages(agent_id, agent_context)
    metadata = {
        "agent_id": agent_id,
        "runtime_mode": plan.runtime_mode,
        "prompt_pack_strategy": plan.prompt_pack_strategy,
        "model_profile": plan.profile.model_profile,
        "orchestration_mode": plan.profile.orchestration_mode,
        "single_pass_strategy": plan.profile.single_pass_strategy,
    }
    return messages, metadata
