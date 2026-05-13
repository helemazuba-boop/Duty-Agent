from __future__ import annotations

import secrets
import time
import threading
import uuid
from datetime import datetime, timezone
from pathlib import Path

from application.command_service import CommandService
from application.query_service import QueryService
from auth import normalize_access_token_mode, verify_pbkdf2_sha256_token
from diagnostics import DutyDiagnosticsLogger
from state_ops import Context, load_host_config

APP_VERSION = "0.50.0"
BRIDGE_HEARTBEAT_TTL_SECONDS = 15.0


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
        self.bridge_status_lock = threading.Lock()
        self.bridge_heartbeat_ttl_seconds = BRIDGE_HEARTBEAT_TTL_SECONDS
        self.bridge_last_seen_monotonic: float | None = None
        self.bridge_last_seen_wall: float | None = None
        self.bridge_last_source = ""
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

    def mark_bridge_heartbeat(self, request_source: str = "bridge") -> dict:
        now_monotonic = time.monotonic()
        now_wall = time.time()
        normalized_source = str(request_source or "").strip() or "bridge"
        with self.bridge_status_lock:
            self.bridge_last_seen_monotonic = now_monotonic
            self.bridge_last_seen_wall = now_wall
            self.bridge_last_source = normalized_source
            return self._build_bridge_status_unlocked(now_monotonic)

    def get_bridge_status(self) -> dict:
        now_monotonic = time.monotonic()
        with self.bridge_status_lock:
            return self._build_bridge_status_unlocked(now_monotonic)

    def _build_bridge_status_unlocked(self, now_monotonic: float) -> dict:
        last_seen_monotonic = self.bridge_last_seen_monotonic
        last_seen_wall = self.bridge_last_seen_wall
        age_seconds = None
        connected = False

        if last_seen_monotonic is not None:
            age_seconds = max(0.0, now_monotonic - last_seen_monotonic)
            connected = age_seconds <= self.bridge_heartbeat_ttl_seconds

        last_seen_iso = None
        if last_seen_wall is not None:
            last_seen_iso = datetime.fromtimestamp(last_seen_wall, tz=timezone.utc).isoformat()

        return {
            "status": "connected" if connected else "disconnected",
            "connected": connected,
            "last_seen_at": last_seen_wall,
            "last_seen_iso": last_seen_iso,
            "age_seconds": round(age_seconds, 3) if age_seconds is not None else None,
            "ttl_seconds": int(self.bridge_heartbeat_ttl_seconds),
            "source": self.bridge_last_source or None,
        }

    def _load_host_config(self) -> dict:
        context = Context(self.data_dir, logger=self.logger, request_source="runtime_startup")
        return load_host_config(context)


def create_runtime(data_dir: Path, disable_mcp_runtime: bool = False) -> DutyRuntime:
    return DutyRuntime(data_dir, disable_mcp_runtime=disable_mcp_runtime)
