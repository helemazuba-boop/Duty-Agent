from __future__ import annotations

from typing import List, Optional

from pydantic import BaseModel, ConfigDict


class DutyRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    instruction: str
    apply_mode: Optional[str] = None
    trace_id: Optional[str] = None
    request_source: Optional[str] = None


class DutyPlanPresetModel(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: str = "standard"
    name: str = "标准"
    mode_id: str = "standard"
    api_key: str = ""
    base_url: str = "https://integrate.api.nvidia.com/v1"
    model: str = "moonshotai/kimi-k2-thinking"
    model_profile: str = "auto"
    provider_hint: str = ""
    multi_agent_execution_mode: str = "auto"


class DutyBackendConfigModel(BaseModel):
    model_config = ConfigDict(extra="forbid")

    api_key: str = ""
    base_url: str = "https://integrate.api.nvidia.com/v1"
    model: str = "moonshotai/kimi-k2-thinking"
    model_profile: str = "auto"
    orchestration_mode: str = "auto"
    multi_agent_execution_mode: str = "auto"
    single_pass_strategy: str = "auto"
    provider_hint: str = ""
    selected_plan_id: str = "standard"
    plan_presets: List[DutyPlanPresetModel] = []
    duty_rule: str = ""


class DutyBackendConfigPatch(BaseModel):
    model_config = ConfigDict(extra="forbid")

    selected_plan_id: Optional[str] = None
    plan_presets: Optional[List[DutyPlanPresetModel]] = None
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
