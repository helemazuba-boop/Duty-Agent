from __future__ import annotations

from typing import Any, Literal

from fastapi import FastAPI
from starlette.applications import Starlette
from starlette.responses import JSONResponse
from starlette.routing import Route

try:
    from mcp.server.fastmcp import Context as McpContext
    from mcp.server.fastmcp import FastMCP
    MCP_SDK_IMPORT_ERROR: Exception | None = None
except Exception as ex:  # pragma: no cover - exercised only in non-embedded runtimes
    McpContext = Any  # type: ignore[assignment]
    FastMCP = None  # type: ignore[assignment]
    MCP_SDK_IMPORT_ERROR = ex

try:
    from auth import get_current_request_bearer_token
    from mcp_loopback import DutyLoopbackBusyError, DutyLoopbackClient
    from models.schemas import DutyPlanPresetModel, DutyRosterEntryPatch
except ImportError:
    from .auth import get_current_request_bearer_token
    from .mcp_loopback import DutyLoopbackBusyError, DutyLoopbackClient
    from .models.schemas import DutyPlanPresetModel, DutyRosterEntryPatch

MCP_REQUEST_SOURCE = "mcp"


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
        instructions=(
            "Expose Duty-Agent workspace inspection and scheduling tools. "
            "These MCP tools always reuse the existing FastAPI HTTP/WebSocket chain instead of calling services directly. "
            "When the selected plan leaves api_key empty, provide the upstream provider key through MCP server env vars "
            "`DUTY_AGENT_MCP_API_KEY` or the legacy fallback `DUTY_AGENT_API_KEY`. "
            "Do not rely on stdin for API keys."
        ),
        streamable_http_path="/",
        stateless_http=True,
        json_response=True,
    )

    def require_runtime():
        runtime = getattr(parent_app.state, "runtime", None)
        if runtime is None:
            raise RuntimeError("Runtime is not initialized.")
        return runtime

    def create_loopback_client() -> DutyLoopbackClient:
        runtime = require_runtime()
        bearer_token = get_current_request_bearer_token()
        if not bearer_token:
            raise RuntimeError("Current MCP request does not have an authenticated bearer token.")
        return DutyLoopbackClient(
            parent_app,
            bearer_token=bearer_token,
            trace_id=runtime.new_trace_id(),
            request_source=MCP_REQUEST_SOURCE,
        )

    async def report_mcp_progress(ctx: McpContext | None, progress_state: dict[str, float], item: dict[str, Any]) -> None:
        if ctx is None:
            return

        phase = str(item.get("phase") or "").strip()
        message = str(item.get("message") or "").strip() or phase or "Running"
        progress_state["value"] += 0.25 if phase == "stream_chunk" else 1.0
        await ctx.report_progress(progress_state["value"], None, message)

    @server.tool(
        name="inspect_workspace",
        description="Read current engine capabilities and the full workspace snapshot through the existing backend HTTP APIs.",
    )
    async def inspect_workspace() -> dict[str, Any]:
        client = create_loopback_client()
        return await client.inspect_workspace()

    @server.tool(
        name="update_scheduler_config",
        description="Patch scheduler config through the existing backend config API.",
    )
    async def update_scheduler_config(
        expected_version: int | None = None,
        selected_plan_id: str | None = None,
        plan_presets: list[DutyPlanPresetModel] | None = None,
        duty_rule: str | None = None,
    ) -> dict[str, Any]:
        client = create_loopback_client()
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
        return await client.update_scheduler_config(patch_payload)

    @server.tool(
        name="replace_roster",
        description="Replace the full roster through the existing backend roster API.",
    )
    async def replace_roster(roster: list[DutyRosterEntryPatch]) -> dict[str, Any]:
        client = create_loopback_client()
        roster_payload = [
            entry.model_dump(exclude_none=True, exclude_unset=True)
            if hasattr(entry, "model_dump")
            else dict(entry)
            for entry in roster
        ]
        return await client.replace_roster(roster_payload)

    @server.tool(
        name="edit_schedule_entry",
        description="Save one schedule entry edit through the existing backend schedule-entry API.",
    )
    async def edit_schedule_entry(
        target_date: str,
        area_assignments: dict[str, list[str]],
        source_date: str | None = None,
        day: str | None = None,
        note: str | None = None,
        confirm_overwrite: bool = False,
        ledger_mode: Literal["record", "skip"] = "record",
    ) -> dict[str, Any]:
        client = create_loopback_client()
        payload = {
            "source_date": source_date,
            "target_date": target_date,
            "day": day,
            "area_assignments": area_assignments,
            "note": note,
            "confirm_overwrite": confirm_overwrite,
            "ledger_mode": ledger_mode,
        }
        return await client.edit_schedule_entry(payload)

    @server.tool(
        name="run_schedule",
        description=(
            "Run the scheduling engine through the existing duty live WebSocket chain. "
            "If the control channel is busy, this tool returns busy instead of silently bypassing the WebSocket path."
        ),
    )
    async def run_schedule(
        instruction: str = "",
        ctx: McpContext | None = None,
    ) -> dict[str, Any]:
        client = create_loopback_client()
        progress_state = {"value": 0.0}
        try:
            return await client.run_schedule(
                instruction=instruction,
                progress_callback=lambda item: report_mcp_progress(ctx, progress_state, item),
            )
        except DutyLoopbackBusyError as ex:
            raise RuntimeError(str(ex)) from ex

    return server.streamable_http_app()
