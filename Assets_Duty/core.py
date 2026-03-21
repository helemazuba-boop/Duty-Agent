#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import argparse
import json
import os
import sys
import time
import traceback
import threading
import signal
from pathlib import Path
from contextlib import asynccontextmanager

# Add current directory to path for local module imports
sys.path.append(os.path.dirname(os.path.abspath(__file__)))

from fastapi import FastAPI, BackgroundTasks, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from fastapi.responses import RedirectResponse
from fastapi.staticfiles import StaticFiles
from auth import build_http_unauthorized_response, is_mcp_path, is_protected_http_path, is_public_http_path, is_request_authorized
from mcp_server import build_mcp_http_app
from runtime import create_runtime
from routers import config, duty, roster
import uvicorn

WEB_DIRECTORY = Path(__file__).resolve().parent / "web"

@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup logic
    print(f"[Lifespan] Engine starting in {os.getcwd()}", flush=True)
    app.state.mcp_http_app = build_mcp_http_app(app)
    async with app.state.mcp_http_app.router.lifespan_context(app.state.mcp_http_app):
        yield
    app.state.mcp_http_app = None
    # Shutdown logic
    print("[Lifespan] Engine shutting down", flush=True)

app = FastAPI(title="Duty-Agent IPC Engine", version="0.50.0", lifespan=lifespan)

# Enable CORS
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
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
        if not is_request_authorized(request, runtime):
            return build_http_unauthorized_response()
        return await call_next(request)

    if is_protected_http_path(path):
        runtime = getattr(request.app.state, "runtime", None)
        if runtime is None:
            return JSONResponse(status_code=503, content={"detail": "Runtime is not initialized."})
        if not is_request_authorized(request, runtime):
            return build_http_unauthorized_response()

    return await call_next(request)

@app.get("/app")
async def web_app_root():
    return RedirectResponse(url="/app/")

if WEB_DIRECTORY.is_dir():
    app.mount("/app", StaticFiles(directory=str(WEB_DIRECTORY), html=True), name="web_app")

# Register modular routers
app.include_router(duty.router)
app.include_router(config.router)
app.include_router(roster.router)

@app.get("/")
async def root(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        return {"status": "running", "engine": "Duty-Agent FastAPI", "version": "0.50.0"}
    payload = runtime.query_service.health()
    payload["engine"] = "Duty-Agent FastAPI"
    return payload

@app.get("/health")
async def health(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        return {"status": "ok", "version": "0.50.0"}
    return runtime.query_service.health()

@app.get("/engine/info")
async def engine_info(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        return {"engine": "Duty-Agent Unified Scheduling Engine", "version": "0.50.0"}
    return runtime.query_service.engine_info()

@app.post("/shutdown")
async def shutdown(background_tasks: BackgroundTasks):
    def exit_process():
        # Give some time for the response to be sent
        time.sleep(0.5)
        print("[Server] Manual shutdown triggered. Exiting...", flush=True)
        os.kill(os.getpid(), signal.SIGTERM)
    
    background_tasks.add_task(exit_process)
    return {"status": "shutting down"}

def monitor_parent_process():
    """
    Monitor if the parent process still exists.
    Uses Win32 API on Windows for robustness (immune to PID reuse).
    """
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
            print(f"[Lifecycle] Cannot open parent PID {parent_pid}, exiting.", flush=True)
            os._exit(1)
        
        print(f"[Lifecycle] Windows SuicideWatch active (Kernel Handle) for parent PID: {parent_pid}", flush=True)
        try:
            result = kernel32.WaitForSingleObject(ctypes.wintypes.HANDLE(handle), INFINITE)
            if result == WAIT_OBJECT_0:
                print(f"[Lifecycle] Parent {parent_pid} exited. Shutting down self...", flush=True)
                os._exit(0)
        finally:
            kernel32.CloseHandle(handle)
    else:
        # Fallback for Posix (os.getppid() monitor)
        print(f"[Lifecycle] Posix SuicideWatch active for parent PID: {parent_pid}", flush=True)
        while True:
            try:
                os.kill(parent_pid, 0)
            except OSError:
                print(f"[Lifecycle] Parent {parent_pid} lost. Shutting down self...", flush=True)
                os._exit(0)
            time.sleep(2)

def main():
    parser = argparse.ArgumentParser(description="Duty-Agent Core Entry")
    parser.add_argument("--data-dir", type=str, default="data")
    parser.add_argument("--server", action="store_true", help="Run in HTTP server mode")
    parser.add_argument("--port", type=int, default=0, help="Port to listen on (0 for random)")
    args = parser.parse_args()

    data_dir = Path(args.data_dir).resolve()
    data_dir.mkdir(parents=True, exist_ok=True)
    
    if args.server:
        app.state.runtime = create_runtime(data_dir)
        
        # Determine actual port
        import socket
        actual_port = args.port
        if actual_port == 0:
            temp_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            temp_sock.bind(('127.0.0.1', 0))
            actual_port = temp_sock.getsockname()[1]
            temp_sock.close()
        
        print(f"__DUTY_SERVER_PORT__:{actual_port}", flush=True)
        print(f"__DUTY_SERVER_TOKEN__:{app.state.runtime.access_token}", flush=True)
        
        # Start suicide watch thread
        watch_thread = threading.Thread(
            target=monitor_parent_process, 
            daemon=True,
            name="SuicideWatch"
        )
        watch_thread.start()

        uvicorn.run(app, host="127.0.0.1", port=actual_port, log_level="warning")
    else:
        # CLI fallback mode (e.g. for debug or isolated run)
        from engine import run_schedule
        from state_ops import Context, save_json_atomic
        ctx = Context(data_dir)
        input_data = {}
        if ctx.paths["input"].exists():
            try:
                with open(ctx.paths["input"], "r", encoding="utf-8-sig") as f:
                    input_data = json.load(f)
            except: pass
                
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
    print("---------------------", flush=True)

if __name__ == "__main__":
    try:
        audit_environment()
        main()
    except Exception:
        print("\n!!! CRITICAL STARTUP ERROR !!!", file=sys.stderr, flush=True)
        traceback.print_exc(file=sys.stderr)
        sys.exit(1)
