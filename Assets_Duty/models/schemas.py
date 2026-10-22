from __future__ import annotations

from typing import Dict, List, Literal, Optional

from pydantic import BaseModel, ConfigDict

from validation_policy import DEFAULT_VALIDATION_MODE


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


class DutyAbsenceRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    action: Literal["add", "clear"] = "add"
    person: Optional[str] = None  # roster ID or name; required for add
    from_date: Optional[str] = None  # YYYY-MM-DD; default today
    to_date: Optional[str] = None  # YYYY-MM-DD; overrides days
    days: Optional[int] = None  # span from from_date (default 1)
    trace_id: Optional[str] = None
    request_source: Optional[str] = None


class DutyRunNoteRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    action: Literal["add", "clear"] = "add"
    text: Optional[str] = None  # required for add
    until: Optional[str] = None  # YYYY-MM-DD; None = until cleared/expired
    trace_id: Optional[str] = None
    request_source: Optional[str] = None


class DutyDayOverrideRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    action: Literal["set", "clear"] = "set"
    date: Optional[str] = None  # YYYY-MM-DD; required
    area: Optional[str] = None  # area name; required for set
    count: Optional[int] = None  # >0; required for set
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
    # Optional multi-area template; empty list = legacy single default area.
    areas: List[str] = []
    area_per_day_counts: dict = {}
    # Validation policy (relaxed / strict / repair / auto) as configured, plus
    # the mode the engine actually applies once "auto" is expanded by
    # model_profile. `effective_validation_mode` is what the UI should show as
    # the running policy when the user picked "auto". Defaults come from the
    # policy module so flipping the product default is a one-line change there.
    validation_mode: str = DEFAULT_VALIDATION_MODE
    effective_validation_mode: str = DEFAULT_VALIDATION_MODE


class DutyBackendConfigPatch(BaseModel):
    model_config = ConfigDict(extra="forbid")

    expected_version: Optional[int] = None
    selected_plan_id: Optional[str] = None
    plan_presets: Optional[List[DutyPlanPresetModel]] = None
    duty_rule: Optional[str] = None
    offline_schedule_days: Optional[int] = None
    offline_skip_weekends: Optional[bool] = None
    areas: Optional[List[str]] = None
    area_per_day_counts: Optional[dict] = None
    # Validation policy ("relaxed" / "strict" / "repair" / "auto"). Stored
    # verbatim; state_ops normalizes it and the runtime projection also
    # exposes the resolved value as `effective_validation_mode`.
    validation_mode: Optional[str] = None


class DutyDataResetRequest(BaseModel):
    """数据重置请求（班级换届 / 学期重置）。

    ``confirm`` 必须与 scope 的中文标签一致（"清空排班" / "换届重置" / "恢复出厂"），
    服务器端校验，防止误调用或误 curl 直接清掉全班数据。``expected_version`` 只在
    该 scope 真的会写 config.json 时生效（handover 勾选清规则、factory）。
    """

    model_config = ConfigDict(extra="forbid")

    scope: Literal["schedule", "handover", "factory"]
    clear_areas_and_rule: bool = False
    expected_version: Optional[int] = None
    confirm: str = ""


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
    # Duty display boundary (HH:MM), stored in host-config.json.
    # After this time, the effective "today" shifts to tomorrow.
    component_refresh_time: Optional[str] = None


class DutyModelProbeRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    base_url: str
    model: str
    api_key: Optional[str] = None


class DutyModelListRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    base_url: str
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
