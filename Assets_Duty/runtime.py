from __future__ import annotations

import hmac
import os
import secrets
import time
import threading
import uuid
from datetime import datetime, time as datetime_time, timezone
from pathlib import Path
from queue import Queue
from typing import Any

from application.command_service import CommandService
from application.query_service import QueryService
from auth import normalize_access_token_mode, verify_pbkdf2_sha256_token
from diagnostics import DutyDiagnosticsLogger
from state_ops import Context, claim_auto_run_today, compute_current_duty_date, load_host_config, load_state, update_host_runtime_fields
from version import APP_VERSION

# A1 provides ``auto_run`` (trigger + catch-up pure functions). It is an optional
# dependency at import time: if it has not landed yet the worker degrades
# gracefully (each tick logs + skips) instead of breaking module import, which
# would take down the whole engine (and every test that imports ``runtime``).
try:
    from auto_run import detect_catchup_due, is_auto_run_triggered
except Exception:  # pragma: no cover - import guard for the A1 dependency
    is_auto_run_triggered = None
    detect_catchup_due = None

SKIP_AUTH_BYPASS = os.getenv("SKIP_AUTH_BYPASS", "").strip().lower() in ("1", "true", "yes")
# 20s = 3 heartbeats at the bridge's 5s health-tick cadence + drift margin; the
# old 15s TTL meant two lost ticks already flipped the bridge to disconnected.
BRIDGE_HEARTBEAT_TTL_SECONDS = 20.0
NOTIFICATION_QUEUE_MAX_SIZE = 100
NOTIFICATION_REMINDER_POLL_SECONDS = 15.0
# Reminders fire when a target time is due-but-unsent within this window, so a
# sleep/hibernate spanning the exact target minute no longer drops them.
NOTIFICATION_REMINDER_CATCHUP_WINDOW_SECONDS = 1800.0
AUTO_RUN_POLL_SECONDS = 60.0
AUTO_RUN_INSTRUCTION = "Please generate duty schedule automatically based on roster.csv."
AUTO_RUN_REQUEST_SOURCE = "automation"
AUTO_RUN_CATCHUP_MAX_LOOKBACK_DAYS = 31


def _safe_int(value: Any, default: int, *, lo: int = 0) -> int:
    """Tolerant int parse for hand-edited host-config fields.

    A dirty value (e.g. "3次") must not raise every poller tick — that would
    spam one ERROR line per minute and stall the whole check_auto_run body.
    """
    if value is None or (isinstance(value, str) and not value.strip()):
        return default
    try:
        parsed = int(str(value).strip())
    except (TypeError, ValueError):
        return default
    return max(lo, parsed)


class DutyRuntime:
    def __init__(self, data_dir: Path, disable_mcp_runtime: bool = False):
        self.data_dir = data_dir.resolve()
        self.data_dir.mkdir(parents=True, exist_ok=True)
        self.logs_dir = self.data_dir.parent / "logs"
        self.logs_dir.mkdir(parents=True, exist_ok=True)
        self.version = APP_VERSION
        self.started_at = time.monotonic()
        self.logger = DutyDiagnosticsLogger(self.logs_dir)
        self.skip_auth_bypass = SKIP_AUTH_BYPASS
        if self.skip_auth_bypass:
            # 任务2：鉴权被绕过必须在文件日志里可见（每进程启动 WARN 一次），
            # 否则只有盯着控制台的人才知道这个环境没有鉴权。
            self.logger.warn(
                "Auth",
                "SKIP_AUTH_BYPASS is ENABLED - token verification is bypassed for this process.",
            )
        self.schedule_run_lock = threading.Lock()
        self.duty_live_owner_lock = threading.Lock()
        self.duty_live_owner_id: str | None = None
        self.bridge_status_lock = threading.Lock()
        self.bridge_heartbeat_ttl_seconds = BRIDGE_HEARTBEAT_TTL_SECONDS
        self.bridge_last_seen_monotonic: float | None = None
        self.bridge_last_seen_wall: float | None = None
        self.bridge_last_source = ""
        self.notification_subscribers_lock = threading.Lock()
        self.notification_subscribers: set[Queue] = set()
        self.notification_history: list[dict[str, Any]] = []
        self.notification_reminder_stop = threading.Event()
        self.notification_reminder_thread: threading.Thread | None = None
        self.notification_reminder_sent_keys: set[str] = set()
        self.notification_reminder_sent_lock = threading.Lock()
        self._host_config_cache: dict | None = None
        self._host_config_cache_mtime: int | None = None
        self._host_config_cache_lock = threading.Lock()
        self._auto_run_helpers_warned = False
        self.host_config = self._load_host_config()
        self.disable_mcp_runtime = bool(disable_mcp_runtime)
        self.access_token_mode = normalize_access_token_mode(self.host_config.get("access_token_mode"))
        self.static_access_token_verifier = str(self.host_config.get("static_access_token_verifier", "") or "").strip()
        self.access_token = secrets.token_urlsafe(32) if self.access_token_mode == "dynamic" else ""
        self.enable_mcp_configured = bool(self.host_config.get("enable_mcp", False))
        self.enable_mcp = self.enable_mcp_configured and not self.disable_mcp_runtime
        self.command_service = CommandService(self)
        self.query_service = QueryService(self)
        self.start_notification_workers()
        self.auto_run_stop = threading.Event()
        self.auto_run_thread: threading.Thread | None = None
        # D5：当日重试完全由胜出进程的内存态决定——认领日期 + attempt 计数，
        # 不再依赖 host-config 失败计数做跨进程重试（跨进程只保证"今日仅一个
        # 进程触发"；进程崩溃导致的当日漏跑由次日 catch-up 兜底）。
        self._auto_run_claim_date: str | None = None
        self._auto_run_attempts: int = 0
        # 任务9：订阅者队列满导致的丢弃必须计数并周期性告警，不再静默。
        self.notification_queue_dropped_total = 0
        self._notification_drop_warn_at = 10
        self.start_auto_run_worker()

    def new_trace_id(self) -> str:
        return uuid.uuid4().hex

    def is_authorized(self, candidate_token: str | None) -> bool:
        if self.access_token_mode == "static":
            return verify_pbkdf2_sha256_token(candidate_token, self.static_access_token_verifier)
        if not candidate_token:
            return False
        return hmac.compare_digest(
            candidate_token.encode("utf-8"),
            self.access_token.encode("utf-8"),
        )

    def try_claim_duty_live_owner(self, owner_id: str) -> bool:
        normalized_owner = str(owner_id or "").strip()
        if not normalized_owner:
            return False

        with self.duty_live_owner_lock:
            if self.duty_live_owner_id is None:
                self.duty_live_owner_id = normalized_owner
                return True
            return self.duty_live_owner_id == normalized_owner

    def release_duty_live_owner(self, owner_id: str) -> None:
        normalized_owner = str(owner_id or "").strip()
        if not normalized_owner:
            return

        with self.duty_live_owner_lock:
            if self.duty_live_owner_id == normalized_owner:
                self.duty_live_owner_id = None

    def get_duty_live_owner(self) -> str | None:
        with self.duty_live_owner_lock:
            return self.duty_live_owner_id

    def mark_bridge_heartbeat(self, request_source: str = "bridge") -> dict:
        now_monotonic = time.monotonic()
        now_wall = time.time()
        normalized_source = str(request_source or "").strip() or "bridge"
        with self.bridge_status_lock:
            self.bridge_last_seen_monotonic = now_monotonic
            self.bridge_last_seen_wall = now_wall
            self.bridge_last_source = normalized_source
            return self._build_bridge_status_unlocked(now_monotonic)

    def get_bridge_status(self) -> dict:
        now_monotonic = time.monotonic()
        with self.bridge_status_lock:
            return self._build_bridge_status_unlocked(now_monotonic)

    def _build_bridge_status_unlocked(self, now_monotonic: float) -> dict:
        last_seen_monotonic = self.bridge_last_seen_monotonic
        last_seen_wall = self.bridge_last_seen_wall
        age_seconds = None
        connected = False

        if last_seen_monotonic is not None:
            age_seconds = max(0.0, now_monotonic - last_seen_monotonic)
            connected = age_seconds <= self.bridge_heartbeat_ttl_seconds

        last_seen_iso = None
        if last_seen_wall is not None:
            last_seen_iso = datetime.fromtimestamp(last_seen_wall, tz=timezone.utc).isoformat()

        return {
            "status": "connected" if connected else "disconnected",
            "connected": connected,
            "last_seen_at": last_seen_wall,
            "last_seen_iso": last_seen_iso,
            "age_seconds": round(age_seconds, 3) if age_seconds is not None else None,
            "ttl_seconds": int(self.bridge_heartbeat_ttl_seconds),
            "source": self.bridge_last_source or None,
        }

    def reload_host_config(self) -> dict:
        # Force a fresh read: the mtime cache can serve a stale snapshot right
        # after our own persist on filesystems with coarse timestamp granularity
        # (two writes within one kernel timekeeping tick share st_mtime_ns).
        with self._host_config_cache_lock:
            self._host_config_cache = None
            self._host_config_cache_mtime = None
        self.host_config = self._load_host_config()
        return self.host_config

    def start_notification_workers(self) -> None:
        if self.notification_reminder_thread is not None:
            return

        self.notification_reminder_thread = threading.Thread(
            target=self._notification_reminder_loop,
            name="DutyNotificationReminder",
            daemon=True,
        )
        self.notification_reminder_thread.start()

    def stop_notification_workers(self) -> None:
        self.notification_reminder_stop.set()
        thread = self.notification_reminder_thread
        if thread is not None and thread.is_alive():
            thread.join(timeout=2.0)

    def start_auto_run_worker(self) -> None:
        if self.auto_run_thread is not None:
            return

        self.auto_run_thread = threading.Thread(
            target=self._auto_run_loop,
            name="DutyAutoRun",
            daemon=True,
        )
        self.auto_run_thread.start()

    def stop_auto_run_worker(self) -> None:
        self.auto_run_stop.set()
        thread = self.auto_run_thread
        if thread is not None and thread.is_alive():
            thread.join(timeout=2.0)

    def _auto_run_loop(self) -> None:
        # Avoid racing other startup work (lock/config init) on the very first tick.
        self.auto_run_stop.wait(5.0)
        while not self.auto_run_stop.is_set():
            try:
                self.check_auto_run()
            except Exception as ex:
                # The handler itself must never raise: an exception here would
                # kill this daemon thread permanently (start_* guards prevent
                # recreation) and silently disable auto-run for the process.
                try:
                    self.logger.error("AutoRun", "tick failed.", exc=ex)
                except Exception:
                    pass
            self.auto_run_stop.wait(AUTO_RUN_POLL_SECONDS)

    def check_auto_run(self) -> None:
        cfg = self._load_host_config()
        mode = str(cfg.get("auto_run_mode") or "Off")
        if mode.lower() == "off":
            return

        if is_auto_run_triggered is None or detect_catchup_due is None:
            # Log the missing dependency once instead of spamming a WARN every
            # 60s tick for the lifetime of the process.
            if not self._auto_run_helpers_warned:
                self._auto_run_helpers_warned = True
                self.logger.warn(
                    "AutoRun",
                    "Trigger helpers unavailable (auto_run module missing); auto-run is disabled.",
                )
            return

        now = datetime.now()
        today = now.strftime("%Y-%m-%d")
        last = str(cfg.get("last_auto_run_date", "") or "")
        retry_times = _safe_int(cfg.get("auto_run_retry_times"), 3, lo=0)
        parameter = str(cfg.get("auto_run_parameter", "") or "")

        # D5：当日重试 = 胜出进程内存态（claim 日期 + attempt 计数）。host-config
        # 里的失败计数只作为诊断信息持久化，不再驱动跨进程重试。
        retry_pending = (
            self._auto_run_claim_date == today
            and self._auto_run_attempts < max(1, retry_times)
        )

        target_time = self._parse_auto_run_time(cfg.get("auto_run_time"))
        normal_due = (
            bool(is_auto_run_triggered(mode, parameter, last, now))
            and last != today
            and (target_time is None or now.time() >= target_time)
        )
        if not retry_pending and not normal_due and not detect_catchup_due(mode, parameter, last, now.date()):
            return

        # Busy pre-check (does not consume a retry): if a manual run holds the
        # schedule lock right now, bail before claiming/executing.
        if not self.schedule_run_lock.acquire(blocking=False):
            self.logger.info("AutoRun", "Skipped: manual run in progress (busy).")
            return
        self.schedule_run_lock.release()

        if retry_pending:
            self.logger.info(
                "AutoRun",
                "Retrying auto run (in-process attempt).",
                attempt=self._auto_run_attempts + 1,
                retry_times=retry_times,
            )
        else:
            # D5：跨进程原子认领——文件锁临界区内"读 last_auto_run_date →
            # 非今日则置为今日"一步完成，先 claim 再执行。认领失败说明另一
            # 进程已拥有今日执行权，本轮直接退出。
            if not claim_auto_run_today(self.data_dir, self.logger, today=today):
                self.logger.info("AutoRun", "Skipped: another process already claimed today's auto run.")
                return
            self._auto_run_claim_date = today
            self._auto_run_attempts = 0

        result = self._invoke_auto_run(now)
        if str(result.get("code", "") or "").lower() == "busy":
            self.logger.info("AutoRun", "Skipped tick: run_schedule reported busy (no retry counted).")
            return

        ok = str(result.get("status", "") or "").lower() in {"success", "ok"}
        # host-config 失败计数仅作诊断持久化；今日预算以内存 attempt 为准。
        failures_today = _safe_int(cfg.get("ai_consecutive_failures"), 0, lo=0)
        if str(cfg.get("ai_failures_date", "") or "") != today:
            failures_today = 0

        if ok:
            self._auto_run_claim_date = None
            self._auto_run_attempts = 0
            self._persist_auto_run_state(ai_consecutive_failures=0, ai_failures_date=today)
            self.logger.info("AutoRun", "Auto schedule succeeded.", last_auto_run_date=today)
            return

        self._auto_run_attempts += 1
        failures_today += 1
        self._persist_auto_run_state(ai_consecutive_failures=failures_today, ai_failures_date=today)
        if self._auto_run_attempts >= max(1, retry_times):
            self._auto_run_claim_date = None  # 今日放弃：内存重试预算耗尽
            self.logger.warn(
                "AutoRun",
                "Auto run failed; gave up for today after retries.",
                attempts=self._auto_run_attempts,
                retry_times=retry_times,
                last_auto_run_date=today,
            )
        else:
            self.logger.warn(
                "AutoRun",
                "Auto run failed; will retry next tick (in-process).",
                attempts=self._auto_run_attempts,
                retry_times=retry_times,
            )

    @staticmethod
    def _parse_auto_run_time(raw: Any) -> datetime_time | None:
        text = str(raw or "").strip()
        if not text:
            return None
        try:
            hour_str, minute_str = text.split(":", 1)
            return datetime_time(hour=int(hour_str), minute=int(minute_str))
        except (ValueError, TypeError):
            return None

    def _invoke_auto_run(self, now: datetime) -> dict:
        # In-process dispatch: no HTTP, no bearer token. ``request_source``
        # marks the run as automation-origin so observers can tell it apart
        # from manual/API-driven runs. No ``client_change_id`` is supplied.
        return self.command_service.run_schedule(
            {"instruction": AUTO_RUN_INSTRUCTION, "request_source": AUTO_RUN_REQUEST_SOURCE},
            progress_callback=None,
            stop_event=None,
        )

    def _persist_auto_run_state(
        self,
        last_auto_run_date: str | None = None,
        ai_consecutive_failures: int | None = None,
        ai_failures_date: str | None = None,
    ) -> None:
        # D5：日期回写已由 state_ops.claim_auto_run_today 在认领临界区内完成，
        # 这里保留该助手但 check_auto_run 只用它更新失败计数（诊断用途）。
        patch: dict[str, Any] = {}
        if last_auto_run_date is not None:
            patch["last_auto_run_date"] = last_auto_run_date
        if ai_consecutive_failures is not None:
            patch["ai_consecutive_failures"] = ai_consecutive_failures
        if ai_failures_date is not None:
            patch["ai_failures_date"] = ai_failures_date
        if not patch:
            return
        ctx = Context(self.data_dir, logger=self.logger, request_source="auto_run")
        update_host_runtime_fields(ctx, patch)
        self.reload_host_config()

    def subscribe_notifications(self) -> Queue:
        queue: Queue = Queue(maxsize=NOTIFICATION_QUEUE_MAX_SIZE)
        with self.notification_subscribers_lock:
            self.notification_subscribers.add(queue)
        return queue

    def unsubscribe_notifications(self, queue: Queue) -> None:
        with self.notification_subscribers_lock:
            self.notification_subscribers.discard(queue)

    def publish_notification(
        self,
        event_type: str,
        title: str,
        body: str = "",
        *,
        level: str = "info",
        route: str = "/dashboard",
        source: str = "backend",
        targets: list[str] | None = None,
        data: dict | None = None,
    ) -> dict:
        normalized_targets = self._normalize_notification_targets(targets)
        event = {
            "id": uuid.uuid4().hex,
            "type": str(event_type or "info").strip() or "info",
            "title": str(title or "").strip() or "Duty-Agent",
            "body": str(body or "").strip(),
            "level": str(level or "info").strip().lower() or "info",
            "route": self._normalize_notification_route(route),
            "source": str(source or "backend").strip() or "backend",
            "targets": normalized_targets,
            "created_at": time.time(),
            "created_at_iso": datetime.now(tz=timezone.utc).isoformat(),
            "data": data or {},
        }
        with self.notification_subscribers_lock:
            subscribers = list(self.notification_subscribers)
            self.notification_history.append(event)
            self.notification_history = self.notification_history[-50:]

        dropped = 0
        for queue in subscribers:
            # Drop-oldest on a full queue, with one retry: between our
            # get_nowait and put_nowait another publisher may refill the
            # queue; without the retry the event would be lost entirely.
            for _ in range(2):
                try:
                    queue.put_nowait(event)
                    break
                except Exception:
                    try:
                        queue.get_nowait()
                    except Exception:
                        pass
            else:
                # 两次尝试后仍未入队：事件对该订阅者彻底丢失（任务9：不再静默）。
                dropped += 1

        if dropped:
            self.notification_queue_dropped_total += dropped
            if self.notification_queue_dropped_total >= self._notification_drop_warn_at:
                # 每 10 次丢弃 WARN 一次：太慢的订阅者应该被看见，但不能
                # 每丢一条就刷一行日志。
                self.logger.warn(
                    "NotificationBus",
                    "Subscriber queue full; notifications were dropped.",
                    dropped_this_event=dropped,
                    dropped_total=self.notification_queue_dropped_total,
                    subscriber_count=len(subscribers),
                )
                self._notification_drop_warn_at += 10

        self.logger.info(
            "NotificationBus",
            "Published notification event.",
            trace_id=event["id"],
            request_source=event["source"],
            event_type=event["type"],
            level=event["level"],
            targets=",".join(normalized_targets),
            subscriber_count=len(subscribers),
        )
        return event

    def publish_schedule_result_notification(self, result: dict) -> None:
        targets = self.get_notification_targets("schedule_completion_notification_enabled")
        if not targets:
            return

        status = str((result or {}).get("status") or "").strip().lower()
        success = status in {"success", "ok"}
        message = str((result or {}).get("message") or "").strip()

        # Look up the effective duty date and area assignments so the
        # notification carries the same richness as the old plugin's
        # PublishRunCompletionNotification (who is on duty per area).
        host_config = self._load_host_config()
        duty_date = compute_current_duty_date(host_config)
        duty_item = self._get_duty_schedule_item(duty_date)
        assignments_text = self._format_duty_reminder_body(duty_item, duty_date)

        self.publish_notification(
            "schedule_completed" if success else "schedule_failed",
            "排班完成" if success else "排班失败",
            message or ("已生成新的值日安排。" if success else "排班执行失败，请打开 Duty-Agent 查看详情。"),
            level="success" if success else "error",
            route="/schedule",
            source="schedule",
            targets=targets,
            data={
                "status": status or "unknown",
                "duty_date": duty_date,
                "assignments": assignments_text,
            },
        )

    def publish_snapshot_changed(self, reason: str, trace_id: str | None = None) -> None:
        """向桥接订阅者广播一次排班快照变更提示（保存/回滚/名单/配置变更后）。

        这不是用户通知，而是数据总线信号：bridge 收到后重拉 /api/v1/snapshot，
        从而免去轮询。targets 固定 classisland——桌面/Web 界面不应为此弹 toast。
        """
        self.publish_notification(
            "snapshot_changed",
            "排班数据已更新",
            str(reason or "").strip(),
            level="info",
            route="/schedule",
            source="snapshot",
            targets=["classisland"],
            data={"reason": str(reason or "").strip(), "trace_id": str(trace_id or "").strip()},
        )

    def _notification_reminder_loop(self) -> None:
        while not self.notification_reminder_stop.is_set():
            try:
                self.check_due_duty_reminders()
            except Exception as ex:
                try:
                    self.logger.warn("Runtime", f"check_due_duty_reminders tick failed: {ex}")
                except Exception:
                    pass
            self.notification_reminder_stop.wait(NOTIFICATION_REMINDER_POLL_SECONDS)

    def check_due_duty_reminders(self) -> None:
        # One config read per tick: the previous version loaded host-config twice
        # every 15s (here and again inside get_notification_targets).
        host_config = self._load_host_config()
        targets = self.get_notification_targets("duty_reminder_enabled", host_config=host_config)
        if not targets:
            return

        reminder_times = list(host_config.get("duty_reminder_times", []) or [])
        if not reminder_times:
            return

        now = datetime.now()
        duty_date = compute_current_duty_date(host_config, now)

        for raw_time in reminder_times:
            target_time = self._parse_auto_run_time(raw_time)
            if target_time is None:
                continue
            target_dt = datetime.combine(now.date(), target_time)
            overdue_seconds = (now - target_dt).total_seconds()
            # Due-but-unsent within the catch-up window: exact-minute equality
            # silently lost reminders whenever sleep/hibernate or a stall
            # spanned the target minute. The window cap avoids replaying a
            # morning reminder after an evening boot.
            if overdue_seconds < 0 or overdue_seconds > NOTIFICATION_REMINDER_CATCHUP_WINDOW_SECONDS:
                continue

            minute_text = target_dt.strftime("%H:%M")
            key = f"{duty_date}|{minute_text}"
            with self.notification_reminder_sent_lock:
                if key in self.notification_reminder_sent_keys:
                    continue
                duty_prefix = f"{duty_date}|"
                self.notification_reminder_sent_keys = {
                    sent_key for sent_key in self.notification_reminder_sent_keys if sent_key.startswith(duty_prefix)
                }

            today_item = self._get_duty_schedule_item(duty_date)
            body = self._format_duty_reminder_body(today_item, duty_date)
            self.publish_notification(
                "duty_reminder",
                f"当前值日提醒 {minute_text}",
                body,
                level="info",
                route="/schedule",
                source="reminder",
                targets=targets,
                data={"time": minute_text, "date": duty_date, "schedule": today_item or {}},
            )
            # Mark as sent only after a successful publish: if publishing
            # throws, the next tick can still retry within the catch-up window
            # instead of silently dropping the reminder for the whole day.
            # (Single-threaded loop, so no double-fire race.)
            with self.notification_reminder_sent_lock:
                self.notification_reminder_sent_keys.add(key)

    def _get_duty_schedule_item(self, duty_date: str) -> dict | None:
        try:
            state = load_state(self.data_dir / "state.json")
        except Exception:
            return None
        for item in state.get("schedule_pool", []) if isinstance(state, dict) else []:
            if isinstance(item, dict) and str(item.get("date", "") or "").strip() == duty_date:
                return item
        return None

    @staticmethod
    def _format_duty_reminder_body(today_item: dict | None, today: str) -> str:
        assignments = (today_item or {}).get("area_assignments")
        if not isinstance(assignments, dict) or not assignments:
            return f"{today} 暂无值日安排。"

        parts: list[str] = []
        for area, students in assignments.items():
            names = [str(name).strip() for name in students if str(name or "").strip()] if isinstance(students, list) else []
            if names:
                parts.append(f"{area}: {', '.join(names)}")
        return "；".join(parts) if parts else f"{today} 暂无值日安排。"

    def get_notification_targets(self, feature_key: str | None = None, host_config: dict | None = None) -> list[str]:
        if host_config is None:
            host_config = self._load_host_config()
        if feature_key is not None and not bool(host_config.get(feature_key, False)):
            return []

        entry = str(host_config.get("notification_entry", "system") or "system").strip().lower()
        if entry == "off":
            return []
        if entry not in {"system", "both", "classisland"}:
            entry = "system"

        targets: list[str] = []
        if entry in {"system", "both"} and bool(host_config.get("system_notifications_enabled", True)):
            targets.append("system")
        if entry in {"classisland", "both"}:
            targets.append("classisland")
        return targets

    @staticmethod
    def _normalize_notification_targets(targets: list[str] | None) -> list[str]:
        target_source = ["system"] if targets is None else targets
        normalized: list[str] = []
        seen: set[str] = set()
        for target in target_source:
            value = str(target or "").strip().lower()
            if value in {"system", "classisland"} and value not in seen:
                seen.add(value)
                normalized.append(value)
        return normalized

    @staticmethod
    def _normalize_notification_route(route: str) -> str:
        normalized = str(route or "").strip()
        if not normalized:
            return "/dashboard"
        if normalized.startswith("#/"):
            normalized = normalized[1:]
        if not normalized.startswith("/"):
            normalized = f"/{normalized}"
        return normalized

    def _load_host_config(self) -> dict:
        """mtime-cached host-config load.

        Two daemon pollers (15s reminder, 60s auto-run) call this forever; the
        cache turns the steady-state cost into a single ``stat`` instead of a
        full read+normalize of the JSON file on every tick.
        """
        config_path = self.data_dir / "host-config.json"
        try:
            current_mtime = config_path.stat().st_mtime_ns
        except OSError:
            current_mtime = None
        if current_mtime is not None:
            with self._host_config_cache_lock:
                if self._host_config_cache is not None and self._host_config_cache_mtime == current_mtime:
                    return dict(self._host_config_cache)

        context = Context(self.data_dir, logger=self.logger, request_source="runtime_startup")
        loaded = load_host_config(context)
        # Re-stat after the load: load_host_config may normalize + rewrite.
        try:
            loaded_mtime = config_path.stat().st_mtime_ns
        except OSError:
            loaded_mtime = None
        with self._host_config_cache_lock:
            self._host_config_cache = dict(loaded)
            self._host_config_cache_mtime = loaded_mtime
        return loaded


def create_runtime(data_dir: Path, disable_mcp_runtime: bool = False) -> DutyRuntime:
    return DutyRuntime(data_dir, disable_mcp_runtime=disable_mcp_runtime)
