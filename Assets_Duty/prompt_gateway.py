from __future__ import annotations

from typing import Any, Dict, List, Tuple

from build_prompt import build_prompt_messages
from execution_profiles import ExecutionPlan


def build_schedule_prompt_messages(plan: ExecutionPlan, **prompt_kwargs: Any) -> Tuple[List[Dict[str, str]], Dict[str, Any]]:
    messages = build_prompt_messages(
        **prompt_kwargs,
        model_profile=plan.profile.model_profile,
        orchestration_mode=plan.profile.orchestration_mode,
    )
    metadata = {
        "runtime_mode": plan.runtime_mode,
        "prompt_pack_strategy": plan.prompt_pack_strategy,
        "logical_task_count": len(plan.tasks),
        "logical_task_ids": [task.agent_id for task in plan.tasks],
        "model_profile": plan.profile.model_profile,
        "orchestration_mode": plan.profile.orchestration_mode,
    }
    return messages, metadata
