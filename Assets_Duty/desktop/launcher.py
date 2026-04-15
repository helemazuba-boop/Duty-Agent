#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Duty-Agent 桌面应用入口。

用法（开发）：
    python desktop/launcher.py

用法（打包后）：
    直接运行 DutyAgent.exe
"""
from __future__ import annotations

import sys
import threading
import traceback
from pathlib import Path

# desktop 包路径
DESKTOP_DIR = Path(__file__).resolve().parent
ASSETS_DIR = DESKTOP_DIR.parent

# 确保 desktop 模块可导入
if str(ASSETS_DIR) not in sys.path:
    sys.path.insert(0, str(ASSETS_DIR))


def _get_webview_module():
    """延迟导入 webview，避免打包时误扫描未安装的模块。"""
    try:
        import webview

        return webview
    except ImportError:
        print("ERROR: pywebview 未安装。请运行：pip install pywebview", file=sys.stderr)
        sys.exit(1)


def _show_error(title: str, message: str) -> None:
    """尝试用 tkinter 弹错误框（pywebview 未安装时也可用）。"""
    try:
        import tkinter as tk

        root = tk.Tk()
        root.withdraw()
        root.attributes("-topmost", True)
        from tkinter import messagebox

        messagebox.showerror(title, message)
        root.destroy()
    except Exception:
        print(f"\n{'=' * 60}")
        print(f"ERROR: {title}")
        print(message)
        print("=" * 60)


def _start_webview(url: str, token: str | None) -> None:
    """在主线程启动 pywebview 窗口（blocking）。"""
    webview = _get_webview_module()

    # 构建带 token 的完整 URL（/app 是 FastAPI 静态文件挂载点）
    import urllib.parse

    if token:
        params = urllib.parse.urlencode({"token": token})
        full_url = f"{url}?{params}"
    else:
        full_url = url

    window = webview.create_window(
        title="Duty-Agent",
        url=full_url,
        width=1280,
        height=800,
        min_size=(800, 600),
        resizable=True,
        js_api=None,
        confirm_close=False,
    )

    webview.start(debug=False, private_mode=False, window=window)


def main() -> None:
    """主入口：启动后端 → 打开桌面窗口。"""
    try:
        from desktop.backend_manager import start_backend, stop_backend

        print("[DutyAgent] 正在启动后端服务...")
        port, token = start_backend(port=8765)

        url = f"http://127.0.0.1:{port}/app"
        print(f"[DutyAgent] 后端就绪 → {url}")
        if token:
            print("[DutyAgent] Token 已获取（静默）")

    except RuntimeError as ex:
        tb = traceback.format_exc()
        _show_error(
            "后端启动失败",
            f"无法启动 Duty-Agent 后端服务：\n\n{ex}\n\n"
            f"可能原因：端口 8765 已被占用，或 core.py 启动出错。\n\n"
            f"详细信息：\n{tb}",
        )
        sys.exit(1)
    except Exception as ex:
        tb = traceback.format_exc()
        _show_error("启动错误", f"{ex}\n\n{tb}")
        sys.exit(1)

    try:
        # 在主线程启动 webview（Windows pywebview 要求主线程调用）
        _start_webview(url, token)
    finally:
        # 窗口关闭后清理后端进程
        print("[DutyAgent] 正在关闭后端服务...")
        stop_backend()
        print("[DutyAgent] 已退出。")


if __name__ == "__main__":
    main()
