# FastAPI Step 1 Scratchpad

- Goal: make WebView and browser load the same FastAPI-hosted web page.
- Scope of this step:
  - FastAPI serves `Assets_Duty/web` at `/app/`
  - C# resolves the backend app URL from the running Python/FastAPI process
  - WebView/browser open the FastAPI app URL instead of `file://test.html`
  - The web page starts in HTTP transport mode and avoids the old host bridge by default
- Follow-up work for later steps:
  - move write operations (`save_config`, `save_roster`, `run_core`) fully onto FastAPI APIs
  - remove `DutyLocalPreviewHostedService` business routes
  - strip unused WebView bridge code from the host

## Implemented

- `Assets_Duty/core.py`
  - mounted `Assets_Duty/web` at `/app`
  - added `/app` redirect to `/app/`
- `Assets_Duty/web/index.html`
  - redirects to `test.html?transport=http`
- `Assets_Duty/web/test.html`
  - added `backend` runtime mode
  - `transport=http` disables the old WebView host bridge
  - startup now loads `/api/v1/snapshot` from FastAPI
  - API keys from snapshot are masked client-side in this step
  - run/save paths are intentionally not migrated yet; page shows transitional warnings
- `Services/DutyPythonIpcService.cs`
  - exposes `ServerBaseUrl` and `WebAppUrl`
- `Services/DutyScheduleOrchestrator.cs`
  - added `GetWebAppUrlAsync()`
- `Views/SettingPages/DutyWebSettingsPage.axaml.cs`
  - WebView now starts from `about:blank`
  - on load it waits for backend readiness and navigates to FastAPI `/app/`
  - browser-open action now targets the FastAPI app URL

## Verification

- `python -m unittest discover -s Assets_Duty -p "test_*.py"`: passed (`60` tests)
- `dotnet build Duty-Agent.csproj`: passed
- known pre-existing warning remains:
  - `WindowsBase` version conflict during `.NET` build

## Next Step

- move write traffic off the old host bridge:
  - config save
  - roster save
  - schedule run
- after that, the host can stop posting snapshots/theme/messages into the page

## 2026-03-18 Follow-up

- removed `area_names` / `area_per_day_counts` from the web test console as user-configurable settings
- `Assets_Duty/web/test.html`
  - deleted the area-management UI and the per-area-count textarea
  - removed both fields from page config state and from the old overwrite payload
  - schedule/roster/legacy preview now infer areas from actual schedule data instead of config
  - local mock mode now falls back to schedule-derived areas, or `default_area` only when no schedule exists
- intent of this cleanup:
  - area metadata is no longer treated as persisted/manual settings
  - page-side config is closer to the future FastAPI canonical settings surface

## 2026-03-18 Config Migration

- completed the one-shot migration of the web config surface to FastAPI canonical backend config
- canonical config handled by the page is now:
  - `version`
  - `selected_plan_id`
  - `plan_presets[]`
  - `duty_rule`
- host-only fields were removed from the web config form:
  - `python_path`
  - `enable_mcp`
  - `enable_auto_run`
  - `auto_run_day`
  - `auto_run_time`
  - `component_refresh_time`
  - `start_from_today`
- added optimistic local draft + explicit save/reload flow in `Assets_Duty/web/test.html`
  - save uses `PATCH /api/v1/config`
  - reload uses `GET /api/v1/config`
  - config save sends `expected_version`
  - version conflict triggers automatic reload
- backend config store now persists and returns `version`
  - old config files are normalized to include it
  - effective no-op patches keep the same version
  - effective config changes increment the version
- compatibility note:
  - desktop host code still compiles and still consumes `/api/v1/config`
  - version fields were added to C# backend models, but host save paths do not depend on them yet

## 2026-03-18 Verification

- `python -m unittest test_config_store` in `Assets_Duty`: passed (`7` tests)
- `python -m unittest discover -s Assets_Duty -p "test_*.py"`: passed (`63` tests)
- `node --check` on the extracted `Assets_Duty/web/test.html` script: passed
- `dotnet build Duty-Agent.csproj`: passed
- existing `WindowsBase` conflict warning remains unchanged

## 2026-03-18 Write Chain Migration

- migrated roster save onto formal FastAPI APIs
  - added `GET /api/v1/roster`
  - added `PUT /api/v1/roster`
  - roster writes now use Python-side normalization + atomic CSV save
  - normalization mirrors the old host behavior:
    - blank names dropped
    - invalid/duplicate IDs re-assigned
    - duplicate names get numeric suffixes
- migrated schedule run in the web page onto the formal FastAPI SSE API
  - page now sends `POST /api/v1/duty/schedule`
  - page reads the streaming SSE response directly with `fetch(...).body.getReader()`
  - progress events are shown in the existing run status/log UI
  - completion refreshes `/api/v1/snapshot`
  - run is blocked when canonical backend config is dirty
  - run waits for any pending roster save to finish successfully before dispatch
- touched backend request metadata handling
  - `routers/duty.py` now respects request headers for `trace_id` / `request_source` when the schedule body omits them

## 2026-03-18 Write Chain Verification

- `python -m unittest discover -s Assets_Duty -p "test_*.py"`: passed (`65` tests)
- added roster-specific tests:
  - Python roster normalization + persistence round-trip
  - FastAPI roster PUT/GET round-trip with `TestClient`
- `node --check` on the extracted `Assets_Duty/web/test.html` script: passed
- `dotnet build Duty-Agent.csproj`: passed
- existing `WindowsBase` conflict warning remains unchanged

## 2026-03-18 Cleanup Pass

- cleaned the web page to remove retired business-side bridge/overwrite flows
  - removed the `bridge` runtime mode option from `test.html`
  - removed the old overwrite API panel and its JavaScript code path
  - page now has only:
    - `backend` mode for the real FastAPI path
    - `mock` mode for local simulation
    - optional host capability bridge for notifications / browser open
- cleaned the WebView host bridge contract
  - `DutyWebSettingsPage.axaml.cs` now rejects `ready / load_all / save_config / save_roster / run_core`
  - retained host-only actions:
    - `publish_notification`
    - `trigger_run_completion_notification`
    - `trigger_duty_reminder_notification`
    - `open_test_in_browser`
- retired the old local preview business endpoints
  - `DutyLocalPreviewHostedService.cs` now returns `410` for:
    - `/`
    - `/test.html`
    - `/api/v1/schedule/overwrite`
  - health now reports blank `preview_url` / `api_overwrite_url`
  - MCP endpoint remains untouched in this pass

## 2026-03-18 Cleanup Verification

- `python -m unittest discover -s Assets_Duty -p "test_*.py"`: passed (`65` tests)
- `node --check` on the extracted `Assets_Duty/web/test.html` script: passed
- `dotnet build Duty-Agent.csproj`: passed
- existing `WindowsBase` conflict warning remains unchanged

## 2026-03-18 Full Legacy Cleanup

- removed the old local preview / MCP C# service completely
  - deleted `Services/DutyLocalPreviewHostedService.cs`
  - removed plugin registration from `Plugin.cs`
  - removed lifecycle start/stop wiring from `Services/DutyPluginLifecycle.cs`
- removed the dead WebView business bridge implementation
  - `Views/SettingPages/DutyWebSettingsPage.axaml.cs` no longer injects the preview service
  - deleted old bridge handlers for config save / roster save / run dispatch
  - deleted old bridge DTOs and message models tied to snapshot/run/config-saved flows
  - host bridge now only keeps desktop capability actions:
    - `publish_notification`
    - `trigger_run_completion_notification`
    - `trigger_duty_reminder_notification`
    - `open_test_in_browser`
- cleaned stale preview wording from `Assets_Duty/web/test.html`
  - removed the local preview URL tag/state helper
  - browser-open fallback now uses the current FastAPI-hosted page URL directly

## 2026-03-18 Full Legacy Cleanup Verification

- targeted residual search for deleted preview/bridge symbols in `Plugin.cs`, `Services/*.cs`, and `Views/*.cs`: clean
- targeted residual search for `localPreviewUrl` / `previewUrlTag` in `Assets_Duty/web/test.html`: clean
- `python -m unittest discover -s Assets_Duty -p "test_*.py"`: passed (`65` tests)
- `node --check` on the re-extracted `Assets_Duty/web/test.html` script: passed
- `dotnet build Duty-Agent.csproj`: passed
- existing `WindowsBase` conflict warning remains unchanged

## 2026-03-18 Hosted WebView Fixes

- fixed the hosted WebView settings-save false positive in `Assets_Duty/web/test.html`
  - local-only debug fields (`per_day`, `notification_templates`) now persist in browser storage under `duty-agent.local-debug-prefs.v1`
  - backend config save now performs a post-save reload verification before reporting success
  - status text for local-only edits now explicitly says they are saved on the current device
- reduced WebView freeze risk during schedule execution in `Assets_Duty/web/test.html`
  - stream chunks are now buffered and flushed on a timer instead of mutating the UI for every chunk
  - per-chunk `STREAM` logs were removed from the hot path
  - log panel re-rendering is now scheduled instead of fully re-rendering on every log append

## 2026-03-18 Hosted WebView Fixes Verification

- re-extracted the current `Assets_Duty/web/test.html` script and ran `node --check`: passed
- `dotnet build Duty-Agent.csproj`: passed
- existing `WindowsBase` conflict warning remains unchanged

## 2026-03-19 Native Host Settings + Thinking Parse Hardening

- fixed native C# host settings persistence / stream freeze issues
  - `Views/SettingPages/Modules/DutyMainSettingsBackendModule.cs`
    - backend config patch now carries `ExpectedVersion` from the current backend snapshot
  - `Views/SettingPages/DutyMainSettingsPage.axaml.cs`
    - page unload now flushes pending config apply work before leaving the settings page
    - added throttled reasoning-stream buffering so WebView/Avalonia UI does not repaint for every streamed chunk
    - preserved streamed reasoning text display while reducing UI churn
- hardened Python LLM structured parsing without removing streamed thinking display
  - `Assets_Duty/llm_transport.py`
    - added normalization for structured parse only
    - strips `<think>`, `<thinking>`, `<reasoning>`, `<analysis>` blocks before JSON/CSV extraction
    - treats `RESET` only as a standalone control line, not as any incidental word inside reasoning text
    - tag extraction is now case-insensitive and uses the last matching block
    - JSON parsing now falls back to decoder-based object scanning after fenced/tagged candidates
  - added `Assets_Duty/test_llm_transport.py`
    - regression coverage for reasoning-wrapped JSON
    - regression coverage for reasoning-wrapped CSV
    - regression coverage for real `RESET` control lines

## 2026-03-19 Native Host Settings + Thinking Parse Verification

- `python -m unittest Assets_Duty.test_llm_transport`: passed (`4` tests)
- `python -m unittest discover -s Assets_Duty -p "test_*.py"`: passed (`69` tests)
- `dotnet build Duty-Agent.csproj`: passed
- existing `WindowsBase` conflict warning remains unchanged

## 2026-03-19 Native Host Settings Follow-up

- tightened native C# host-page backend config persistence
  - `Views/SettingPages/Modules/DutyMainSettingsBackendModule.cs`
    - added `MatchesSettings(...)` to compare the intended host-page backend settings against the reloaded FastAPI config
  - `Views/SettingPages/Modules/DutyMainSettingsSaveCoordinator.cs`
    - backend config save now performs a read-after-write verification load
    - if the reloaded FastAPI config does not match the requested plan preset/model/rule state, the save is surfaced as failed and the page is rebound to the actual backend values
- reduced post-run host UI freeze risk
  - `Services/DutyPythonIpcService.cs`
    - backend HTTP and SSE awaits now use `ConfigureAwait(false)` so stream parsing and final response handling do not resume on the UI thread
  - `Services/DutyScheduleOrchestrator.cs`
    - backend async calls also avoid capturing the UI context
  - `Views/SettingPages/DutyMainSettingsPage.axaml.cs`
    - after a successful run, preview refresh is now posted in the background instead of being awaited inline by the run button handler
    - the host page can render the success state and re-enable interaction before the schedule/roster preview redraw completes

## 2026-03-19 Native Host Settings Follow-up Verification

- `dotnet build Duty-Agent.csproj`: passed
- `python -m unittest discover -s Assets_Duty -p "test_*.py"`: passed (`69` tests)
- existing `WindowsBase` conflict warning remains unchanged

## 2026-03-20 Unified Settings API + Dual-Entry Save Unification

- added unified FastAPI settings API and application-layer orchestration
  - new `Assets_Duty/application/settings_service.py`
    - combines `host-config.json` and `config.json`
    - validates `expected.host_version` / `expected.backend_version`
    - writes backend then host with authoritative read-back
    - returns unified `document/applied/versions/warnings/trace_id`
  - new `Assets_Duty/routers/settings.py`
    - `GET /api/v1/settings`
    - `PATCH /api/v1/settings`
  - `Assets_Duty/core.py`
    - registered the new settings router
  - `Assets_Duty/runtime.py`
    - runtime now exposes `settings_service`
- extended Python schema/state support for unified settings
  - `Assets_Duty/models/schemas.py`
    - added host editable models, unified settings document, patch request, mutation result
  - `Assets_Duty/state_ops.py`
    - added `host_config` path support to `Context`
    - added host-config normalization / default creation / load / save helpers
    - host config now has persistent `version`
- unified C# models and IPC client
  - `Models/DutyConfig.cs`
    - added persistent `Version`
  - new `Models/DutySettingsModels.cs`
    - unified settings document/patch/result models
  - `Services/DutyConfigManager.cs`
    - host config version now increments only on effective changes
    - persisted snapshot tracking added for version semantics
  - `Services/DutyPythonIpcService.cs`
    - added `GetSettingsAsync` / `PatchSettingsAsync`
    - unified settings patch now parses body on non-2xx and returns structured result
  - `Services/DutyScheduleOrchestrator.cs`
    - added `LoadSettingsAsync` / `PatchSettingsAsync`
- native settings page now uses unified settings load/save flow
  - `Views/SettingPages/Modules/DutyMainSettingsSaveCoordinator.cs`
    - no longer saves host/backend separately
    - now builds a unified patch and consumes the authoritative returned document
  - `Views/SettingPages/Modules/DutyMainSettingsModels.cs`
    - save context now carries `LastLoadedDocument`
    - save outcome now carries `AppliedDocument`
  - `Views/SettingPages/DutyMainSettingsPage.axaml.cs`
    - removed host-first/backend-later dual-stage load path
    - page now loads unified settings once on startup / refresh
    - auto-save now carries both expected versions and rebinds to authoritative settings on both success and failure
    - settings controls are disabled until unified settings load completes
- hosted web settings page now uses unified settings instead of backend config + snapshot merge
  - `Assets_Duty/web/test.html`
    - startup now loads `/api/v1/settings` for config and `/api/v1/snapshot` only for roster/state
    - formal config save switched from `/api/v1/config` to `/api/v1/settings`
    - formal config now auto-saves with debounce instead of waiting for explicit save
    - config reload/save failures now always rebind to the authoritative settings document returned by FastAPI
    - `/snapshot` no longer mutates the formal config in memory
    - local-only debug prefs (`per_day`, `notification_templates`) remain local-only

## 2026-03-20 Unified Settings Verification

- added `Assets_Duty/test_settings_api.py`
  - covers unified get
  - host-only patch version increment
  - backend-only patch version increment
  - mixed patch
  - stale version conflict
  - second-step write failure returning authoritative current document
- `python -m unittest discover -s Assets_Duty -p "test_*.py"`: passed (`75` tests)
- `dotnet build Duty-Agent.csproj`: passed
- `node --check` on extracted `Assets_Duty/web/test.html` script: passed
- existing `WindowsBase` conflict warning remains unchanged
## 2026-03-20 native settings durability

- Added `DutySettingsDraftService` and `SettingsDraftPath` to persist a local editable settings draft with `draft_id` under plugin `data/settings-draft.json`.
- Native `DutyMainSettingsPage` now persists the current settings draft on every queued config edit instead of waiting for `LostFocus`/page unload durability.
- Added `TextChanged` handlers for native settings text inputs (`PlanNameBox`, `ApiKeyBox`, `ModelBox`, `BaseUrlBox`, `DutyRuleBox`, `AutoRunIntervalBox`) so model/base-url edits enter the save pipeline immediately.
- `OnPageUnloaded` no longer awaits the HTTP save chain; it just queues an immediate sync attempt after persisting the local draft.
- Unified save flow now tracks local edit revisions so an older `/api/v1/settings` response cannot overwrite newer edits made while a save is in flight.
- Successful authoritative save clears the local draft only if the same `draft_id` is still current; newer local edits keep their draft file intact.
- `LoadSettingsAsync()` now restores any pending local draft after authoritative settings load, applies it to the native form, and immediately replays it to unified settings.
- `DutyPluginLifecycle.StartAsync()` now starts a background replay of any pending local settings draft so restarted apps can recover unsynced edits without requiring the settings page to stay open.
- Verification after these changes:
  - `dotnet build Duty-Agent.csproj` passed (existing `WindowsBase` warning unchanged)
  - `python -m unittest discover -s Assets_Duty -p "test_*.py"` passed (`75` tests)

## 2026-03-20 unified live websocket

- Added FastAPI live settings websocket endpoint at `/api/v1/settings/live` in `Assets_Duty/routers/settings.py`.
- Live websocket protocol:
  - client `hello` -> server replies `hello` with authoritative `document`
  - client `patch` -> server replies `accepted` then `applied`/`conflict`/`error`
- The websocket still delegates to the existing unified `settings_service.patch_settings()` implementation, so application-layer save logic stays single-sourced.
- `DutyPythonIpcService.PatchSettingsAsync()` now prefers a persistent `ClientWebSocket` live session and falls back to HTTP `PATCH /api/v1/settings` if the websocket fails.
- The desktop host therefore keeps the existing save coordinator/page logic, but the active transport for settings saves is now an ordered live socket instead of repeated one-off HTTP patches.
- Added websocket coverage in `Assets_Duty/test_settings_api.py` for `hello -> accepted -> applied`.
- Verification after live websocket:
  - `dotnet build Duty-Agent.csproj` passed (existing `WindowsBase` warning unchanged)
  - `python -m unittest discover -s Assets_Duty -p "test_*.py"` passed (`76` tests)

## 2026-03-20 native settings save-chain stabilization

- Native host settings no longer rely on Python unified `/api/v1/settings` for their formal save path.
  - `Services/DutyScheduleOrchestrator.cs`
    - `LoadSettingsAsync()` now composes authoritative settings from:
      - host config via `DutyConfigManager`
      - backend config via FastAPI `/api/v1/config`
    - `PatchSettingsAsync()` now acts as the real C# application-layer facade:
      - host changes write locally through `DutyConfigManager`
      - backend changes write through `/api/v1/config`
      - result is recomposed into one `DutySettingsMutationResult`
- FastAPI unified settings service was also hardened for web clients:
  - `Assets_Duty/application/settings_service.py`
    - `GET /api/v1/settings` is now pure-read and no longer normalizes by rewriting files during reads
    - `PATCH /api/v1/settings` now locks only the domains actually being changed:
      - backend-only patch -> `config.json` lock only
      - host-only patch -> `host-config.json` lock only
      - mixed patch -> both locks in fixed order
- Partial settings patch semantics were aligned end-to-end:
  - `Views/SettingPages/Modules/DutyMainSettingsSaveCoordinator.cs`
  - `Services/DutySettingsDraftService.cs`
  - `Assets_Duty/web/test.html`
  - `Assets_Duty/application/settings_service.py`
  - `Services/DutyScheduleOrchestrator.cs`
  - only the versions for changed domains are now sent and checked
  - backend-only saves no longer fail because host version changed
  - host-only saves no longer fail because backend version changed
- Added regression coverage in `Assets_Duty/test_settings_api.py` for:
  - backend-only patch ignoring stale host version
  - host-only patch ignoring stale backend version
- Verification after save-chain stabilization:
  - `dotnet build Duty-Agent.csproj` passed (existing `WindowsBase` warning unchanged)
  - `python -m unittest discover -s Assets_Duty -p "test_*.py"` passed (`78` tests)
  - extracted `Assets_Duty/web/test.html` script parsed successfully in Node

## 2026-03-20 review-finding fixes: socket serialization and host/backend ownership

- Resolved the `host-config.json` dual-writer regression by re-drawing ownership:
  - C# host remains the only writer for `host-config.json`
  - Python/FastAPI remains the writer for backend `config.json`
- `Assets_Duty/application/settings_service.py`
  - `PATCH /api/v1/settings` now rejects any `changes.host` payload with `400` and warning `host_settings_managed_by_host`
  - backend-only patches still work and continue to return a combined read-only settings document
  - Python no longer writes `host-config.json` in unified settings patch flow
- `Services/DutyScheduleOrchestrator.cs`
  - native unified settings load/save is again composed in C#:
    - host settings from `DutyConfigManager`
    - backend settings from FastAPI `/api/v1/config`
  - host patch is applied locally, backend patch remotely, and the result is recomposed into one `DutySettingsMutationResult`
- `Services/DutyPythonIpcService.cs`
  - settings patch path now uses HTTP for backend settings writes instead of sharing the long-running control websocket gate with schedule execution
  - this removes the IPC-layer serialization that caused schedule runs to block settings saves
- `Assets_Duty/routers/settings.py`
  - `/api/v1/duty/live` now uses a per-connection send queue plus a dedicated sender task
  - schedule progress and settings responses no longer call `websocket.send_json(...)` concurrently from multiple coroutines
- `Assets_Duty/test_settings_api.py`
  - updated to match the new ownership boundary:
    - host-only patch rejected
    - mixed host+backend patch rejected
    - backend-only patch still works
    - backend-only patch ignores stale host version
    - backend write failure still returns authoritative readback
- Verification after these fixes:
  - `python -m unittest discover -s Assets_Duty -p "test_settings_api.py"` passed (`8` tests)
  - `python -m unittest discover -s Assets_Duty -p "test_*.py"` passed (`77` tests)
  - `dotnet build Duty-Agent.csproj` passed (existing `WindowsBase` warning unchanged)
