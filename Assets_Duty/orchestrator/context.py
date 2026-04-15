from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date, datetime
from typing import Any, Dict, List, Set


@dataclass
class TimeWindow:
    """一个时间窗口，由 Orchestrator 切分后分派给 TimeWindow Agent."""

    index: int
    name: str
    start_date: date
    end_date: date
    dates: List[date]
    area_names: List[str]
    area_per_day: Dict[str, int]
    available_ids: List[int]
    debt_ids: List[int]
    credit_ids: List[int]

    @property
    def total_slots(self) -> int:
        return sum(self.area_per_day.get(a, 0) * len(self.dates) for a in self.area_names)


@dataclass
class OrchestratorContext:
    """Orchestrator Agent 的完整上下文."""

    trace_id: str
    request_source: str
    instruction: str
    request_time: datetime
    start_date: date
    config: Dict[str, Any]
    state: Dict[str, Any]
    id_to_name: Dict[int, str]
    id_to_area: Dict[int, str]
    all_ids: List[int]
    active_ids: List[int]
    inactive_ids: List[int]
    id_to_active: Dict[int, int]
    debt_list: List[int]
    credit_list: List[int]
    last_pointer: int
    previous_note: str
    duty_rule: str
    all_areas: List[str]
    area_per_day_counts: Dict[str, int]
    hints_on: bool = True
    max_rounds: int = 15
    week_threshold_days: int = 7
    model: str = "qwen3.5:9b"
    temperature: float = 0.1
    max_tokens: int = 4096
    timeout_seconds: int = 120

    @classmethod
    def from_frozen_snapshot(
        cls,
        snapshot: Any,
        all_areas: List[str],
        area_per_day_counts: Dict[str, int],
        hints_on: bool = True,
    ) -> OrchestratorContext:
        return cls(
            trace_id=snapshot.trace_id,
            request_source=snapshot.request_source,
            instruction=snapshot.instruction,
            request_time=snapshot.request_time,
            start_date=snapshot.start_date,
            config=snapshot.config,
            state=snapshot.state,
            id_to_name=snapshot.id_to_name,
            id_to_area={int(k): v for k, v in snapshot.id_to_active.items()},
            all_ids=snapshot.all_ids,
            active_ids=snapshot.active_ids,
            inactive_ids=snapshot.inactive_ids,
            id_to_active=snapshot.id_to_active,
            debt_list=snapshot.debt_list,
            credit_list=snapshot.credit_list,
            last_pointer=snapshot.last_pointer,
            previous_note=snapshot.previous_note,
            duty_rule=snapshot.duty_rule,
            all_areas=all_areas,
            area_per_day_counts=area_per_day_counts,
            hints_on=hints_on,
            max_rounds=snapshot.config.get("polling", {}).get("max_rounds", 15),
            week_threshold_days=7,
        )

    def total_schedule_slots(self, total_dates: List[date]) -> int:
        return sum(
            self.area_per_day_counts.get(area, 0) * len(total_dates)
            for area in self.all_areas
        )

    def available_ids_set(self) -> Set[int]:
        return set(self.active_ids)

    def format_debt_list(self) -> str:
        if not self.debt_list:
            return "无"
        result = []
        for pid in self.debt_list:
            name = self.id_to_name.get(pid, str(pid))
            result.append(f"{name}(ID={pid})")
        return ", ".join(result)

    def format_credit_list(self) -> str:
        if not self.credit_list:
            return "无"
        result = []
        for pid in self.credit_list:
            name = self.id_to_name.get(pid, str(pid))
            result.append(f"{name}(ID={pid})")
        return ", ".join(result)

    def format_person_pool(self) -> str:
        lines = []
        for pid in self.all_ids:
            tags = []
            if pid in self.inactive_ids:
                tags.append("不可用")
            elif pid in self.debt_list:
                tags.append("债务")
            elif pid in self.credit_list:
                tags.append("信用+")
            if pid in self.id_to_area:
                tags.append(f"{self.id_to_area[pid]}")
            tag_str = f" [{', '.join(tags)}]" if tags else ""
            name = self.id_to_name.get(pid, str(pid))
            lines.append(f"  {name} (ID={pid}){tag_str}")
        return "\n".join(lines)


@dataclass
class HintsResult:
    """INI 解析器返回的 hints 解析结果."""

    finalized: bool
    hints: List[Hint]
    response_ini: str
    updated_units: List[Any]
    state_delta: Dict[str, Any]
    applied_schedule: List[Any]


@dataclass
class Hint:
    """单条 hint."""

    level: str  # CONFLICT / WARNING / INFO
    message: str
    affects_window: int | None = None  # 影响的窗口索引，None = 全局
    slot_key: str | None = None
