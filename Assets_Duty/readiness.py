# -*- coding: utf-8 -*-
"""First-run readiness checks and model connectivity probe.

Consolidates the diagnostics we learned to run by hand (roster present, plan
has base_url/model/key, model actually reachable, first run done) into one
deterministic place. Both the Web first-run wizard and the ``duty-cli doctor``
command consume this; the reasoning lives here only, per the project's
"deterministic core, thin entry points" philosophy.
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Any

try:
    from llm_transport import _build_llm_target
except ImportError:  # pragma: no cover - import path fallback
    from .llm_transport import _build_llm_target

PROBE_TIMEOUT_SECONDS = 5.0


def _loopback_opener() -> urllib.request.OpenerDirector:
    """Opener that bypasses any system/registry proxy for loopback hosts, so a
    local model server (LM Studio / Ollama at 127.0.0.1) is not turned into a
    502 by a machine-wide proxy."""
    return urllib.request.build_opener(urllib.request.ProxyHandler({}))


def probe_model(base_url: str, model: str, api_key: str, timeout: float = PROBE_TIMEOUT_SECONDS) -> dict:
    """Send a tiny completion request and translate the outcome into a
    human-actionable result: ``{ok, status, detail, fix}``."""
    base_url = str(base_url or "").strip()
    model = str(model or "").strip()
    if not base_url or not model:
        return {
            "ok": False,
            "status": "unconfigured",
            "detail": "base_url 或 model 为空。",
            "fix": "在设置页“方案与模型”卡填写模型服务地址与模型名称。",
        }

    config = {"base_url": base_url, "model": model, "api_key": api_key or ""}
    messages = [{"role": "user", "content": "ping"}]
    try:
        url, payload, resolved_key = _build_llm_target(config, messages, {"max_tokens": 1})
    except Exception as ex:  # noqa: BLE001 - defensive: bad config shapes
        return {"ok": False, "status": "unconfigured", "detail": str(ex), "fix": "检查方案配置。"}

    data = json.dumps(payload).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    if str(resolved_key or "").strip():
        headers["Authorization"] = f"Bearer {resolved_key.strip()}"
    request = urllib.request.Request(url=url, data=data, headers=headers, method="POST")

    is_loopback = "127.0.0.1" in base_url or "localhost" in base_url or "::1" in base_url
    opener = _loopback_opener() if is_loopback else urllib.request.build_opener()
    try:
        with opener.open(request, timeout=timeout) as response:
            response.read(1)
        return {"ok": True, "status": "ok", "detail": f"模型 {model} 连通。", "fix": ""}
    except urllib.error.HTTPError as ex:
        code = ex.code
        if code == 401 or code == 403:
            return {"ok": False, "status": str(code), "detail": "鉴权失败。", "fix": "检查 API Key 是否正确（本地模型通常可留空）。"}
        if code == 404:
            return {"ok": False, "status": "404", "detail": "端点或模型不存在。", "fix": "检查 base_url 路径与 model 名称。"}
        if code == 502 or code == 503 or code == 504:
            return {
                "ok": False,
                "status": str(code),
                "detail": f"网关错误 {code}（常见于系统代理拦截了本地端口）。",
                "fix": "为后端设置 NO_PROXY=localhost,127.0.0.1，或在代理中放行本地地址。",
            }
        return {"ok": False, "status": str(code), "detail": f"HTTP {code}。", "fix": "查看模型服务日志。"}
    except urllib.error.URLError as ex:
        reason = str(getattr(ex, "reason", ex))
        return {
            "ok": False,
            "status": "unreachable",
            "detail": f"无法连接：{reason}",
            "fix": "确认模型服务已启动且地址/端口正确（如 LM Studio 需开启本地服务器）。",
        }
    except Exception as ex:  # noqa: BLE001 - timeouts and misc socket errors
        return {
            "ok": False,
            "status": "timeout",
            "detail": f"探测失败：{ex}",
            "fix": "确认模型服务可达；本地大模型首次加载可能较慢，可稍后重试。",
        }


def evaluate_readiness(config: dict, roster: list, state: dict, probe: bool = False) -> dict:
    """Assemble the readiness report from already-loaded config/roster/state.

    ``config`` is the hydrated backend config (top-level base_url/model/api_key
    reflect the selected plan).
    """
    checks: list[dict[str, Any]] = []
    is_offline = str((config or {}).get("orchestration_mode", "") or "").strip().lower() == "offline"

    # 1) Roster present.
    roster_count = len(roster or [])
    checks.append({
        "id": "roster",
        "ok": roster_count > 0,
        "detail": f"花名册 {roster_count} 人。" if roster_count else "花名册为空。",
        "fix": "" if roster_count else "在“花名册管理”导入名单，或用 CLI: replace-roster。",
    })

    # 2) Plan has a usable provider (relaxed for the offline algorithm mode).
    base_url = str((config or {}).get("base_url", "") or "").strip()
    model = str((config or {}).get("model", "") or "").strip()
    api_key = str((config or {}).get("api_key", "") or "").strip()
    if is_offline:
        checks.append({
            "id": "plan",
            "ok": True,
            "detail": "离线算法模式：无需配置模型服务。",
            "fix": "",
        })
    else:
        plan_ok = bool(base_url and model)
        plan_detail = "方案已配置模型服务地址与模型名称。"
        plan_fix = ""
        if not plan_ok:
            plan_detail = "选中方案缺少 base_url 或 model。"
            plan_fix = "在设置页“方案与模型”卡填写。"
        elif base_url.lower().startswith("https://") and not api_key:
            # Cloud endpoint without a key is usually a misconfiguration (warn).
            plan_ok = True
            plan_detail = "云端端点未填写 API Key（本地模型可忽略）。"
            plan_fix = "若为云端服务，请在方案卡填写 API Key。"
        checks.append({"id": "plan", "ok": plan_ok, "detail": plan_detail, "fix": plan_fix, "warn": bool(plan_fix and plan_ok)})

    # 3) Model connectivity (opt-in; skipped entirely in offline mode).
    if is_offline:
        checks.append({
            "id": "model",
            "ok": True,
            "detail": "离线算法模式：无需模型连通性。",
            "fix": "",
        })
    elif probe:
        result = probe_model(base_url, model, api_key)
        checks.append({
            "id": "model",
            "ok": bool(result.get("ok")),
            "detail": result.get("detail", ""),
            "fix": result.get("fix", ""),
        })

    # 4) First run done?
    pool = (state or {}).get("schedule_pool", []) if isinstance(state, dict) else []
    has_run = bool(pool)
    checks.append({
        "id": "first_run",
        "ok": has_run,
        "detail": "已有排班记录。" if has_run else "尚未生成过排班。",
        "fix": "" if has_run else "配置完成后在首页/向导点“试跑一次”，或用 CLI: run。",
    })

    ready = all(c["ok"] for c in checks)
    next_steps = [c["fix"] for c in checks if not c["ok"] and c.get("fix")]
    return {"ready": ready, "checks": checks, "next_steps": next_steps}
