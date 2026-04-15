from __future__ import annotations

from datetime import date, timedelta
from typing import List, Tuple

from .context import OrchestratorContext, TimeWindow

WEEK_THRESHOLD = 7


def should_use_timewindow_agents(ctx: OrchestratorContext, total_dates: List[date]) -> bool:
    """总 slot 数 < 7 天时，Orchestrator 自己填充，不分派 Agent."""
    total = ctx.total_schedule_slots(total_dates)
    return total >= ctx.week_threshold_days


def decompose_round(
    ctx: OrchestratorContext,
    total_dates: List[date],
) -> List[TimeWindow]:
    """
    动态切分轮次为多个时间窗口。

    切分策略：
      - total_slots < 7：天 → 不分派（Orchestrator 自己做）
      - 7 ~ 21 slots：按连续日期段切分，每段 3~5 天
      - > 21 slots：每段 3~4 天，窗口数 = ceil(total_days / 3)
    """
    total = ctx.total_schedule_slots(total_dates)
    if total < ctx.week_threshold_days:
        return []

    days = len(total_dates)
    if total <= 21:
        chunk_size = 3
    else:
        chunk_size = 3

    windows: List[TimeWindow] = []
    available_pool = set(ctx.active_ids)
    debt_pool = list(ctx.debt_list)

    for i in range(0, days, chunk_size):
        chunk_dates = total_dates[i : i + chunk_size]
        chunk_slots = sum(
            ctx.area_per_day_counts.get(a, 0) * len(chunk_dates)
            for a in ctx.all_areas
        )

        chunk_debt = _allocate_debt(debt_pool, chunk_slots)
        debt_pool = [d for d in debt_pool if d not in chunk_debt]

        chunk_available = list(available_pool)
        for pid in chunk_available:
            if pid in chunk_debt:
                available_pool.discard(pid)

        window = TimeWindow(
            index=i // chunk_size,
            name=f"phase{i // chunk_size + 1}",
            start_date=chunk_dates[0],
            end_date=chunk_dates[-1],
            dates=chunk_dates,
            area_names=ctx.all_areas,
            area_per_day=ctx.area_per_day_counts,
            available_ids=chunk_available,
            debt_ids=chunk_debt,
            credit_ids=[p for p in ctx.credit_list if p in chunk_available],
        )
        windows.append(window)

    return windows


def _allocate_debt(debt_list: List[int], slots: int) -> List[int]:
    """从债务列表中分配不超过 slots 个债务 ID."""
    allocated: List[int] = []
    for pid in debt_list:
        if len(allocated) >= slots:
            break
        if pid not in allocated:
            allocated.append(pid)
    return allocated


def build_remaining_slots(
    ctx: OrchestratorContext,
    dates: List[date],
    assigned: dict[str, list[int]],
) -> List[Tuple[date, str]]:
    """
    返回所有尚未安排的 (date, area) slot 列表.

    assigned: {(date_iso, area): [id1, id2, ...]}
    """
    slots = []
    for d in dates:
        for area in ctx.all_areas:
            need = ctx.area_per_day_counts.get(area, 0)
            key = (d.isoformat(), area)
            filled = len(assigned.get(key, []))
            if filled < need:
                slots.append((d, area))
    return slots
