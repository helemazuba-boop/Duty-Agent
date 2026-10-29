"""数据重置（班级换届 / 学期重置）回归。

覆盖三档 scope 的文件级结果、强制归档、归档不被自己删掉、以及三道拒绝闸门
（排班持锁 / duty/live 主人在位 / config 版本过期）。跑法（CI 逐文件独立进程）：

    Assets_Duty/python-embed/python.exe test_data_reset.py
"""

import json
import os
import queue
import tempfile
import unittest
from pathlib import Path

from fastapi.testclient import TestClient

from core import app
from runtime import create_runtime
from state_ops import (
    RESET_ARCHIVE_DIRNAME,
    RESET_ARCHIVE_KEEP,
    Context,
    archive_class_data,
    list_reset_archives,
    load_config,
    load_host_config,
    load_roster_entries,
    load_state,
    reset_class_data,
    save_config,
    save_host_config,
    save_roster_entries,
    save_state,
    update_state,
)


def _auth_headers(runtime, extra: dict | None = None) -> dict:
    headers = {"Authorization": f"Bearer {runtime.access_token}"}
    if extra:
        headers.update(extra)
    return headers


def _canonical_path(value) -> str:
    """把路径规范化成可比较的形式。

    Windows 上同一个目录可能以 8.3 短名出现（CI runner 是 ``C:\\Users\\runneradmin``，
    某些解析路径会返回 ``RUNNER~1``）。字符串不同但目录相同，比较前统一
    realpath + normcase，避免把"同一目录的两种写法"判成失败。
    """
    return os.path.normcase(os.path.realpath(str(value)))


def _seed_class_data(data_dir: Path) -> dict:
    """A populated data dir, shaped like a real install: the client has already
    written host-config.json, the engine has written a non-default config.json,
    and a term's worth of schedule/ledger data is on disk."""
    context = Context(data_dir)
    save_roster_entries(
        context,
        [
            {"id": 1, "name": "Alice", "active": True},
            {"id": 2, "name": "Bob", "active": True},
            {"id": 3, "name": "Carol", "active": True},
        ],
    )
    save_config(
        context,
        {
            "selected_plan_id": "agents",
            "plan_presets": [
                {
                    "id": "agents",
                    "name": "6-Agent",
                    "mode_id": "campus_6agent",
                    "api_key": "sk-test-key",
                    "base_url": "https://example.invalid/v1",
                    "model": "test-model",
                    "model_profile": "campus_small",
                    "provider_hint": "",
                    "multi_agent_execution_mode": "parallel",
                }
            ],
            "duty_rule": "周一至周五，两人一组",
            "offline_schedule_days": 14,
            "areas": ["教室", "走廊"],
            "area_per_day_counts": {"教室": 2, "走廊": 1},
        },
    )
    save_host_config(context, {"server_port": 8765})
    state = {
        "schedule_pool": [
            {"date": "2026-09-01", "day": "周二", "area_assignments": {"教室": ["Alice", "Bob"]}, "note": ""},
            {"date": "2026-09-02", "day": "周三", "area_assignments": {"教室": ["Carol"]}, "note": ""},
        ],
        "next_run_note": "machine summary",
        "absences": [{"id": 2, "from": "2026-09-03", "to": "2026-09-04"}],
        "user_notes": [{"text": "运动会停扫", "until": None, "created_at": "2026-09-01T00:00:00"}],
        "day_overrides": {"2026-09-05": {"教室": 3}},
        "debt_counts": {"1": 2},
        "credit_counts": {"3": 1},
        "last_pointer": 7,
        "roster_fingerprint": "stale-generation",
    }
    save_state(context, state, snapshot_previous=False)
    return state


class _RuntimeCase(unittest.TestCase):
    def setUp(self):
        self._temp = tempfile.TemporaryDirectory()
        self.data_dir = Path(self._temp.name)
        self._original_runtime = getattr(app.state, "runtime", None)

    def tearDown(self):
        runtime = getattr(app.state, "runtime", None)
        if runtime is not None:
            runtime.stop_auto_run_worker()
            runtime.stop_notification_workers()
        app.state.runtime = self._original_runtime
        self._temp.cleanup()

    def _with_runtime(self):
        runtime = create_runtime(self.data_dir)
        app.state.runtime = runtime
        return runtime


class TestResetScopes(_RuntimeCase):
    def test_schedule_scope_clears_state_but_keeps_roster_and_config(self):
        _seed_class_data(self.data_dir)
        context = Context(self.data_dir)
        roster_before = load_roster_entries(context.paths["roster"])
        config_before = load_config(context)

        result = reset_class_data(context, "schedule")

        state = load_state(context.paths["state"])
        self.assertEqual(state["schedule_pool"], [])
        self.assertEqual(state["absences"], [])
        self.assertEqual(state["user_notes"], [])
        self.assertEqual(state["day_overrides"], {})
        self.assertEqual(state["debt_counts"], {})
        self.assertEqual(state["credit_counts"], {})
        self.assertEqual(state["last_pointer"], 0)
        # roster_fingerprint belongs to a roster generation; an empty state must
        # not carry a stale one, or a later rollback check reads a phantom generation.
        self.assertNotIn("roster_fingerprint", state)
        # .prev would let "rollback" resurrect the wiped class; the archive is the undo path.
        self.assertFalse(context.paths["state"].with_suffix(".prev.json").exists())

        self.assertEqual(load_roster_entries(context.paths["roster"]), roster_before)
        self.assertEqual(load_config(context), config_before)
        self.assertEqual(result["scope"], "schedule")
        self.assertIn("花名册", result["kept"])
        self.assertIn("模型与方案配置", result["kept"])

    def test_handover_scope_also_drops_roster(self):
        _seed_class_data(self.data_dir)
        context = Context(self.data_dir)
        config_before = load_config(context)

        result = reset_class_data(context, "handover")

        # Deleted, not emptied: load_roster raises for a missing file, which is
        # exactly the first-run state the onboarding wizard handles.
        self.assertFalse(context.paths["roster"].exists())
        self.assertRaises(FileNotFoundError, load_roster_entries, context.paths["roster"])
        self.assertEqual(load_state(context.paths["state"])["schedule_pool"], [])
        # Model credentials are the one thing worth surviving a handover.
        self.assertEqual(load_config(context), config_before)
        self.assertIn("花名册", result["removed"])
        self.assertNotIn("花名册", result["kept"])

    def test_handover_can_also_clear_areas_and_rule(self):
        _seed_class_data(self.data_dir)
        context = Context(self.data_dir)
        context.config = load_config(context)
        version_before = context.config["version"]
        reset_class_data(context, "handover", clear_areas_and_rule=True)

        after = load_config(context)
        self.assertEqual(after["areas"], [])
        self.assertEqual(after["area_per_day_counts"], {})
        self.assertEqual(after["duty_rule"], "")
        self.assertEqual(after["version"], version_before + 1)

    def test_factory_scope_resets_config_to_defaults_and_bumps_version(self):
        _seed_class_data(self.data_dir)
        context = Context(self.data_dir)
        context.config = load_config(context)
        version_before = context.config["version"]

        result = reset_class_data(context, "factory")

        after = load_config(context)
        self.assertEqual(after["version"], version_before + 1)
        self.assertEqual(after["areas"], [])
        self.assertEqual(after["duty_rule"], "")
        self.assertEqual(load_state(context.paths["state"])["schedule_pool"], [])
        self.assertFalse(context.paths["roster"].exists())
        self.assertIn("模型与方案配置", result["removed"])
        self.assertEqual(result["kept"], ["宿主配置"])

    def test_unknown_scope_is_rejected(self):
        _seed_class_data(self.data_dir)
        context = Context(self.data_dir)
        self.assertRaises(ValueError, reset_class_data, context, "everything")
        # Nothing was touched.
        self.assertEqual(len(load_state(context.paths["state"])["schedule_pool"]), 2)


class TestResetArchive(_RuntimeCase):
    def test_archive_is_written_before_the_wipe_and_survives_it(self):
        _seed_class_data(self.data_dir)
        context = Context(self.data_dir)

        result = reset_class_data(context, "handover")
        archive = Path(result["archive"]["path"])

        self.assertTrue(archive.is_dir())
        self.assertEqual(
            sorted(result["archive"]["files"]),
            ["config.json", "host-config.json", "roster.csv", "state.json"],
        )
        # The archived copies are the PRE-reset data, not the wiped one.
        archived_state = json.loads((archive / "state.json").read_text(encoding="utf-8"))
        self.assertEqual(len(archived_state["schedule_pool"]), 2)
        self.assertEqual(archived_state["last_pointer"], 7)
        self.assertTrue((archive / "roster.csv").exists())
        # host-config is archived for a full picture but never reset itself.
        self.assertTrue((archive / "host-config.json").exists())
        self.assertTrue((self.data_dir / "host-config.json").exists())

    def test_second_reset_does_not_delete_the_first_archive(self):
        _seed_class_data(self.data_dir)
        context = Context(self.data_dir)

        first = reset_class_data(context, "handover")
        second = reset_class_data(context, "factory")

        self.assertTrue(Path(first["archive"]["path"]).is_dir())
        self.assertTrue(Path(second["archive"]["path"]).is_dir())
        self.assertEqual(len(list_reset_archives(self.data_dir)), 2)
        # The archive root itself is never inside the wipe scope.
        self.assertTrue((self.data_dir / RESET_ARCHIVE_DIRNAME).is_dir())

    def test_archives_are_pruned_to_the_retention_limit(self):
        _seed_class_data(self.data_dir)
        context = Context(self.data_dir)
        for _ in range(RESET_ARCHIVE_KEEP + 3):
            reset_class_data(context, "schedule")

        archives = list_reset_archives(self.data_dir)
        self.assertEqual(len(archives), RESET_ARCHIVE_KEEP)
        # Newest first, so the survivor is the most recent reset.
        self.assertEqual(archives[0]["name"].startswith("清空排班-"), True)

    def test_archive_of_a_fresh_install_is_empty_but_valid(self):
        context = Context(self.data_dir)
        result = reset_class_data(context, "schedule")
        self.assertEqual(result["archive"]["files"], [])
        self.assertTrue(Path(result["archive"]["path"]).is_dir())

    def test_archive_class_data_does_not_move_the_originals(self):
        _seed_class_data(self.data_dir)
        context = Context(self.data_dir)
        archive_class_data(self.data_dir, "schedule")
        # copy2, not move: the originals must stay until the wipe succeeds.
        self.assertTrue(context.paths["roster"].exists())
        self.assertEqual(len(load_state(context.paths["state"])["schedule_pool"]), 2)


class TestResetApi(_RuntimeCase):
    def _post(self, client, runtime, payload):
        return client.post(
            "/api/v1/data-reset",
            json=payload,
            headers=_auth_headers(runtime, {"X-Duty-Request-Source": "test_suite"}),
        )

    def test_handover_reset_round_trip(self):
        _seed_class_data(self.data_dir)
        runtime = self._with_runtime()
        with TestClient(app) as client:
            response = self._post(
                client,
                runtime,
                {"scope": "handover", "confirm": "换届重置"},
            )

        self.assertEqual(response.status_code, 200)
        body = response.json()
        self.assertEqual(body["status"], "success")
        self.assertEqual(body["scope"], "handover")
        self.assertIn("花名册", body["removed"])
        self.assertIn("模型与方案配置", body["kept"])
        self.assertTrue(Path(body["archive"]["path"]).is_dir())
        self.assertFalse((self.data_dir / "roster.csv").exists())

    def test_reset_publishes_snapshot_changed_so_the_bridge_refreshes(self):
        _seed_class_data(self.data_dir)
        runtime = self._with_runtime()
        subscription = runtime.subscribe_notifications()
        try:
            with TestClient(app) as client:
                response = self._post(client, runtime, {"scope": "schedule", "confirm": "清空排班"})
            self.assertEqual(response.status_code, 200)
            reasons = []
            while True:
                try:
                    reasons.append(subscription.get_nowait())
                except queue.Empty:
                    break
        finally:
            runtime.unsubscribe_notifications(subscription)

        joined = json.dumps(reasons, ensure_ascii=False)
        self.assertIn("snapshot_changed", joined)
        self.assertIn("data-reset", joined)

    def test_confirm_mismatch_is_a_400_and_changes_nothing(self):
        _seed_class_data(self.data_dir)
        runtime = self._with_runtime()
        with TestClient(app) as client:
            response = self._post(client, runtime, {"scope": "handover", "confirm": "删除"})

        self.assertEqual(response.status_code, 400)
        self.assertIn("换届重置", response.json()["detail"])
        self.assertTrue((self.data_dir / "roster.csv").exists())
        self.assertEqual(len(load_state(self.data_dir / "state.json")["schedule_pool"]), 2)

    def test_factory_reset_with_stale_expected_version_is_a_409(self):
        _seed_class_data(self.data_dir)
        runtime = self._with_runtime()
        with TestClient(app) as client:
            response = self._post(
                client,
                runtime,
                {"scope": "factory", "confirm": "恢复出厂", "expected_version": 999},
            )

        self.assertEqual(response.status_code, 409)
        self.assertTrue((self.data_dir / "roster.csv").exists())
        self.assertEqual(len(load_state(self.data_dir / "state.json")["schedule_pool"]), 2)

    def test_factory_reset_with_current_expected_version_succeeds(self):
        _seed_class_data(self.data_dir)
        runtime = self._with_runtime()
        current_version = load_config(Context(self.data_dir))["version"]
        with TestClient(app) as client:
            response = self._post(
                client,
                runtime,
                {
                    "scope": "factory",
                    "confirm": "恢复出厂",
                    "expected_version": current_version,
                },
            )

        self.assertEqual(response.status_code, 200)
        self.assertEqual(load_config(Context(self.data_dir))["version"], current_version + 1)

    def test_reset_refused_while_a_schedule_run_holds_the_lock(self):
        _seed_class_data(self.data_dir)
        runtime = self._with_runtime()
        self.assertTrue(runtime.schedule_run_lock.acquire(blocking=False))
        try:
            with TestClient(app) as client:
                response = self._post(client, runtime, {"scope": "handover", "confirm": "换届重置"})
        finally:
            runtime.schedule_run_lock.release()

        self.assertEqual(response.status_code, 409)
        self.assertIn("排班", response.json()["detail"])
        # Nothing was wiped.
        self.assertTrue((self.data_dir / "roster.csv").exists())
        self.assertEqual(len(load_state(self.data_dir / "state.json")["schedule_pool"]), 2)

    def test_reset_refused_while_another_entrance_owns_the_live_socket(self):
        _seed_class_data(self.data_dir)
        runtime = self._with_runtime()
        self.assertTrue(runtime.try_claim_duty_live_owner("web-ui-session"))
        try:
            with TestClient(app) as client:
                response = self._post(client, runtime, {"scope": "handover", "confirm": "换届重置"})
        finally:
            runtime.release_duty_live_owner("web-ui-session")

        self.assertEqual(response.status_code, 409)
        self.assertTrue((self.data_dir / "roster.csv").exists())
        self.assertEqual(len(load_state(self.data_dir / "state.json")["schedule_pool"]), 2)

    def test_data_reset_requires_bearer_token(self):
        _seed_class_data(self.data_dir)
        self._with_runtime()
        with TestClient(app) as client:
            response = client.post("/api/v1/data-reset", json={"scope": "handover", "confirm": "换届重置"})

        self.assertEqual(response.status_code, 401)
        self.assertTrue((self.data_dir / "roster.csv").exists())

    def test_archives_endpoint_lists_what_was_backed_up(self):
        _seed_class_data(self.data_dir)
        runtime = self._with_runtime()
        with TestClient(app) as client:
            self._post(client, runtime, {"scope": "schedule", "confirm": "清空排班"})
            response = client.get(
                "/api/v1/data-reset/archives",
                headers=_auth_headers(runtime, {"X-Duty-Request-Source": "test_suite"}),
            )

        self.assertEqual(response.status_code, 200)
        body = response.json()
        self.assertEqual(len(body["archives"]), 1)
        self.assertIn("roster.csv", body["archives"][0]["files"])
        # 不能直接比 Path 对象：Windows 上同一个目录可能以 8.3 短名返回
        # （CI 的 runner 路径是 C:\Users\runneradmin，解析后可能变成 RUNNER~1），
        # 字符串不同但指的是同一个目录。比对规范化后的真实路径。
        self.assertEqual(
            _canonical_path(body["data_dir"]),
            _canonical_path(self.data_dir),
        )


class TestResetConcurrency(_RuntimeCase):
    def test_reset_releases_the_state_lock_for_the_next_write(self):
        """The wipe holds the state file lock, so a following operational write
        must not hit a stale lock and time out."""
        _seed_class_data(self.data_dir)
        context = Context(self.data_dir)
        reset_class_data(context, "handover")

        def _add_absence(current: dict) -> dict:
            current["absences"] = [{"id": 1, "from": "2026-10-01", "to": "2026-10-02"}]
            return current

        update_state(context.paths["state"], _add_absence, snapshot_previous=False)

        self.assertEqual(len(load_state(context.paths["state"])["absences"]), 1)

    def test_reset_leaves_no_temp_or_lock_files_behind(self):
        _seed_class_data(self.data_dir)
        context = Context(self.data_dir)
        reset_class_data(context, "factory")

        leftovers = sorted(
            p.name
            for p in self.data_dir.iterdir()
            if p.is_file() and (p.name.endswith(".tmp") or p.name.endswith(".lock"))
        )
        self.assertEqual(leftovers, [])


if __name__ == "__main__":
    unittest.main()
