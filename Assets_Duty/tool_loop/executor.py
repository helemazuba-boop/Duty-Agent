"""
Tool-loop executor — INI-based polling loop.

Protocol:
  1. Python builds the system prompt (with or without @command hints, controlled by hints_on).
  2. LLM outputs INI text (optionally via tool_calls).
  3. Python calls ini_handler.parse_and_apply() to:
       - parse [[#batch]] blocks + @keep/@drop/@replace/@finalize commands
       - validate and accept schedule entries
       - simulate pointer advance
       - return authoritative INI response
  4. Python returns result to LLM as a tool result or user message.
  5. Repeat until @finalize, empty [remaining], or max_rounds reached.
  6. Commit authoritative state to state.json.
"""
from __future__ import annotations

import json
import re
import traceback
from datetime import date, datetime, timedelta
from typing import Any, Callable, Dict, List, Optional, Set, Tuple

from execution_profiles import ExecutionPlan
from llm_transport import (
    call_llm_raw,
    _normalize_structured_output,
    _extract_fenced_block,
)
from state_ops import (
    Context,
    DEFAULT_SINGLE_AREA_NAME,
    anonymize_instruction,
    load_api_key_from_env,
    load_config,
    load_roster,
    load_state,
    normalize_count_map,
    save_json_atomic,
)

from .ini_handler import (
    ExecutionCtx,
    PollingFlags,
    ScheduleUnit,
    parse_and_apply,
)
from .tool_prompt import (
    TOOL_DESCRIPTION,
    build_tool_system_prompt,
    build_hints_prompt,
)


# ------------------------------------------------------------------------------
# Constants
# ------------------------------------------------------------------------------

MAX_ROUNDS_DEFAULT = 15
TOOL_NAME = "fill_schedule"
FINAL_STATE_RECHECK_ROUNDS = 2
TOOL_DEFINITION: List[dict] = [
    {
        "type": "function",
        "function": {
            "name": TOOL_NAME,
            "description": (
                "Submit INI-formatted schedule for validation and persistence. "
                "Pass the full INI text as the 'schedule' argument. "
                "Returns the validated INI with authoritative Python [state]."
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "schedule": {
                        "type": "string",
                        "description": (
                            "Complete INI text with optional [[#batch]] blocks, "
                            "[areas], [schedule], and [state] sections."
                        ),
                    }
                },
                "required": ["schedule"],
            },
        },
    }
]

# Markers the LLM uses to signal completion without calling the tool
SCHEDULE_DONE_MARKERS = frozenset({
    "@finalize", "@done", "done", "complete", "完成了",
    "all dates covered", "all slots filled", "no more dates",
})


# ------------------------------------------------------------------------------
# Progress helpers
# ------------------------------------------------------------------------------

def _emit_progress(
    emit_progress_fn: Optional[Callable[..., None]],
    phase: str,
    message: str,
    payload: Optional[Dict[str, Any]] = None,
) -> None:
    if not emit_progress_fn:
        return
    try:
        emit_progress_fn(phase, message, json.dumps(payload or {}, ensure_ascii=False))
    except Exception:
        pass


# ------------------------------------------------------------------------------
# Tool call extraction
# ------------------------------------------------------------------------------

# Pattern for finding fill_schedule calls
_FILL_SCHEDULE_CALL_RE = re.compile(
    r'fill_schedule\s*\(\s*(?P<json>\{.{0,4000}\})',
    re.DOTALL,
)


def _extract_tool_calls(ai_content: str) -> List[Dict[str, Any]]:
    """
    Extract fill_schedule tool calls from raw AI content.
    Handles OpenAI function_calling, markdown code blocks, and plain text.
    """
    results: List[Dict[str, Any]] = []

    cleaned = _normalize_structured_output(ai_content)
    if not cleaned.strip():
        return []

    # Try fenced JSON block first
    fenced = _extract_fenced_block(cleaned, "json")
    candidates = [fenced] if fenced else []
    candidates.append(cleaned)

    for candidate in candidates:
        # OpenAI function_calling format
        try:
            obj = json.loads(candidate)
            tc_list = obj.get("tool_calls", [])
            for tc in tc_list:
                fn = tc.get("function", tc)
                name = str(fn.get("name", "")).strip()
                if name == TOOL_NAME:
                    raw_args = fn.get("arguments", "{}")
                    if isinstance(raw_args, str):
                        args = json.loads(raw_args)
                    else:
                        args = raw_args or {}
                    results.append({"name": name, "arguments": args})
            if results:
                return results
        except Exception:
            pass

        # Plain text fill_schedule(...) call
        for m in _FILL_SCHEDULE_CALL_RE.finditer(candidate):
            raw_json = m.group("json").strip()
            try:
                args = json.loads(raw_json)
            except Exception:
                raw_json = raw_json.strip()
                try:
                    args = json.loads(raw_json)
                except Exception:
                    sch_m = re.search(
                        r'"schedule"\s*:\s*("(?:[^"\\]|\\.)*"|"""\s*(.*?)\s*""")',
                        raw_json,
                        re.DOTALL,
                    )
                    if sch_m:
                        try:
                            schedule_str = json.loads(sch_m.group(1))
                            args = {"schedule": schedule_str}
                        except Exception:
                            continue
                    else:
                        continue
            if "schedule" in args:
                results.append({"name": TOOL_NAME, "arguments": args})
                break

    return results


# ------------------------------------------------------------------------------
# Snapshot reading
# ------------------------------------------------------------------------------

def _read_snapshot(
    ctx: Context,
    input_data: dict,
) -> Tuple[
    List[int],        # all_ids
    Dict[int, int],   # debt_counts
    Dict[int, int],   # credit_counts
    int,              # last_pointer
    Set[int],         # inactive_ids
    str,              # instruction
    str,              # start_date ISO
    datetime,         # request_time
    Dict[str, str],   # name_to_id
    Dict[int, str],   # id_to_name
    List[dict],       # schedule_pool
    PollingFlags,
]:
    config = load_config(ctx)
    state_data = load_state(ctx.paths["state"])
    name_to_id, id_to_name, all_ids, id_to_active = load_roster(ctx.paths["roster"])

    debt_counts = normalize_count_map(state_data.get("debt_counts", {}))
    credit_counts = normalize_count_map(state_data.get("credit_counts", {}))
    last_pointer = int(state_data.get("last_pointer", 0) or 0)
    inactive_ids = {pid for pid in all_ids if id_to_active.get(pid, 1) == 0}
    schedule_pool = list(state_data.get("schedule_pool", []) or [])

    instruction = str(input_data.get("instruction", "Generate duty schedule")).strip()
    instruction = anonymize_instruction(instruction, name_to_id)

    request_time = datetime.now()
    start_date = request_time.date()
    start_date_iso = start_date.isoformat()

    # Polling flags from settings
    polling_config: dict = config.get("polling", {}) or {}
    flags = PollingFlags(
        hints_on=bool(polling_config.get("hints_on", True)),
        max_rounds=int(polling_config.get("max_rounds", MAX_ROUNDS_DEFAULT)),
    )

    api_key = str(config.get("api_key", "")).strip() or load_api_key_from_env()

    return (
        all_ids,
        debt_counts,
        credit_counts,
        last_pointer,
        inactive_ids,
        instruction,
        start_date_iso,
        request_time,
        name_to_id,
        id_to_name,
        schedule_pool,
        flags,
    )


# ------------------------------------------------------------------------------
# Required slots computation
# ------------------------------------------------------------------------------

# Supported area aliases (always declared so LLM knows them)
DEFAULT_AREA_NAMES = [DEFAULT_SINGLE_AREA_NAME]

_DATE_RANGE_RE = re.compile(
    r'(\d{1,2})[月/-](\d{1,2})(?:\s*日?\s*[-~至到]\s*(\d{1,2})[月/-](\d{1,2})(?:\s*日?))?',
)
_DATE_SINGLE_RE = re.compile(r'(\d{1,2})[月/-](\d{1,2})')


def _parse_date_from_tokens(year: int, mm_str: str, dd_str: str) -> date:
    """Parse month/day strings into a date, defaulting to year."""
    month = int(mm_str)
    day = int(dd_str)
    for y in (year, year + 1):
        try:
            return date(y, month, day)
        except ValueError:
            continue
    raise ValueError(f"invalid date: {mm_str}/{dd_str}")


def _build_required_slots(
    instruction: str,
    start_date: date,
    id_to_name: Dict[int, str],
    schedule_pool: List[dict],
    area_names: List[str],
) -> List[ScheduleUnit]:
    """
    Compute all (date, area) slots that need to be filled.

    Parses date ranges from the instruction (e.g. "4月1日到4月7日",
    "04-01~04-07") and combines with area names to generate the slot list.
    Already-scheduled dates (in schedule_pool) are excluded.
    """
def _build_required_slots(
    instruction: str,
    start_date: date,
    id_to_name: Dict[int, str],
    schedule_pool: List[dict],
    area_names: List[str],
) -> List[ScheduleUnit]:
    """
    Compute all (date, area) slots that need to be filled.

    Parses date ranges from the instruction (e.g. "4月1日到4月7日",
    "04-01~04-07") and combines with area names to generate the slot list.
    Already-scheduled dates (in schedule_pool) are excluded.
    Falls back to the next 7 days if no dates are specified.
    """
    filled_dates: Set[str] = set()
    for entry in schedule_pool:
        d = str(entry.get("date", "")).strip()
        if d:
            filled_dates.add(d)

    slots: List[ScheduleUnit] = []
    year = start_date.year

    # Try explicit date range first (e.g. "4月1日到4月3日", "04-01~04-07")
    range_match = _DATE_RANGE_RE.search(instruction or "")
    if range_match:
        try:
            start_parsed = _parse_date_from_tokens(year, range_match.group(1), range_match.group(2))
            end_str = range_match.group(3)
            end_parsed = _parse_date_from_tokens(year, range_match.group(3), range_match.group(4)) if end_str else start_parsed
        except (ValueError, TypeError):
            start_parsed = None
            end_parsed = None

        if start_parsed and end_parsed:
            if start_parsed > end_parsed:
                end_parsed = date(end_parsed.year + 1, end_parsed.month, end_parsed.day)
            current = start_parsed
            while current <= end_parsed:
                if current.isoformat() not in filled_dates:
                    for area in area_names:
                        slots.append(ScheduleUnit(date_iso=current.isoformat(), area_name=area, alias=""))
                current += timedelta(days=1)
            return slots

    # Try single date (e.g. "4月5日", "04-01")
    single_match = _DATE_SINGLE_RE.search(instruction or "")
    if single_match:
        try:
            d = _parse_date_from_tokens(year, single_match.group(1), single_match.group(2))
            if d.isoformat() not in filled_dates:
                for area in area_names:
                    slots.append(ScheduleUnit(date_iso=d.isoformat(), area_name=area, alias=""))
        except ValueError:
            pass
        if slots:
            return slots

    # Default: next 7 days starting from tomorrow
    for i in range(1, 8):
        d = start_date + timedelta(days=i)
        if d.isoformat() not in filled_dates:
            for area in area_names:
                slots.append(ScheduleUnit(date_iso=d.isoformat(), area_name=area, alias=""))

    return slots


# ------------------------------------------------------------------------------
# State persistence
# ------------------------------------------------------------------------------

def _write_state(
    ctx: Context,
    ctx_exec: ExecutionCtx,
    trace_id: str,
) -> None:
    """Persist authoritative state to state.json."""
    state_path = ctx.paths["state"]
    existing = load_state(state_path)
    existing["debt_counts"] = dict(ctx_exec.debt_counts)
    existing["credit_counts"] = dict(ctx_exec.credit_counts)
    existing["last_pointer"] = ctx_exec.last_pointer
    existing["schedule_pool"] = list(ctx_exec.schedule_pool)
    save_json_atomic(state_path, existing)


# ------------------------------------------------------------------------------
# Completion detection
# ------------------------------------------------------------------------------

def _detect_completion(raw_content: str) -> bool:
    """Check if LLM indicated schedule completion."""
    lower = raw_content.lower()
    return any(marker in lower for marker in SCHEDULE_DONE_MARKERS)


# ------------------------------------------------------------------------------
# Main executor
# ------------------------------------------------------------------------------

def run_tool_loop_schedule(
    ctx: Context,
    input_data: dict,
    execution_plan: ExecutionPlan,
    emit_progress_fn: Optional[Callable[..., None]] = None,
    stop_event: Optional[object] = None,
) -> Dict[str, Any]:
    """
    Tool-loop schedule executor with INI-based polling.

    Polls the LLM in rounds:
      1. Build system prompt (with hints if hints_on=True).
      2. Send conversation history to LLM.
      3. LLM returns INI text.
      4. parse_and_apply() validates, executes @commands, simulates pointer.
      5. Return authoritative INI to LLM.
      6. Repeat until @finalize, empty remaining, or max_rounds.
      7. Write state to state.json.
    """
    try:
        if stop_event is not None and getattr(stop_event, "is_set", lambda: False)():
            raise InterruptedError("Cancelled before start.")

        # --------------------------------------------------------------------------
        # Bootstrap
        # --------------------------------------------------------------------------
        (
            all_ids,
            debt_counts,
            credit_counts,
            last_pointer,
            inactive_ids,
            instruction,
            start_date_iso,
            request_time,
            name_to_id,
            id_to_name,
            schedule_pool,
            flags,
        ) = _read_snapshot(ctx, input_data)

        trace_id = str(input_data.get("trace_id", "")).strip() or ""
        start_date = datetime.fromisoformat(start_date_iso).date()

        # Load config (with api_key) before the polling loop
        config = load_config(ctx)
        api_key = str(config.get("api_key", "")).strip() or load_api_key_from_env()
        config = dict(config)
        config["api_key"] = api_key
        duty_rule = str(config.get("duty_rule", "")).strip()

        _emit_progress(emit_progress_fn, "tool_loop_start", "Tool-loop executor started.", {
            "max_rounds": flags.max_rounds,
            "hints_on": flags.hints_on,
            "all_ids": all_ids,
            "start_date": start_date_iso,
        })

        # --------------------------------------------------------------------------
        # Build system prompt
        # --------------------------------------------------------------------------
        current_time = request_time.strftime("%Y-%m-%d %H:%M")
        system_prompt = build_tool_system_prompt(
            instruction=instruction,
            duty_rule=duty_rule,
            all_ids=all_ids,
            inactive_ids=list(inactive_ids),
            debt_counts=debt_counts,
            credit_counts=credit_counts,
            last_pointer=last_pointer,
            start_date=start_date_iso,
            current_time=current_time,
        )

        # Optional hints injection
        hints_text = ""
        if flags.hints_on:
            hints_text = build_hints_prompt()

        if hints_text:
            system_prompt = system_prompt.rstrip() + "\n\n" + hints_text

        messages: List[dict] = [
            {"role": "system", "content": system_prompt},
        ]

        # --------------------------------------------------------------------------
        # Compute required slots
        # --------------------------------------------------------------------------
        required_slots = _build_required_slots(
            instruction=instruction,
            start_date=start_date,
            id_to_name=id_to_name,
            schedule_pool=schedule_pool,
            area_names=DEFAULT_AREA_NAMES,
        )

        # --------------------------------------------------------------------------
        # Polling state machine
        # --------------------------------------------------------------------------
        ctx_exec: Optional[ExecutionCtx] = None
        consecutive_no_tool = 0
        tool_call_count = 0
        seen_dates: Set[str] = set()
        alias_map: Dict[str, str] = {}

        for round_num in range(1, flags.max_rounds + 1):
            if stop_event is not None and getattr(stop_event, "is_set", lambda: False)():
                raise InterruptedError("Cancelled during tool-loop execution.")

            _emit_progress(emit_progress_fn, "tool_loop_round", f"Round {round_num}/{flags.max_rounds}", {
                "round": round_num,
                "tool_calls_total": tool_call_count,
                "dates_covered": len(seen_dates),
            })

            # --------------------------------------------------------------------------
            # LLM call
            # --------------------------------------------------------------------------
            try:
                raw_content = call_llm_raw(
                    messages=messages,
                    config=config,
                    progress_callback=emit_progress_fn,
                    stop_event=stop_event,
                    tools=TOOL_DEFINITION,
                )
            except Exception as ex:
                _emit_progress(emit_progress_fn, "tool_loop_error", f"LLM call failed: {ex}", {})
                raise

            assistant_msg = {"role": "assistant", "content": raw_content}
            messages.append(assistant_msg)

            # --------------------------------------------------------------------------
            # Extract tool calls
            # --------------------------------------------------------------------------
            tool_calls = _extract_tool_calls(raw_content)

            if not tool_calls:
                consecutive_no_tool += 1
                _emit_progress(
                    emit_progress_fn,
                    "tool_loop_no_tool",
                    f"Round {round_num}: LLM did not call fill_schedule.",
                    {"round": round_num, "consecutive": consecutive_no_tool},
                )

                if _detect_completion(raw_content):
                    _emit_progress(
                        emit_progress_fn,
                        "tool_loop_complete",
                        "LLM indicated schedule is complete.",
                        {},
                    )
                    break

                if consecutive_no_tool >= FINAL_STATE_RECHECK_ROUNDS:
                    _emit_progress(
                        emit_progress_fn,
                        "tool_loop_give_up",
                        f"No tool call for {FINAL_STATE_RECHECK_ROUNDS} rounds. Treating as done.",
                        {},
                    )
                    break

                # Ask LLM to retry
                correction = (
                    "You did not call the fill_schedule tool. "
                    "Output a JSON object with {\"tool_calls\": [{\"function\": {\"name\": \"fill_schedule\", "
                    "\"arguments\": {\"schedule\": \"<INI TEXT HERE>\"}}}]} "
                    "or call fill_schedule with your INI template."
                )
                messages.append({"role": "user", "content": correction})
                continue

            consecutive_no_tool = 0

            # --------------------------------------------------------------------------
            # Process tool calls
            # --------------------------------------------------------------------------
            for tc in tool_calls:
                tc_name = tc.get("name", "")
                if tc_name != TOOL_NAME:
                    messages.append({
                        "role": "user",
                        "content": f"Unknown tool '{tc_name}'. Only fill_schedule is available.",
                    })
                    continue

                args = tc.get("arguments", {})
                schedule_text = str(args.get("schedule", "")).strip()
                if not schedule_text:
                    messages.append({
                        "role": "user",
                        "content": "fill_schedule called with empty schedule. Provide the INI template.",
                    })
                    continue

                tool_call_count += 1

                # ----------------------------------------------------------------
                # Parse and apply INI
                # ----------------------------------------------------------------
                try:
                    response_text, ctx_exec = parse_and_apply(
                        raw_ini=schedule_text,
                        all_ids=all_ids,
                        debt_counts=debt_counts,
                        credit_counts=credit_counts,
                        inactive_ids=list(inactive_ids),
                        last_pointer=last_pointer,
                        start_date=start_date,
                        schedule_pool=schedule_pool,
                        required_slots=required_slots,
                        flags=flags,
                        round_num=round_num,
                        alias_map=alias_map,
                    )
                except Exception as ex:
                    tb = traceback.format_exc()
                    error_msg = (
                        f"Python error while parsing your INI:\n"
                        f"  {type(ex).__name__}: {ex}\n"
                        f"Please fix the INI and call fill_schedule again."
                    )
                    messages.append({"role": "user", "content": error_msg})
                    _emit_progress(emit_progress_fn, "tool_loop_parse_error", str(ex), {
                        "error": tb,
                        "tool_call": tool_call_count,
                    })
                    continue

                # Update state for next round
                debt_counts = ctx_exec.debt_counts
                credit_counts = ctx_exec.credit_counts
                last_pointer = ctx_exec.last_pointer
                schedule_pool = ctx_exec.schedule_pool
                alias_map = ctx_exec.alias_map

                # Track covered dates
                for entry in ctx_exec.accepted:
                    d = entry.get("date", "")
                    if d:
                        seen_dates.add(d)

                # Feedback on invalid @commands
                if ctx_exec.invalid_cmds:
                    warn_lines = ["[command feedback]:"]
                    for ic in ctx_exec.invalid_cmds:
                        warn_lines.append(f"  {ic}")
                    messages.append({"role": "user", "content": "\n".join(warn_lines)})
                    _emit_progress(emit_progress_fn, "tool_loop_cmd_warnings", "\n".join(ctx_exec.invalid_cmds), {
                        "warnings": ctx_exec.invalid_cmds,
                    })

                # Feedback on rejected entries
                if ctx_exec.rejected:
                    rej_lines = ["[rejected entries]:"]
                    for date_str, code, detail in ctx_exec.rejected:
                        rej_lines.append(f"  {date_str}: [{code}] {detail}")
                    messages.append({"role": "user", "content": "\n".join(rej_lines)})
                    _emit_progress(emit_progress_fn, "tool_loop_rejections", "", {
                        "rejected": ctx_exec.rejected,
                    })

                # Build tool result message
                if ctx_exec.finalized:
                    tool_result = (
                        "[TOOL RESULT]\n"
                        "Schedule finalized. Python's authoritative [state] is:\n\n"
                        + response_text
                        + "\n\nThe schedule is complete."
                    )
                else:
                    tool_result = (
                        "[TOOL RESULT]\n"
                        "INI processed. Python's authoritative [state] is:\n\n"
                        + response_text
                        + "\n\nReview the [state] above and continue adding to the schedule, "
                        "or call fill_schedule with @finalize to complete."
                    )

                messages.append({
                    "role": "tool",
                    "content": tool_result,
                    "tool_call_id": f"call_{tool_call_count}",
                })

                _emit_progress(emit_progress_fn, "tool_loop_tool_processed", "Tool processed.", {
                    "tool_call": tool_call_count,
                    "accepted": len(ctx_exec.accepted),
                    "remaining": len(ctx_exec.remaining),
                    "dates_covered": len(seen_dates),
                    "finalized": ctx_exec.finalized,
                })

                # Exit if finalized
                if ctx_exec.finalized:
                    _emit_progress(emit_progress_fn, "tool_loop_finalized", "Finalize confirmed.", {})
                    break

            # Check finalize after processing all tool calls in this round
            if ctx_exec is not None and ctx_exec.finalized:
                break

        # --------------------------------------------------------------------------
        # Finalize
        # --------------------------------------------------------------------------
        if ctx_exec is None:
            raise RuntimeError(
                "Tool loop ended without processing any tool calls. "
                "Ensure the LLM calls fill_schedule in its response."
            )

        # Write authoritative state to disk
        _write_state(ctx, ctx_exec, trace_id)

        dates_sorted = sorted(seen_dates)
        _emit_progress(emit_progress_fn, "tool_loop_done", "State written.", {
            "tool_calls": tool_call_count,
            "rounds": round_num,
            "dates": dates_sorted,
        })

        return {
            "status": "ok",
            "schedule_entries": ctx_exec.schedule_pool,
            "invalid_cmds": ctx_exec.invalid_cmds,
            "rejected": ctx_exec.rejected,
            "final_state": {
                "debt_counts": dict(ctx_exec.debt_counts),
                "credit_counts": dict(ctx_exec.credit_counts),
                "last_pointer": ctx_exec.last_pointer,
            },
            "execution_plan": execution_plan.to_metadata(),
            "tool_loop_meta": {
                "rounds": round_num,
                "tool_calls": tool_call_count,
                "dates_covered": dates_sorted,
                "finalized": ctx_exec.finalized,
                "hints_on": flags.hints_on,
            },
            "trace_id": trace_id,
        }

    except InterruptedError:
        raise
    except Exception as ex:
        tb = traceback.format_exc()
        _logger = getattr(ctx, "logger", None)
        if _logger is not None:
            _logger.error(
                "ToolLoop",
                "run_tool_loop_schedule failed.",
                trace_id=getattr(ctx, "trace_id", ""),
                exc=ex,
            )
        return {
            "status": "error",
            "message": str(ex),
            "trace_id": str(input_data.get("trace_id", "")).strip() or "",
        }
