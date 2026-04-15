from __future__ import annotations

import os
import re
import signal
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Optional, Tuple

_BACKEND_STARTUP_TIMEOUT_SECONDS = 15
_PORT_PATTERN = re.compile(r"__DUTY_SERVER_PORT__:(\d+)")
_TOKEN_PATTERN = re.compile(r"__DUTY_SERVER_TOKEN__:(.+)")


def _get_python_executable() -> str:
    return sys.executable


def _get_core_py_path() -> Path:
    base = Path(__file__).resolve().parent.parent
    core = base / "core.py"
    if not core.exists():
        raise FileNotFoundError(f"core.py not found at {core}")
    return core


class BackendProcess:
    def __init__(self, process: subprocess.Popen, port: int, token: Optional[str] = None):
        self.process = process
        self.port = port
        self.token = token

    def is_alive(self) -> bool:
        return self.process.poll() is None


_process: Optional[BackendProcess] = None
_lock = threading.Lock()


def start_backend(
    port: int = 0,
    data_dir: Optional[str] = None,
) -> Tuple[int, Optional[str]]:
    """
    启动 FastAPI 后端子进程，阻塞直到端口就绪。

    Args:
        port: 监听端口，0 表示随机端口
        data_dir: 可选，指定 data 目录路径

    Returns:
        (actual_port, access_token)

    Raises:
        RuntimeError: 后端启动超时或失败
    """
    global _process

    with _lock:
        if _process is not None and _process.is_alive():
            return _process.port, _process.token

        core_path = _get_core_py_path()
        python_exe = _get_python_executable()

        args = [python_exe, str(core_path), "--server", "--port", str(port)]
        if data_dir:
            args.extend(["--data-dir", str(data_dir)])

        env = dict(os.environ)

        process = subprocess.Popen(
            args,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            stdin=subprocess.DEVNULL,
            text=True,
            bufsize=1,
            env=env,
        )

        port_found = False
        actual_port = port if port != 0 else 0
        access_token: Optional[str] = None
        deadline = time.monotonic() + _BACKEND_STARTUP_TIMEOUT_SECONDS

        while time.monotonic() < deadline:
            line = process.stdout.readline()
            if not line:
                rc = process.poll()
                if rc is not None:
                    stderr = process.stderr.read() if process.stderr else ""
                    raise RuntimeError(
                        f"Backend process exited before exposing port. RC={rc}\nStderr: {stderr}"
                    )
                time.sleep(0.05)
                continue

            m_port = _PORT_PATTERN.match(line.strip())
            if m_port:
                actual_port = int(m_port.group(1))
                port_found = True
                _process = BackendProcess(process, actual_port)
                continue

            m_token = _TOKEN_PATTERN.match(line.strip())
            if m_token:
                access_token = m_token.group(1).strip()

            if port_found and (access_token is not None or not line.strip()):
                break

        if not port_found:
            process.terminate()
            process.wait(timeout=5)
            raise RuntimeError(
                f"Backend did not emit __DUTY_SERVER_PORT__ within "
                f"{_BACKEND_STARTUP_TIMEOUT_SECONDS}s. Check stdout."
            )

        _process = BackendProcess(process, actual_port, access_token)
        return actual_port, access_token


def stop_backend() -> None:
    """杀掉后端子进程（安全关闭）。"""
    global _process

    with _lock:
        if _process is None:
            return

        proc = _process.process
        _process = None

    try:
        proc.terminate()
        proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=3)
    except OSError:
        pass


def get_backend_info() -> Tuple[Optional[int], Optional[str]]:
    """返回当前后端端口和 token，未启动返回 (None, None)。"""
    with _lock:
        if _process is None:
            return None, None
        return _process.port, _process.token


def is_backend_running() -> bool:
    with _lock:
        if _process is None:
            return False
        return _process.is_alive()
