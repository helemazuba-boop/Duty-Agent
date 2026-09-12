"""snapshot_changed 推送事件回归：排班数据/名单/配置变更后，桥接订阅者必须在
无需轮询的情况下收到一条 targets=["classisland"] 的 snapshot_changed 通知。"""

import json
import queue
import tempfile
import unittest
from pathlib import Path

from fastapi.testclient import TestClient

from core import app
from runtime import create_runtime
from state_ops import Context, save_roster_entries

BASE_URL = "http://testserver"


def _seed_roster(temp_dir: str) -> None:
    context = Context(Path(temp_dir))
    context.paths  # noqa: B018 - 触发目录创建
    save_roster_entries(
        context,
        [
            {"id": 1, "name": "Alice", "active": True},
            {"id": 2, "name": "Bob", "active": True},
        ],
    )


def _seed_state_with_prev(temp_dir: str) -> None:
    state_path = Path(temp_dir) / "state.json"
    prev_path = Path(temp_dir) / "state.prev.json"
    current = {"schedule_pool": [{"date": "2026-09-14", "day": "Mon", "area_assignments": {"A": ["Alice"]}}],
               "last_pointer": 1}
    previous = {"schedule_pool": [], "last_pointer": 0}
    state_path.write_text(json.dumps(current), encoding="utf-8")
    prev_path.write_text(json.dumps(previous), encoding="utf-8")


class TestSnapshotChangedPush(unittest.TestCase):
    def _with_runtime(self, temp_dir: str):
        runtime = create_runtime(Path(temp_dir))
        app.state.runtime = runtime
        return runtime

    @staticmethod
    def _teardown_runtime(runtime, original) -> None:
        # 后台线程先停，避免临时目录清理时与其文件访问竞态（WinError 145）。
        if runtime is not None:
            runtime.stop_auto_run_worker()
            runtime.stop_notification_workers()
        app.state.runtime = original

    def _collect_events(self, runtime, action):
        """订阅通知队列 → 执行 action → 排空队列，返回全部事件。"""
        captured: queue.Queue = runtime.subscribe_notifications()
        try:
            action()
            events = []
            while True:
                try:
                    events.append(captured.get(timeout=2.0))
                except queue.Empty:
                    break
            return events
        finally:
            runtime.unsubscribe_notifications(captured)

    @staticmethod
    def _snapshot_changed(events: list[dict]) -> list[dict]:
        return [event for event in events if event.get("type") == "snapshot_changed"]

    def test_roster_update_publishes_snapshot_changed(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            _seed_roster(temp_dir)
            original = getattr(app.state, "runtime", None)
            runtime = self._with_runtime(temp_dir)
            try:
                with TestClient(app) as client:
                    events = self._collect_events(
                        runtime,
                        lambda: client.put(
                            f"{BASE_URL}/api/v1/roster",
                            headers={"Authorization": f"Bearer {runtime.access_token}"},
                            json={"roster": [{"id": 1, "name": "Alice", "active": True},
                                             {"id": 2, "name": "Bob", "active": True},
                                             {"id": 3, "name": "Carol", "active": True}]},
                        ),
                    )
            finally:
                self._teardown_runtime(runtime, original)

        changed = self._snapshot_changed(events)
        self.assertEqual(len(changed), 1)
        self.assertEqual(changed[0]["targets"], ["classisland"])
        self.assertEqual(changed[0]["data"]["reason"], "roster-updated")

    def test_rollback_publishes_snapshot_changed(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            _seed_state_with_prev(temp_dir)
            original = getattr(app.state, "runtime", None)
            runtime = self._with_runtime(temp_dir)
            try:
                with TestClient(app) as client:
                    events = self._collect_events(
                        runtime,
                        lambda: client.post(
                            f"{BASE_URL}/api/v1/duty/schedule-rollback",
                            headers={"Authorization": f"Bearer {runtime.access_token}"},
                        ),
                    )
            finally:
                self._teardown_runtime(runtime, original)

        changed = self._snapshot_changed(events)
        self.assertEqual(len(changed), 1)
        self.assertEqual(changed[0]["targets"], ["classisland"])
        self.assertEqual(changed[0]["data"]["reason"], "rollback")

    def test_config_patch_publishes_snapshot_changed(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            original = getattr(app.state, "runtime", None)
            runtime = self._with_runtime(temp_dir)
            try:
                with TestClient(app) as client:
                    config = client.get(
                        f"{BASE_URL}/api/v1/config",
                        headers={"Authorization": f"Bearer {runtime.access_token}"},
                    ).json()
                    version = int(config.get("version", 1))
                    events = self._collect_events(
                        runtime,
                        lambda: client.patch(
                            f"{BASE_URL}/api/v1/config",
                            headers={"Authorization": f"Bearer {runtime.access_token}"},
                            json={"expected_version": version, "duty_rule": "测试规则"},
                        ),
                    )
            finally:
                self._teardown_runtime(runtime, original)

        changed = self._snapshot_changed(events)
        self.assertEqual(len(changed), 1)
        self.assertEqual(changed[0]["targets"], ["classisland"])
        self.assertEqual(changed[0]["data"]["reason"], "config-updated")

    def test_failed_save_does_not_publish(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            _seed_roster(temp_dir)
            _seed_state_with_prev(temp_dir)
            original = getattr(app.state, "runtime", None)
            runtime = self._with_runtime(temp_dir)
            try:
                with TestClient(app) as client:
                    events = self._collect_events(
                        runtime,
                        lambda: client.post(
                            f"{BASE_URL}/api/v1/duty/schedule-rollback",
                            headers={"Authorization": f"Bearer {runtime.access_token}"},
                        ),
                    )
                    # 第一次 rollback 已消费 .prev；第二次必然失败且不得再发事件。
                    second = self._collect_events(
                        runtime,
                        lambda: client.post(
                            f"{BASE_URL}/api/v1/duty/schedule-rollback",
                            headers={"Authorization": f"Bearer {runtime.access_token}"},
                        ),
                    )
            finally:
                self._teardown_runtime(runtime, original)

        self.assertEqual(len(self._snapshot_changed(events)), 1)
        self.assertEqual(len(self._snapshot_changed(second)), 0)


if __name__ == "__main__":
    unittest.main()
