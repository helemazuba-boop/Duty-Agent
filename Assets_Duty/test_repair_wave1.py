#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Wave 1 回归测试固化（REPAIR_PLAN §5-E）。

把 Wave 1 三个 agent 落地的新行为固化为测试：

* a) ``state_ops.claim_auto_run_today``（D5）跨进程原子认领：并发下恰好一个
     胜出；次日（注入新的 ``today``）可再次认领。
* b) claim 与失败计数解耦：清空 ``last_auto_run_date`` 模拟跨日后可再次认领，
     且认领不触碰 ``ai_consecutive_failures`` / ``ai_failures_date``。
* c) ``GET /api/v1/state``（C1）：Bearer 保护；payload 恒有 ``state`` /
     ``mtime_ns`` 两键；预置 state.json 后 ``mtime_ns`` 为 int。
* d) ``POST /shutdown``（任务1）：置 ``app.state.uvicorn_server.should_exit``；
     未捕获 server 实例时 503。
* e) ``.dev-token`` 门控（任务3）：仅 ``DUTY_DEV_WRITE_TOKEN=1`` 时落盘。
* f) ``dpapi_compat``（D4/C2）：protect→unprotect 往返；明文透传；坏密文
     返回空串不抛。
* g) ``/health`` 的 ``auth_bypassed``（任务2）：默认 False，SKIP_AUTH_BYPASS
     生效时 True。

约定：全部使用 tempfile 数据目录（数据目录嵌套在临时目录内，使 runtime 的
logs/ 也落在临时目录里），绝不触碰 %LOCALAPPDATA% 下的真实数据。
"""

import base64
import contextlib
import io
import json
import os
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from unittest import mock

# Ensure the modules under test are importable
sys.path.insert(0, str(Path(__file__).resolve().parent))

from fastapi.testclient import TestClient

import dpapi_compat
import runtime as runtime_module
from core import app
from core import main as core_main
from runtime import create_runtime
from state_ops import (
    Context,
    claim_auto_run_today,
    save_json_atomic,
    update_host_runtime_fields,
)

TODAY = "2026-07-11"
TOMORROW = "2026-07-12"


def _auth_headers(runtime) -> dict:
    return {"Authorization": f"Bearer {runtime.access_token}"}


@contextlib.contextmanager
def _app_state_attr(name: str, value, *, absent: bool = False):
    """临时替换/删除 ``app.state.<name>``，结束后精确恢复原状。

    ``absent=True`` 表示在块内让该属性不存在（用于模拟"server 实例尚未捕获"）。
    core.app 是跨测试文件共享的模块级单例，任何改动都必须在退出时还原。
    """
    had_attr = hasattr(app.state, name)
    original = getattr(app.state, name, None)
    if absent:
        if had_attr:
            try:
                delattr(app.state, name)
            except AttributeError:
                pass
    else:
        setattr(app.state, name, value)
    try:
        yield
    finally:
        if had_attr:
            setattr(app.state, name, original)
        else:
            try:
                # starlette State.__delattr__ 对缺失键抛 KeyError 而非
                # AttributeError，两者都要容错。
                delattr(app.state, name)
            except (AttributeError, KeyError):
                pass


def _runtime_on_app(runtime):
    return _app_state_attr("runtime", runtime)


def _uvicorn_server_absent_on_app():
    return _app_state_attr("uvicorn_server", None, absent=True)


class _Wave1RuntimeTestBase(unittest.TestCase):
    """tempfile 数据目录 + 短命 runtime 的公共脚手架。"""

    def make_data_dir(self) -> Path:
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        # 数据目录嵌套一层：DutyRuntime 把日志写到 data_dir.parent/logs，
        # 嵌套可让 logs/ 也落在临时目录内，避免污染真实文件系统。
        data_dir = Path(tmp.name) / "data"
        data_dir.mkdir(parents=True, exist_ok=True)
        return data_dir

    def make_runtime(self, **host_overrides):
        data_dir = self.make_data_dir()
        if host_overrides:
            payload = {
                "auto_run_mode": "Weekly",
                "auto_run_parameter": "Monday",
                "auto_run_time": "00:00",
                "auto_run_retry_times": 3,
                "ai_consecutive_failures": 0,
                "last_auto_run_date": "",
            }
            payload.update(host_overrides)
            (data_dir / "host-config.json").write_text(
                json.dumps(payload), encoding="utf-8"
            )
        runtime = create_runtime(data_dir)
        # 只允许同步调用点改动数据目录，后台 worker 一律停掉。
        runtime.stop_auto_run_worker()
        runtime.stop_notification_workers()
        self.addCleanup(runtime.stop_auto_run_worker)
        self.addCleanup(runtime.stop_notification_workers)
        return runtime

    def read_host_config(self, data_dir: Path) -> dict:
        return json.loads(
            (data_dir / "host-config.json").read_text(encoding="utf-8-sig")
        )


class TestClaimAutoRunToday(_Wave1RuntimeTestBase):
    """a) D5 跨进程原子认领。"""

    def test_ten_concurrent_threads_exactly_one_claims(self):
        data_dir = self.make_data_dir()
        thread_count = 10
        barrier = threading.Barrier(thread_count)
        results = [None] * thread_count
        errors = []

        def _worker(index: int) -> None:
            try:
                # 栅栏把认领时刻对齐，最大化 O_EXCL 锁竞争。
                barrier.wait(timeout=10)
                results[index] = claim_auto_run_today(data_dir, today=TODAY)
            except Exception as ex:  # pragma: no cover - 只在实现回归时触发
                errors.append(ex)

        threads = [
            threading.Thread(target=_worker, args=(index,), name=f"claim-{index}")
            for index in range(thread_count)
        ]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(timeout=30)
        self.assertFalse(errors, f"claim_auto_run_today raised: {errors}")
        self.assertEqual(
            sum(1 for claimed in results if claimed is True),
            1,
            f"exactly one thread must win, got {results}",
        )
        # 落盘的日期恰好被翻到今日，且只翻一次。
        self.assertEqual(self.read_host_config(data_dir)["last_auto_run_date"], TODAY)

        # 次日（调用方注入新 today）：认领再次成功。
        self.assertTrue(claim_auto_run_today(data_dir, today=TOMORROW))
        self.assertEqual(
            self.read_host_config(data_dir)["last_auto_run_date"], TOMORROW
        )

    def test_claim_is_decoupled_from_failure_counters(self):
        data_dir = self.make_data_dir()
        context = Context(data_dir)
        # 预置遗留失败计数（昨日）。
        update_host_runtime_fields(
            context, {"ai_consecutive_failures": 4, "ai_failures_date": "2026-07-10"}
        )

        self.assertTrue(claim_auto_run_today(data_dir, today=TODAY))
        # 当日重复认领失败（D5：每日单次）。
        self.assertFalse(claim_auto_run_today(data_dir, today=TODAY))

        # 模拟跨日：清空日期（失败计数保留），认领再次成功。
        update_host_runtime_fields(context, {"last_auto_run_date": ""})
        self.assertTrue(claim_auto_run_today(data_dir, today=TODAY))

        config = self.read_host_config(data_dir)
        self.assertEqual(config["last_auto_run_date"], TODAY)
        # 认领只写日期，绝不触碰失败诊断计数（失败重试走内存 attempt）。
        self.assertEqual(config["ai_consecutive_failures"], 4)
        self.assertEqual(config["ai_failures_date"], "2026-07-10")


class _StubUvicornServer:
    """足够 /shutdown 使用的最小 uvicorn.Server 替身。"""

    def __init__(self):
        self.should_exit = False


class TestShutdownEndpoint(_Wave1RuntimeTestBase):
    """d) /shutdown 优雅关停（任务1：不再 os.kill(SIGTERM)）。"""

    def test_shutdown_sets_stub_should_exit(self):
        runtime = self.make_runtime()
        stub = _StubUvicornServer()
        with _runtime_on_app(runtime), _app_state_attr("uvicorn_server", stub):
            with TestClient(app) as client:
                response = client.post("/shutdown", headers=_auth_headers(runtime))
        self.assertEqual(response.status_code, 200)
        self.assertEqual(response.json(), {"status": "shutting down"})
        self.assertTrue(stub.should_exit)

    def test_shutdown_without_captured_server_returns_503(self):
        runtime = self.make_runtime()
        with _runtime_on_app(runtime), _uvicorn_server_absent_on_app():
            with TestClient(app) as client:
                response = client.post("/shutdown", headers=_auth_headers(runtime))
        self.assertEqual(response.status_code, 503)

    def test_shutdown_requires_bearer_token(self):
        runtime = self.make_runtime()
        with _runtime_on_app(runtime):
            with TestClient(app) as client:
                response = client.post("/shutdown")
        self.assertEqual(response.status_code, 401)


class TestGetStateEndpoint(_Wave1RuntimeTestBase):
    """c) GET /api/v1/state（C1 契约）。"""

    def test_state_endpoint_requires_bearer_token(self):
        runtime = self.make_runtime()
        with _runtime_on_app(runtime):
            with TestClient(app) as client:
                response = client.get("/api/v1/state")
        self.assertEqual(response.status_code, 401)

    def test_state_endpoint_without_state_file_returns_empty_state_and_null_mtime(self):
        runtime = self.make_runtime()
        with _runtime_on_app(runtime):
            with TestClient(app) as client:
                response = client.get(
                    "/api/v1/state", headers=_auth_headers(runtime)
                )
        self.assertEqual(response.status_code, 200)
        payload = response.json()
        # C1：payload 恒有且仅有 state / mtime_ns 两键（B 侧以 mtime_ns 去抖）。
        self.assertEqual(set(payload.keys()), {"state", "mtime_ns"})
        self.assertIsNone(payload["mtime_ns"])
        self.assertEqual(payload["state"]["schedule_pool"], [])
        self.assertEqual(payload["state"]["last_pointer"], 0)

    def test_state_endpoint_reports_int_mtime_ns_for_existing_state(self):
        runtime = self.make_runtime()
        save_json_atomic(
            runtime.data_dir / "state.json",
            {
                "schedule_pool": [
                    {"date": TODAY, "area_assignments": {"教室": ["Alice"]}}
                ],
                "next_run_note": "wave1",
                "debt_counts": {"1": 2},
                "credit_counts": {},
                "last_pointer": 3,
            },
        )
        with _runtime_on_app(runtime):
            with TestClient(app) as client:
                response = client.get(
                    "/api/v1/state", headers=_auth_headers(runtime)
                )
        self.assertEqual(response.status_code, 200)
        payload = response.json()
        self.assertIsInstance(payload["mtime_ns"], int)
        self.assertEqual(payload["state"]["last_pointer"], 3)
        self.assertEqual(payload["state"]["next_run_note"], "wave1")
        self.assertEqual(payload["state"]["debt_counts"], {"1": 2})
        self.assertEqual(
            payload["state"]["schedule_pool"],
            [{"date": TODAY, "area_assignments": {"教室": ["Alice"]}}],
        )


class TestDevTokenGating(_Wave1RuntimeTestBase):
    """e) .dev-token 仅在 DUTY_DEV_WRITE_TOKEN=1 时写盘（任务3）。"""

    def _run_server_mode(self, data_dir: Path, *, write_token_env: bool) -> str:
        """以 --server 模式跑 core.main()（uvicorn.run/Thread 全 mock，不真起服务）。

        返回捕获的 stdout（含 __DUTY_SERVER_TOKEN__ 管道行）。与
        test_auth_runtime 的既有 mock 手法一致。
        """
        original_runtime = getattr(app.state, "runtime", None)
        had_runtime = hasattr(app.state, "runtime")
        stdout_buffer = io.StringIO()
        try:
            with contextlib.redirect_stdout(stdout_buffer):
                with mock.patch.object(
                    sys,
                    "argv",
                    ["core.py", "--data-dir", str(data_dir), "--server", "--port", "0"],
                ):
                    with mock.patch("core.uvicorn.run"):
                        with mock.patch("core.threading.Thread") as thread_mock:
                            thread_mock.return_value.start.return_value = None
                            with mock.patch.dict(os.environ):
                                if write_token_env:
                                    os.environ["DUTY_DEV_WRITE_TOKEN"] = "1"
                                else:
                                    os.environ.pop("DUTY_DEV_WRITE_TOKEN", None)
                                core_main()
        finally:
            if had_runtime:
                app.state.runtime = original_runtime
            else:
                try:
                    del app.state.runtime
                except AttributeError:
                    pass
        return stdout_buffer.getvalue()

    @staticmethod
    def _piped_token(stdout_text: str) -> str:
        for line in stdout_text.splitlines():
            if line.startswith("__DUTY_SERVER_TOKEN__:"):
                return line.split(":", 1)[1]
        raise AssertionError("__DUTY_SERVER_TOKEN__ line missing from stdout")

    def test_dev_token_absent_without_env(self):
        data_dir = self.make_data_dir()
        stdout_text = self._run_server_mode(data_dir, write_token_env=False)
        self.assertFalse((data_dir / ".dev-token").exists())
        # token 仍通过管道行交付（生产客户端的取法），只是不再落盘。
        self.assertTrue(self._piped_token(stdout_text))

    def test_dev_token_written_when_env_enabled(self):
        data_dir = self.make_data_dir()
        stdout_text = self._run_server_mode(data_dir, write_token_env=True)
        token_file = data_dir / ".dev-token"
        self.assertTrue(token_file.exists())
        self.assertEqual(token_file.read_text(encoding="utf-8"), self._piped_token(stdout_text))


@unittest.skipIf(os.name != "nt", "DPAPI is Windows-only")
class TestDpapiCompat(unittest.TestCase):
    """f) dpapi_compat（D4/C2：C# 写密文 → Python 读明文）。"""

    def test_protect_unprotect_roundtrip(self):
        for plain in ("s3cret-key", "中文密钥123", 'p@$$w0rd!"\\'):
            ciphertext = dpapi_compat.protect(plain)
            self.assertTrue(ciphertext.startswith(dpapi_compat.DPAPI_PREFIX))
            self.assertNotIn(plain, ciphertext)
            self.assertEqual(dpapi_compat.unprotect(ciphertext), plain)

    def test_plaintext_without_prefix_passes_through(self):
        self.assertEqual(dpapi_compat.unprotect("legacy-plain-key"), "legacy-plain-key")
        self.assertEqual(dpapi_compat.unprotect(""), "")
        self.assertEqual(dpapi_compat.unprotect(None), "")

    def test_corrupt_ciphertext_returns_empty_without_raising(self):
        # C2 契约：解密失败 → 置空 + 告警，绝不抛。
        self.assertEqual(dpapi_compat.unprotect("dpapi:v1:%%%not-base64%%%"), "")
        garbage = base64.b64encode(b"definitely-not-a-dpapi-blob").decode("ascii")
        self.assertEqual(dpapi_compat.unprotect("dpapi:v1:" + garbage), "")


class TestHealthAuthBypassed(_Wave1RuntimeTestBase):
    """g) /health、/engine/info 的 auth_bypassed 可见性（任务2）。

    core/runtime 在模块级读一次环境变量；这里按对既有测试破坏最小的方式，
    用 mock.patch.object 替换 runtime 模块的 SKIP_AUTH_BYPASS 常量后构造
    runtime（DutyRuntime.__init__ 在构造时读取该模块全局量），不 reload 模块、
    不起子进程，避免污染共享的 core.app 与其他测试。
    """

    def test_health_reports_auth_bypassed_false_by_default(self):
        with mock.patch.object(runtime_module, "SKIP_AUTH_BYPASS", False):
            runtime = self.make_runtime()
        with _runtime_on_app(runtime):
            with TestClient(app) as client:
                health = client.get("/health").json()
                engine_info = client.get("/engine/info").json()
        self.assertIs(health["auth_bypassed"], False)
        self.assertIs(engine_info["auth_bypassed"], False)
        self.assertFalse(runtime.skip_auth_bypass)

    def test_health_reports_auth_bypassed_true_when_skip_enabled(self):
        with mock.patch.object(runtime_module, "SKIP_AUTH_BYPASS", True):
            runtime = self.make_runtime()
        self.assertTrue(runtime.skip_auth_bypass)
        with _runtime_on_app(runtime):
            with TestClient(app) as client:
                health = client.get("/health").json()
                engine_info = client.get("/engine/info").json()
        self.assertIs(health["auth_bypassed"], True)
        self.assertIs(engine_info["auth_bypassed"], True)


if __name__ == "__main__":
    unittest.main()
