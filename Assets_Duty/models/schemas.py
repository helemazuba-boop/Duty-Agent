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


class DutyBackendConfigPatch(BaseModel):
    model_config = ConfigDict(extra="forbid")

    expected_version: Optional[int] = None
    selected_plan_id: Optional[str] = None
    plan_presets: Optional[List[DutyPlanPresetModel]] = None
    duty_rule: Optional[str] = None


class DutyHostConfigModel(BaseModel):
    model_config = ConfigDict(extra="forbid")

    version: int = 1
    python_path: str = r".\Assets_Duty\python-embed\python.exe"
    auto_run_mode: str = "Off"
    auto_run_parameter: str = "Monday"
    enable_mcp: bool = False
    enable_webview_debug_layer: bool = False
    auto_run_time: str = "08:00"
    auto_run_trigger_notification_enabled: bool = True
    auto_run_retry_times: int = 3
    ai_consecutive_failures: int = 0
    last_auto_run_date: str = ""
    component_refresh_time: str = "08:00"
    notification_duration_seconds: int = 8
    duty_reminder_enabled: bool = False
    duty_reminder_times: List[str] = ["07:40"]


class DutyEditableHostSettingsModel(BaseModel):
    model_config = ConfigDict(extra="forbid")

    auto_run_mode: str = "Off"
    auto_run_parameter: str = "Monday"
    auto_run_time: str = "08:00"
    auto_run_trigger_notification_enabled: bool = True
    duty_reminder_enabled: bool = False
    duty_reminder_times: List[str] = ["07:40"]
    enable_mcp: bool = False
    enable_webview_debug_layer: bool = False
    component_refresh_time: str = "08:00"
    notification_duration_seconds: int = 8


class DutyEditableHostSettingsPatch(BaseModel):
    model_config = ConfigDict(extra="forbid")

    auto_run_mode: Optional[str] = None
    auto_run_parameter: Optional[str] = None
    auto_run_time: Optional[str] = None
    auto_run_trigger_notification_enabled: Optional[bool] = None
    duty_reminder_enabled: Optional[bool] = None
    duty_reminder_times: Optional[List[str]] = None
    enable_mcp: Optional[bool] = None
    enable_webview_debug_layer: Optional[bool] = None
    component_refresh_time: Optional[str] = None
    notification_duration_seconds: Optional[int] = None


class DutyEditableBackendSettingsModel(BaseModel):
    model_config = ConfigDict(extra="forbid")

    selected_plan_id: str = "standard"
    plan_presets: List[DutyPlanPresetModel] = []
    duty_rule: str = ""


class DutyEditableBackendSettingsPatch(BaseModel):
    model_config = ConfigDict(extra="forbid")

    selected_plan_id: Optional[str] = None
    plan_presets: Optional[List[DutyPlanPresetModel]] = None
    duty_rule: Optional[str] = None


class DutySettingsDocument(BaseModel):
    model_config = ConfigDict(extra="forbid")

    host_version: int = 1
    backend_version: int = 1
    host: DutyEditableHostSettingsModel = DutyEditableHostSettingsModel()
    backend: DutyEditableBackendSettingsModel = DutyEditableBackendSettingsModel()


class DutySettingsExpectedVersions(BaseModel):
    model_config = ConfigDict(extra="forbid")

    host_version: Optional[int] = None
    backend_version: Optional[int] = None


class DutySettingsChangesPatch(BaseModel):
    model_config = ConfigDict(extra="forbid")

    host: Optional[DutyEditableHostSettingsPatch] = None
    backend: Optional[DutyEditableBackendSettingsPatch] = None


class DutySettingsPatchRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    expected: DutySettingsExpectedVersions = DutySettingsExpectedVersions()
    changes: DutySettingsChangesPatch = DutySettingsChangesPatch()


class DutySettingsAppliedChanges(BaseModel):
    model_config = ConfigDict(extra="forbid")

    host: Optional[DutyEditableHostSettingsModel] = None
    backend: Optional[DutyEditableBackendSettingsModel] = None


class DutySettingsVersions(BaseModel):
    model_config = ConfigDict(extra="forbid")

    host: int = 1
    backend: int = 1


class DutySettingsMutationResult(BaseModel):
    model_config = ConfigDict(extra="forbid")

    success: bool = False
    message: str = ""
    restart_required: bool = False
    document: DutySettingsDocument = DutySettingsDocument()
    applied: DutySettingsAppliedChanges = DutySettingsAppliedChanges()
    versions: DutySettingsVersions = DutySettingsVersions()
    warnings: List[str] = []
    trace_id: str = ""


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
