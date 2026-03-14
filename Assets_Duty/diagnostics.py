from __future__ import annotations

import json
import os
import threading
import traceback
from datetime import datetime, timedelta
from pathlib import Path
from typing import Any

KEEP_DAYS = 14
LOG_FILE_PREFIX = "duty-backend"


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
        with self._lock:
            self._log_path().parent.mkdir(parents=True, exist_ok=True)
            with self._log_path().open("a", encoding="utf-8") as file:
                file.write(line)
                file.write("\n")

    def _log_path(self) -> Path:
        return self._log_dir / f"{LOG_FILE_PREFIX}-{datetime.now():%Y%m%d}.log"

    def _prune_expired_logs(self) -> None:
        cutoff = datetime.now() - timedelta(days=KEEP_DAYS)
        for file in self._log_dir.glob(f"{LOG_FILE_PREFIX}-*.log"):
            try:
                if datetime.fromtimestamp(file.stat().st_mtime) < cutoff:
                    file.unlink()
            except OSError:
                continue
