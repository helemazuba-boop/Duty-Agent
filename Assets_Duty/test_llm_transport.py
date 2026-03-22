#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))

from llm_transport import _extract_json_candidate, call_llm, call_llm_json


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
    def test_call_llm_parses_csv_after_reasoning_blocks(self, mock_call_llm_raw):
        mock_call_llm_raw.return_value = """
<think>
I might mention RESET here, but this is still just reasoning.
</think>
<csv>
Date,Assigned_IDs,Note
2026-03-19,"1001,1002",Ready
</csv>
<next_run_note>Next time keep balance.</next_run_note>
"""

        parsed, raw_text = call_llm(
            [{"role": "user", "content": "Return CSV"}],
            TEST_CONFIG,
        )

        self.assertEqual(len(parsed["schedule"]), 1)
        self.assertEqual(parsed["schedule"][0]["date"], "2026-03-19")
        self.assertEqual(parsed["schedule"][0]["area_ids"]["default_area"], "1001,1002")
        self.assertEqual(parsed["next_run_note"], "Next time keep balance.")
        self.assertIn("<think>", raw_text)

    @patch("llm_transport.call_llm_raw")
    def test_call_llm_uses_content_after_reset_control_line(self, mock_call_llm_raw):
        mock_call_llm_raw.return_value = """
<csv>
Date,Assigned_IDs,Note
2026-03-19,9999,Bad draft
</csv>
RESET
<csv>
Date,Assigned_IDs,Note
2026-03-20,1003,Final draft
</csv>
"""

        parsed, _ = call_llm(
            [{"role": "user", "content": "Return CSV"}],
            TEST_CONFIG,
        )

        self.assertEqual(len(parsed["schedule"]), 1)
        self.assertEqual(parsed["schedule"][0]["date"], "2026-03-20")
        self.assertEqual(parsed["schedule"][0]["area_ids"]["default_area"], "1003")


if __name__ == "__main__":
    unittest.main()
