"""
Duty-Agent Dev Environment Orchestrator
========================================
启动后端 → 捕获 token → 写入 .env.local → 启动前端 → 打开浏览器

用法:
    python orchestrator.py          # 正常启动（带鉴权）
    python orchestrator.py --skip-auth  # 跳过鉴权（开发调试用）
"""
import subprocess
import sys
import threading
import time
import os
import socket
import argparse
from pathlib import Path

# Everything is derived from this file's location so the dev flow survives
# cloning the repo to any path (the old hardcoded D:\projects\... constants
# broke on every other machine).
ROOT = Path(__file__).resolve().parent
FRONTEND_ENV_FILE = str(ROOT / "duty-agent-ui" / ".env.local")
BACKEND_PY = str(ROOT / "Assets_Duty" / "core.py")
PYTHON_EMBED = str(ROOT / "Assets_Duty" / "python-embed" / "python.exe")
FRONTEND_DIR = str(ROOT / "duty-agent-ui")


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


def drain_pipe(pipe, prefix: str = "") -> None:
    """终身排空一条子进程输出管道（任务10）。

    捕获 token 之后管道仍必须持续被读：后端 stdout/stderr 是通向宿主的管道，
    写满缓冲区会把后端永久阻塞（见 engine.py 同样的约束）。随子进程退出，
    readline 返回 "" 自然结束。
    """
    try:
        for line in iter(pipe.readline, ""):
            text = line.rstrip("\r\n")
            if text:
                print(f"{prefix}{text}", flush=True)
    except (OSError, ValueError):
        # 管道随子进程退出而关闭，读端异常属于正常收尾。
        pass


def kill_process_tree(pid: int) -> None:
    """taskkill /T /F 终止整个子进程树（任务10）。

    前端经 `cmd /c npm run dev` 启动，实际监听 5173 的 node 是孙进程；
    只 terminate 顶层 cmd 会留下孤儿 node 继续占用端口，必须整树终止。
    """
    if os.name != "nt":
        return
    try:
        subprocess.run(
            ["taskkill", "/PID", str(pid), "/T", "/F"],
            capture_output=True,
            check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except OSError:
        pass


def main():
    parser = argparse.ArgumentParser(description="Duty-Agent Dev Environment Startup")
    parser.add_argument(
        "--skip-auth",
        action="store_true",
        help="跳过 Token 鉴权（开发调试用）",
    )
    args = parser.parse_args()

    skip_auth = args.skip_auth
    env = os.environ.copy()
    if skip_auth:
        env["SKIP_AUTH_BYPASS"] = "1"
        print("[INFO] SKIP_AUTH_BYPASS=1 — Token 鉴权已禁用")
    # 任务3：dev 流程显式授权后端把 dynamic token 落盘 .dev-token（cli 的
    # token 回退链会读它）。没有这个 env 时后端不再写该文件。
    env["DUTY_DEV_WRITE_TOKEN"] = "1"

    print("=" * 60)
    print("  Duty-Agent Dev Environment Startup")
    print("=" * 60)
    if skip_auth:
        print("  [MODE] 绕过鉴权（开发调试）")
    print()
    print("[1/5] python-embed check OK")
    print("[2/5] Starting backend (port 8765)...")

    backend_proc = subprocess.Popen(
        [PYTHON_EMBED, BACKEND_PY, "--server", "--port", "8765"],
        stdout=subprocess.PIPE,
        # stderr 独立成管道，与 stdout 各配一个排空线程（任务10：终身排空两管道）。
        stderr=subprocess.PIPE,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        text=True,
        bufsize=1,
        env=env,
    )
    threading.Thread(
        target=drain_pipe, args=(backend_proc.stderr, "[backend:err] "),
        name="drain-backend-stderr", daemon=True,
    ).start()

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

    # 捕获结束：stdout 交给专职排空线程直到后端退出，不再有缓冲写满风险。
    threading.Thread(
        target=drain_pipe, args=(backend_proc.stdout,),
        name="drain-backend-stdout", daemon=True,
    ).start()

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
        cwd=FRONTEND_DIR,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        text=True,
        bufsize=1,
    )
    threading.Thread(
        target=drain_pipe, args=(frontend_proc.stdout, "[frontend] "),
        name="drain-frontend-stdout", daemon=True,
    ).start()
    if frontend_proc.stderr is not None:
        threading.Thread(
            target=drain_pipe, args=(frontend_proc.stderr, "[frontend:err] "),
            name="drain-frontend-stderr", daemon=True,
        ).start()

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
        # 任务10：整树终止。后端可能带 MCP 子进程，前端的 node 是 cmd 的
        # 孙进程——单杀顶层会留孤儿进程继续占端口。
        kill_process_tree(backend_proc.pid)
        kill_process_tree(frontend_proc.pid)
        try:
            backend_proc.wait(timeout=10)
            frontend_proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            print("[orchestrator] WARN: some child processes did not exit in time.")
        print("[orchestrator] Done.")


if __name__ == "__main__":
    main()
