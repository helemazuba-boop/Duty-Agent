from __future__ import annotations

from typing import Any, Dict, List

from postprocess import merge_schedule_pool, reconcile_credit_list, recover_missing_debts, restore_schedule
from state_ops import extract_ids_from_value, update_state

from .contracts import FrozenSnapshot
from .validators import validate_final_schedule


def build_next_run_note(snapshot: FrozenSnapshot, barrier2: Dict[str, Any]) -> str:
    warning_count = len(barrier2.get("warnings", [])) + len(barrier2.get("unsupported_rules", []))
    return (
        f"mode=multi_agent; dates={len(barrier2['dates'])}; "
        f"priority={len(barrier2['priority_pool'])}; pointer={len(barrier2['pointer_pool'])}; "
        f"last_pointer={barrier2['pointer_after']}; warnings={warning_count}"
    )


def finalize_multi_agent_run(
    ctx,
    snapshot: FrozenSnapshot,
    barrier1: Dict[str, Any],
    barrier2: Dict[str, Any],
    assembled_schedule: List[Dict[str, Any]],
    stop_event=None,
) -> Dict[str, Any]:
    validate_final_schedule(assembled_schedule, barrier2)

    restored = restore_schedule(assembled_schedule, snapshot.id_to_name, barrier2["area_names"], {})
    if not restored:
        raise ValueError("No valid schedule entries after multi-agent settlement.")

    def _apply_state_update(current_state: Dict[str, Any]) -> Dict[str, Any]:
        state_data = dict(current_state)
        state_data["next_run_note"] = build_next_run_note(snapshot, barrier2)
        state_data["debt_list"] = recover_missing_debts(
            extract_ids_from_value(state_data.get("debt_list", []), set(snapshot.all_ids)),
            extract_ids_from_value(barrier1["new_debt_ids"], set(snapshot.all_ids)),
            assembled_schedule,
        )

        credit_seed = [
            person_id
            for person_id in extract_ids_from_value(state_data.get("credit_list", []), set(snapshot.all_ids))
            if person_id not in set(barrier2["consumed_credit_ids"])
        ]
        state_data["credit_list"] = reconcile_credit_list(
            credit_seed,
            extract_ids_from_value(barrier1["new_credit_ids"], set(snapshot.all_ids)),
            assembled_schedule,
            set(snapshot.all_ids),
            state_data["debt_list"],
            True,
        )
        state_data["last_pointer"] = barrier2["pointer_after"]
        state_data["schedule_pool"] = merge_schedule_pool(
            state_data,
            restored,
            snapshot.apply_mode,
            snapshot.start_date,
        )
        return state_data

    state_data = update_state(ctx.paths["state"], _apply_state_update, stop_event=stop_event)

    return {
        "state_data": state_data,
        "restored_schedule": restored,
    }
