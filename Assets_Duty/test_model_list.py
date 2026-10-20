"""Regression tests for the OpenAI-compatible model listing endpoint.

Why this file exists (2026-10-21):

* ``POST /api/v1/duty/model-list`` was **always 500**: ``QueryService.list_models``
  called a bare ``list_models(...)`` that was never imported (``NameError``).
  Nothing exercised the route, so nothing caught it.
* ``readiness.list_models`` hand-rolled its own URL normalization instead of
  reusing the chat one, so it drifted: ``.../v1/models`` became
  ``.../v1/models/v1/models``.
* The endpoint only honoured the key carried in the request body. If the caller
  omitted it, a key that *was* configured for that very endpoint was ignored and
  the cloud provider answered 401.
"""

import json
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from application.query_service import QueryService  # noqa: E402
from llm_transport import _build_llm_target, resolve_provider_endpoint  # noqa: E402
from readiness import list_models  # noqa: E402
from state_ops import Context, load_config, save_config  # noqa: E402


# --------------------------------------------------------------------------- #
# mock provider
# --------------------------------------------------------------------------- #
class _Provider(BaseHTTPRequestHandler):
    payload = {"data": [{"id": "model-a"}, {"id": "model-b"}]}
    status = 200
    seen = []

    def do_GET(self):
        _Provider.seen.append(
            {"path": self.path, "auth": self.headers.get("Authorization")}
        )
        body = json.dumps(_Provider.payload).encode("utf-8")
        self.send_response(_Provider.status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):  # silence request logging
        pass


def _start_provider():
    server = HTTPServer(("127.0.0.1", 0), _Provider)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{server.server_address[1]}"


class _StubRuntime:
    """QueryService only touches ``data_dir`` for the key fallback."""

    def __init__(self, data_dir):
        self.data_dir = Path(data_dir)


# --------------------------------------------------------------------------- #
# URL resolution
# --------------------------------------------------------------------------- #
class TestResolveProviderEndpoint(unittest.TestCase):
    def test_chat_rules_unchanged(self):
        """The refactor must not change any existing chat URL."""
        cases = [
            ("http://localhost:1234/v1", "http://localhost:1234/v1/chat/completions"),
            ("http://localhost:1234/v1/", "http://localhost:1234/v1/chat/completions"),
            ("http://localhost:1234", "http://localhost:1234/v1/chat/completions"),
            (
                "https://api.example.com/v1/chat/completions",
                "https://api.example.com/v1/chat/completions",
            ),
        ]
        for base, expected in cases:
            with self.subTest(base=base):
                self.assertEqual(
                    resolve_provider_endpoint(base, "/chat/completions"), expected
                )

    def test_chat_url_via_build_target_is_stable(self):
        url, _, _ = _build_llm_target(
            {"base_url": "http://127.0.0.1:11434/v1", "model": "m", "api_key": ""},
            [{"role": "user", "content": "hi"}],
        )
        self.assertEqual(url, "http://127.0.0.1:11434/v1/chat/completions")

    def test_models_rules(self):
        cases = [
            # 老实现会把这两种拼成 /v1/models/v1/models —— 回归重点。
            (
                "https://api.example.com/v1/models",
                "https://api.example.com/v1/models",
            ),
            (
                "https://api.example.com/v1",
                "https://api.example.com/v1/models",
            ),
            (
                "https://api.example.com",
                "https://api.example.com/v1/models",
            ),
            (
                "https://api.example.com/v1/chat/completions",
                "https://api.example.com/v1/models",
            ),
            (
                "http://localhost:1234/v1/",
                "http://localhost:1234/v1/models",
            ),
            # 版本段不是 /v1 的端点（智谱 /api/paas/v4）：不能再补一个 /v1。
            (
                "https://open.bigmodel.cn/api/paas/v4",
                "https://open.bigmodel.cn/api/paas/v4/models",
            ),
        ]
        for base, expected in cases:
            with self.subTest(base=base):
                self.assertEqual(resolve_provider_endpoint(base, "/models"), expected)

    def test_chat_respects_non_v1_version_segment(self):
        self.assertEqual(
            resolve_provider_endpoint(
                "https://open.bigmodel.cn/api/paas/v4", "/chat/completions"
            ),
            "https://open.bigmodel.cn/api/paas/v4/chat/completions",
        )

    def test_v1beta_is_not_mistaken_for_a_version_segment(self):
        # 只认 /v<数字>：/v1beta 仍按"未带版本段"处理，补 /v1。
        self.assertEqual(
            resolve_provider_endpoint("https://host/v1beta", "/models"),
            "https://host/v1beta/v1/models",
        )


# --------------------------------------------------------------------------- #
# HTTP behaviour
# --------------------------------------------------------------------------- #
class TestListModelsHttp(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server, cls.base = _start_provider()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()

    def setUp(self):
        _Provider.payload = {"data": [{"id": "model-a"}, {"id": "model-b"}]}
        _Provider.status = 200
        _Provider.seen.clear()

    def test_sends_authorization_when_key_present(self):
        result = list_models(self.base + "/v1", "sk-test")
        self.assertTrue(result["ok"])
        self.assertEqual(_Provider.seen[0]["auth"], "Bearer sk-test")
        self.assertEqual(result["models"], ["model-a", "model-b"])

    def test_no_authorization_for_local_without_key(self):
        result = list_models(self.base + "/v1", "")
        self.assertTrue(result["ok"])
        self.assertIsNone(_Provider.seen[0]["auth"])

    def test_accepts_bare_list_and_dedupes(self):
        _Provider.payload = [{"id": "a"}, {"id": "b"}, {"id": "a"}]
        result = list_models(self.base + "/v1", "")
        self.assertEqual(result["models"], ["a", "b"])

    def test_surfaces_error_payload_returned_with_200(self):
        _Provider.payload = {"error": {"message": "invalid api key"}}
        result = list_models(self.base + "/v1", "sk-bad")
        self.assertFalse(result["ok"])
        self.assertIn("invalid api key", result["detail"])

    def test_https_without_key_fails_before_any_request(self):
        result = list_models("https://api.example.com/v1", "")
        self.assertFalse(result["ok"])
        self.assertEqual(result["status"], "unconfigured")
        self.assertIn("API Key", result["detail"])
        self.assertEqual(_Provider.seen, [])


# --------------------------------------------------------------------------- #
# service wiring + stored-key fallback
# --------------------------------------------------------------------------- #
class TestQueryServiceModelList(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server, cls.base = _start_provider()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()

    def setUp(self):
        _Provider.payload = {"data": [{"id": "model-a"}]}
        _Provider.status = 200
        _Provider.seen.clear()
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)

    def _service(self):
        return QueryService(_StubRuntime(self._tmp.name))

    def _seed_config(self, base_url, api_key):
        ctx = Context(Path(self._tmp.name))
        config = load_config(ctx)
        for preset in config.get("plan_presets") or []:
            if preset.get("id") == "standard":
                preset["base_url"] = base_url
                preset["api_key"] = api_key
        save_config(ctx, config)

    def test_service_list_models_is_wired(self):
        """回归：这里曾经 NameError（list_models 没导入），接口永远 500。"""
        result = self._service().list_models(self.base + "/v1", "sk-direct")
        self.assertTrue(result["ok"], result)
        self.assertEqual(_Provider.seen[0]["auth"], "Bearer sk-direct")

    def test_falls_back_to_stored_key_for_same_endpoint(self):
        self._seed_config(self.base + "/v1", "sk-stored")
        result = self._service().list_models(self.base + "/v1", "")
        self.assertTrue(result["ok"], result)
        self.assertEqual(_Provider.seen[0]["auth"], "Bearer sk-stored")

    def test_does_not_reuse_stored_key_for_a_different_endpoint(self):
        """绝不能把为 A 端点配的 key 发给 B 端点（那等于泄露密钥）。"""
        self._seed_config("https://api.somewhere-else.com/v1", "sk-stored")
        result = self._service().list_models(self.base + "/v1", "")
        self.assertTrue(result["ok"], result)
        self.assertIsNone(_Provider.seen[0]["auth"])

    def test_explicit_key_wins_over_stored_key(self):
        self._seed_config(self.base + "/v1", "sk-stored")
        self._service().list_models(self.base + "/v1", "sk-explicit")
        self.assertEqual(_Provider.seen[0]["auth"], "Bearer sk-explicit")


if __name__ == "__main__":
    unittest.main(verbosity=2)
