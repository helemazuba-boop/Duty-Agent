# -*- mode: python ; coding: utf-8 -*-
# pyinstaller desktop/desktop.spec
#
# 从 Assets_Duty/ 目录运行：
#   pyinstaller desktop/desktop.spec --clean --noconfirm
#
# 打包后运行 dist/DutyAgent/DutyAgent.exe
#
# 注意：pywebview 在 Windows 上依赖 WebView2Loader.dll（通常随系统 Edge 安装）
# 如需额外分发，见：https://pywebview.filp.com/examples/multithreading.html

block_cipher = None

a = Analysis(
    # 入口脚本（相对 Assets_Duty/ 目录）
    ["desktop\\launcher.py"],
    pathex=[],
    binaries=[],
    datas=[
        # 前端构建产物（npm run build 输出）
        # 路径格式：(源路径, 打包后相对路径)
        ("..\\..\\..\\..\\Duty-Agent-UI\\dist", "ui_dist"),
    ],
    hiddenimports=[
        # pywebview 本身
        "webview",
        "webview.window",
        # FastAPI 后端完整依赖链
        "fastapi",
        "uvicorn",
        "uvicorn.logging",
        "uvicorn.loops",
        "uvicorn.loops.auto",
        "uvicorn.protocols",
        "uvicorn.protocols.http",
        "uvicorn.protocols.http.auto",
        "uvicorn.protocols.websockets",
        "uvicorn.protocols.websockets.auto",
        "uvicorn.lifespan",
        "uvicorn.lifespan.on",
        "starlette",
        "starlette.middleware",
        "starlette.middleware.cors",
        "starlette.staticfiles",
        "starlette.responses",
        "pydantic",
        "pydantic.dataclasses",
        "pydantic.main",
        "pydantic.fields",
        "python_multipart",
        "python_socketio",
        "socketio",
        "asyncpg",
        "httptools",
        "uvloop",
        # 业务模块（Assets_Duty 核心）
        "core",
        "auth",
        "runtime",
        "routers",
        "routers.config",
        "routers.duty",
        "routers.roster",
        "state_ops",
        "llm_transport",
        "execution_profiles",
        "postprocess",
        "multi_agent",
        "multi_agent.executor",
        "multi_agent.settlement",
        "multi_agent.contracts",
        "multi_agent.validators",
        "tool_loop",
        "tool_loop.executor",
        "tool_loop.ini_handler",
        "tool_loop.tool_prompt",
        "orchestrator",
        "orchestrator.context",
        "orchestrator.decomposer",
        "orchestrator.prompt",
        "orchestrator.aggregator",
        "orchestrator.hints_handler",
        "orchestrator.executor",
        "single_pass_executor",
        "build_prompt",
        "prompt_config",
        "mcp_server",
    ],
    hookspath=[],
    hooksconfig={},
    keys=[],
    exclude_binaries=False,
    name="DutyAgent",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,          # 隐藏黑窗口
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="DutyAgent",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    disable_windowed_traceback=False,
    runtime_tmpdir=None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.zipfiles,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name="DutyAgent",
)
