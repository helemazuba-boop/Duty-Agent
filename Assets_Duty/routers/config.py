from __future__ import annotations

import asyncio
import time

from fastapi import APIRouter, HTTPException, Request

try:
    from models.schemas import DutyBackendConfigPatch, DutyDataResetRequest
    from state_ops import RESET_SCOPE_LABELS, ConfigVersionConflictError, load_state, sanitize_error_for_client
except ImportError:
    from ..models.schemas import DutyBackendConfigPatch, DutyDataResetRequest
    from ..state_ops import RESET_SCOPE_LABELS, ConfigVersionConflictError, load_state, sanitize_error_for_client

router = APIRouter(prefix="/api/v1", tags=["Config"])


def _resolve_request_meta(request: Request, runtime) -> tuple[str, str]:
    trace_id = (request.headers.get("X-Duty-Trace-Id") or "").strip() or runtime.new_trace_id()
    request_source = (request.headers.get("X-Duty-Request-Source") or "").strip() or "api"
    return trace_id, request_source


@router.get("/config")
async def get_config(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    started_at = time.monotonic()
    runtime.logger.info("ConfigRoute", "Received GET /api/v1/config.", trace_id=trace_id, request_source=request_source)
    try:
        payload = runtime.query_service.get_config(trace_id=trace_id, request_source=request_source)
        runtime.logger.info(
            "ConfigRoute",
            "Completed GET /api/v1/config.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        return payload
    except Exception as ex:
        runtime.logger.error(
            "ConfigRoute",
            "GET /api/v1/config failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        raise


@router.patch("/config")
async def patch_config(config_patch: DutyBackendConfigPatch, request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    started_at = time.monotonic()
    patch_payload = config_patch.model_dump(exclude_none=True, exclude_unset=True)
    runtime.logger.info(
        "ConfigRoute",
        "Received PATCH /api/v1/config.",
        trace_id=trace_id,
        request_source=request_source,
        patch_keys=sorted(list(patch_payload.keys())),
    )
    try:
        payload = runtime.command_service.update_config(
            patch_payload,
            trace_id=trace_id,
            request_source=request_source,
        )
        runtime.logger.info(
            "ConfigRoute",
            "Completed PATCH /api/v1/config.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        return payload
    except ConfigVersionConflictError as ex:
        runtime.logger.warn(
            "ConfigRoute",
            "PATCH /api/v1/config rejected due to version conflict.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
            patch_keys=sorted(list(patch_payload.keys())),
        )
        raise HTTPException(status_code=409, detail=sanitize_error_for_client(str(ex))) from ex
    except Exception as ex:
        runtime.logger.error(
            "ConfigRoute",
            "PATCH /api/v1/config failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
            patch_keys=sorted(list(patch_payload.keys())),
        )
        raise


@router.get("/state")
async def get_state(request: Request):
    """C1 契约：state.json 的唯一只读出口（D1：插件不再直接读 state 文件）。

    插件以 ``mtime_ns`` 的变化作为 StateChanged 的去抖依据；文件不存在时
    state 为空态、mtime_ns 为 null。走 /api/v1/ 前缀既有 Bearer 保护。
    """
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    started_at = time.monotonic()
    runtime.logger.info("ConfigRoute", "Received GET /api/v1/state.", trace_id=trace_id, request_source=request_source)
    try:
        state_path = runtime.data_dir / "state.json"
        try:
            mtime_ns: int | None = state_path.stat().st_mtime_ns
        except OSError:
            mtime_ns = None
        payload = {"state": load_state(state_path), "mtime_ns": mtime_ns}
        runtime.logger.info(
            "ConfigRoute",
            "Completed GET /api/v1/state.",
            trace_id=trace_id,
            request_source=request_source,
            mtime_ns=mtime_ns,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        return payload
    except Exception as ex:
        runtime.logger.error(
            "ConfigRoute",
            "GET /api/v1/state failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        raise HTTPException(status_code=500, detail=sanitize_error_for_client(str(ex))) from ex


@router.post("/data-reset")
async def post_data_reset(reset_request: DutyDataResetRequest, request: Request):
    """班级换届 / 学期重置：按 scope 删除班级数据，删除前自动归档。

    ``confirm`` 必须与 scope 的中文标签一致才放行——这是防呆闸门，让误触的
    按钮或误粘贴的 curl 都炸不掉全班数据。排班进行中（本进程持锁、或别的入口
    占着 duty/live WebSocket）返回 409，config 版本过期同样是 409。
    """
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    started_at = time.monotonic()

    expected_label = RESET_SCOPE_LABELS.get(reset_request.scope, "")
    if str(reset_request.confirm or "").strip() != expected_label:
        runtime.logger.warn(
            "ConfigRoute",
            "Rejected POST /api/v1/data-reset: confirmation mismatch.",
            trace_id=trace_id,
            request_source=request_source,
            reset_scope=reset_request.scope,
        )
        raise HTTPException(
            status_code=400,
            detail=f"确认文字不匹配：请准确输入「{expected_label}」后重试。",
        )

    runtime.logger.info(
        "ConfigRoute",
        "Received POST /api/v1/data-reset.",
        trace_id=trace_id,
        request_source=request_source,
        reset_scope=reset_request.scope,
        clear_areas_and_rule=str(bool(reset_request.clear_areas_and_rule)).lower(),
    )
    try:
        payload = await asyncio.to_thread(
            runtime.command_service.reset_data,
            reset_request.scope,
            reset_request.clear_areas_and_rule,
            reset_request.expected_version,
            trace_id=trace_id,
            request_source=request_source,
        )
    except ValueError as ex:
        # ConfigVersionConflictError is a ValueError, and both the version
        # conflict and the "schedule in progress" refusal are 409s.
        runtime.logger.warn(
            "ConfigRoute",
            "POST /api/v1/data-reset rejected.",
            trace_id=trace_id,
            request_source=request_source,
            reset_scope=reset_request.scope,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        raise HTTPException(status_code=409, detail=sanitize_error_for_client(str(ex))) from ex
    except Exception as ex:
        runtime.logger.error(
            "ConfigRoute",
            "POST /api/v1/data-reset failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        raise
    runtime.logger.info(
        "ConfigRoute",
        "Completed POST /api/v1/data-reset.",
        trace_id=trace_id,
        request_source=request_source,
        reset_scope=reset_request.scope,
        archive=str(payload.get("archive", {}).get("name", "")),
        duration_ms=round((time.monotonic() - started_at) * 1000, 2),
    )
    return payload


@router.get("/data-reset/archives")
async def get_data_reset_archives(request: Request):
    """自动归档列表 + 数据目录路径，供设置页展示"删除前已备份到哪里"。"""
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    return runtime.query_service.get_reset_archives(trace_id=trace_id, request_source=request_source)


@router.get("/snapshot")
async def get_snapshot(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    trace_id, request_source = _resolve_request_meta(request, runtime)
    started_at = time.monotonic()
    runtime.logger.info("ConfigRoute", "Received GET /api/v1/snapshot.", trace_id=trace_id, request_source=request_source)
    try:
        payload = runtime.query_service.get_snapshot(trace_id=trace_id, request_source=request_source)
        runtime.logger.info(
            "ConfigRoute",
            "Completed GET /api/v1/snapshot.",
            trace_id=trace_id,
            request_source=request_source,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        return payload
    except Exception as ex:
        runtime.logger.error(
            "ConfigRoute",
            "GET /api/v1/snapshot failed.",
            trace_id=trace_id,
            request_source=request_source,
            exc=ex,
            duration_ms=round((time.monotonic() - started_at) * 1000, 2),
        )
        raise
