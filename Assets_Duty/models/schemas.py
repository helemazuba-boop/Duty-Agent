from __future__ import annotations

from typing import Dict, List, Literal, Optional

from pydantic import BaseModel, ConfigDict


class DutyRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    instruction: str
    trace_id: Optional[str] = None
    request_source: Optional[str] = None


class DutyScheduleEntrySaveRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    source_date: Optional[str] = None
    target_date: str
    day: Optional[str] = None
    area_assignments: Dict[str, List[str]] = {}
    note: Optional[str] = None
    confirm_overwrite: bool = False
    ledger_mode: Literal["record", "skip"] = "record"


class DutyPlanPromptRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    instruction: str
    trace_id: Optional[str] = None
    request_source: Optional[str] = None


class DutyPlanIngestRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    completion: str
    resume_context: Dict = {}
    trace_id: Optional[str] = None
    request_source: Optional[str] = None


class DutyScheduleEntryModel(BaseModel):
    model_config = ConfigDict(extra="forbid")

    date: str
    day: str = ""
    area_assignments: Dict[str, List[str]] = {}
    note: str = ""


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

    version: int = 1
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
    polling: dict = {}
    offline_schedule_days: int = 7
    offline_skip_weekends: bool = True


class DutyBackendConfigPatch(BaseModel):
    model_config = ConfigDict(extra="forbid")

    expected_version: Optional[int] = None
    selected_plan_id: Optional[str] = None
    plan_presets: Optional[List[DutyPlanPresetModel]] = None
    duty_rule: Optional[str] = None
    offline_schedule_days: Optional[int] = None
    offline_skip_weekends: Optional[bool] = None


class DutyNotificationSettingsPatch(BaseModel):
    model_config = ConfigDict(extra="forbid")

    expected_version: Optional[int] = None
    notification_entry: Optional[Literal["system", "classisland", "both", "off"]] = None
    system_notifications_enabled: Optional[bool] = None
    schedule_completion_notification_enabled: Optional[bool] = None
    auto_run_trigger_notification_enabled: Optional[bool] = None
    duty_reminder_enabled: Optional[bool] = None
    duty_reminder_times: Optional[List[str]] = None
    notification_duration_seconds: Optional[int] = None
    # Auto-run scheduling fields (host-config; normalized by state_ops).
    auto_run_mode: Optional[str] = None
    auto_run_parameter: Optional[str] = None
    auto_run_time: Optional[str] = None
    auto_run_retry_times: Optional[int] = None
    # Standalone-client lifecycle switches (Web settings "系统与自启" tab).
    client_auto_start: Optional[bool] = None
    client_close_action: Optional[Literal["ask", "tray", "exit"]] = None


class DutyModelProbeRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    base_url: str
    model: str
    api_key: Optional[str] = None


class DutyNotificationTestRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    title: str = "Duty-Agent 通知测试"
    body: str = "系统通知已接入独立客户端。"
    route: str = "/settings"


class SnapshotRosterEntry(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: int
    name: str
    active: bool = True


class DutyRosterEntryPatch(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: Optional[int] = None
    name: str
    active: bool = True


class DutyRosterUpdateRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    roster: List[DutyRosterEntryPatch] = []


class DutyRosterResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    roster: List[SnapshotRosterEntry] = []


class DutySnapshotResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    config: DutyBackendConfigModel
    roster: List[SnapshotRosterEntry] = []
    state: dict


class DutyScheduleEntrySaveResponse(BaseModel):
    model_config = ConfigDict(extra="forbid")

    status: str
    message: str
    ledger_mode: Literal["record", "skip"]
    ledger_applied: bool = False
    snapshot: DutySnapshotResponse
    overwrite_target_date: Optional[str] = None
    existing_entry: Optional[DutyScheduleEntryModel] = None
    proposed_entry: Optional[DutyScheduleEntryModel] = None
