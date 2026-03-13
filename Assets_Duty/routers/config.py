from __future__ import annotations

from fastapi import APIRouter, HTTPException, Request

try:
    from models.schemas import DutyBackendConfigPatch
except ImportError:
    from ..models.schemas import DutyBackendConfigPatch

router = APIRouter(prefix="/api/v1", tags=["Config"])


@router.get("/config")
async def get_config(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    return runtime.query_service.get_config()


@router.patch("/config")
async def patch_config(config_patch: DutyBackendConfigPatch, request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    return runtime.command_service.update_config(config_patch.model_dump(exclude_none=True, exclude_unset=True))


@router.get("/snapshot")
async def get_snapshot(request: Request):
    runtime = getattr(request.app.state, "runtime", None)
    if runtime is None:
        raise HTTPException(status_code=503, detail="Runtime is not initialized.")
    return runtime.query_service.get_snapshot()
