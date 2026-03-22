from __future__ import annotations

import secrets
import time
import threading
import uuid
from pathlib import Path

from application.command_service import CommandService
from application.query_service import QueryService
from auth import normalize_access_token_mode, verify_pbkdf2_sha256_token
from diagnostics import DutyDiagnosticsLogger
from state_ops import Context, load_host_config

APP_VERSION = "0.50.0"


class DutyRuntime:
    def __init__(self, data_dir: Path, disable_mcp_runtime: bool = False):
        self.data_dir = data_dir.resolve()
        self.data_dir.mkdir(parents=True, exist_ok=True)
        self.logs_dir = self.data_dir.parent / "logs"
        self.logs_dir.mkdir(parents=True, exist_ok=True)
        self.version = APP_VERSION
        self.started_at = time.monotonic()
        self.logger = DutyDiagnosticsLogger(self.logs_dir)
        self.schedule_run_lock = threading.Lock()
        self.duty_live_owner_lock = threading.Lock()
        self.duty_live_owner_id: str | None = None
        self.host_config = self._load_host_config()
        self.disable_mcp_runtime = bool(disable_mcp_runtime)
        self.access_token_mode = normalize_access_token_mode(self.host_config.get("access_token_mode"))
        self.static_access_token_verifier = str(self.host_config.get("static_access_token_verifier", "") or "").strip()
        self.access_token = secrets.token_urlsafe(32) if self.access_token_mode == "dynamic" else ""
        self.enable_mcp_configured = bool(self.host_config.get("enable_mcp", False))
        self.enable_mcp = self.enable_mcp_configured and not self.disable_mcp_runtime
        self.command_service = CommandService(self)
        self.query_service = QueryService(self)

    def new_trace_id(self) -> str:
        return uuid.uuid4().hex

    def is_authorized(self, candidate_token: str | None) -> bool:
        if self.access_token_mode == "static":
            return verify_pbkdf2_sha256_token(candidate_token, self.static_access_token_verifier)
        return bool(candidate_token) and candidate_token == self.access_token

    def try_claim_duty_live_owner(self, owner_id: str) -> bool:
        normalized_owner = str(owner_id or "").strip()
        if not normalized_owner:
            return False

        with self.duty_live_owner_lock:
            if self.duty_live_owner_id is None:
                self.duty_live_owner_id = normalized_owner
                return True
            return self.duty_live_owner_id == normalized_owner

    def release_duty_live_owner(self, owner_id: str) -> None:
        normalized_owner = str(owner_id or "").strip()
        if not normalized_owner:
            return

        with self.duty_live_owner_lock:
            if self.duty_live_owner_id == normalized_owner:
                self.duty_live_owner_id = None

    def get_duty_live_owner(self) -> str | None:
        with self.duty_live_owner_lock:
            return self.duty_live_owner_id

    def _load_host_config(self) -> dict:
        context = Context(self.data_dir, logger=self.logger, request_source="runtime_startup")
        return load_host_config(context)


def create_runtime(data_dir: Path, disable_mcp_runtime: bool = False) -> DutyRuntime:
    return DutyRuntime(data_dir, disable_mcp_runtime=disable_mcp_runtime)
