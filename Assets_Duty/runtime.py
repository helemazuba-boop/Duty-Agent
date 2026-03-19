from __future__ import annotations

import time
import threading
import uuid
from pathlib import Path

from application.command_service import CommandService
from application.query_service import QueryService
from application.settings_service import SettingsService
from diagnostics import DutyDiagnosticsLogger

APP_VERSION = "0.50.0"


class DutyRuntime:
    def __init__(self, data_dir: Path):
        self.data_dir = data_dir.resolve()
        self.data_dir.mkdir(parents=True, exist_ok=True)
        self.logs_dir = self.data_dir.parent / "logs"
        self.logs_dir.mkdir(parents=True, exist_ok=True)
        self.version = APP_VERSION
        self.started_at = time.monotonic()
        self.logger = DutyDiagnosticsLogger(self.logs_dir)
        self.schedule_run_lock = threading.Lock()
        self.command_service = CommandService(self)
        self.query_service = QueryService(self)
        self.settings_service = SettingsService(self)

    def new_trace_id(self) -> str:
        return uuid.uuid4().hex


def create_runtime(data_dir: Path) -> DutyRuntime:
    return DutyRuntime(data_dir)
