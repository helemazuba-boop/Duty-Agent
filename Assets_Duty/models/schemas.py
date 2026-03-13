from __future__ import annotations

from typing import List, Optional

from pydantic import BaseModel, ConfigDict


class DutyRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    instruction: str
    apply_mode: Optional[str] = None
    trace_id: Optional[str] = None
    request_source: Optional[str] = None


class DutyBackendConfigModel(BaseModel):
    model_config = ConfigDict(extra="forbid")

    api_key: str = ""
    base_url: str = "https://integrate.api.nvidia.com/v1"
    model: str = "moonshotai/kimi-k2-thinking"
    model_profile: str = "auto"
    orchestration_mode: str = "auto"
    provider_hint: str = ""
    per_day: int = 2
    duty_rule: str = ""


class DutyBackendConfigPatch(BaseModel):
    model_config = ConfigDict(extra="forbid")

    api_key: Optional[str] = None
    base_url: Optional[str] = None
    model: Optional[str] = None
    model_profile: Optional[str] = None
    orchestration_mode: Optional[str] = None
    provider_hint: Optional[str] = None
    per_day: Optional[int] = None
    duty_rule: Optional[str] = None


class SnapshotRosterEntry(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: int
    name: str
    active: bool = True


class DutySnapshotResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    config: DutyBackendConfigModel
    roster: List[SnapshotRosterEntry] = []
    state: dict
