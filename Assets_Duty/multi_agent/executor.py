from __future__ import annotations

import json
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timedelta
from typing import Any, Callable, Dict, List, Tuple

from execution_profiles import ExecutionPlan
from llm_transport import call_llm_json
from prompt_gateway import build_agent_prompt
from state_ops import (
    Context,
    anonymize_instruction,
    count_map_to_id_list,
    get_pool_entries_with_date,
    load_api_key_from_env,
    load_config,
    load_roster,
    load_state,
)

from .contracts import AgentTrace, FrozenSnapshot
from .settlement import finalize_multi_agent_run
from .validators import (
    fallback_fill_schedule,
    merge_barrier1,
    merge_barrier2,
    validate_agent1_output,
    validate_agent2_output,
    validate_agent3_output,
    validate_agent4_output,
    validate_agent5_output,
    validate_agent6_output,
)

AGENT_JSON_RETRIES = 2


def _emit_progress(emit_progress_fn, phase: str, message: str, payload: Dict[str, Any] | None = None) -> None:
    if not emit_progress_fn:
        return
    emit_progress_fn(phase, message, json.dumps(payload or {}, ensure_ascii=False))


def _agent_transport_overrides(plan: ExecutionPlan) -> Dict[str, Any] | None:
    if plan.profile.single_pass_strategy in {"edge_tuned", "edge_generic"}:
        return {"temperature": 0.0}
    return None


def _freeze_snapshot(ctx: Context, input_data: dict) -> FrozenSnapshot:
    run_now = datetime.now()
    config = load_config(ctx)
    name_to_id, id_to_name, all_ids, id_to_active = load_roster(ctx.paths["roster"])
    state_data = load_state(ctx.paths["state"])

    api_key = (
        str(config.get("api_key", "")).strip()
        or load_api_key_from_env()
    )
    config = dict(config)
    config["api_key"] = api_key
    config["llm_stream"] = False

    apply_mode = str(input_data.get("apply_mode", "append")).lower()
    entries = get_pool_entries_with_date(state_data)
    start_date = (entries[-1][1] + timedelta(days=1)) if apply_mode == "append" and entries else run_now.date()

    active_ids = [person_id for person_id in all_ids if id_to_active.get(person_id, 1) != 0]
    inactive_ids = [person_id for person_id in all_ids if id_to_active.get(person_id, 1) == 0]
    instruction = str(input_data.get("instruction", "Generate duty schedule")).strip()
    request_source = str(input_data.get("request_source", "api")).strip() or "api"
    trace_id = str(input_data.get("trace_id", "")).strip()

    return FrozenSnapshot(
        trace_id=trace_id,
        request_source=request_source,
        instruction=anonymize_instruction(instruction, name_to_id),
        apply_mode=apply_mode,
        request_time=run_now,
        start_date=start_date,
        config=config,
        state=state_data,
        name_to_id=name_to_id,
        id_to_name=id_to_name,
        all_ids=all_ids,
        active_ids=active_ids,
        inactive_ids=inactive_ids,
        id_to_active=id_to_active,
        debt_list=count_map_to_id_list(state_data.get("debt_counts", {}), set(all_ids)),
        credit_list=count_map_to_id_list(state_data.get("credit_counts", {}), set(all_ids)),
        last_pointer=int(state_data.get("last_pointer", 0) or 0),
        previous_note=str(state_data.get("next_run_note", "") or "").strip(),
        duty_rule=anonymize_instruction(str(config.get("duty_rule", "") or ""), name_to_id),
    )


def _run_agent(
    agent_id: str,
    agent_context: Dict[str, Any],
    validator: Callable[[Dict[str, Any]], Dict[str, Any]],
    snapshot: FrozenSnapshot,
    plan: ExecutionPlan,
    stop_event=None,
) -> Tuple[Dict[str, Any], str, Dict[str, Any], AgentTrace]:
    if stop_event and stop_event.is_set():
        raise InterruptedError("Cancelled.")

    started_at = time.perf_counter()
    messages, prompt_metadata = build_agent_prompt(agent_id, agent_context, plan)
    result, raw_text = call_llm_json(
        messages,
        snapshot.config,
        validator=validator,
        stop_event=stop_event,
        transport_overrides=_agent_transport_overrides(plan),
        max_parse_retries=AGENT_JSON_RETRIES,
    )
    duration_ms = int((time.perf_counter() - started_at) * 1000)
    trace = AgentTrace(
        agent_id=agent_id,
        status="ok",
        attempts=AGENT_JSON_RETRIES + 1,
        duration_ms=duration_ms,
    )
    return result, raw_text, prompt_metadata, trace


def _run_batch(
    batch: List[Tuple[str, Dict[str, Any], Callable[[Dict[str, Any]], Dict[str, Any]]]],
    snapshot: FrozenSnapshot,
    plan: ExecutionPlan,
    parallel: bool,
    emit_progress_fn=None,
    stop_event=None,
) -> Tuple[Dict[str, Dict[str, Any]], Dict[str, str], Dict[str, Dict[str, Any]], List[Dict[str, Any]]]:
    results: Dict[str, Dict[str, Any]] = {}
    raw_texts: Dict[str, str] = {}
    prompt_metadata: Dict[str, Dict[str, Any]] = {}
    traces: List[Dict[str, Any]] = []

    def _execute(entry):
        agent_id, agent_context, validator = entry
        return agent_id, _run_agent(agent_id, agent_context, validator, snapshot, plan, stop_event)

    if parallel:
        with ThreadPoolExecutor(max_workers=len(batch)) as executor:
            future_map = {}
            for entry in batch:
                agent_id = entry[0]
                _emit_progress(emit_progress_fn, "agent_start", f"{agent_id} started.", {"agent_id": agent_id})
                future_map[executor.submit(_execute, entry)] = agent_id
            for future in as_completed(future_map):
                agent_id = future_map[future]
                try:
                    _, payload = future.result()
                except Exception as ex:
                    _emit_progress(
                        emit_progress_fn,
                        "agent_fail",
                        f"{agent_id} failed: {ex}",
                        {"agent_id": agent_id, "error": str(ex)},
                    )
                    raise RuntimeError(f"{agent_id} failed: {ex}") from ex
                result, raw_text, metadata, trace = payload
                results[agent_id] = result
                raw_texts[agent_id] = raw_text
                prompt_metadata[agent_id] = metadata
                traces.append(trace.__dict__)
                _emit_progress(emit_progress_fn, "agent_done", f"{agent_id} completed.", {"agent_id": agent_id, "duration_ms": trace.duration_ms})
    else:
        for entry in batch:
            agent_id = entry[0]
            _emit_progress(emit_progress_fn, "agent_start", f"{agent_id} started.", {"agent_id": agent_id})
            try:
                _, payload = _execute(entry)
            except Exception as ex:
                _emit_progress(
                    emit_progress_fn,
                    "agent_fail",
                    f"{agent_id} failed: {ex}",
                    {"agent_id": agent_id, "error": str(ex)},
                )
                raise RuntimeError(f"{agent_id} failed: {ex}") from ex
            result, raw_text, metadata, trace = payload
            results[agent_id] = result
            raw_texts[agent_id] = raw_text
            prompt_metadata[agent_id] = metadata
            traces.append(trace.__dict__)
            _emit_progress(emit_progress_fn, "agent_done", f"{agent_id} completed.", {"agent_id": agent_id, "duration_ms": trace.duration_ms})

    return results, raw_texts, prompt_metadata, traces


def run_multi_agent_schedule(
    ctx: Context,
    input_data: dict,
    execution_plan: ExecutionPlan,
    emit_progress_fn=None,
    stop_event=None,
) -> dict:
    snapshot = _freeze_snapshot(ctx, input_data)
    parallel = execution_plan.runtime_mode == "multi_agent_parallel"
    execution_trace: List[Dict[str, Any]] = []
    prompt_trace: Dict[str, Dict[str, Any]] = {}
    final_ai_response = ""

    _emit_progress(
        emit_progress_fn,
        "snapshot_frozen",
        "Frozen snapshot created for multi-agent execution.",
        {
            "trace_id": snapshot.trace_id,
            "runtime_mode": execution_plan.runtime_mode,
            "request_source": snapshot.request_source,
            "start_date": snapshot.start_date.isoformat(),
            "last_pointer": snapshot.last_pointer,
        },
    )

    stage1_batch = [
        (
            "agent1_anchor",
            {
                "request_time": snapshot.request_time.strftime("%Y-%m-%d %H:%M"),
                "instruction": snapshot.instruction,
                "previous_note": snapshot.previous_note,
                "duty_rule": snapshot.duty_rule,
            },
            lambda raw: validate_agent1_output(raw, snapshot),
        ),
        (
            "agent2_accountant",
            {
                "instruction": snapshot.instruction,
                "request_time": snapshot.request_time.strftime("%Y-%m-%d %H:%M"),
                "all_ids": snapshot.all_ids,
                "debt_list": snapshot.debt_list,
                "credit_list": snapshot.credit_list,
            },
            lambda raw: validate_agent2_output(raw, snapshot),
        ),
        (
            "agent3_rule",
            {
                "instruction": snapshot.instruction,
                "request_time": snapshot.request_time.strftime("%Y-%m-%d %H:%M"),
                "all_ids": snapshot.all_ids,
                "duty_rule": snapshot.duty_rule,
            },
            lambda raw: validate_agent3_output(raw, snapshot),
        ),
    ]
    stage1_results, _, stage1_meta, stage1_trace = _run_batch(
        stage1_batch,
        snapshot,
        execution_plan,
        parallel=parallel,
        emit_progress_fn=emit_progress_fn,
        stop_event=stop_event,
    )
    execution_trace.extend(stage1_trace)
    prompt_trace.update(stage1_meta)
    anchor = stage1_results["agent1_anchor"]

    barrier1 = merge_barrier1(
        snapshot,
        anchor,
        stage1_results["agent2_accountant"],
        stage1_results["agent3_rule"],
    )
    _emit_progress(
        emit_progress_fn,
        "barrier_pass",
        "Barrier1 merged stage1 outputs.",
        {
            "stage": "barrier1",
            "total_slots": barrier1["total_slots"],
            "dates": barrier1["dates"],
            "warnings": barrier1["warnings"],
        },
    )

    stage2_batch = [
        (
            "agent4_priority",
            {
                "dates": barrier1["dates"],
                "total_slots": barrier1["total_slots"],
                "active_ids": snapshot.active_ids,
                "absent_ids": barrier1["absent_ids"],
                "debt_list": snapshot.debt_list,
                "new_debt_ids": barrier1["new_debt_ids"],
                "must_run_ids": barrier1["must_run_ids"],
                "volunteer_ids": barrier1["volunteer_ids"],
            },
            lambda raw: validate_agent4_output(raw, snapshot, barrier1),
        ),
        (
            "agent5_pointer",
            {
                "dates": barrier1["dates"],
                "total_slots": barrier1["total_slots"],
                "all_ids": snapshot.all_ids,
                "active_ids": snapshot.active_ids,
                "inactive_ids": snapshot.inactive_ids,
                "absent_ids": barrier1["absent_ids"],
                "credit_list": snapshot.credit_list + barrier1["new_credit_ids"],
                "last_pointer": snapshot.last_pointer,
            },
            lambda raw: validate_agent5_output(raw, snapshot, barrier1),
        ),
    ]
    stage2_results, _, stage2_meta, stage2_trace = _run_batch(
        stage2_batch,
        snapshot,
        execution_plan,
        parallel=parallel,
        emit_progress_fn=emit_progress_fn,
        stop_event=stop_event,
    )
    execution_trace.extend(stage2_trace)
    prompt_trace.update(stage2_meta)
    barrier2 = merge_barrier2(
        snapshot,
        barrier1,
        stage2_results["agent4_priority"],
        stage2_results["agent5_pointer"],
    )
    _emit_progress(
        emit_progress_fn,
        "barrier_pass",
        "Barrier2 produced the final candidate pool.",
        {
            "stage": "barrier2",
            "final_pool_size": len(barrier2["final_pool"]),
            "pointer_after": barrier2["pointer_after"],
        },
    )

    try:
        _emit_progress(emit_progress_fn, "agent_start", "agent6_assembly started.", {"agent_id": "agent6_assembly"})
        agent6_result, agent6_raw, agent6_meta, agent6_trace = _run_agent(
            "agent6_assembly",
            {
                "dates": barrier2["dates"],
                "template": barrier2["template"],
                "final_pool": barrier2["final_pool"],
                "supported_rules": barrier2["supported_rules"],
            },
            lambda raw: validate_agent6_output(raw, barrier2),
            snapshot,
            execution_plan,
            stop_event,
        )
        final_schedule = agent6_result["schedule"]
        final_ai_response = agent6_raw
        execution_trace.append(agent6_trace.__dict__)
        prompt_trace["agent6_assembly"] = agent6_meta
        _emit_progress(emit_progress_fn, "agent_done", "agent6_assembly completed.", {"agent_id": "agent6_assembly", "duration_ms": agent6_trace.duration_ms})
    except Exception as ex:
        _emit_progress(
            emit_progress_fn,
            "agent_fallback",
            f"Agent6 failed, trying code fallback: {ex}",
            {"agent_id": "agent6_assembly"},
        )
        final_schedule = fallback_fill_schedule(barrier2)
        final_ai_response = "fallback_fill"

    _emit_progress(
        emit_progress_fn,
        "settlement",
        "Running settlement and atomic commit.",
        {"trace_id": snapshot.trace_id},
    )
    settlement = finalize_multi_agent_run(
        ctx,
        snapshot,
        barrier1,
        barrier2,
        final_schedule,
        stop_event=stop_event,
    )
    _emit_progress(
        emit_progress_fn,
        "commit",
        "Multi-agent schedule committed.",
        {
            "trace_id": snapshot.trace_id,
            "schedule_pool_size": len(settlement["state_data"].get("schedule_pool", [])),
        },
    )

    return {
        "status": "success",
        "ai_response": final_ai_response[:20000],
        "trace_id": snapshot.trace_id,
        "selected_executor": execution_plan.runtime_mode,
        "execution_plan": execution_plan.to_metadata(),
        "prompt_gateway": prompt_trace,
        "agent_summary": {
            "warnings": barrier2["warnings"],
            "unsupported_rules": barrier2["unsupported_rules"],
            "priority_pool_size": len(barrier2["priority_pool"]),
            "pointer_pool_size": len(barrier2["pointer_pool"]),
            "final_pool_size": len(barrier2["final_pool"]),
        },
        "execution_trace": execution_trace,
    }
