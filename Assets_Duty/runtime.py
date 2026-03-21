from __future__ import annotations

import secrets
import time
import threading
import uuid
from pathlib import Path

from application.command_service import CommandService
from application.query_service import QueryService
from diagnostics import DutyDiagnosticsLogger
from state_ops import Context, load_host_config

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
        self.access_token = secrets.token_urlsafe(32)
        self.host_config = self._load_host_config()
        self.enable_mcp = bool(self.host_config.get("enable_mcp", False))
        self.command_service = CommandService(self)
        self.query_service = QueryService(self)

    def new_trace_id(self) -> str:
        return uuid.uuid4().hex

    def is_authorized(self, candidate_token: str | None) -> bool:
        return bool(candidate_token) and candidate_token == self.access_token

    def _load_host_config(self) -> dict:
        context = Context(self.data_dir, logger=self.logger, request_source="runtime_startup")
        return load_host_config(context)


def create_runtime(data_dir: Path) -> DutyRuntime:
    return DutyRuntime(data_dir)
