"""Regression tests for CoT isolation, retry visibility and pointer authority.

These are the concrete failure modes from the 2026-11 audit: a process-global
reasoning buffer shared by concurrent agents and retries, HTTP retries that
emitted no progress event, and a model-declared pointer trusted as truth.
"""

from __future__ import annotations

import io
import socket
import sys
import tempfile
import unittest
import urllib.error
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))

import llm_transport  # noqa: E402
from build_prompt import build_prompt_messages  # noqa: E402
from llm_transport import (  # noqa: E402
    execute_with_retries,
    get_and_clear_last_reasoning,
    new_reasoning_collector,
)
from schedule_window import resolve_schedule_dates  # noqa: E402
from single_pass_executor import apply_single_pass_completion  # noqa: E402
from state_ops import Context, load_state, save_json_atomic  # noqa: E402

ROSTER = (
    "id,name,active\n"
    "1001,张三,1\n1002,李四,1\n1003,王五,1\n1004,赵六,1\n"
)


def _make_ctx(tmp: Path, *, state=None) -> Context:
    (tmp / "roster.csv").write_text(ROSTER, encoding="utf-8")
    save_json_atomic(
        tmp / "state.json",
        state
        if state is not None
        else {"schedule_pool": [], "debt_counts": {}, "credit_counts": {}, "last_pointer": 0},
    )
    save_json_atomic(
        tmp / "config.json",
        {
            "version": 1,
            "areas": ["教室"],
            "area_per_day_counts": {"教室": 2},
            "duty_rule": "",
            "validation_mode": "repair",
        },
    )
    return Context(data_dir=tmp, trace_id="t")


class CollectorIsolationTests(unittest.TestCase):
    def setUp(self):
        llm_transport.reset_last_reasoning()

    def test_two_collectors_are_independent(self):
        first = new_reasoning_collector("A")
        second = new_reasoning_collector("B")
        first.start_attempt(1)
        first.add("reasoning-A")
        second.start_attempt(1)
        second.add("reasoning-B")
        self.assertEqual(first.text, "reasoning-A")
        self.assertEqual(second.text, "reasoning-B")

    def test_retry_drops_previous_attempt_text(self):
        collector = new_reasoning_collector("A")
        collector.start_attempt(1)
        collector.add("failed attempt's 18000 chars")
        attempt_id = collector.start_attempt(2)
        collector.add("fresh answer")
        self.assertEqual(collector.text, "fresh answer")
        self.assertEqual(collector.attempt_id, attempt_id)
        self.assertEqual(len(collector.summary()["attempts"]), 2)

    def test_parallel_writers_do_not_cross_contaminate(self):
        first = new_reasoning_collector("A")
        second = new_reasoning_collector("B")
        with ThreadPoolExecutor(max_workers=2) as pool:
            pool.submit(lambda: [first.start_attempt(1), first.add("A" * 50)])
            pool.submit(lambda: [second.start_attempt(1), second.add("B" * 50)])
        # Both futures are waited on by the context manager.
        self.assertTrue(first.text.startswith("A"))
        self.assertTrue(second.text.startswith("B"))
        self.assertNotIn("B", first.text)
        self.assertNotIn("A", second.text)

    def test_call_llm_raw_clears_stale_legacy_reasoning(self):
        llm_transport._record_reasoning("leftover from a failed run")
        with patch.object(
            llm_transport, "_build_llm_target", side_effect=RuntimeError("stop here")
        ):
            with self.assertRaises(RuntimeError):
                llm_transport.call_llm_raw([], {"base_url": "", "model": "m"})
        self.assertEqual(get_and_clear_last_reasoning(), "")


class RetryVisibilityTests(unittest.TestCase):
    def _run(self, side_effects):
        events = []
        collector = new_reasoning_collector("test")

        def request_fn():
            item = side_effects.pop(0)
            if isinstance(item, BaseException):
                collector.add("reasoning from failed attempt")
                raise item
            collector.add("reasoning from successful attempt")
            return item

        with patch.object(llm_transport.time, "sleep"):
            result = execute_with_retries(
                request_fn,
                mode="non_stream",
                progress_callback=lambda phase, msg, chunk: events.append((phase, chunk)),
                reasoning_collector=collector,
            )
        return result, events, collector

    def test_network_retry_emits_both_phases_and_discards_old_cot(self):
        result, events, collector = self._run([socket.timeout("timeout"), "ok"])
        self.assertEqual(result, "ok")
        self.assertIn("llm_retry", [phase for phase, _ in events])
        self.assertIn("reasoning_reset", [phase for phase, _ in events])
        self.assertEqual(collector.text, "reasoning from successful attempt")
        self.assertNotIn("failed attempt", collector.text)
        self.assertGreater(collector.summary()["attempts"][0]["chars"], 0)

    def test_http_5xx_retry_is_no_longer_silent(self):
        error = urllib.error.HTTPError(
            "http://x", 503, "unavailable", None, io.BytesIO(b"down")
        )
        result, events, _ = self._run([error, "ok"])
        self.assertEqual(result, "ok")
        self.assertIn("llm_retry", [phase for phase, _ in events])

    def test_reasoning_reset_carries_attempt_id(self):
        _, events, collector = self._run([ConnectionError("boom"), "ok"])
        resets = [chunk for phase, chunk in events if phase == "reasoning_reset"]
        self.assertEqual(len(resets), 1)
        self.assertEqual(resets[0], collector.attempt_id)


class ResponseTelemetryTests(unittest.TestCase):
    def test_stream_reports_finish_reason_and_token_usage(self):
        import contextlib
        import json as _json

        sse = (
            "data: " + _json.dumps({"choices": [{"delta": {"content": "[areas]\nA = 教室\n"}}]}) + "\n\n"
            + "data: " + _json.dumps({
                "model": "test-model",
                "usage": {"prompt_tokens": 10, "completion_tokens": 321},
                "choices": [{"finish_reason": "stop"}],
            }) + "\n\n"
            + "data: [DONE]\n\n"
        )
        events = []

        @contextlib.contextmanager
        def fake_urlopen(url, timeout=None):
            yield io.BytesIO(sse.encode("utf-8"))

        with patch.object(llm_transport.urllib.request, "urlopen", fake_urlopen):
            llm_transport.request_llm_stream(
                "http://x/v1/chat/completions",
                {"model": "test-model"},
                "",
                lambda phase, msg, chunk: events.append((phase, chunk)),
                None,
            )
        meta_events = [chunk for phase, chunk in events if phase == "llm_response_meta"]
        self.assertEqual(len(meta_events), 1)
        meta = _json.loads(meta_events[0])
        self.assertEqual(meta["finish_reason"], "stop")
        self.assertEqual(meta["completion_tokens"], 321)
        self.assertEqual(meta["model"], "test-model")


class PointerAuthorityTests(unittest.TestCase):
    def test_model_pointer_is_a_hint_not_truth(self):
        tmp = Path(tempfile.mkdtemp())
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        ctx = _make_ctx(tmp)
        result = apply_single_pass_completion(
            ctx,
            "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n[state]\npointer = 0\n",
            {"mode": "single_pass", "start_date": "2026-10-22", "trace_id": "t"},
        )
        self.assertEqual(result["status"], "success")
        # Allocated 1001 (index 0) and 1002 (index 1) => next index is 2,
        # not the model-declared 0.
        self.assertEqual(load_state(ctx.paths["state"])["last_pointer"], 2)
        self.assertTrue(any("指针" in w for w in result["adjustment_warnings"]))

    def test_external_ingest_does_not_inherit_stale_reasoning(self):
        tmp = Path(tempfile.mkdtemp())
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        ctx = _make_ctx(tmp)
        llm_transport._record_reasoning("stale from a failed run")
        result = apply_single_pass_completion(
            ctx,
            "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n",
            {"mode": "single_pass", "start_date": "2026-10-22", "trace_id": "t"},
        )
        self.assertNotIn("reasoning_content", result)

    def test_inline_think_reasoning_still_surfaces(self):
        tmp = Path(tempfile.mkdtemp())
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        ctx = _make_ctx(tmp)
        result = apply_single_pass_completion(
            ctx,
            "<think>inline reasoning</think>\n"
            "[areas]\nA = 教室\n[schedule]\n10-22 = A:1001 1002\n",
            {"mode": "single_pass", "start_date": "2026-10-22", "trace_id": "t"},
        )
        self.assertIn("inline reasoning", result.get("reasoning_content", ""))


class PromptContractTests(unittest.TestCase):
    def _prompt(self, **overrides):
        kwargs = dict(
            all_ids=list(range(1, 49)),
            current_time="2026-11-07 12:43",
            id_to_active={i: 1 for i in range(1, 49)},
            instruction="从今天19#，20#开始，安排今天及下周值日",
            duty_rule="周一至周五分为教室清洁区两个区域，每个区域2人 周六安排教室2人",
            area_names=[],
            area_per_day_counts={},
            debt_counts={},
            credit_counts={},
            start_date="2026-11-07",
            model_profile="cloud",
            orchestration_mode="single_pass",
            single_pass_strategy="cloud_standard",
            last_pointer=41,
        )
        kwargs.update(overrides)
        return build_prompt_messages(**kwargs)[0]["content"]

    def test_explicit_dates_and_weekdays_reach_the_prompt(self):
        window = resolve_schedule_dates(
            "从今天19#，20#开始，安排今天及下周值日",
            __import__("datetime").date(2026, 11, 7),
            7,
            "周一至周五分为教室清洁区两个区域，每个区域2人 周六安排教室2人",
        )
        content = self._prompt(schedule_window=window)
        self.assertIn("scheduled_dates=2026-11-07, 2026-11-09", content)
        self.assertIn("weekday_hints=11-07=Sat", content)
        self.assertIn("Only generate the exact requested dates", content)

    def test_pointer_and_id_notation_are_defined(self):
        content = self._prompt()
        self.assertIn("0-based", content)
        self.assertIn("NOT a person ID", content)
        self.assertIn("`N#`", content)
        self.assertIn("all_roster_ids", content)

    def test_area_rule_no_longer_contradicts_user_defined_rule(self):
        content = self._prompt()
        self.assertNotIn("cover the ones you declared on every scheduled date", content)
        self.assertIn("does NOT have to appear on every date", content)
        self.assertIn("user_defined_rule takes priority", content)

    def test_compact_template_keeps_the_full_protocol(self):
        content = self._prompt(model_profile="campus_small")
        self.assertIn("consumed_credit", content)
        self.assertIn("0-based", content)
        self.assertIn("OPTIONAL", content)
        self.assertIn("MM-DD = A:1001 1002 | B:1003 1004", content)


if __name__ == "__main__":
    unittest.main()
