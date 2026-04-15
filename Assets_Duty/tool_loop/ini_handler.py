"""
Polling INI handler for the tool-loop executor.

Two-phase protocol:
  Phase 1: Parse [[#batch]] blocks + special @commands from LLM INI output.
  Phase 2: Validate [schedule], apply @keep/@drop/@replace, simulate pointer,
           compute authoritative [state], and build the Python→LLM response.

Design principles:
  - Python drives the polling loop (debt/credit/pointer simulation).
  - LLM only references batches via @keep/@drop/@replace/@finalize.
  - hints_on=False → special @commands are silently ignored (LLM doesn't know them).
  - consumed_credit is NOT a field in state.json.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from datetime import date, timedelta
from typing import Any, Dict, List, Optional, Set, Tuple

from state_ops import (
    clone_count_map,
    decrement_count_map_entry,
    increment_count_map_entry,
)


# ------------------------------------------------------------------------------
# Data structures
# ------------------------------------------------------------------------------

@dataclass
class ScheduleSlot:
    """One (date, area) assignment within a batch."""
    date_iso: str       # ISO date string, e.g. "2026-04-01"
    area_name: str      # actual area name, e.g. "教室"
    alias: str          # alias used by LLM, e.g. "A"
    ids: List[int]      # assigned person IDs
    batch_name: str     # e.g. "week1"
    slot_key: str       # unique key e.g. "04-01 教室"
    line_index: int     # position in LLM output (for ordering)


@dataclass
class ParsedBatch:
    """One [[#batchname]] block from LLM."""
    name: str                       # e.g. "week1"
    slots: List[ScheduleSlot]       # all slots in this batch
    drop: bool = False              # true if [[#batchname-drop]] was emitted
    keep_keys: Set[str] = field(default_factory=set)   # slot_keys to keep
    replace_map: Dict[str, int] = field(default_factory=dict)  # slot_key -> new_id


@dataclass
class SpecialCmd:
    """One @keep/@drop/@replace/@finalize command from LLM."""
    verb: str        # KEEP / DROP / REPLACE / FINALIZE
    batch_name: str  # e.g. "week1"
    # For KEEP: keep_keys = set of slot_keys to preserve
    # For DROP: nothing extra
    # For REPLACE: slot_key -> new_id
    # For FINALIZE: nothing
    keep_keys: Set[str] = field(default_factory=set)
    replace_map: Dict[str, int] = field(default_factory=dict)


@dataclass
class PollingFlags:
    """Runtime flags from settings."""
    hints_on: bool
    max_rounds: int = 15


@dataclass
class ScheduleUnit:
    """One unfilled (date, area) slot tracked by Python."""
    date_iso: str
    area_name: str
    alias: str

    @property
    def slot_key(self) -> str:
        return f"{self.date_iso[5:]} {self.area_name}"  # "04-01 教室"


@dataclass
class ExecutionCtx:
    """
    Mutable execution context through one polling round.
    """
    # ---- authoritative state ----
    debt_counts: Dict[int, int]
    credit_counts: Dict[int, int]
    inactive_ids: Set[int]
    last_pointer: int

    # ---- schedule tracking ----
    schedule_pool: List[dict]        # all finalized entries: [{date, area_ids, note}, ...]
    remaining: List[ScheduleUnit]    # unfilled (date, area) slots

    # ---- polling metadata ----
    round_num: int
    flags: PollingFlags
    start_date: date

    # ---- per-round results ----
    accepted: List[dict]      # [{date, area_ids, note, source}, ...] this round
    rejected: List[Tuple[str, str, str]]   # [(date_str, reason_code, detail), ...]
    invalid_cmds: List[str]   # human-readable invalid command feedback
    finalized: bool = False    # True if @finalize was processed

    # ---- area alias map ----
    alias_map: Dict[str, str] = field(default_factory=dict)   # alias -> area_name

    # ---- roster validity ----
    _valid_ids: Set[int] = field(default_factory=set)

    @property
    def valid_ids(self) -> Set[int]:
        return self._valid_ids

    def mark_finalized(self) -> None:
        self.finalized = True


# ------------------------------------------------------------------------------
# INI text parsing
# ------------------------------------------------------------------------------

# [[#batchname]] or [[#batchname-drop]]
_BATCH_HEADER_RE = re.compile(
    r'^\s*\[\[#(?P<name>[a-zA-Z0-9_-]+)(?:-(?P<modifier>drop))?\]\]\s*$',
    re.IGNORECASE,
)
# [[#batchname-drop]] (explicit)
_BATCH_DROP_RE = re.compile(
    r'^\s*\[\[#(?P<name>[a-zA-Z0-9_-]+)-drop\]\]\s*$',
    re.IGNORECASE,
)
# @keep(#batch slot_key) or @keep(#batch)  — slot_key may be omitted (keep all)
_SPECIAL_CMD_RE = re.compile(
    r'^\s*@(?P<verb>KEEP|DROP|REPLACE|FINALIZE)'
    r'(?:\(#(?P<batch>[a-zA-Z0-9_-]+)'
    r'(?:\s+(?P<slot>[^\)]+))?\)?)?',
    re.IGNORECASE,
)
# REPLACE has additional args: SLOT=NEW_ID
_REPLACE_ARGS_RE = re.compile(
    r'^\s*@REPLACE\s*\(#(?P<batch>[a-zA-Z0-9_-]+)\s+(?P<replace_arg>[^\)]+)\)\s*$',
    re.IGNORECASE,
)
# @finalize alone on a line
_FINALIZE_RE = re.compile(r'^\s*@FINALIZE\s*$', re.IGNORECASE)
# [[...]] block header that is NOT a batch
_GENERIC_BLOCK_RE = re.compile(r'^\s*\[\[.+\]\]\s*$')
# Area section line: A = 教室
_AREA_LINE_RE = re.compile(r'^(?P<alias>[A-Z][A-Z0-9]*)\s*=\s*(?P<name>.+?)\s*$')
# Schedule line: 04-01 = A:1001 1002 | B:1003 1004 # 备注
_SCHEDULE_LINE_RE = re.compile(
    r'^(?P<mmdd>\d{2}-\d{2})\s*=\s*(?P<body>[^#]*?)(?:\s*#\s*(?P<note>.*))?$',
    re.IGNORECASE,
)
# Assignment segment within a schedule line: A:1001 1002
_ASSIGN_SEG_RE = re.compile(
    r'(?P<alias>[A-Z][A-Z0-9]*)\s*:\s*(?P<ids>[^|\s]+(?:\s+[^|\s]+)*)',
)


def _parse_mmdd_to_date(mmdd: str, start_date: date, previous_date: Optional[date]) -> date:
    """Resolve MM-DD to a concrete date near start_date."""
    m = re.match(r'^(\d{2})-(\d{2})$', mmdd.strip())
    if not m:
        raise ValueError(f"invalid MM-DD: {mmdd}")
    month, day = int(m.group(1)), int(m.group(2))
    candidate_year = (previous_date or start_date).year
    while True:
        try:
            candidate = date(candidate_year, month, day)
        except ValueError:
            raise ValueError(f"invalid MM-DD: {mmdd}")
        if previous_date is None:
            if candidate < start_date:
                candidate_year += 1
                continue
        elif candidate < previous_date:
            candidate_year += 1
            continue
        if candidate > start_date + timedelta(days=370):
            raise ValueError(f"date {mmdd} exceeds supported window")
        return candidate


def _split_sections(raw_text: str) -> Tuple[
    Dict[str, str],  # areas: alias -> area_name
    List[Tuple[str, str, str]],  # schedule: (mmdd, body, note)
    List[str],  # comments/annotations (everything not in [areas]/[schedule])
    List[str],  # special command lines
]:
    """
    Split raw INI text into sections and special blocks.
    Returns (areas_dict, schedule_lines, annotations, special_cmd_lines).
    """
    text = raw_text.strip().replace('\ufeff', '')
    if not text:
        return {}, [], [], []

    alias_map: Dict[str, str] = {}
    schedule_lines: List[Tuple[str, str, str]] = []
    annotations: List[str] = []
    special_cmd_lines: List[str] = []

    current_section: Optional[str] = None
    current_alias_map: Dict[str, str] = {}

    for raw_line in text.splitlines():
        line = raw_line.rstrip()
        stripped = line.strip()

        # Skip empty lines and full-line comments
        if not stripped or stripped.startswith(';'):
            continue

        # Check for batch / special block headers first
        if stripped.startswith('[['):
            # [[#batchname]] or [[#batchname-drop]]
            batch_match = _BATCH_HEADER_RE.match(stripped)
            if batch_match:
                # Start of a new batch block — no section context needed
                current_section = "batch"
                annotations.append(stripped)
                continue

            # Generic [[...]] block (not a batch)
            if _GENERIC_BLOCK_RE.match(stripped) and not _BATCH_HEADER_RE.match(stripped):
                current_section = "annotation"
                annotations.append(stripped)
                continue

        # Check for special @commands (can appear anywhere)
        if stripped.startswith('@'):
            # @finalize on its own line
            if _FINALIZE_RE.match(stripped):
                special_cmd_lines.append(stripped)
                continue
            # @keep / @drop / @replace(...)
            sc_match = _SPECIAL_CMD_RE.match(stripped)
            if sc_match:
                special_cmd_lines.append(stripped)
                continue
            # @REPLACE(...) with extra args
            rep_match = _REPLACE_ARGS_RE.match(stripped)
            if rep_match:
                special_cmd_lines.append(stripped)
                continue

        # Section header
        header_m = re.match(r'^\[(?P<name>[a-z_]+)\]$', stripped, re.IGNORECASE)
        if header_m:
            section_name = header_m.group('name').lower()
            if section_name == 'areas':
                current_section = 'areas'
                current_alias_map = {}
            elif section_name in ('schedule', 'state'):
                current_section = section_name
            else:
                current_section = None
            continue

        # Inside [areas]
        if current_section == 'areas':
            area_m = _AREA_LINE_RE.match(stripped)
            if area_m:
                alias = area_m.group('alias').strip()
                name = area_m.group('name').strip()
                if alias and name:
                    current_alias_map[alias] = name
            continue

        # Inside [schedule]
        if current_section == 'schedule':
            sched_m = _SCHEDULE_LINE_RE.match(stripped)
            if sched_m:
                mmdd = sched_m.group('mmdd').strip()
                body = sched_m.group('body').strip()
                note = sched_m.group('note') or ''
                schedule_lines.append((mmdd, body, note.strip()))
            continue

        # In a batch or annotation block
        if current_section in ('batch', 'annotation'):
            annotations.append(stripped)
            continue

        # Standalone lines outside sections go to annotations
        if stripped and not stripped.startswith('#'):
            annotations.append(stripped)

    # Merge all [areas] from any batch context
    for alias, name in current_alias_map.items():
        if alias not in alias_map:
            alias_map[alias] = name

    return alias_map, schedule_lines, annotations, special_cmd_lines


def parse_special_commands(cmd_lines: List[str]) -> Tuple[List[SpecialCmd], List[str]]:
    """
    Parse @keep / @drop / @replace / @finalize command lines.
    Returns (commands, invalid_lines).
    """
    commands: List[SpecialCmd] = []
    invalid: List[str] = []

    for line in cmd_lines:
        stripped = line.strip()

        # @finalize
        if _FINALIZE_RE.match(stripped):
            commands.append(SpecialCmd(verb='FINALIZE', batch_name=''))
            continue

        # @replace(...)
        rep_m = _REPLACE_ARGS_RE.match(stripped)
        if rep_m:
            batch = rep_m.group('batch').strip()
            raw_arg = rep_m.group('replace_arg').strip()
            # Format: SLOT=NEW_ID, e.g. "04-01 A=1005"
            parts = raw_arg.split('=', 1)
            if len(parts) != 2:
                invalid.append(f"INVALID: @REPLACE arg must be SLOT=ID, got: {raw_arg!r}")
                continue
            slot_part, id_part = parts[0].strip(), parts[1].strip()
            try:
                new_id = int(id_part)
            except ValueError:
                invalid.append(f"INVALID: @REPLACE new ID must be integer, got: {id_part!r}")
                continue
            cmd = SpecialCmd(verb='REPLACE', batch_name=batch)
            cmd.replace_map[slot_part] = new_id
            commands.append(cmd)
            continue

        # @keep / @drop
        sc_m = _SPECIAL_CMD_RE.match(stripped)
        if sc_m:
            verb = sc_m.group('verb').upper()
            batch = (sc_m.group('batch') or '').strip()
            slot_str = sc_m.group('slot') or ''

            if verb not in ('KEEP', 'DROP'):
                invalid.append(f"INVALID: unknown @command: {verb}")
                continue

            cmd = SpecialCmd(verb=verb, batch_name=batch)
            if verb == 'KEEP' and slot_str:
                # slot_str might be "04-01 A" or "04-01 教室"
                cmd.keep_keys.add(slot_str)
            commands.append(cmd)
            continue

        # Not a special command
        invalid.append(f"INVALID: unrecognized command: {stripped!r}")

    return commands, invalid


def parse_ini_to_batches(
    raw_text: str,
    alias_map: Dict[str, str],
    start_date: date,
) -> Tuple[List[ParsedBatch], List[Tuple[str, str, str]], List[str]]:
    """
    Parse LLM INI output into batch structures.

    Returns (batches, schedule_rejections, annotations).
    Annotations are lines inside [[...]] blocks (for display, not execution).
    """
    areas, schedule_lines, annotations, cmd_lines = _split_sections(raw_text)

    # Merge areas from this parse
    merged_alias: Dict[str, str] = dict(alias_map)
    for alias, name in areas.items():
        if alias not in merged_alias:
            merged_alias[alias] = name

    # Group schedule lines into batches based on [[...]] markers
    batches: List[ParsedBatch] = []
    current_batch: Optional[ParsedBatch] = None
    previous_date: Optional[date] = None
    slot_index = 0

    rejections: List[Tuple[str, str, str]] = []

    # Track which [[#batchname-drop]] markers appeared
    drop_batches: Set[str] = set()

    for ann in annotations:
        bm = _BATCH_HEADER_RE.match(ann)
        if bm:
            batch_name = bm.group('name')
            modifier = bm.group('modifier') or ''
            is_drop = (modifier.lower() == 'drop')
            if is_drop:
                drop_batches.add(batch_name)
            current_batch = ParsedBatch(name=batch_name, slots=[])
            batches.append(current_batch)
            continue

        # A schedule line inside a batch
        if current_batch is not None:
            # Check if this annotation line is actually a schedule line
            sm = _SCHEDULE_LINE_RE.match(ann)
            if sm:
                # This is a schedule line embedded in the batch annotation
                mmdd = sm.group('mmdd').strip()
                body = sm.group('body').strip()
                note = sm.group('note') or ''
                _process_schedule_line(
                    mmdd, body, note, merged_alias, start_date,
                    current_batch, previous_date, slot_index, rejections,
                )
                slot_index += 1
                if current_batch.slots:
                    last_slot = current_batch.slots[-1]
                    try:
                        previous_date = date.fromisoformat(last_slot.date_iso)
                    except (ValueError, TypeError):
                        pass
                continue

    # Process the [schedule] section lines
    for mmdd, body, note in schedule_lines:
        # Find or create a batch for these lines (if no [[...]] marker, use a default)
        if not batches:
            batches.append(ParsedBatch(name='_default', slots=[]))
        current_batch = batches[-1]

        _process_schedule_line(
            mmdd, body, note, merged_alias, start_date,
            current_batch, previous_date, slot_index, rejections,
        )
        slot_index += 1
        if current_batch.slots:
            last_slot = current_batch.slots[-1]
            try:
                previous_date = date.fromisoformat(last_slot.date_iso)
            except (ValueError, TypeError):
                pass

    # Mark drop batches
    for batch in batches:
        if batch.name in drop_batches:
            batch.drop = True

    return batches, rejections, annotations


def _process_schedule_line(
    mmdd: str,
    body: str,
    note: str,
    alias_map: Dict[str, str],
    start_date: date,
    batch: ParsedBatch,
    previous_date: Optional[date],
    slot_index: int,
    rejections: List[Tuple[str, str, str]],
) -> None:
    """Parse one schedule line and add slots to the batch."""
    resolved_date = _parse_mmdd_to_date(mmdd, start_date, previous_date)
    date_iso = resolved_date.isoformat()
    date_key = mmdd  # "04-01"

    # Parse segments: "A:1001 1002 | B:1003 1004"
    segments = [s.strip() for s in body.split('|') if s.strip()]
    for seg in segments:
        if ':' not in seg:
            rejections.append((date_key, 'BAD_SEGMENT', f'invalid segment: {seg!r}'))
            continue
        alias, ids_str = seg.split(':', 1)
        alias = alias.strip()
        ids_str = ids_str.strip()

        if alias not in alias_map:
            rejections.append((date_key, 'UNKNOWN_ALIAS', f'alias {alias!r} not declared in [areas]'))
            continue

        area_name = alias_map[alias]
        slot_key = f"{date_key} {area_name}"

        # Parse IDs
        parsed_ids: List[int] = []
        seen_ids: set[int] = set()
        for token in ids_str.split():
            try:
                pid = int(token)
            except ValueError:
                rejections.append((slot_key, 'BAD_ID', f'invalid ID: {token!r}'))
                continue
            if pid in seen_ids:
                rejections.append((slot_key, 'DUPLICATE', f'duplicate ID {pid}'))
                continue
            seen_ids.add(pid)
            parsed_ids.append(pid)

        batch.slots.append(ScheduleSlot(
            date_iso=date_iso,
            area_name=area_name,
            alias=alias,
            ids=parsed_ids,
            batch_name=batch.name,
            slot_key=slot_key,
            line_index=slot_index,
        ))


def apply_special_commands(
    ctx: ExecutionCtx,
    batches: List[ParsedBatch],
    special_cmds: List[SpecialCmd],
) -> None:
    """
    Apply @keep / @drop / @replace commands.
    Modifies ctx.accepted and ctx.remaining in-place.
    """
    # Build slot_key -> batch index map
    batch_by_name: Dict[str, int] = {}
    for i, b in enumerate(batches):
        batch_by_name.setdefault(b.name, i)

    # Phase 1: @KEEP — determine which slot_keys to preserve
    keep_by_batch: Dict[str, Set[str]] = {}   # batch_name -> kept keys
    for cmd in special_cmds:
        if cmd.verb != 'KEEP':
            continue
        bn = cmd.batch_name
        if bn not in keep_by_batch:
            keep_by_batch[bn] = set()
        keep_by_batch[bn].update(cmd.keep_keys)

    # Phase 2: @DROP — mark entire batches
    drop_batches: Set[str] = set()
    for cmd in special_cmds:
        if cmd.verb == 'DROP' and cmd.batch_name:
            drop_batches.add(cmd.batch_name)

    # Phase 3: @REPLACE — slot_key -> new_id
    replace_map: Dict[str, int] = {}   # "batch_name:slot_key" -> new_id
    for cmd in special_cmds:
        if cmd.verb != 'REPLACE':
            continue
        for slot_key, new_id in cmd.replace_map.items():
            replace_map[f"{cmd.batch_name}:{slot_key}"] = new_id

    # Phase 4: Process each batch (keep/drop/replace logic)
    for batch in batches:
        if batch.drop or batch.name in drop_batches:
            # Drop entire batch — return all its slots to remaining
            for slot in batch.slots:
                ctx.remaining.append(ScheduleUnit(
                    date_iso=slot.date_iso,
                    area_name=slot.area_name,
                    alias=slot.alias,
                ))
            continue

        kept_keys = keep_by_batch.get(batch.name, set())
        # @keep sent (with or without keys): keep only listed or keep all in batch
        # No @keep at all: default to KEEP ALL (accept all slots)
        batch_mentioned = batch.name in keep_by_batch
        keep_all = not batch_mentioned or len(kept_keys) == 0

        for slot in batch.slots:
            # Apply @replace if applicable
            replace_key = f"{batch.name}:{slot.slot_key}"
            effective_ids = list(slot.ids)
            if replace_key in replace_map:
                new_id = replace_map[replace_key]
                effective_ids = [new_id]

            # Determine if this slot should be kept
            should_keep = keep_all or (slot.slot_key in kept_keys)

            if should_keep:
                # Accept the slot
                _accept_slot(ctx, slot, effective_ids)
            else:
                # Release back to remaining
                ctx.remaining.append(ScheduleUnit(
                    date_iso=slot.date_iso,
                    area_name=slot.area_name,
                    alias=slot.alias,
                ))

    # Phase 5: @FINALIZE — mark as finalized (after all other commands)
    for cmd in special_cmds:
        if cmd.verb == 'FINALIZE':
            ctx.mark_finalized()
            return  # Stop after finalize


def _accept_slot(ctx: ExecutionCtx, slot: ScheduleSlot, ids: List[int]) -> None:
    """Validate and accept one slot into the schedule pool."""
    # Basic validation
    for pid in ids:
        if ctx.valid_ids and pid not in ctx.valid_ids:
            ctx.rejected.append((slot.slot_key, 'NOT_FOUND', f'ID {pid} not in roster'))
            continue
        if pid in ctx.inactive_ids:
            ctx.rejected.append((slot.slot_key, 'INACTIVE', f'ID {pid} is inactive'))
            continue

    # Find or create entry for this date
    entry = next(
        (e for e in ctx.accepted if e.get('date') == slot.date_iso),
        None,
    )
    if entry is None:
        entry = {'date': slot.date_iso, 'area_ids': {}, 'note': '', 'source': 'polling'}
        ctx.accepted.append(entry)

    if slot.area_name not in entry['area_ids']:
        entry['area_ids'][slot.area_name] = []

    seen = set(entry['area_ids'][slot.area_name])
    for pid in ids:
        if pid in seen:
            ctx.rejected.append((slot.slot_key, 'DUPLICATE', f'{pid} already in {slot.area_name} on {slot.date_iso}'))
            continue
        seen.add(pid)
        entry['area_ids'][slot.area_name].append(pid)

        # Update state tracking
        if ctx.debt_counts.get(pid, 0) > 0:
            decrement_count_map_entry(ctx.debt_counts, pid)
        if ctx.credit_counts.get(pid, 0) > 0:
            decrement_count_map_entry(ctx.credit_counts, pid)


def simulate_pointer_advance(
    ctx: ExecutionCtx,
    all_ids: List[int],
) -> int:
    """
    Simulate the pointer walking through all_ids starting from ctx.last_pointer.

    The pointer value represents the "number of people who have been walked past".
    When it advances past an ID, that ID loses priority (credit consumed, etc.).

    Returns the new pointer position (total steps walked, modulo total_ids).
    """
    if not all_ids:
        return ctx.last_pointer

    total_ids = len(all_ids)

    # Collect IDs assigned this round
    assigned: set[int] = set()
    for entry in ctx.accepted:
        for ids in entry.get('area_ids', {}).values():
            assigned.update(ids)

    # Credit IDs with remaining balance
    credit_ids = set(ctx.credit_counts.keys())

    steps_walked = 0
    max_steps = max(total_ids * 3, len(credit_ids) * 4)
    for _ in range(max_steps):
        cursor_idx = (ctx.last_pointer + steps_walked) % total_ids
        pid = all_ids[cursor_idx]
        steps_walked += 1
        if pid in assigned:
            continue
        if pid in credit_ids and ctx.credit_counts.get(pid, 0) > 0:
            decrement_count_map_entry(ctx.credit_counts, pid)

    # Return updated pointer position
    return ctx.last_pointer + steps_walked - total_ids  # walk past the cycle boundary


# ------------------------------------------------------------------------------
# Remaining slots computation
# ------------------------------------------------------------------------------

def compute_remaining_units(
    required_slots: List[ScheduleUnit],
    accepted: List[dict],
    alias_map: Optional[Dict[str, str]] = None,
) -> List[ScheduleUnit]:
    """
    Subtract accepted entries from required slots to get remaining.
    required_slots: all (date, area) slots that need to be filled.
    accepted: entries already in the pool [{date, area_ids}, ...].
    alias_map: unused in the current implementation (reserved for future).
    Returns the set of unfilled slots.
    """
    # A (date, area) unit is filled when accepted contains that date.
    # Area names in required_slots reflect the initial area set (e.g. DEFAULT_SINGLE_AREA_NAME),
    # but the LLM may declare different [areas] in its INI. Since we cannot know
    # the mapping at this stage, we consider a date "filled" if accepted has any area for it.
    filled_dates: Set[str] = set()
    for entry in accepted:
        d = entry.get("date", "")
        if d:
            filled_dates.add(d)

    return [
        unit
        for unit in required_slots
        if unit.date_iso not in filled_dates
    ]


# ------------------------------------------------------------------------------
# Format helpers
# ------------------------------------------------------------------------------

def _format_count_map(counts: Dict[int, int]) -> str:
    if not counts:
        return ""
    parts: List[str] = []
    for pid in sorted(counts.keys()):
        cnt = counts.get(pid, 0)
        if cnt <= 0:
            continue
        parts.append(f"{pid}*{cnt}" if cnt > 1 else str(pid))
    return " ".join(parts)


def _format_remaining(remaining: List[ScheduleUnit], ctx: ExecutionCtx) -> str:
    """Format [remaining] section for Python→LLM response."""
    lines: List[str] = []
    lines.append("[remaining]")
    # Group by date
    by_date: Dict[str, List[ScheduleUnit]] = {}
    for unit in remaining:
        by_date.setdefault(unit.date_iso, []).append(unit)
    for date_iso in sorted(by_date.keys()):
        # MM-DD
        mmdd = date_iso[5:]
        units = sorted(by_date[date_iso], key=lambda u: u.area_name)
        for unit in units:
            # Try to find the alias
            alias = next((a for a, n in ctx.alias_map.items() if n == unit.area_name), unit.area_name)
            lines.append(f"{mmdd} = {alias}:?")
    lines.append("")
    return "\n".join(lines)


def _format_personnel_summary(ctx: ExecutionCtx, all_ids: List[int], id_to_name: Dict[int, str]) -> str:
    """Format available personnel summary for Python→LLM response."""
    lines: List[str] = ["; 可用人员:"]
    # Group by area (first occurrence in alias_map)
    area_alias_map: Dict[str, str] = {v: k for k, v in ctx.alias_map.items()}

    # People not yet assigned this round (not in accepted)
    assigned_this_round: set[int] = set()
    for entry in ctx.accepted:
        for ids in entry.get('area_ids', {}).values():
            assigned_this_round.update(ids)

    for pid in all_ids:
        if pid in ctx.inactive_ids:
            continue
        name = id_to_name.get(pid, str(pid))
        credit = ctx.credit_counts.get(pid, 0)
        debt = ctx.debt_counts.get(pid, 0)
        status = []
        if debt > 0:
            status.append(f"欠{debt}")
        if credit > 0:
            status.append(f"信用+{credit}")
        if pid in assigned_this_round:
            status.append("已安排")
        status_str = " ".join(status) if status else "空闲"
        # Find their area (simplified: show without area)
        lines.append(f";   {pid} {name} {status_str}")
    return "\n".join(lines)


# ------------------------------------------------------------------------------
# Build Python→LLM response
# ------------------------------------------------------------------------------

def build_response(
    ctx: ExecutionCtx,
    all_ids: List[int],
    id_to_name: Dict[int, str],
    instructions: Optional[List[str]] = None,
) -> str:
    """
    Build the Python→LLM response INI text.
    Contains authoritative [state], finalized entries, and [remaining].
    """
    lines: List[str] = []

    # [state] — authoritative
    lines.append("[state]")
    lines.append(f"round = {ctx.round_num}")
    lines.append(f"pointer = {ctx.last_pointer}")
    debt_str = _format_count_map(ctx.debt_counts)
    lines.append(f"debt = {debt_str}")
    credit_str = _format_count_map(ctx.credit_counts)
    lines.append(f"credit = {credit_str}")
    lines.append("")

    # Finalized entries (merged accepted into pool)
    if ctx.schedule_pool or ctx.accepted:
        lines.append("; 已安排的班次:")
        all_entries = list(ctx.schedule_pool) + ctx.accepted
        # Merge by date
        by_date: Dict[str, dict] = {}
        for e in all_entries:
            d = e.get('date', '')
            if d not in by_date:
                by_date[d] = {'date': d, 'area_ids': {}, 'note': e.get('note', '')}
            for area, ids in e.get('area_ids', {}).items():
                if area not in by_date[d]['area_ids']:
                    by_date[d]['area_ids'][area] = []
                for pid in ids:
                    if pid not in by_date[d]['area_ids'][area]:
                        by_date[d]['area_ids'][area].append(pid)

        for date_iso in sorted(by_date.keys()):
            entry = by_date[date_iso]
            mmdd = date_iso[5:]
            parts: List[str] = []
            for area_name in sorted(entry['area_ids'].keys()):
                alias = next((a for a, n in ctx.alias_map.items() if n == area_name), area_name)
                ids = entry['area_ids'][area_name]
                if ids:
                    parts.append(f"{alias}:{' '.join(str(pid) for pid in ids)}")
            if parts:
                note = entry.get('note', '')
                note_str = f" # {note}" if note else ""
                lines.append(f"{mmdd} = {' | '.join(parts)}{note_str}")
        lines.append("")

    # [remaining]
    if ctx.remaining:
        lines.append(_format_remaining(ctx.remaining, ctx))

    # Personnel summary
    lines.append(_format_personnel_summary(ctx, all_ids, id_to_name))
    lines.append("")

    # Instruction hints
    if instructions:
        lines.append("; 提示:")
        for instr in instructions:
            lines.append(f";   {instr}")
        lines.append("")

    return "\n".join(lines)


# ------------------------------------------------------------------------------
# Main entry point
# ------------------------------------------------------------------------------

def parse_and_apply(
    raw_ini: str,
    all_ids: List[int],
    debt_counts: Dict[int, int],
    credit_counts: Dict[int, int],
    inactive_ids: List[int],
    last_pointer: int,
    start_date: date,
    schedule_pool: List[dict],
    required_slots: List[ScheduleUnit],
    flags: PollingFlags,
    round_num: int,
    alias_map: Optional[Dict[str, str]] = None,
) -> Tuple[str, ExecutionCtx]:
    """
    Top-level function called by the executor each polling round.

    Args:
        raw_ini: LLM's INI output this round.
        all_ids: Full roster IDs (for pointer simulation).
        debt_counts: Current debt state.
        credit_counts: Current credit state.
        inactive_ids: Disabled person IDs.
        last_pointer: Current pointer position.
        start_date: Schedule start date.
        schedule_pool: All previously finalized entries.
        required_slots: All (date, area) slots that need to be filled.
        flags: hints_on, max_rounds.
        round_num: Current round number.
        alias_map: Pre-populated alias→area_name map (from previous rounds).

    Returns:
        (response_text_for_llm, ctx)
    """
    # Build execution context
    ctx = ExecutionCtx(
        debt_counts=clone_count_map(debt_counts),
        credit_counts=clone_count_map(credit_counts),
        inactive_ids=set(inactive_ids),
        last_pointer=last_pointer,
        schedule_pool=list(schedule_pool),
        remaining=list(required_slots),
        round_num=round_num,
        flags=flags,
        start_date=start_date,
        accepted=[],
        rejected=[],
        invalid_cmds=[],
        finalized=False,
        alias_map=dict(alias_map or {}),
        _valid_ids=set(all_ids),
    )

    # Step 1: Split LLM text into sections
    areas, schedule_lines, annotations, cmd_lines = _split_sections(raw_ini)

    # Merge [areas]
    for alias, name in areas.items():
        if alias not in ctx.alias_map:
            ctx.alias_map[alias] = name

    # Step 2: Parse special @commands
    special_cmds: List[SpecialCmd] = []
    if flags.hints_on:
        special_cmds, ctx.invalid_cmds = parse_special_commands(cmd_lines)
    # If hints_on=False, special commands are silently ignored

    # Step 3: Parse batches + [schedule]
    batches, schedule_rejections, _ = parse_ini_to_batches(
        raw_ini, ctx.alias_map, start_date,
    )
    ctx.rejected.extend(schedule_rejections)

    # Step 4: Apply @keep/@drop/@replace commands
    apply_special_commands(ctx, batches, special_cmds)

    # Step 5: Add accepted entries to the pool
    for entry in ctx.accepted:
        ctx.schedule_pool.append(entry)

    # Step 6: Recompute remaining (subtract what was just finalized)
    ctx.remaining = compute_remaining_units(required_slots, ctx.schedule_pool, ctx.alias_map)

    # Step 7: Simulate pointer advance
    ctx.last_pointer = simulate_pointer_advance(ctx, all_ids)

    # Step 8: Check termination conditions
    should_finalize = (
        ctx.finalized
        or not ctx.remaining
        or round_num >= flags.max_rounds
    )
    if should_finalize:
        ctx.mark_finalized()

    # Step 9: Build response
    response = build_response(
        ctx=ctx,
        all_ids=all_ids,
        id_to_name={},  # will be filled by caller if needed
    )

    return response, ctx
