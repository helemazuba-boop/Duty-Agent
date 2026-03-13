from __future__ import annotations

from dataclasses import dataclass
from datetime import date, datetime
from typing import Any, Dict, List


@dataclass(frozen=True)
class FrozenSnapshot:
    trace_id: str
    request_source: str
    instruction: str
    apply_mode: str
    request_time: datetime
    start_date: date
    config: Dict[str, Any]
    state: Dict[str, Any]
    name_to_id: Dict[str, int]
    id_to_name: Dict[int, str]
    all_ids: List[int]
    active_ids: List[int]
    inactive_ids: List[int]
    id_to_active: Dict[int, int]
    debt_list: List[int]
    credit_list: List[int]
    last_pointer: int
    previous_note: str
    duty_rule: str


@dataclass
class AgentTrace:
    agent_id: str
    status: str
    attempts: int
    duration_ms: int
    detail: str = ""
