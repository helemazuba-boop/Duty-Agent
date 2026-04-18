"""
Duty-Agent Dev Environment Orchestrator
========================================
启动后端 → 捕获 token → 写入 .env.local → 启动前端 → 打开浏览器
"""
import subprocess
import sys
import time
import os
import socket
from pathlib import Path

BACKEND_TOKEN_FILE = r"D:\projects\Duty-Agent\Assets_Duty\data\.dev-token"
FRONTEND_ENV_FILE = r"D:\projects\Duty-Agent\duty-agent-ui\.env.local"
BACKEND_PY = r"D:\projects\Duty-Agent\Assets_Duty\core.py"
PYTHON_EMBED = r"D:\projects\Duty-Agent\Assets_Duty\python-embed\python.exe"


def wait_port(host: str, port: int, timeout_sec: float = 30) -> bool:
    deadline = time.monotonic() + timeout_sec
    targets = [("::1", port), ("127.0.0.1", port)]
    while time.monotonic() < deadline:
        for h, _ in targets:
            try:
                family = socket.AF_INET6 if ":" in h else socket.AF_INET
                s = socket.socket(family, socket.SOCK_STREAM)
                s.settimeout(1)
                s.connect((h, port))
                s.close()
                return True
            except (OSError, socket.error):
                pass
        time.sleep(0.5)
    return False


def write_env_local(token: str) -> None:
    with open(FRONTEND_ENV_FILE, "w", encoding="utf-8") as f:
        f.write(f"VITE_BACKEND_TOKEN={token}\n")
    print(f"[orchestrator] .env.local written: {FRONTEND_ENV_FILE}")


def main():
    print("=" * 60)
    print("  Duty-Agent Dev Environment Startup")
    print("=" * 60)
    print()

    # 1. Start backend
    print("[1/5] python-embed check OK")
    print("[2/5] Starting backend (port 8765)...")

    backend_proc = subprocess.Popen(
        [PYTHON_EMBED, BACKEND_PY, "--server", "--port", "8765"],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        text=True,
        bufsize=1,
    )

    token = None
    port_seen = False
    deadline = time.monotonic() + 30

    while time.monotonic() < deadline:
        line = backend_proc.stdout.readline()
        if not line:
            rc = backend_proc.poll()
            if rc is not None:
                print(f"[ERROR] Backend exited early with rc={rc}")
                sys.exit(1)
            time.sleep(0.05)
            continue

        stripped = line.rstrip("\n\r")
        if stripped:
            print(f"        {stripped}")

        if stripped.startswith("__DUTY_SERVER_PORT__:"):
            port_seen = True
        if stripped.startswith("__DUTY_SERVER_TOKEN__:"):
            token = stripped.split(":", 1)[1].strip()

        if port_seen and token is not None:
            break

    # Write .env.local
    if token:
        write_env_local(token)
    else:
        print("[WARN] No token captured, .env.local not written")

    # Wait for port
    if wait_port("127.0.0.1", 8765, 30):
        print("[3/5] Backend ready: http://127.0.0.1:8765")
    else:
        print("[WARN] Backend port not ready after 30s, continuing...")

    # Keep backend alive
    print()
    print("[4/5] Starting frontend dev server...")
    frontend_proc = subprocess.Popen(
        ["cmd", "/c", "npm run dev"],
        cwd=r"D:\projects\Duty-Agent\duty-agent-ui",
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        text=True,
        bufsize=1,
    )

    if wait_port("::1", 5173, 60):
        print("[4/5] Frontend ready: http://localhost:5173")
    else:
        print("[WARN] Frontend port not ready after 60s, continuing...")

    print()
    print("=" * 60)
    print("  DONE!")
    print()
    print("  Backend:  http://127.0.0.1:8765")
    print("  Frontend: http://localhost:5173")
    print()
    print("  Press Enter to open browser, Ctrl+C to stop...")
    print("=" * 60)

    try:
        input()  # wait for Enter
    except (EOFError, KeyboardInterrupt):
        pass

    # Open browser
    subprocess.Popen(["cmd", "/c", "start", "http://localhost:5173"])

    # Wait for user Ctrl+C
    try:
        backend_proc.wait()
    except KeyboardInterrupt:
        print("\n[orchestrator] Stopping...")
        backend_proc.terminate()
        backend_proc.wait()
        frontend_proc.terminate()
        frontend_proc.wait()
        print("[orchestrator] Done.")


if __name__ == "__main__":
    main()
