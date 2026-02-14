#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Unit tests for core.py bug fixes."""

import json
import os
import sys
import tempfile
import unittest
from datetime import date, timedelta
from pathlib import Path
from unittest.mock import patch, MagicMock

# Ensure the module under test is importable
sys.path.insert(0, str(Path(__file__).resolve().parent))

from core import (
    fill_rotation_ids,
    merge_schedule_pool,
    anonymize_instruction,
    save_json_atomic,
    generate_target_dates,
    dedupe_pool_by_date,
    merge_input_config,
)


class TestMergeInputConfig(unittest.TestCase):
    """Bug: core.py ignored nested config from MCP."""

    def test_flat_input(self):
        """Flat input remains unchanged."""
        data = {"instruction": "test", "base_url": "http://flat.com"}
        result = merge_input_config(data)
        self.assertEqual(result, data)

    def test_nested_config_merges(self):
        """Nested config merges into root, overriding root if collision."""
        data = {
            "instruction": "test",
            "val": 1,
            "config": {
                "base_url": "http://merged.com",
                "val": 2
            }
        }
        result = merge_input_config(data)
        self.assertEqual(result["base_url"], "http://merged.com")
        self.assertEqual(result["val"], 2) # Override
        self.assertEqual(result["instruction"], "test") # Preserved

    def test_instruction_preserved_even_if_in_config(self):
        """Instruction in root takes precedence if we want? Actually logic says root preserved."""
        # Logic: merged = input.copy(); merged.update(config); if instruction in input: restore it.
        data = {
            "instruction": "root",
            "config": {
                "instruction": "config"
            }
        }
        result = merge_input_config(data)
        self.assertEqual(result["instruction"], "root")

    def test_empty_or_invalid(self):
        self.assertEqual(merge_input_config({}), {})
        self.assertEqual(merge_input_config(None), {})



class TestFillRotationIdsFallback(unittest.TestCase):
    """Bug #1: fill_rotation_ids should not produce empty results."""

    def test_normal_case(self):
        """Normal case: enough candidates available."""
        active = [1, 2, 3, 4]
        result, _ = fill_rotation_ids([], active, 0, 2)
        self.assertEqual(len(result), 2)

    def test_avoid_ids_blocks_some(self):
        """When avoid_ids blocks some, picks from remaining."""
        active = [1, 2, 3, 4]
        result, _ = fill_rotation_ids([], active, 0, 2, avoid_ids={1, 2})
        self.assertEqual(len(result), 2)
        self.assertTrue(all(pid not in {1, 2} for pid in result))

    def test_avoid_ids_blocks_all_fallback(self):
        """Bug fix: when avoid_ids blocks ALL active_ids, fallback ignores avoid_ids."""
        active = [1, 2, 3]
        result, _ = fill_rotation_ids([], active, 0, 2, avoid_ids={1, 2, 3})
        # Should NOT be empty; fallback should provide results
        self.assertGreater(len(result), 0)
        self.assertLessEqual(len(result), 2)

    def test_per_day_exceeds_active_count(self):
        """When per_day > len(active_ids), result has at most len(active_ids)."""
        active = [1, 2]
        result, _ = fill_rotation_ids([], active, 0, 5)
        self.assertEqual(len(result), 2)

    def test_initial_ids_respected(self):
        """Initial IDs from LLM are used first."""
        active = [1, 2, 3, 4]
        result, _ = fill_rotation_ids([3, 4], active, 0, 2)
        self.assertEqual(result, [3, 4])


class TestMergeSchedulePoolReplaceFuture(unittest.TestCase):
    """Bug #2: replace_future should use start_date, not datetime.now()."""

    def test_replace_future_uses_start_date(self):
        """Only entries before start_date are kept."""
        pool = [
            {"date": "2026-02-10"},
            {"date": "2026-02-11"},
            {"date": "2026-02-12"},
            {"date": "2026-02-13"},
        ]
        state = {"schedule_pool": pool}
        new_entries = [{"date": "2026-02-12"}, {"date": "2026-02-13"}]
        start = date(2026, 2, 12)

        result = merge_schedule_pool(state, new_entries, "replace_future", start)
        dates = [e["date"] for e in result]
        # 2026-02-10 and 2026-02-11 are before start_date → kept
        # 2026-02-12 and 2026-02-13 come from new_entries
        self.assertIn("2026-02-10", dates)
        self.assertIn("2026-02-11", dates)
        self.assertIn("2026-02-12", dates)
        self.assertIn("2026-02-13", dates)
        self.assertEqual(len(result), 4)

    def test_replace_future_does_not_keep_start_date_old(self):
        """Old entry on start_date itself should be replaced by new entry."""
        pool = [{"date": "2026-02-12", "old": True}]
        state = {"schedule_pool": pool}
        new_entries = [{"date": "2026-02-12", "old": False}]
        start = date(2026, 2, 12)

        result = merge_schedule_pool(state, new_entries, "replace_future", start)
        self.assertEqual(len(result), 1)
        # new entry wins via dedupe (later in merged list)
        self.assertFalse(result[0].get("old", True))

    def test_replace_all(self):
        """replace_all discards all old entries."""
        pool = [{"date": "2026-02-10"}, {"date": "2026-02-11"}]
        state = {"schedule_pool": pool}
        new_entries = [{"date": "2026-02-12"}]

        result = merge_schedule_pool(state, new_entries, "replace_all", date(2026, 2, 12))
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["date"], "2026-02-12")

    def test_append(self):
        """append merges old + new, deduped."""
        pool = [{"date": "2026-02-10"}]
        state = {"schedule_pool": pool}
        new_entries = [{"date": "2026-02-11"}]

        result = merge_schedule_pool(state, new_entries, "append", date(2026, 2, 11))
        self.assertEqual(len(result), 2)


class TestAnonymizeInstruction(unittest.TestCase):
    """Bug #6: anonymize_instruction should not double-replace."""

    def test_basic_replacement(self):
        """Names are replaced by their IDs."""
        name_to_id = {"张三": 101, "李四": 102}
        result = anonymize_instruction("今天张三和李四值日", name_to_id)
        self.assertIn("101", result)
        self.assertIn("102", result)
        self.assertNotIn("张三", result)
        self.assertNotIn("李四", result)

    def test_no_double_replacement(self):
        """Replacing '王' with '1' should not cause '13' to match '王三'."""
        # Simulate: name "王" has ID 1, name "王三" has ID 13
        # Without fix: "王三" → "13", then "王" matches inside "13" → "113"
        name_to_id = {"王三": 13, "王": 1}
        text = "王三和王都要值日"
        result = anonymize_instruction(text, name_to_id)
        # "王三" should become "13", "王" should become "1"
        self.assertIn("13", result)
        self.assertIn("1", result)
        # The "13" should NOT be further mutated (e.g., to "113")
        self.assertNotIn("113", result)

    def test_empty_text(self):
        result = anonymize_instruction("", {"张三": 1})
        self.assertEqual(result, "")

    def test_no_names_in_text(self):
        result = anonymize_instruction("无关文本", {"张三": 1})
        self.assertEqual(result, "无关文本")


class TestSaveJsonAtomic(unittest.TestCase):
    """Bug #10: save_json_atomic should handle basic writes correctly."""

    def test_basic_write(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "test.json"
            data = {"key": "value", "num": 42}
            save_json_atomic(path, data)
            with open(path, "r", encoding="utf-8") as f:
                loaded = json.load(f)
            self.assertEqual(loaded, data)

    def test_overwrite_existing(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "test.json"
            save_json_atomic(path, {"old": True})
            save_json_atomic(path, {"new": True})
            with open(path, "r", encoding="utf-8") as f:
                loaded = json.load(f)
            self.assertEqual(loaded, {"new": True})

    def test_no_temp_file_left(self):
        """After successful write, temp file should be cleaned up."""
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "test.json"
            save_json_atomic(path, {"a": 1})
            tmp = path.with_suffix(".json.tmp")
            self.assertFalse(tmp.exists())


class TestGenerateTargetDates(unittest.TestCase):
    """Regression test for date generation logic."""

    def test_skip_weekends(self):
        # 2026-02-09 is Monday
        start = date(2026, 2, 9)
        dates = generate_target_dates(start, 5, skip_weekends=True)
        self.assertEqual(len(dates), 5)
        for d in dates:
            self.assertLess(d.weekday(), 5, f"{d} is a weekend day!")

    def test_no_skip_weekends(self):
        start = date(2026, 2, 9)
        dates = generate_target_dates(start, 7, skip_weekends=False)
        self.assertEqual(len(dates), 7)
        # Should include Sat/Sun
        weekdays = {d.weekday() for d in dates}
        self.assertIn(5, weekdays)  # Sat
        self.assertIn(6, weekdays)  # Sun


if __name__ == "__main__":
    unittest.main()
