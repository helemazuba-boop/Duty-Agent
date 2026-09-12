#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import argparse
import json
import os
import sys
import time
import traceback
import threading
from pathlib import Path
from contextlib import asynccontextmanager

SKIP_AUTH_BYPASS = os.getenv("SKIP_AUTH_BYPASS", "").strip().lower() in ("1", "true", "yes")

if SKIP_AUTH_BYPASS:
    print("[WARNING] SKIP_AUTH_BYPASS is ENABLED — token verification is bypassed!", file=sys.stderr, flush=True)
    print("[WARNING] Starting in 5 seconds...", file=sys.stderr, flush=True)
    time.sleep(5)

sys.path.append(os.path.dirname(os.path.abspath(__file__)))

from fastapi import FastAPI, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from fastapi.responses import RedirectResponse
from fastapi.staticfiles import StaticFiles
from auth import (
    build_http_unauthorized_response,
    extract_bearer_token,
    extract_bearer_token_from_query,
    is_mcp_path,
    is_protected_http_path,
    is_public_http_path,
    is_request_authorized,
    pop_current_request_bearer_token,
    push_current_request_bearer_token,
)
from mcp_server import build_mcp_http_app
from runtime import create_runtime
from routers import bridge, config, duty, notifications, readiness, roster
from version import APP_VERSION
import uvicorn
import uvicorn.main  # noqa: F401 — run_uvicorn_server 需替换 uvicorn.main.Server

WEB_DIRECTORY = Path(__file__).resolve().parent / "web"

@asynccontextmanager
async def lifespan(app: FastAPI):
    print(f"[Lifespan] Engine starting in {os.getcwd()}", flush=True)
    app.state.mcp_http_app = build_mcp_http_app(app)
    async with app.state.mcp_http_app.router.lifespan_context(app.state.mcp_http_app):
        yield
    app.state.mcp_http_app = None
    runtime = getattr(app.state, "runtime", None)
    if runtime is not None:
        runtime.stop_notification_workers()
        runtime.stop_auto_run_worker()
    print("[Lifespan] Engine shutting down", flush=True)

app = FastAPI(title="Duty-Agent IPC Engine", version=APP_VERSION, lifespan=lifespan)

app.add_middleware(
    CORSMiddleware,
    allow_origins=[
        "http://localhost:5173",
        "http://127.0.0.1:5173",
        "http://localhost",
        "http://127.0.0.1",
    ],
    allow_methods=["*"],
    allow_headers=["*"],
    expose_headers=["Mcp-Session-Id"],
)


async def mcp_mount(scope, receive, send):
    mounted_app = getattr(app.state, "mcp_http_app", None)
    if mounted_app is None:
        response = JSONResponse(status_code=503, content={"detail": "MCP app is not initialized."})
        await response(scope, receive, send)
        return
    await mounted_app(scope, receive, send)


app.mount("/mcp", mcp_mount, name="mcp")


@app.middleware("http")
async def require_bearer_for_protected_routes(request: Request, call_next):
    path = request.url.path
    if request.method.upper() == "OPTIONS" or is_public_http_path(path):
        return await call_next(request)

    if is_mcp_path(path):
        runtime = getattr(request.app.state, "runtime", None)
        if runtime is None or not getattr(runtime, "enable_mcp", False):
            return JSONResponse(status_code=404, content={"detail": "Not Found"})
        candidate_token = extract_bearer_token(request.headers)
        if candidate_token is None:
            candidate_token = extract_bearer_token_from_query(request.query_params)
        if not SKIP_AUTH_BYPASS:
            if not runtime.is_authorized(candidate_token):
                return build_http_unauthorized_response()
        token_scope = push_current_request_bearer_token(candidate_token)
        try:
            return await call_next(request)
        finally:
            pop_current_request_bearer_token(token_scope)

    if is_protected_http_path(path):
        if not SKIP_AUTH_BYPASS:
            runtime = getattr(request.app.state, "runtime", None)
            if runtime is None:
                return JSONResponse(status_code=503, content={"detail": "Runtime is not initialized."})
            if not is_request_authorized(request, runtime):
                return build_http_unauthorized_response()
        return await call_next(request)

    return build_http_unauthorized_response()


@app.get("/app")
async def web_app_root():
    return RedirectResponse(url="/app/")


class WebAppStaticFiles(StaticFiles):
    """"/app" 静态托管:index.html 永远 revalidate,带 hash 的 assets 正常缓存。
    不加 no-cache 时 WebView2 会启发式缓存旧 HTML,刷新后仍加载旧 hash 的 JS/CSS。"""

    def file_response(self, *args, **kwargs):
        response = super().file_response(*args, **kwargs)
        if response.media_type == "text/html":
            response.headers["Cache-Control"] = "no-cache"
        return response


if WEB_DIRECTORY.is_dir():
    app.mount("/app", WebAppStaticFiles(directory=str(WEB_DIRECTORY), html=True), name="web_app")

app.include_router(duty.router)
app.include_router(bridge.router)
app.include_router(notifications.router)
app.include_router(config.router)
app.include_router(roster.router)
app.include_router(readiness.router)


@app.get("/")
async def root(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        return {"status": "running", "engine": "Duty-Agent FastAPI", "version": APP_VERSION}
    payload = runtime.query_service.health()
    payload["engine"] = "Duty-Agent FastAPI"
    return payload


@app.get("/health")
async def health(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        return {"status": "ok", "version": APP_VERSION, "auth_bypassed": SKIP_AUTH_BYPASS}
    return runtime.query_service.health()


@app.get("/engine/info")
async def engine_info(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        return {"engine": "Duty-Agent Unified Scheduling Engine", "version": APP_VERSION, "auth_bypassed": SKIP_AUTH_BYPASS}
    return runtime.query_service.engine_info()


@app.post("/shutdown")
async def shutdown():
    # 任务1：优雅关停 —— 置 should_exit 让 uvicorn 主循环走完整退出路径
    # （停止 accept → 等待在途请求 → lifespan shutdown → runtime worker join）。
    # 旧实现 os.kill(SIGTERM) 在 Windows 上等价于硬杀进程，lifespan 永远不执行。
    server = getattr(app.state, "uvicorn_server", None)
    if server is None:
        return JSONResponse(status_code=503, content={"detail": "Uvicorn server instance is not captured yet."})
    server.should_exit = True
    return {"status": "shutting down"}


def run_uvicorn_server(app: FastAPI, *, host: str, port: int, log_level: str, prebound_socket=None) -> None:
    """启动 uvicorn，并把运行中的 Server 实例捕获到 app.state.uvicorn_server（任务1）。

    uvicorn.run() 在内部才构造 Server，外部拿不到实例；这里在 run() 调用期间
    临时把 Server 类指向捕获子类（uvicorn.main.run 通过自身模块全局量构造
    ``Server(config)``，因此必须替换 ``uvicorn.main.Server``；包级
    ``uvicorn.Server`` 一并替换以兼容不同引用路径），run() 内部构造即落到子类
    上，从而把实例挂到 app.state 供 /shutdown 置 should_exit。finally 恢复
    原类；既有测试通过 mock ``core.uvicorn.run`` 拦截启动的方式依然有效
    （mock 生效时子类根本不会被构造）。

    任务4：传入 prebound_socket 时，经由 ``Server.serve(sockets=[sock])``
    （uvicorn 0.41.0 支持；Config/run 不暴露 sock 参数）直接接管已绑定的监听
    socket，消除"探测端口 → close → 重新 bind"窗口内端口被其他进程抢占的
    TOCTOU；asyncio 在接管时会自行调用 listen(backlog)，这里只需保持句柄存活。
    """
    class _CapturedServer(uvicorn.Server):
        def __init__(self, config):
            super().__init__(config)
            app.state.uvicorn_server = self

        async def serve(self, sockets=None):
            if sockets is None and prebound_socket is not None:
                sockets = [prebound_socket]
            await super().serve(sockets=sockets)

    # uvicorn/__init__.py 把包属性 main 覆盖成了 click 的 Command 对象，
    # 子模块本体只能从 sys.modules 里取（module-level 的 import uvicorn.main
    # 已保证它被加载）。
    uvicorn_main_module = sys.modules["uvicorn.main"]
    real_main_server = uvicorn_main_module.Server
    real_pkg_server = uvicorn.Server
    uvicorn_main_module.Server = _CapturedServer
    uvicorn.Server = _CapturedServer
    try:
        # timeout_graceful_shutdown=5：SSE/WebSocket 等长连接不会主动结束，
        # 必须给优雅退出一个硬上限，否则 POST /shutdown 可能无限等待。
        uvicorn.run(app, host=host, port=port, log_level=log_level, timeout_graceful_shutdown=5)
    finally:
        uvicorn_main_module.Server = real_main_server
        uvicorn.Server = real_pkg_server


def monitor_parent_process():
    def _safe_print(message: str) -> None:
        # 父进程死亡后 stdout/stderr 管道读端即关闭，任何 print 都可能抛
        # BrokenPipeError（或阻塞）；控制台输出绝不能挡在退出路径之前。
        try:
            print(message, flush=True)
        except OSError:
            pass

    parent_pid = os.getppid()
    if parent_pid <= 1:
        return

    if os.name == 'nt':
        import ctypes
        import ctypes.wintypes

        SYNCHRONIZE = 0x00100000
        WAIT_OBJECT_0 = 0x00000000
        INFINITE = 0xFFFFFFFF

        kernel32 = ctypes.windll.kernel32
        handle = kernel32.OpenProcess(SYNCHRONIZE, False, parent_pid)
        if not handle:
            _safe_print(f"[Lifecycle] Cannot open parent PID {parent_pid}, exiting.")
            os._exit(1)

        _safe_print(f"[Lifecycle] Windows SuicideWatch active (Kernel Handle) for parent PID: {parent_pid}")
        try:
            result = kernel32.WaitForSingleObject(ctypes.wintypes.HANDLE(handle), INFINITE)
            if result == WAIT_OBJECT_0:
                os._exit(0)
        finally:
            kernel32.CloseHandle(handle)
    else:
        _safe_print(f"[Lifecycle] Posix SuicideWatch active for parent PID: {parent_pid}")
        while True:
            if os.getppid() != parent_pid:
                os._exit(0)
            try:
                os.kill(parent_pid, 0)
            except OSError:
                os._exit(0)
            time.sleep(2)


def main():
    parser = argparse.ArgumentParser(description="Duty-Agent Core Entry")
    parser.add_argument("--data-dir", type=str, default="data")
    parser.add_argument("--server", action="store_true", help="Run in HTTP server mode")
    parser.add_argument("--port", type=int, default=0, help="Port to listen on (0 for random)")
    parser.add_argument("--disable-mcp-runtime", action="store_true", help="Disable MCP for this process without changing saved config")
    parser.add_argument("--no-parent-watch", action="store_true", help="Skip the parent-process SuicideWatch; the process lifecycle is managed by an external manager (e.g. duty-cli serve) via a pid file.")
    args = parser.parse_args()

    data_dir = Path(args.data_dir).resolve()
    data_dir.mkdir(parents=True, exist_ok=True)

    if args.server:
        app.state.runtime = create_runtime(data_dir, disable_mcp_runtime=args.disable_mcp_runtime)

        import socket
        # 任务4：--port 0 时先 bind 探测端口；bind 后不 close —— 保持监听句柄
        # 直至 uvicorn 经 serve(sockets=[sock]) 接管（见 run_uvicorn_server），
        # 消除"关闭探测 socket → 重新 bind"窗口内端口被抢占的 TOCTOU。
        prebound_socket = None
        actual_port = args.port
        if actual_port == 0:
            prebound_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            prebound_socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            prebound_socket.bind(("127.0.0.1", 0))
            actual_port = prebound_socket.getsockname()[1]

        print(f"__DUTY_SERVER_PORT__:{actual_port}", flush=True)
        print(f"__DUTY_SERVER_TOKEN_MODE__:{app.state.runtime.access_token_mode}", flush=True)
        if app.state.runtime.access_token_mode == "dynamic":
            print(f"__DUTY_SERVER_TOKEN__:{app.state.runtime.access_token}", flush=True)
            # 任务3：.dev-token 只在显式开发流程落盘（orchestrator.py 注入
            # DUTY_DEV_WRITE_TOKEN=1）。生产客户端从 __DUTY_SERVER_TOKEN__ 管道
            # 拿 token，数据目录不再默认出现明文 token 文件。
            if os.getenv("DUTY_DEV_WRITE_TOKEN", "").strip() == "1":
                try:
                    token_file = data_dir / ".dev-token"
                    token_file.write_text(app.state.runtime.access_token, encoding="utf-8")
                except Exception:
                    pass

        if not args.no_parent_watch:
            watch_thread = threading.Thread(
                target=monitor_parent_process,
                daemon=True,
                name="SuicideWatch"
            )
            watch_thread.start()
        else:
            print("[Lifecycle] SuicideWatch disabled (--no-parent-watch).", flush=True)

        run_uvicorn_server(app, host="127.0.0.1", port=actual_port, log_level="warning", prebound_socket=prebound_socket)
    else:
        from engine import run_schedule
        from state_ops import Context, sanitize_error_for_client, save_json_atomic
        ctx = Context(data_dir)
        input_data = {}
        if ctx.paths["input"].exists():
            try:
                with open(ctx.paths["input"], "r", encoding="utf-8-sig") as f:
                    input_data = json.load(f)
            except (OSError, ValueError) as ex:
                # 任务9：损坏的 input.json 不能再静默吞掉——把空输入当合法输入
                # 跑完会写出误导性的 result。这里显式记录并以 error 结果返回。
                print(f"[core] Failed to parse {ctx.paths['input'].name}: {ex}", file=sys.stderr, flush=True)
                save_json_atomic(
                    ctx.paths["result"],
                    {
                        "status": "error",
                        "message": sanitize_error_for_client(f"Failed to parse input.json: {ex}"),
                    },
                )
                return

        result = run_schedule(ctx, input_data)

        payload = {"status": result.get("status", "error")}
        if "message" in result: payload["message"] = result["message"]
        if "ai_response" in result: payload["ai_response"] = result["ai_response"]

        save_json_atomic(ctx.paths["result"], payload)


def audit_environment():
    print("--- Start-up Audit ---", flush=True)
    print(f"Exec: {sys.executable}", flush=True)
    print(f"CWD: {os.getcwd()}", flush=True)
    print(f"Python: {sys.version.split()[0]}", flush=True)
    try:
        import fastapi
        import uvicorn
        print(f"FastAPI: {fastapi.__version__}, Uvicorn: {uvicorn.__version__}", flush=True)
    except ImportError as e:
        print(f"CRITICAL: Missing dependency: {e}", file=sys.stderr, flush=True)
        sys.exit(1)
    if SKIP_AUTH_BYPASS:
        print("[WARNING] SKIP_AUTH_BYPASS is ENABLED — token verification is bypassed!", flush=True)
    print("---------------------", flush=True)


if __name__ == "__main__":
    try:
        audit_environment()
        main()
    except Exception:
        print("\n!!! CRITICAL STARTUP ERROR !!!", file=sys.stderr, flush=True)
        traceback.print_exc(file=sys.stderr)
        sys.exit(1)
