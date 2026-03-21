from __future__ import annotations

import asyncio
import threading
from typing import Any, Literal

from fastapi import FastAPI
from starlette.responses import JSONResponse
from starlette.routing import Route
from starlette.applications import Starlette

try:
    from mcp.server.fastmcp import Context as McpContext
    from mcp.server.fastmcp import FastMCP
    MCP_SDK_IMPORT_ERROR: Exception | None = None
except Exception as ex:  # pragma: no cover - exercised only in non-embedded runtimes
    McpContext = Any  # type: ignore[assignment]
    FastMCP = None  # type: ignore[assignment]
    MCP_SDK_IMPORT_ERROR = ex

try:
    from models.schemas import DutyPlanPresetModel, DutyRosterEntryPatch
except ImportError:
    from .models.schemas import DutyPlanPresetModel, DutyRosterEntryPatch


def build_mcp_http_app(parent_app: FastAPI):
    if FastMCP is None:
        async def unavailable_endpoint(_request):
            return JSONResponse(
                status_code=503,
                content={"detail": f"MCP dependency is unavailable: {MCP_SDK_IMPORT_ERROR}"},
            )

        return Starlette(routes=[Route("/", endpoint=unavailable_endpoint, methods=["GET", "POST", "DELETE"])])

    server = FastMCP(
        name="Duty-Agent MCP",
        instructions="Expose Duty-Agent scheduling, config, roster, and snapshot tools.",
        streamable_http_path="/",
        stateless_http=True,
        json_response=True,
    )

    def require_runtime():
        runtime = getattr(parent_app.state, "runtime", None)
        if runtime is None:
            raise RuntimeError("Runtime is not initialized.")
        return runtime

    @server.tool(name="get_engine_info", description="Return engine capabilities and runtime info.")
    def get_engine_info() -> dict[str, Any]:
        runtime = require_runtime()
        payload = runtime.query_service.engine_info()
        payload["mcp_enabled"] = bool(getattr(runtime, "enable_mcp", False))
        return payload

    @server.tool(name="get_snapshot", description="Return current backend snapshot including config, roster, and state.")
    def get_snapshot() -> dict[str, Any]:
        runtime = require_runtime()
        return runtime.query_service.get_snapshot(request_source="mcp")

    @server.tool(name="get_config", description="Return current backend config.")
    def get_config() -> dict[str, Any]:
        runtime = require_runtime()
        return runtime.query_service.get_config(request_source="mcp")

    @server.tool(name="patch_config", description="Patch backend config and return the updated config.")
    def patch_config(
        expected_version: int | None = None,
        selected_plan_id: str | None = None,
        plan_presets: list[DutyPlanPresetModel] | None = None,
        duty_rule: str | None = None,
    ) -> dict[str, Any]:
        runtime = require_runtime()
        patch_payload: dict[str, Any] = {}
        if expected_version is not None:
            patch_payload["expected_version"] = expected_version
        if selected_plan_id is not None:
            patch_payload["selected_plan_id"] = selected_plan_id
        if plan_presets is not None:
            patch_payload["plan_presets"] = [
                preset.model_dump(exclude_none=True, exclude_unset=True)
                if hasattr(preset, "model_dump")
                else dict(preset)
                for preset in plan_presets
            ]
        if duty_rule is not None:
            patch_payload["duty_rule"] = duty_rule
        return runtime.command_service.update_config(patch_payload, request_source="mcp")

    @server.tool(name="get_roster", description="Return current roster entries.")
    def get_roster() -> dict[str, Any]:
        runtime = require_runtime()
        return {"roster": runtime.query_service.get_roster(request_source="mcp")}

    @server.tool(name="put_roster", description="Replace the full roster and return the persisted roster.")
    def put_roster(roster: list[DutyRosterEntryPatch]) -> dict[str, Any]:
        runtime = require_runtime()
        roster_payload = [
            entry.model_dump(exclude_none=True, exclude_unset=True)
            if hasattr(entry, "model_dump")
            else dict(entry)
            for entry in roster
        ]
        return {"roster": runtime.command_service.update_roster(roster_payload, request_source="mcp")}

    @server.tool(name="save_schedule_entry", description="Persist a single schedule entry edit and return the updated snapshot.")
    def save_schedule_entry(
        target_date: str,
        area_assignments: dict[str, list[str]],
        source_date: str | None = None,
        day: str | None = None,
        note: str | None = None,
        create_if_missing: bool = False,
        ledger_mode: Literal["record", "skip"] = "record",
    ) -> dict[str, Any]:
        runtime = require_runtime()
        payload = {
            "source_date": source_date,
            "target_date": target_date,
            "day": day,
            "area_assignments": area_assignments,
            "note": note,
            "create_if_missing": create_if_missing,
            "ledger_mode": ledger_mode,
        }
        return runtime.command_service.save_schedule_entry(payload, request_source="mcp")

    @server.tool(name="run_schedule", description="Run the scheduling engine and return the final result.")
    async def run_schedule(
        instruction: str,
        apply_mode: str = "append",
        ctx: McpContext | None = None,
    ) -> dict[str, Any]:
        runtime = require_runtime()
        trace_id = runtime.new_trace_id()
        stop_event = threading.Event()
        loop = asyncio.get_running_loop()
        queue: asyncio.Queue[dict[str, Any]] = asyncio.Queue()

        def put_progress(phase: str, message: str, stream_chunk: str | None = None):
            loop.call_soon_threadsafe(queue.put_nowait, {
                "type": "progress",
                "phase": phase,
                "message": message,
                "stream_chunk": stream_chunk,
            })

        def run_task():
            try:
                result = runtime.command_service.run_schedule(
                    {
                        "instruction": instruction,
                        "apply_mode": apply_mode,
                        "trace_id": trace_id,
                        "request_source": "mcp",
                    },
                    put_progress,
                    stop_event,
                )
                loop.call_soon_threadsafe(queue.put_nowait, {"type": "done", "data": result})
            except InterruptedError:
                loop.call_soon_threadsafe(queue.put_nowait, {"type": "error", "message": "Cancelled by user."})
            except Exception as ex:
                loop.call_soon_threadsafe(queue.put_nowait, {"type": "error", "message": str(ex)})

        threading.Thread(target=run_task, daemon=True, name="McpScheduleRun").start()

        progress_count = 0.0
        try:
            while True:
                item = await queue.get()
                item_type = str(item.get("type") or "")
                if item_type == "progress":
                    if ctx is not None:
                        progress_count += 0.25 if str(item.get("phase") or "") == "stream_chunk" else 1.0
                        progress_message = str(item.get("message") or "").strip() or str(item.get("phase") or "").strip() or "Running"
                        ctx.report_progress(progress_count, None, progress_message)
                    continue
                if item_type == "done":
                    return dict(item.get("data") or {})
                if item_type == "error":
                    raise RuntimeError(str(item.get("message") or "Unknown error."))
        finally:
            stop_event.set()

    return server.streamable_http_app()
