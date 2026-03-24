#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import sys
import unittest
from datetime import date
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))

from llm_transport import _extract_json_candidate, call_llm, call_llm_json
from state_ops import DEFAULT_SINGLE_AREA_NAME


TEST_CONFIG = {
    "base_url": "http://127.0.0.1:11434/v1",
    "model": "unit-test-model",
    "api_key": "",
    "llm_stream": False,
}


class TestLlmTransportStructuredParsing(unittest.TestCase):
    def test_extract_json_candidate_ignores_reasoning_blocks(self):
        content = """
<think>
I should first inspect this draft payload: {"debug": true}
</think>
```json
{"status": "ok", "count": 2}
```
"""
        parsed = _extract_json_candidate(content)
        self.assertEqual(parsed["status"], "ok")
        self.assertEqual(parsed["count"], 2)

    @patch("llm_transport.call_llm_raw")
    def test_call_llm_json_ignores_reasoning_blocks(self, mock_call_llm_raw):
        mock_call_llm_raw.return_value = """
<thinking>
Need to think first. {"noise": 1}
</thinking>
<json>{"result": "success", "items": [1, 2]}</json>
"""

        parsed, raw_text = call_llm_json(
            [{"role": "user", "content": "Return JSON"}],
            TEST_CONFIG,
        )

        self.assertEqual(parsed["result"], "success")
        self.assertEqual(parsed["items"], [1, 2])
        self.assertIn("<thinking>", raw_text)

    @patch("llm_transport.call_llm_raw")
    def test_call_llm_parses_v2_sections_after_reasoning_blocks(self, mock_call_llm_raw):
        mock_call_llm_raw.return_value = """
<think>
draft only
</think>
@areas
A=教室 B=清洁区 S=大扫除

@schedule
03-24: A=1001 1002; B=1003 1004
03-28: A=1009 1010; B=1011 1012; S=1013 1014; _note=周五大扫除

@state
debt=1004*2 1005
credit=1002
"""

        parsed, raw_text = call_llm(
            [{"role": "user", "content": "Return V2"}],
            TEST_CONFIG,
            start_date_value=date(2026, 3, 24),
        )

        self.assertEqual(len(parsed["schedule"]), 2)
        self.assertEqual(parsed["schedule"][0]["date"], "2026-03-24")
        self.assertEqual(parsed["schedule"][1]["area_ids"]["大扫除"], [1013, 1014])
        self.assertEqual(parsed["schedule"][1]["note"], "周五大扫除")
        self.assertEqual(parsed["state_delta"]["debt_counts"], {1004: 2, 1005: 1})
        self.assertEqual(parsed["state_delta"]["credit_counts"], {1002: 1})
        self.assertIn("<think>", raw_text)

    @patch("llm_transport.call_llm_raw")
    def test_call_llm_rejects_note_not_last(self, mock_call_llm_raw):
        mock_call_llm_raw.return_value = """
@areas
A=教室

@schedule
03-24: _note=bad; A=1001
"""
        with self.assertRaisesRegex(RuntimeError, "_note must be last"):
            call_llm(
                [{"role": "user", "content": "Return V2"}],
                TEST_CONFIG,
                start_date_value=date(2026, 3, 24),
            )

    @patch("llm_transport.call_llm_raw")
    def test_call_llm_keeps_legacy_assigned_ids_as_single_area_fallback(self, mock_call_llm_raw):
        mock_call_llm_raw.return_value = """
<csv>
Date,Assigned_IDs,Note
2026-03-22,1004,Legacy
</csv>
"""

        parsed, _ = call_llm(
            [{"role": "user", "content": "Return CSV"}],
            TEST_CONFIG,
            start_date_value=date(2026, 3, 22),
        )

        self.assertEqual(parsed["schedule"][0]["area_ids"][DEFAULT_SINGLE_AREA_NAME], "1004")
        self.assertEqual(parsed["state_delta"]["debt_counts"], {})
        self.assertEqual(parsed["state_delta"]["credit_counts"], {})

    @patch("llm_transport.call_llm_raw")
    def test_call_llm_handles_cross_year_mmdd(self, mock_call_llm_raw):
        mock_call_llm_raw.return_value = """
@areas
A=值日

@schedule
12-31: A=1001
01-02: A=1002
"""

        parsed, _ = call_llm(
            [{"role": "user", "content": "Return V2"}],
            TEST_CONFIG,
            start_date_value=date(2026, 12, 31),
        )

        self.assertEqual(parsed["schedule"][0]["date"], "2026-12-31")
        self.assertEqual(parsed["schedule"][1]["date"], "2027-01-02")


if __name__ == "__main__":
    unittest.main()
