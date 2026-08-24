from __future__ import annotations

import json
import os
import sys
import threading
import time
import traceback
from datetime import datetime, timedelta
from pathlib import Path
from typing import Any

KEEP_DAYS = 14
LOG_FILE_PREFIX = "duty-backend"
# Long-running processes only pruned at startup, so old daily files piled up
# forever. Prune on the first write of each new day instead, and rotate the
# current day's file once it outgrows this cap (one .1 generation kept, so a
# single day can never exceed ~2x this size on disk).
MAX_LOG_FILE_BYTES = 32 * 1024 * 1024


def mask_secret(value: str | None) -> str:
    normalized = (value or "").strip()
    if not normalized:
        return "<empty>"
    if len(normalized) <= 4:
        return "<redacted>"
    return f"<redacted:{len(normalized)}:{normalized[-4:]}>"


def truncate_for_log(value: str | None, max_length: int = 160) -> str:
    normalized = (value or "").replace("\r", " ").replace("\n", " ").strip()
    if len(normalized) <= max_length:
        return normalized
    return normalized[:max_length]


class DutyDiagnosticsLogger:
    def __init__(self, log_dir: Path):
        self._log_dir = Path(log_dir).resolve()
        self._log_dir.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()
        self._last_prune_date = datetime.now().date()
        self._prune_expired_logs()

    def info(self, scope: str, message: str, *, trace_id: str | None = None, request_source: str | None = None, **data: Any) -> None:
        self._write("INFO", scope, message, trace_id=trace_id, request_source=request_source, data=data)

    def warn(self, scope: str, message: str, *, trace_id: str | None = None, request_source: str | None = None, **data: Any) -> None:
        self._write("WARN", scope, message, trace_id=trace_id, request_source=request_source, data=data)

    def error(
        self,
        scope: str,
        message: str,
        *,
        trace_id: str | None = None,
        request_source: str | None = None,
        exc: BaseException | None = None,
        **data: Any,
    ) -> None:
        if exc is not None:
            data = dict(data)
            data["exception_type"] = type(exc).__name__
            data["exception_message"] = truncate_for_log(str(exc), 300)
            data["traceback"] = truncate_for_log(" | ".join(traceback.format_exception(type(exc), exc, exc.__traceback__)), 2000)
        self._write("ERROR", scope, message, trace_id=trace_id, request_source=request_source, data=data)

    def _write(self, level: str, scope: str, message: str, *, trace_id: str | None, request_source: str | None, data: dict[str, Any]) -> None:
        payload = {
            "ts": datetime.now().isoformat(timespec="milliseconds"),
            "level": level,
            "pid": os.getpid(),
            "tid": threading.get_ident(),
            "scope": scope,
            "message": message,
            "trace_id": trace_id or "",
            "request_source": request_source or "",
            "data": data or {},
        }
        line = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
        # Logging must never raise into callers: the background worker loops'
        # exception handlers log too, so an OSError here (disk full / AV lock)
        # would kill the auto-run and reminder threads permanently. Degrade to
        # a rate-limited stderr note instead (stderr is capped by the host).
        try:
            with self._lock:
                today = datetime.now().date()
                if today != self._last_prune_date:
                    self._last_prune_date = today
                    self._prune_expired_logs()
                path = self._log_path()
                self._rotate_if_oversized(path)
                path.parent.mkdir(parents=True, exist_ok=True)
                # Single write call: two separate writes can interleave into
                # half-lines when a second backend process briefly coexists.
                with path.open("a", encoding="utf-8") as file:
                    file.write(line + "\n")
        except OSError as exc:
            now = time.monotonic()
            if now - getattr(self, "_last_drop_notice", 0.0) > 60.0:
                self._last_drop_notice = now
                try:
                    print(f"duty-backend: log write failed ({type(exc).__name__}); dropping log lines", file=sys.stderr)
                except Exception:
                    pass

    def _log_path(self) -> Path:
        return self._log_dir / f"{LOG_FILE_PREFIX}-{datetime.now():%Y%m%d}.log"

    def _rotate_if_oversized(self, path: Path) -> None:
        try:
            if not path.exists() or path.stat().st_size < MAX_LOG_FILE_BYTES:
                return
            os.replace(str(path), str(path) + ".1")
        except OSError:
            # Rotation is best-effort; keep appending if the rename is blocked.
            pass

    def _prune_expired_logs(self) -> None:
        cutoff = datetime.now() - timedelta(days=KEEP_DAYS)
        for file in self._log_dir.glob(f"{LOG_FILE_PREFIX}-*.log*"):
            try:
                if datetime.fromtimestamp(file.stat().st_mtime) < cutoff:
                    file.unlink()
            except OSError:
                continue
