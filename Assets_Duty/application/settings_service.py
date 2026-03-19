from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Dict, Optional

from state_ops import (
    CONFIG_LOCK_TIMEOUT_SECONDS,
    Context,
    _config_lock_path,
    _hydrate_runtime_config,
    _host_config_lock_path,
    _load_persisted_config_unlocked,
    _load_persisted_host_config_unlocked,
    _normalize_persisted_config,
    _normalize_persisted_host_config,
    _persisted_config_body,
    _persisted_host_config_body,
    acquire_file_lock,
    load_config,
    load_host_config,
    release_file_lock,
    save_json_atomic,
)


@dataclass
class SettingsServiceError(Exception):
    result: Dict[str, Any]
    status_code: int = 500

    def __str__(self) -> str:
        return str(self.result.get("message", "Settings service error."))


class SettingsVersionConflictError(SettingsServiceError):
    pass


def _editable_host_document(host_config: dict) -> dict:
    return {
        "auto_run_mode": host_config.get("auto_run_mode", "Off"),
        "auto_run_parameter": host_config.get("auto_run_parameter", "Monday"),
        "auto_run_time": host_config.get("auto_run_time", "08:00"),
        "auto_run_trigger_notification_enabled": bool(host_config.get("auto_run_trigger_notification_enabled", True)),
        "duty_reminder_enabled": bool(host_config.get("duty_reminder_enabled", False)),
        "duty_reminder_times": list(host_config.get("duty_reminder_times", ["07:40"])),
        "enable_mcp": bool(host_config.get("enable_mcp", False)),
        "enable_webview_debug_layer": bool(host_config.get("enable_webview_debug_layer", False)),
        "component_refresh_time": host_config.get("component_refresh_time", "08:00"),
        "notification_duration_seconds": int(host_config.get("notification_duration_seconds", 8)),
    }


def _editable_backend_document(runtime_config: dict) -> dict:
    return {
        "selected_plan_id": runtime_config.get("selected_plan_id", "standard"),
        "plan_presets": list(runtime_config.get("plan_presets", [])),
        "duty_rule": runtime_config.get("duty_rule", ""),
    }


def _settings_document(host_config: dict, backend_config: dict) -> dict:
    return {
        "host_version": int(host_config.get("version", 1)),
        "backend_version": int(backend_config.get("version", 1)),
        "host": _editable_host_document(host_config),
        "backend": _editable_backend_document(backend_config),
    }


def _settings_versions(document: dict) -> dict:
    return {
        "host": int(document.get("host_version", 1)),
        "backend": int(document.get("backend_version", 1)),
    }


def _host_change_patch(change_payload: dict) -> dict:
    supported_keys = {
        "auto_run_mode",
        "auto_run_parameter",
        "auto_run_time",
        "auto_run_trigger_notification_enabled",
        "duty_reminder_enabled",
        "duty_reminder_times",
        "enable_mcp",
        "enable_webview_debug_layer",
        "component_refresh_time",
        "notification_duration_seconds",
    }
    return {
        str(key): value
        for key, value in (change_payload or {}).items()
        if key in supported_keys and value is not None
    }


def _backend_change_patch(change_payload: dict) -> dict:
    supported_keys = {"selected_plan_id", "plan_presets", "duty_rule"}
    return {
        str(key): value
        for key, value in (change_payload or {}).items()
        if key in supported_keys and value is not None
    }


class SettingsService:
    def __init__(self, runtime):
        self._runtime = runtime

    def _load_current_document(self, trace_id: str, request_source: str) -> dict:
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=trace_id,
            request_source=request_source,
        )
        host_config = load_host_config(context)
        backend_config = load_config(context)
        return _settings_document(host_config, backend_config)

    def get_settings(self, trace_id: Optional[str] = None, request_source: str = "api") -> dict:
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        self._runtime.logger.info(
            "SettingsService",
            "Starting get_settings.",
            trace_id=effective_trace_id,
            request_source=request_source,
        )
        document = self._load_current_document(effective_trace_id, request_source)
        self._runtime.logger.info(
            "SettingsService",
            "Finished get_settings.",
            trace_id=effective_trace_id,
            request_source=request_source,
            host_version=document["host_version"],
            backend_version=document["backend_version"],
        )
        return document

    def patch_settings(
        self,
        patch_payload: Dict[str, Any],
        trace_id: Optional[str] = None,
        request_source: str = "api",
    ) -> dict:
        effective_trace_id = trace_id or self._runtime.new_trace_id()
        context = Context(
            self._runtime.data_dir,
            logger=self._runtime.logger,
            trace_id=effective_trace_id,
            request_source=request_source,
        )

        expected = dict((patch_payload or {}).get("expected") or {})
        changes = dict((patch_payload or {}).get("changes") or {})
        host_patch = _host_change_patch(changes.get("host") or {})
        backend_patch = _backend_change_patch(changes.get("backend") or {})

        self._runtime.logger.info(
            "SettingsService",
            "Starting patch_settings.",
            trace_id=effective_trace_id,
            request_source=request_source,
            host_patch_keys=sorted(host_patch.keys()),
            backend_patch_keys=sorted(backend_patch.keys()),
            expected_host_version=expected.get("host_version"),
            expected_backend_version=expected.get("backend_version"),
        )

        if not host_patch and not backend_patch:
            document = self._load_current_document(effective_trace_id, request_source)
            return {
                "success": True,
                "message": "No settings changes.",
                "restart_required": False,
                "document": document,
                "applied": {"host": None, "backend": None},
                "versions": _settings_versions(document),
                "warnings": [],
                "trace_id": effective_trace_id,
            }

        config_lock_path = _config_lock_path(context)
        host_lock_path = _host_config_lock_path(context)
        candidate_backend_runtime: Optional[dict] = None
        candidate_host_persisted: Optional[dict] = None
        restart_required = False
        warnings: list[str] = []
        write_exception: Optional[Exception] = None

        acquire_file_lock(config_lock_path, CONFIG_LOCK_TIMEOUT_SECONDS)
        acquire_file_lock(host_lock_path, CONFIG_LOCK_TIMEOUT_SECONDS)
        try:
            current_backend_persisted, backend_normalized = _load_persisted_config_unlocked(context.paths["config"])
            current_host_persisted, host_normalized = _load_persisted_host_config_unlocked(context.paths["host_config"])
            if backend_normalized:
                save_json_atomic(context.paths["config"], current_backend_persisted)
            if host_normalized:
                save_json_atomic(context.paths["host_config"], current_host_persisted)

            current_backend_version = int(current_backend_persisted.get("version", 1))
            current_host_version = int(current_host_persisted.get("version", 1))
            expected_backend_version = expected.get("backend_version")
            expected_host_version = expected.get("host_version")

            current_backend_runtime = _hydrate_runtime_config(current_backend_persisted)
            current_document = _settings_document(current_host_persisted, current_backend_runtime)

            if expected_host_version is not None and int(expected_host_version) != current_host_version:
                result = {
                    "success": False,
                    "message": f"Host config version mismatch: expected {expected_host_version}, current {current_host_version}",
                    "restart_required": False,
                    "document": current_document,
                    "applied": {"host": None, "backend": None},
                    "versions": _settings_versions(current_document),
                    "warnings": ["host_version_conflict"],
                    "trace_id": effective_trace_id,
                }
                raise SettingsVersionConflictError(result=result, status_code=409)

            if expected_backend_version is not None and int(expected_backend_version) != current_backend_version:
                result = {
                    "success": False,
                    "message": f"Config version mismatch: expected {expected_backend_version}, current {current_backend_version}",
                    "restart_required": False,
                    "document": current_document,
                    "applied": {"host": None, "backend": None},
                    "versions": _settings_versions(current_document),
                    "warnings": ["backend_version_conflict"],
                    "trace_id": effective_trace_id,
                }
                raise SettingsVersionConflictError(result=result, status_code=409)

            if backend_patch:
                candidate_backend_persisted = dict(current_backend_persisted)
                candidate_backend_persisted.update(backend_patch)
                candidate_backend_persisted = _normalize_persisted_config(candidate_backend_persisted)
                if _persisted_config_body(candidate_backend_persisted) != _persisted_config_body(current_backend_persisted):
                    candidate_backend_persisted["version"] = current_backend_version + 1
                else:
                    candidate_backend_persisted["version"] = current_backend_version
                save_json_atomic(context.paths["config"], candidate_backend_persisted)
                candidate_backend_runtime = _hydrate_runtime_config(candidate_backend_persisted)

            if host_patch:
                candidate_host_persisted = dict(current_host_persisted)
                previous_enable_mcp = bool(current_host_persisted.get("enable_mcp", False))
                previous_enable_webview_debug_layer = bool(current_host_persisted.get("enable_webview_debug_layer", False))
                candidate_host_persisted.update(host_patch)
                candidate_host_persisted = _normalize_persisted_host_config(candidate_host_persisted)
                if _persisted_host_config_body(candidate_host_persisted) != _persisted_host_config_body(current_host_persisted):
                    candidate_host_persisted["version"] = current_host_version + 1
                else:
                    candidate_host_persisted["version"] = current_host_version
                restart_required = (
                    previous_enable_mcp != bool(candidate_host_persisted.get("enable_mcp", False))
                    or previous_enable_webview_debug_layer
                    != bool(candidate_host_persisted.get("enable_webview_debug_layer", False))
                )
                save_json_atomic(context.paths["host_config"], candidate_host_persisted)
        except SettingsServiceError:
            raise
        except Exception as ex:
            write_exception = ex
        finally:
            release_file_lock(host_lock_path)
            release_file_lock(config_lock_path)

        if write_exception is not None:
            failure_document = self._load_current_document(effective_trace_id, request_source)
            raise SettingsServiceError(
                result={
                    "success": False,
                    "message": f"Settings save failed: {write_exception}",
                    "restart_required": False,
                    "document": failure_document,
                    "applied": {"host": None, "backend": None},
                    "versions": _settings_versions(failure_document),
                    "warnings": ["write_exception"],
                    "trace_id": effective_trace_id,
                },
                status_code=500,
            ) from write_exception

        authoritative_document = self._load_current_document(effective_trace_id, request_source)

        target_host = _editable_host_document(candidate_host_persisted) if candidate_host_persisted is not None else None
        target_backend = _editable_backend_document(candidate_backend_runtime) if candidate_backend_runtime is not None else None
        applied_host = None
        applied_backend = None
        if target_host is not None and authoritative_document["host"] == target_host:
            applied_host = target_host
        elif target_host is not None:
            warnings.append("host_readback_mismatch")
        if target_backend is not None and authoritative_document["backend"] == target_backend:
            applied_backend = target_backend
        elif target_backend is not None:
            warnings.append("backend_readback_mismatch")

        success = (
            (not host_patch or applied_host is not None)
            and (not backend_patch or applied_backend is not None)
        )

        message = "Settings saved."
        if not success:
            if host_patch and backend_patch:
                message = "Settings save failed verification; authoritative settings were reloaded."
            elif host_patch:
                message = "Host settings save failed verification; authoritative settings were reloaded."
            else:
                message = "Backend settings save failed verification; authoritative settings were reloaded."

        result = {
            "success": success,
            "message": message,
            "restart_required": restart_required,
            "document": authoritative_document,
            "applied": {"host": applied_host, "backend": applied_backend},
            "versions": _settings_versions(authoritative_document),
            "warnings": warnings,
            "trace_id": effective_trace_id,
        }

        self._runtime.logger.info(
            "SettingsService",
            "Finished patch_settings.",
            trace_id=effective_trace_id,
            request_source=request_source,
            success=success,
            restart_required=restart_required,
            host_applied=applied_host is not None,
            backend_applied=applied_backend is not None,
            warnings=",".join(warnings),
        )
        return result
