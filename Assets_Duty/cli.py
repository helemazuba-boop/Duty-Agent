#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Duty-Agent CLI — external context-engineering client.

This CLI connects to an *already running* Duty-Agent backend over its loopback
HTTP API. Its headline capability is *control inversion*: instead of the backend
sending the schedule prompt to the user-configured model provider, an external
AI (the caller of this CLI) can take over the reasoning layer via a stateless
two-phase delegation:

    plan-prompt  -> backend builds the anonymized single_pass prompt + an opaque
                    resume handle. The external AI reasons over it and produces a
                    V2 INI completion in the *current mode format*.
    plan-ingest  -> the completion is fed back; the backend runs the existing
                    deterministic parse / validate / settle / persist pipeline.

All person names are anonymized to numeric IDs inside the prompt, so nothing the
external AI sees contains real identities. Only ``single_pass`` mode is supported
for delegation in v1; the four provider-backed modes remain untouched.

Conventions:
  * stdout always emits exactly one JSON object.
  * progress / diagnostics go to stderr.
  * failures exit non-zero with ``{"status": "error", ...}`` on stdout.
  * response ``api_key`` fields are redacted unless ``--show-secrets`` is set.

Dependencies: Python stdlib + httpx (both bundled with python-embed).
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path
from typing import Any, Optional

import httpx

DEFAULT_PORT = 8765
DEFAULT_HOST = "127.0.0.1"
DEFAULT_REQUEST_SOURCE = "cli"
DEFAULT_TIMEOUT_SECONDS = 120.0
META_FILE_NAME = ".duty-agent-meta.json"
PID_FILE_NAME = ".duty-agent-serve.pid"
SERVE_LOG_NAME = "duty-serve.log"
DEV_TOKEN_NAME = ".dev-token"
SERVE_HEALTH_TIMEOUT_SECONDS = 30.0


# --------------------------------------------------------------------------- #
# Output helpers
# --------------------------------------------------------------------------- #
def _log(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def _redact_api_keys(value: Any) -> Any:
    """Mirror mcp_loopback._redact_api_keys: blank any ``api_key`` field."""
    if isinstance(value, dict):
        return {
            key: ("" if str(key) == "api_key" else _redact_api_keys(item))
            for key, item in value.items()
        }
    if isinstance(value, list):
        return [_redact_api_keys(item) for item in value]
    return value


def emit(payload: Any, *, pretty: bool, show_secrets: bool, exit_code: int = 0, out_path: str = "") -> int:
    body = payload if show_secrets else _redact_api_keys(payload)
    if pretty:
        text = json.dumps(body, ensure_ascii=False, indent=2)
    else:
        text = json.dumps(body, ensure_ascii=False)
    print(text, flush=True)
    if out_path:
        # Write a UTF-8 (no-BOM) copy so downstream file readers are not tripped
        # up by PowerShell's '>' redirect, which writes UTF-16 and breaks tools
        # that expect UTF-8 (e.g. opencode's read tool).
        try:
            Path(out_path).expanduser().write_text(text, encoding="utf-8")
        except OSError as ex:
            _log(f"[cli] warning: could not write --out {out_path}: {ex}")
    return exit_code


def emit_error(message: str, *, pretty: bool, show_secrets: bool, extra: Optional[dict] = None, out_path: str = "") -> int:
    payload = {"status": "error", "message": str(message)}
    if extra:
        payload.update(extra)
    return emit(payload, pretty=pretty, show_secrets=show_secrets, exit_code=1, out_path=out_path)


# --------------------------------------------------------------------------- #
# Value loading (@file / - stdin / literal)
# --------------------------------------------------------------------------- #
def read_value(raw: Optional[str]) -> str:
    if raw is None:
        return ""
    if raw == "-":
        return sys.stdin.read()
    if raw.startswith("@"):
        return Path(raw[1:]).expanduser().read_text(encoding="utf-8-sig")
    return raw


def _value_from(args: argparse.Namespace, base: str) -> str:
    """Resolve a text argument, preferring a ``--<base>-file PATH`` companion.

    The ``@file`` convention on the primary flag collides with PowerShell's ``@``
    splatting operator, which makes ``--completion @x.ini`` fail for PowerShell /
    opencode callers on Windows. The ``--<base>-file`` companion takes a plain
    path (no ``@``) and reads it BOM-safely, sidestepping that collision."""
    file_arg = str(getattr(args, f"{base}_file", "") or "").strip()
    if file_arg:
        return Path(file_arg).expanduser().read_text(encoding="utf-8-sig")
    return read_value(getattr(args, base, None))


def _parse_json_text(text: str, *, field: str) -> Any:
    text = str(text or "").strip()
    if not text:
        return {}
    try:
        return json.loads(text)
    except json.JSONDecodeError as ex:
        raise ValueError(f"--{field} is not valid JSON: {ex}") from ex


def read_json_value(raw: Optional[str], *, field: str) -> Any:
    return _parse_json_text(read_value(raw), field=field)


# --------------------------------------------------------------------------- #
# Connection resolution
# --------------------------------------------------------------------------- #
class Connection:
    def __init__(self, base_url: str, token: Optional[str], source: str):
        self.base_url = base_url.rstrip("/")
        self.token = (token or "").strip() or None
        self.source = source

    def headers(self, trace_id: Optional[str]) -> dict:
        headers = {"X-Duty-Request-Source": self.source}
        if self.token:
            headers["Authorization"] = f"Bearer {self.token}"
        if trace_id:
            headers["X-Duty-Trace-Id"] = trace_id
        return headers


def _candidate_meta_paths(explicit: Optional[str]) -> list[Path]:
    candidates: list[Path] = []
    if explicit:
        candidates.append(Path(explicit).expanduser())
    env_meta = os.environ.get("DUTY_AGENT_META", "").strip()
    if env_meta:
        candidates.append(Path(env_meta).expanduser())
    appdata = os.environ.get("APPDATA", "").strip()
    localappdata = os.environ.get("LOCALAPPDATA", "").strip()
    for base in (appdata, localappdata):
        if base:
            candidates.append(Path(base) / "ClassIsland" / "DutyAgentBridge" / META_FILE_NAME)
    if localappdata:
        candidates.append(Path(localappdata) / "DutyAgent" / "data" / META_FILE_NAME)
    return candidates


def _load_meta(explicit: Optional[str]) -> Optional[dict]:
    for path in _candidate_meta_paths(explicit):
        try:
            if path.is_file():
                meta = json.loads(path.read_text(encoding="utf-8"))
                if isinstance(meta, dict) and int(meta.get("port", 0) or 0) > 0:
                    _log(f"[cli] discovered backend meta: {path}")
                    return meta
        except (OSError, ValueError):
            continue
    return None


def _default_data_dir() -> Path:
    # Alongside this file: Assets_Duty/data (matches dev orchestrator layout).
    return (Path(__file__).resolve().parent / "data")


def resolve_connection(args: argparse.Namespace) -> Connection:
    source = str(getattr(args, "request_source", "") or "").strip() or DEFAULT_REQUEST_SOURCE

    # 1) Explicit flags win.
    base_url = str(getattr(args, "base_url", "") or "").strip()
    token = str(getattr(args, "token", "") or "").strip()

    # 2) Environment.
    if not base_url:
        base_url = os.environ.get("DUTY_AGENT_BASE_URL", "").strip()
    if not token:
        token = os.environ.get("DUTY_AGENT_TOKEN", "").strip()

    # 3) Auto-discovered meta file.
    if not base_url or not token:
        meta = _load_meta(getattr(args, "meta_file", None))
        if meta is not None:
            if not base_url:
                port = int(meta.get("port", 0) or 0)
                if port > 0:
                    base_url = f"http://{DEFAULT_HOST}:{port}"
            if not token:
                token = str(meta.get("token", "") or "").strip()

    # 4) data-dir + port fallback (dev flow: run_dev.bat writes data/.dev-token).
    port = int(getattr(args, "port", 0) or 0)
    if not base_url:
        base_url = f"http://{DEFAULT_HOST}:{port or DEFAULT_PORT}"
    if not token:
        data_dir = getattr(args, "data_dir", None)
        data_path = Path(data_dir).expanduser() if data_dir else _default_data_dir()
        token_file = data_path / ".dev-token"
        try:
            if token_file.is_file():
                token = token_file.read_text(encoding="utf-8").strip()
        except OSError:
            token = ""

    return Connection(base_url, token, source)


# --------------------------------------------------------------------------- #
# HTTP
# --------------------------------------------------------------------------- #
def _extract_detail(response: httpx.Response) -> str:
    try:
        data = response.json()
    except ValueError:
        return response.text.strip() or f"HTTP {response.status_code}"
    if isinstance(data, dict):
        return str(data.get("detail") or data.get("message") or data)
    return str(data)


def request(
    conn: Connection,
    method: str,
    path: str,
    *,
    trace_id: Optional[str],
    json_body: Optional[dict] = None,
    timeout: float = DEFAULT_TIMEOUT_SECONDS,
) -> Any:
    url = f"{conn.base_url}{path}"
    _log(f"[cli] {method} {url}")
    try:
        # trust_env=False: this CLI only ever talks to a loopback backend, so we
        # must NOT honor a system/registry HTTP proxy. On Windows a machine-wide
        # proxy that does not bypass 127.0.0.1 otherwise turns every request into
        # a 502, making the CLI completely unusable.
        response = httpx.request(
            method,
            url,
            headers=conn.headers(trace_id),
            json=json_body,
            timeout=timeout,
            trust_env=False,
        )
    except httpx.HTTPError as ex:
        raise RuntimeError(f"Request to {url} failed: {ex}") from ex

    if response.status_code >= 400:
        raise RuntimeError(f"{method} {path} -> HTTP {response.status_code}: {_extract_detail(response)}")

    if not response.content:
        return {}
    try:
        return response.json()
    except ValueError:
        return {"raw": response.text}


# --------------------------------------------------------------------------- #
# Command handlers
# --------------------------------------------------------------------------- #
def _slim_plan_prompt(result: dict) -> dict:
    """Reduce the AI-facing plan-prompt payload to cut token waste.

    Drops the near-duplicate ``messages`` (``prompt_text`` carries the same
    content), the top-level ``prompt_metadata`` (identical to
    ``resume_context.prompt_metadata``), and the verbose, downstream-cosmetic
    ``execution_plan_meta.tasks`` inside the handle. The handle stays valid for
    plan-ingest (only ``start_date`` is required to settle; the rest is surfaced
    cosmetically)."""
    slim = dict(result)
    slim.pop("messages", None)
    slim.pop("prompt_metadata", None)
    rc = slim.get("resume_context")
    if isinstance(rc, dict):
        rc = dict(rc)
        epm = rc.get("execution_plan_meta")
        if isinstance(epm, dict):
            rc["execution_plan_meta"] = {k: v for k, v in epm.items() if k != "tasks"}
        slim["resume_context"] = rc
    return slim


def cmd_plan_prompt(conn: Connection, args: argparse.Namespace) -> Any:
    body = {"instruction": _value_from(args, "instruction").strip()}
    result = request(conn, "POST", "/api/v1/duty/plan-prompt", trace_id=args.trace_id, json_body=body, timeout=args.timeout)
    if isinstance(result, dict) and result.get("status") != "error":
        result = _slim_plan_prompt(result)
        handle_out = str(getattr(args, "handle_out", "") or "").strip()
        if handle_out:
            # Write the opaque handle straight to a UTF-8 file so the caller can
            # feed it to 'plan-ingest --handle-file' without hand-extracting
            # resume_context (no ConvertFrom-Json dance, no UTF-16).
            try:
                Path(handle_out).expanduser().write_text(
                    json.dumps(result.get("resume_context", {}), ensure_ascii=False),
                    encoding="utf-8",
                )
                result["handle_file"] = str(Path(handle_out).expanduser())
            except OSError as ex:
                _log(f"[cli] warning: could not write --handle-out {handle_out}: {ex}")
    return result


def cmd_plan_ingest(conn: Connection, args: argparse.Namespace) -> Any:
    completion = _value_from(args, "completion")
    resume_context = _parse_json_text(_value_from(args, "handle"), field="handle")
    if not isinstance(resume_context, dict):
        raise ValueError("--handle must be a JSON object (the resume_context from plan-prompt)")
    body = {"completion": completion, "resume_context": resume_context}
    return request(conn, "POST", "/api/v1/duty/plan-ingest", trace_id=args.trace_id, json_body=body, timeout=args.timeout)


def cmd_inspect(conn: Connection, args: argparse.Namespace) -> Any:
    engine = request(conn, "GET", "/engine/info", trace_id=args.trace_id, timeout=args.timeout)
    snapshot = request(conn, "GET", "/api/v1/snapshot", trace_id=args.trace_id, timeout=args.timeout)
    return {"status": "success", "engine": engine, "snapshot": snapshot}


def cmd_get_config(conn: Connection, args: argparse.Namespace) -> Any:
    return request(conn, "GET", "/api/v1/config", trace_id=args.trace_id, timeout=args.timeout)


def cmd_update_config(conn: Connection, args: argparse.Namespace) -> Any:
    patch = _parse_json_text(_value_from(args, "patch"), field="patch")
    if not isinstance(patch, dict):
        raise ValueError("--patch must be a JSON object")
    return request(conn, "PATCH", "/api/v1/config", trace_id=args.trace_id, json_body=patch, timeout=args.timeout)


def cmd_get_roster(conn: Connection, args: argparse.Namespace) -> Any:
    return request(conn, "GET", "/api/v1/roster", trace_id=args.trace_id, timeout=args.timeout)


def cmd_replace_roster(conn: Connection, args: argparse.Namespace) -> Any:
    roster = _parse_json_text(_value_from(args, "roster"), field="roster")
    if isinstance(roster, list):
        body = {"roster": roster}
    elif isinstance(roster, dict) and "roster" in roster:
        body = {"roster": roster["roster"]}
    else:
        raise ValueError("--roster must be a JSON array or an object with a 'roster' array")
    return request(conn, "PUT", "/api/v1/roster", trace_id=args.trace_id, json_body=body, timeout=args.timeout)


def cmd_edit_entry(conn: Connection, args: argparse.Namespace) -> Any:
    entry = _parse_json_text(_value_from(args, "entry"), field="entry")
    if not isinstance(entry, dict):
        raise ValueError("--entry must be a JSON object")
    return request(conn, "POST", "/api/v1/duty/schedule-entry", trace_id=args.trace_id, json_body=entry, timeout=args.timeout)


def cmd_rollback(conn: Connection, args: argparse.Namespace) -> Any:
    return request(conn, "POST", "/api/v1/duty/schedule-rollback", trace_id=args.trace_id, timeout=args.timeout)


def cmd_health(conn: Connection, args: argparse.Namespace) -> Any:
    return request(conn, "GET", "/health", trace_id=args.trace_id, timeout=args.timeout)


def cmd_run(conn: Connection, args: argparse.Namespace) -> Any:
    """Provider-backed schedule run: trigger the backend to call its configured
    model and settle the result. Consumes the ``/schedule`` SSE stream and
    returns the final ``event: complete`` payload."""
    instruction = _value_from(args, "instruction").strip()
    body = {"instruction": instruction, "request_source": conn.source}
    url = f"{conn.base_url}/api/v1/duty/schedule"
    # Provider runs invoke a real model (a local reasoning model can be slow), so
    # floor the SSE read timeout well above the default to avoid a premature
    # ReadTimeout mid-generation.
    run_timeout = max(float(getattr(args, "timeout", 0) or 0), 300.0)
    _log(f"[cli] POST {url} (SSE, timeout={run_timeout:.0f}s)")
    final: Optional[dict] = None
    last_data: Optional[dict] = None
    try:
        with httpx.stream(
            "POST", url,
            headers=conn.headers(args.trace_id),
            json=body,
            timeout=run_timeout,
            trust_env=False,
        ) as resp:
            if resp.status_code >= 400:
                resp.read()
                raise RuntimeError(f"POST /api/v1/duty/schedule -> HTTP {resp.status_code}: {_extract_detail(resp)}")
            event = None
            for line in resp.iter_lines():
                if not line:
                    continue
                if line.startswith("event:"):
                    event = line[len("event:"):].strip()
                elif line.startswith("data:"):
                    raw = line[len("data:"):].strip()
                    try:
                        parsed = json.loads(raw)
                    except ValueError:
                        continue
                    if isinstance(parsed, dict):
                        last_data = parsed
                        phase = parsed.get("phase") or parsed.get("message")
                        if phase:
                            _log(f"[cli] progress: {phase}")
                    if event == "complete":
                        final = parsed if isinstance(parsed, dict) else final
    except httpx.HTTPError as ex:
        raise RuntimeError(f"Request to {url} failed: {ex}") from ex
    result = final if final is not None else last_data
    if result is None:
        return {"status": "error", "message": "Schedule stream ended without a completion event."}
    return result


# --------------------------------------------------------------------------- #
# Backend lifecycle management (serve / status)
# --------------------------------------------------------------------------- #
def _core_py_path() -> Path:
    return Path(__file__).resolve().parent / "core.py"


def _resolve_data_dir(args: argparse.Namespace) -> Path:
    raw = str(getattr(args, "data_dir", "") or "").strip()
    return Path(raw).expanduser() if raw else _default_data_dir()


def _pid_file_for(data_dir: Path) -> Path:
    return data_dir / PID_FILE_NAME


def _read_pid(path: Path) -> Optional[int]:
    try:
        text = path.read_text(encoding="utf-8").strip()
        return int(text) if text else None
    except (OSError, ValueError):
        return None


def _write_pid(path: Path, pid: int) -> None:
    try:
        path.write_text(str(int(pid)), encoding="utf-8")
    except OSError as ex:
        _log(f"[cli] warning: could not write pid file {path}: {ex}")


def _remove_pid_file(path: Path) -> None:
    try:
        if path.exists():
            path.unlink()
    except OSError:
        pass


def _pid_alive(pid: Optional[int]) -> bool:
    if not pid or pid <= 0:
        return False
    if os.name == "nt":
        import ctypes

        PROCESS_QUERY_LIMITED = 0x1000
        handle = ctypes.windll.kernel32.OpenProcess(PROCESS_QUERY_LIMITED, False, int(pid))
        if not handle:
            return False
        ctypes.windll.kernel32.CloseHandle(handle)
        return True
    try:
        os.kill(int(pid), 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return False
    return True


def _terminate_pid(pid: int, timeout: float = 5.0) -> bool:
    """Best-effort terminate a managed backend by pid. Returns True if the
    process is gone afterwards (polls until exit or timeout)."""
    if os.name == "nt":
        try:
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True, timeout=timeout)
        except (OSError, subprocess.SubprocessError) as ex:
            _log(f"[cli] taskkill failed for pid {pid}: {ex}")
    else:
        try:
            proc = subprocess.Popen(["kill", str(pid)])
            proc.wait(timeout=timeout)
        except (OSError, subprocess.SubprocessError):
            pass
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if not _pid_alive(pid):
            return True
        time.sleep(0.2)
    return not _pid_alive(pid)


def _health_ok(conn: Connection, timeout: float) -> bool:
    try:
        request(conn, "GET", "/health", trace_id=None, timeout=timeout)
        return True
    except (RuntimeError, OSError, ValueError):
        return False


def _wait_health(base_url: str, source: str, timeout: float) -> bool:
    conn = Connection(base_url, None, source)
    deadline = time.monotonic() + max(1.0, timeout)
    while time.monotonic() < deadline:
        if _health_ok(conn, 2.0):
            return True
        time.sleep(0.5)
    return False


def _effective_serve_base(args: argparse.Namespace, data_dir: Path) -> tuple[str, int]:
    """Base URL + port for lifecycle commands. ``--port`` (when > 0) is
    authoritative; otherwise fall back to an explicit ``--base-url`` / env /
    discovered meta, else the default port."""
    port = int(getattr(args, "port", 0) or 0)
    if port > 0:
        return f"http://{DEFAULT_HOST}:{port}", port
    explicit = str(getattr(args, "base_url", "") or "").strip()
    if explicit:
        return explicit.rstrip("/"), port
    env_url = os.environ.get("DUTY_AGENT_BASE_URL", "").strip()
    if env_url:
        return env_url.rstrip("/"), port
    meta = _load_meta(getattr(args, "meta_file", None))
    if meta is not None and int(meta.get("port", 0) or 0) > 0:
        meta_port = int(meta.get("port", 0) or 0)
        return f"http://{DEFAULT_HOST}:{meta_port}", meta_port
    return f"http://{DEFAULT_HOST}:{DEFAULT_PORT}", DEFAULT_PORT


def _stop_managed(data_dir: Path) -> dict:
    pid_path = _pid_file_for(data_dir)
    pid = _read_pid(pid_path)
    if pid is None:
        return {"status": "success", "stopped": False, "reason": "no managed pid file"}
    if not _pid_alive(pid):
        _remove_pid_file(pid_path)
        return {"status": "success", "stopped": False, "reason": "stale pid file; process not running", "pid": pid}
    gone = _terminate_pid(pid)
    _remove_pid_file(pid_path)
    return {"status": "success", "stopped": gone, "pid": pid}


def cmd_serve(conn: Connection, args: argparse.Namespace) -> Any:
    data_dir = _resolve_data_dir(args)
    data_dir.mkdir(parents=True, exist_ok=True)

    if getattr(args, "stop", False):
        return _stop_managed(data_dir)

    base_url, port = _effective_serve_base(args, data_dir)
    source = str(getattr(args, "request_source", "") or "").strip() or DEFAULT_REQUEST_SOURCE
    probe = Connection(base_url, None, source)

    already = _health_ok(probe, 2.0)
    if already and not getattr(args, "force", False):
        return {
            "status": "success",
            "already_running": True,
            "base_url": base_url,
            "pid": _read_pid(_pid_file_for(data_dir)),
        }
    if already and getattr(args, "force", False):
        stop_result = _stop_managed(data_dir)
        if not stop_result.get("stopped"):
            return {
                "status": "error",
                "message": (
                    f"A backend is already running at {base_url} but is not managed by "
                    "duty-cli (no pid file). Stop it manually, then re-run 'duty-cli serve'."
                ),
            }
        _log(f"[cli] stopped previous managed backend (pid={stop_result.get('pid')}); restarting.")

    if not _core_py_path().exists():
        raise RuntimeError(f"core.py not found at {_core_py_path()}")

    serve_port = port if port > 0 else DEFAULT_PORT
    log_path = data_dir / SERVE_LOG_NAME
    creationflags = 0
    if os.name == "nt":
        creationflags = getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
    log_handle = open(log_path, "ab")
    # Ensure the managed backend never routes loopback model servers (e.g. a
    # local LM Studio / Ollama at 127.0.0.1) through a system HTTP proxy, which
    # would turn provider calls into 502s. NO_PROXY is honored by urllib.
    child_env = dict(os.environ)
    existing_no_proxy = str(child_env.get("NO_PROXY", "") or "").strip()
    loopback = "localhost,127.0.0.1,::1"
    child_env["NO_PROXY"] = f"{existing_no_proxy},{loopback}" if existing_no_proxy else loopback
    child_env["no_proxy"] = child_env["NO_PROXY"]
    try:
        proc = subprocess.Popen(
            [
                sys.executable,
                str(_core_py_path()),
                "--server",
                "--port",
                str(serve_port),
                "--data-dir",
                str(data_dir),
                "--no-parent-watch",
            ],
            stdout=log_handle,
            stderr=subprocess.STDOUT,
            stdin=subprocess.DEVNULL,
            creationflags=creationflags,
            close_fds=True,
            env=child_env,
        )
    finally:
        log_handle.close()

    timeout = float(getattr(args, "timeout", 0) or SERVE_HEALTH_TIMEOUT_SECONDS)
    if not _wait_health(base_url, source, timeout):
        _terminate_pid(proc.pid)
        raise RuntimeError(
            f"Backend did not become healthy at {base_url} within {timeout:.0f}s. See log: {log_path}"
        )

    _write_pid(_pid_file_for(data_dir), proc.pid)

    token = ""
    token_file = data_dir / DEV_TOKEN_NAME
    try:
        if token_file.is_file():
            token = token_file.read_text(encoding="utf-8").strip()
    except OSError:
        token = ""

    try:
        (data_dir / META_FILE_NAME).write_text(
            json.dumps({"port": serve_port, "token": token}, ensure_ascii=False),
            encoding="utf-8",
        )
    except OSError as ex:
        _log(f"[cli] warning: could not write meta file: {ex}")

    return {
        "status": "success",
        "started": True,
        "pid": proc.pid,
        "port": serve_port,
        "base_url": base_url,
        "token_present": bool(token),
        "log_file": str(log_path),
    }


def cmd_status(conn: Connection, args: argparse.Namespace) -> Any:
    data_dir = _resolve_data_dir(args)
    base_url, _port = _effective_serve_base(args, data_dir)
    source = str(getattr(args, "request_source", "") or "").strip() or DEFAULT_REQUEST_SOURCE
    probe = Connection(base_url, None, source)

    running = _health_ok(probe, 2.0)
    pid = _read_pid(_pid_file_for(data_dir))
    managed = pid is not None and _pid_alive(pid)

    token = ""
    token_file = data_dir / DEV_TOKEN_NAME
    try:
        if token_file.is_file():
            token = token_file.read_text(encoding="utf-8").strip()
    except OSError:
        token = ""

    result: dict = {
        "status": "success",
        "running": running,
        "base_url": base_url,
        "token_present": bool(token),
        "managed": managed,
        "managed_pid": pid if managed else None,
    }
    if running:
        try:
            info = request(probe, "GET", "/engine/info", trace_id=None, timeout=5.0)
            if isinstance(info, dict):
                if info.get("version"):
                    result["version"] = info.get("version")
                if info.get("orchestration_mode"):
                    result["orchestration_mode"] = info.get("orchestration_mode")
        except (RuntimeError, OSError, ValueError):
            pass
    return result


def cmd_doctor(conn: Connection, args: argparse.Namespace) -> Any:
    """First-run health check: ensure the backend is up (bootstrapping it if
    needed), then return the readiness report with a real model probe. One
    command answers 'can I use this, and if not, what do I fix'."""
    data_dir = _resolve_data_dir(args)
    base_url, _port = _effective_serve_base(args, data_dir)
    source = str(getattr(args, "request_source", "") or "").strip() or DEFAULT_REQUEST_SOURCE
    started_info = None
    if not _health_ok(Connection(base_url, None, source), 2.0):
        started_info = cmd_serve(conn, args)
        if isinstance(started_info, dict) and started_info.get("status") == "error":
            return started_info
    # Re-resolve so a freshly written .dev-token is picked up.
    fresh = resolve_connection(args)
    result = request(fresh, "GET", "/api/v1/readiness?probe_model=true", trace_id=args.trace_id, timeout=args.timeout)
    if isinstance(result, dict) and isinstance(started_info, dict) and started_info.get("started"):
        result["backend_started"] = True
    return result


# --------------------------------------------------------------------------- #
# Self-description (no network)
# --------------------------------------------------------------------------- #
COMMAND_SPECS: list[dict] = [
    {
        "name": "plan-prompt",
        "summary": "Phase 1 of external-AI delegation: build the anonymized single_pass "
                   "prompt and an opaque resume_context handle. single_pass mode only.",
        "method": "POST",
        "endpoint": "/api/v1/duty/plan-prompt",
        "args": {
            "--instruction": "Scheduling instruction text; literal, @file, or - for stdin.",
            "--instruction-file": "Path to instruction text (no @; PowerShell/opencode-safe).",
            "--handle-out": "Write ONLY the resume_context handle to this UTF-8 path; feed it to 'plan-ingest --handle-file' (skips manual extraction).",
        },
        "returns": "{status, mode, prompt_text, resume_context, trace_id} (slim: messages and duplicate prompt_metadata omitted; pass resume_context back verbatim)",
        "notes": "If the resolved mode is not single_pass, returns {status:error, mode}. Prefer '--out FILE --handle-out FILE' over PowerShell '>' redirection (which writes UTF-16).",
    },
    {
        "name": "plan-ingest",
        "summary": "Phase 2 of external-AI delegation: feed the external AI's V2 INI "
                   "completion back to run parse/validate/settle/persist.",
        "method": "POST",
        "endpoint": "/api/v1/duty/plan-ingest",
        "args": {
            "--completion": "The V2 INI completion text; literal, @file, or - for stdin.",
            "--completion-file": "Path to the V2 INI completion (no @; PowerShell/opencode-safe). Preferred.",
            "--handle": "The resume_context JSON from plan-prompt; literal, @file, or -.",
            "--handle-file": "Path to the resume_context JSON (no @; PowerShell/opencode-safe). Preferred.",
        },
        "returns": "{status, ai_response, snapshot, execution_plan, ...}",
    },
    {
        "name": "inspect",
        "summary": "Read engine/info + snapshot (config, roster, state) in one call.",
        "method": "GET",
        "endpoint": "/engine/info + /api/v1/snapshot",
        "args": {},
        "returns": "{status, engine, snapshot}",
    },
    {
        "name": "get-config",
        "summary": "Read the backend configuration.",
        "method": "GET",
        "endpoint": "/api/v1/config",
        "args": {},
    },
    {
        "name": "update-config",
        "summary": "Patch the backend configuration (optimistic version via expected_version).",
        "method": "PATCH",
        "endpoint": "/api/v1/config",
        "args": {"--patch": "JSON object of fields to patch; literal, @file, or -. Required."},
    },
    {
        "name": "get-roster",
        "summary": "Read the roster.",
        "method": "GET",
        "endpoint": "/api/v1/roster",
        "args": {},
    },
    {
        "name": "replace-roster",
        "summary": "Replace the full roster.",
        "method": "PUT",
        "endpoint": "/api/v1/roster",
        "args": {"--roster": "JSON array of roster entries (or {roster:[...]}); literal, @file, or -. Required."},
    },
    {
        "name": "edit-entry",
        "summary": "Create or overwrite a single schedule entry (ledger-aware).",
        "method": "POST",
        "endpoint": "/api/v1/duty/schedule-entry",
        "args": {"--entry": "JSON object matching DutyScheduleEntrySaveRequest; literal, @file, or -. Required."},
    },
    {
        "name": "rollback",
        "summary": "Roll back to the previous persisted state.",
        "method": "POST",
        "endpoint": "/api/v1/duty/schedule-rollback",
        "args": {},
    },
    {
        "name": "health",
        "summary": "Backend health probe (unauthenticated endpoint).",
        "method": "GET",
        "endpoint": "/health",
        "args": {},
    },
    {
        "name": "run",
        "summary": "Provider-backed schedule run: the backend calls its configured "
                   "model provider and settles the result. Use this when you want "
                   "Duty-Agent (not an external AI) to do the reasoning.",
        "method": "POST",
        "endpoint": "/api/v1/duty/schedule (SSE)",
        "args": {
            "--instruction": "Scheduling instruction; literal, @file, or - for stdin.",
            "--instruction-file": "Path to instruction text (no @ needed; PowerShell-safe).",
        },
        "returns": "The final completion event: {status, ai_response, snapshot, ...}",
        "notes": "Requires the selected plan's base_url/model to point at a reachable provider.",
    },
    {
        "name": "serve",
        "summary": "Idempotently start the backend detached in the background and "
                   "manage it via a pid file. If a healthy backend is already "
                   "reachable it is reused. Use --stop to stop the managed backend, "
                   "--force to stop a managed backend and restart it.",
        "method": "-",
        "endpoint": "spawns core.py --server --no-parent-watch",
        "args": {
            "--stop": "Stop the backend managed by duty-cli (via pid file) and exit.",
            "--force": "Stop the managed backend (if any) and start a fresh one.",
            "--port": "Port to serve/probe on (default 8765).",
            "--data-dir": "Data directory for pid/meta/token/log files (default Assets_Duty/data).",
        },
        "returns": "{status, started|already_running|stopped, pid, port, base_url, token_present, log_file}",
        "notes": "Writes <data-dir>/.duty-agent-serve.pid and .duty-agent-meta.json so later "
                 "commands auto-discover the backend.",
    },
    {
        "name": "status",
        "summary": "Probe backend liveness and report discovery info (base_url, "
                   "token presence, managed pid, version). Never errors on an "
                   "unreachable backend; reports running:false instead.",
        "method": "GET",
        "endpoint": "/health + /engine/info",
        "args": {
            "--port": "Port to probe (default 8765).",
            "--data-dir": "Data directory holding the pid/token files.",
        },
        "returns": "{status, running, base_url, token_present, managed, managed_pid, version?}",
    },
    {
        "name": "doctor",
        "summary": "First-run health check: bootstrap the backend if needed, then "
                   "report readiness with a real model probe. Run this first on a "
                   "fresh setup; each failing check carries a 'fix' string.",
        "method": "GET",
        "endpoint": "serve (if needed) + /api/v1/readiness?probe_model=true",
        "args": {
            "--port": "Port to serve/probe on (default 8765).",
            "--data-dir": "Data directory for backend files.",
        },
        "returns": "{ready, checks:[{id,ok,detail,fix}], next_steps, backend_started?}",
    },
    {
        "name": "describe",
        "summary": "Emit this machine-readable command catalog (no network).",
        "method": "-",
        "endpoint": "-",
        "args": {},
    },
]


def cmd_describe(_conn: Optional[Connection], _args: argparse.Namespace) -> Any:
    return {
        "status": "success",
        "tool": "duty-agent-cli",
        "delegation": {
            "supported_modes": ["single_pass"],
            "flow": ["plan-prompt", "external AI produces V2 INI completion", "plan-ingest"],
            "anonymization": "prompts contain only numeric IDs; names never leave the backend",
        },
        "first_run": {
            "summary": "Recommended order on a fresh setup.",
            "steps": [
                "doctor  (bootstraps backend + reports readiness; follow each check's 'fix')",
                "replace-roster --roster-file roster.json  (only if the roster check fails)",
                "--out prompt.json plan-prompt --instruction '...' --handle-out handle.json",
                "plan-ingest --completion-file completion.ini --handle-file handle.json",
            ],
            "provider_alternative": "run --instruction '...'  (backend's own model does the reasoning)",
        },
        "global_flags": {
            "--base-url": "Backend base URL; else env DUTY_AGENT_BASE_URL; else meta file; else http://127.0.0.1:<port>.",
            "--token": "Bearer token; else env DUTY_AGENT_TOKEN; else meta file token; else <data-dir>/.dev-token.",
            "--port": f"Port for the base-url fallback (default {DEFAULT_PORT}). Must precede the subcommand.",
            "--data-dir": "Directory holding .dev-token for the token fallback. Must precede the subcommand.",
            "--meta-file": "Explicit path to .duty-agent-meta.json for discovery.",
            "--trace-id": "Optional X-Duty-Trace-Id to correlate logs.",
            "--request-source": f"X-Duty-Request-Source value (default {DEFAULT_REQUEST_SOURCE}).",
            "--timeout": f"HTTP timeout seconds (default {DEFAULT_TIMEOUT_SECONDS}).",
            "--pretty": "Pretty-print the stdout JSON.",
            "--show-secrets": "Do not redact api_key fields in output.",
            "--out": "Also write the stdout JSON to this path as UTF-8 (use instead of PowerShell '>' which writes UTF-16 that some read tools cannot parse).",
        },
        "conventions": {
            "flag_order": "Global flags must precede the subcommand (e.g. 'duty-cli --port 8811 plan-prompt ...').",
            "file_args": "For file-valued args prefer the '--<name>-file PATH' variant over '@PATH': the '@' form collides with PowerShell's splatting operator.",
        },
        "commands": COMMAND_SPECS,
    }


HANDLERS = {
    "plan-prompt": cmd_plan_prompt,
    "plan-ingest": cmd_plan_ingest,
    "inspect": cmd_inspect,
    "get-config": cmd_get_config,
    "update-config": cmd_update_config,
    "get-roster": cmd_get_roster,
    "replace-roster": cmd_replace_roster,
    "edit-entry": cmd_edit_entry,
    "rollback": cmd_rollback,
    "health": cmd_health,
    "run": cmd_run,
    "serve": cmd_serve,
    "status": cmd_status,
    "doctor": cmd_doctor,
    "describe": cmd_describe,
}


# --------------------------------------------------------------------------- #
# Argument parsing
# --------------------------------------------------------------------------- #
class _JsonErrorParser(argparse.ArgumentParser):
    """ArgumentParser that honors the CLI's stdout contract on usage errors.

    argparse's default ``error()`` prints plain text to stderr and exits 2,
    which would break the "stdout is always one JSON object" promise that AI
    callers rely on. Here we emit ``{"status": "error", ...}`` on stdout with a
    hint pointing at the machine-readable ``describe`` catalog, then exit 2.
    """

    def error(self, message: str):  # noqa: D401 - argparse hook
        payload = {
            "status": "error",
            "message": f"{self.prog}: {message}",
            "hint": (
                "Run 'duty-cli describe' for the machine-readable command catalog "
                "(JSON, no network), or 'duty-cli --help' for human-readable usage."
            ),
        }
        print(json.dumps(payload, ensure_ascii=False), flush=True)
        raise SystemExit(2)


def build_parser() -> argparse.ArgumentParser:
    parser = _JsonErrorParser(
        prog="duty-cli",
        description="Duty-Agent CLI: external context-engineering client for an already-running backend.",
        epilog=(
            "AI callers: run 'duty-cli describe' first for a machine-readable JSON "
            "catalog of every command, its arguments, and the two-phase delegation "
            "flow. Running with no command prints that same catalog."
        ),
    )
    parser.add_argument("--base-url", default="", help="Backend base URL (overrides env/meta discovery).")
    parser.add_argument("--token", default="", help="Bearer token (overrides env/meta discovery).")
    parser.add_argument("--port", type=int, default=0, help=f"Port for base-url fallback (default {DEFAULT_PORT}).")
    parser.add_argument("--data-dir", default="", help="Directory holding .dev-token for token fallback.")
    parser.add_argument("--meta-file", default="", help="Explicit path to .duty-agent-meta.json.")
    parser.add_argument("--trace-id", default="", help="Optional X-Duty-Trace-Id header value.")
    parser.add_argument("--request-source", default=DEFAULT_REQUEST_SOURCE, help="X-Duty-Request-Source header value.")
    parser.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT_SECONDS, help="HTTP timeout in seconds.")
    parser.add_argument("--pretty", action="store_true", help="Pretty-print stdout JSON.")
    parser.add_argument("--show-secrets", action="store_true", help="Do not redact api_key fields.")
    parser.add_argument("--out", default="", help="Also write the stdout JSON to this path as UTF-8 (avoids PowerShell '>' UTF-16).")

    sub = parser.add_subparsers(dest="command", required=False)

    p = sub.add_parser("plan-prompt", help="Build a single_pass prompt for external-AI delegation.")
    g = p.add_mutually_exclusive_group(required=True)
    g.add_argument("--instruction", help="Instruction text; literal, @file, or - for stdin.")
    g.add_argument("--instruction-file", help="Path to instruction text (no @ needed; PowerShell-safe).")
    p.add_argument("--handle-out", default="", help="Write only the resume_context handle to this UTF-8 path (feed to plan-ingest --handle-file).")

    p = sub.add_parser("plan-ingest", help="Ingest an external-AI completion.")
    g = p.add_mutually_exclusive_group(required=True)
    g.add_argument("--completion", help="Completion text; literal, @file, or - for stdin.")
    g.add_argument("--completion-file", help="Path to the V2 INI completion (no @ needed; PowerShell-safe).")
    g = p.add_mutually_exclusive_group(required=True)
    g.add_argument("--handle", help="resume_context JSON; literal, @file, or - for stdin.")
    g.add_argument("--handle-file", help="Path to the resume_context JSON (no @ needed; PowerShell-safe).")

    sub.add_parser("inspect", help="Read engine/info + snapshot.")
    sub.add_parser("get-config", help="Read config.")

    p = sub.add_parser("update-config", help="Patch config.")
    g = p.add_mutually_exclusive_group(required=True)
    g.add_argument("--patch", help="JSON patch object; literal, @file, or - for stdin.")
    g.add_argument("--patch-file", help="Path to the JSON patch (no @ needed; PowerShell-safe).")

    sub.add_parser("get-roster", help="Read roster.")

    p = sub.add_parser("replace-roster", help="Replace the full roster.")
    g = p.add_mutually_exclusive_group(required=True)
    g.add_argument("--roster", help="JSON roster array; literal, @file, or - for stdin.")
    g.add_argument("--roster-file", help="Path to the JSON roster (no @ needed; PowerShell-safe).")

    p = sub.add_parser("edit-entry", help="Create/overwrite one schedule entry.")
    g = p.add_mutually_exclusive_group(required=True)
    g.add_argument("--entry", help="JSON entry object; literal, @file, or - for stdin.")
    g.add_argument("--entry-file", help="Path to the JSON entry (no @ needed; PowerShell-safe).")

    sub.add_parser("rollback", help="Roll back to the previous state.")
    sub.add_parser("health", help="Backend health probe.")

    p = sub.add_parser("run", help="Provider-backed schedule run (backend calls its configured model).")
    g = p.add_mutually_exclusive_group(required=True)
    g.add_argument("--instruction", help="Instruction text; literal, @file, or - for stdin.")
    g.add_argument("--instruction-file", help="Path to instruction text (no @ needed; PowerShell-safe).")

    p = sub.add_parser("serve", help="Start/stop the backend (detached, pid-file managed).")
    p.add_argument("--stop", action="store_true", help="Stop the backend managed by duty-cli.")
    p.add_argument("--force", action="store_true", help="Stop a managed backend and restart it.")
    # SUPPRESS default: when the flag is omitted at the subcommand level, do NOT
    # write the dest, so a global-position ``--port``/``--data-dir`` survives
    # instead of being clobbered by the subparser default.
    p.add_argument("--port", type=int, default=argparse.SUPPRESS, help=f"Port to serve/probe on (default {DEFAULT_PORT}).")
    p.add_argument("--data-dir", default=argparse.SUPPRESS, help="Data directory for pid/meta/token/log files.")

    p = sub.add_parser("status", help="Probe backend liveness and discovery info.")
    p.add_argument("--port", type=int, default=argparse.SUPPRESS, help=f"Port to probe (default {DEFAULT_PORT}).")
    p.add_argument("--data-dir", default=argparse.SUPPRESS, help="Data directory holding the pid/token files.")

    p = sub.add_parser("doctor", help="First-run check: bootstrap backend + readiness report (with model probe).")
    p.add_argument("--port", type=int, default=argparse.SUPPRESS, help=f"Port to serve/probe on (default {DEFAULT_PORT}).")
    p.add_argument("--data-dir", default=argparse.SUPPRESS, help="Data directory for backend files.")

    sub.add_parser("describe", help="Emit the machine-readable command catalog (no network).")

    return parser


def main(argv: Optional[list[str]] = None) -> int:
    # Guarantee UTF-8 stdout/stderr: the payloads carry Chinese (preset/area
    # names) and are emitted with ensure_ascii=False, which would raise
    # UnicodeEncodeError on a non-UTF-8 Windows console (cp936) and crash the CLI.
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")
        except Exception:
            pass
    parser = build_parser()
    args = parser.parse_args(argv)
    pretty = bool(getattr(args, "pretty", False))
    show_secrets = bool(getattr(args, "show_secrets", False))
    out_path = str(getattr(args, "out", "") or "").strip()

    # Normalize the dashed dest names used by handlers.
    args.trace_id = str(getattr(args, "trace_id", "") or "").strip() or None

    handler = HANDLERS.get(args.command)

    # No command (bare invocation): emit the self-describing catalog so an AI's
    # very first contact yields the full machine-readable usage, no prior
    # knowledge required.
    if args.command is None:
        _log("[cli] no command given; emitting the 'describe' catalog. Use --help for usage.")
        return emit(cmd_describe(None, args), pretty=pretty, show_secrets=show_secrets, out_path=out_path)

    if handler is None:
        return emit_error(f"Unknown command: {args.command}", pretty=pretty, show_secrets=show_secrets, out_path=out_path)

    # describe needs no connection.
    if args.command == "describe":
        return emit(cmd_describe(None, args), pretty=pretty, show_secrets=show_secrets, out_path=out_path)

    try:
        conn = resolve_connection(args)
        result = handler(conn, args)
    except (ValueError, RuntimeError, OSError) as ex:
        return emit_error(str(ex), pretty=pretty, show_secrets=show_secrets, out_path=out_path)

    # Surface backend-declared errors as non-zero exit codes.
    exit_code = 1 if isinstance(result, dict) and result.get("status") == "error" else 0
    return emit(result, pretty=pretty, show_secrets=show_secrets, exit_code=exit_code, out_path=out_path)


if __name__ == "__main__":
    sys.exit(main())
