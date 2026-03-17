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

from engine import (
    merge_schedule_pool,
    reconcile_credit_list,
    call_llm,
    anonymize_instruction,
    save_json_atomic,
    dedupe_pool_by_date,
    normalize_area_names,
    normalize_multi_area_schedule_ids,
    restore_schedule,
    validate_llm_schedule_entries,
)


class TestAreaNameNormalization(unittest.TestCase):
    def test_empty_area_names_remains_empty(self):
        self.assertEqual(normalize_area_names([]), [])

    def test_missing_area_names_remains_empty(self):
        self.assertEqual(normalize_area_names(None), [])

class TestNormalizeScheduleNoValidation(unittest.TestCase):
    """Test the new 'no-validation' normalization logic."""

    def test_parses_valid_date_and_ids(self):
        """Should extract date and IDs as-is."""
        raw = [{"date": "2023-10-23", "area_ids": {"A": [1, 2]}}]
        active = [1, 2, 3]
        areas = ["A"]
        counts = {"A": 2}
        
        normalized = normalize_multi_area_schedule_ids(raw, active, areas, counts)
        self.assertEqual(len(normalized), 1)
        self.assertEqual(normalized[0]["date"], "2023-10-23")
        self.assertEqual(normalized[0]["area_ids"]["A"], [1, 2])

    def test_skips_invalid_date(self):
        """Should ignore entries with missing or too-short dates."""
        raw = [{"date": "", "area_ids": {"A": [1]}}, {"date": "INVALID", "area_ids": {"A": [1]}}]
        active = [1]
        areas = ["A"]
        counts = {"A": 1}
        
        normalized = normalize_multi_area_schedule_ids(raw, active, areas, counts)
        self.assertEqual(len(normalized), 0)

    def test_no_force_fill(self):
        """Should NOT fill up to the default slot count if IDs are missing."""
        # Request says 2 per day, but AI gives 0 or 1
        raw = [{"date": "2023-10-23", "area_ids": {"A": [1]}}]
        active = [1, 2, 3]
        areas = ["A"]
        counts = {"A": 2} # Expect 2
        
        normalized = normalize_multi_area_schedule_ids(raw, active, areas, counts)
        # Result should still have only 1 ID
        self.assertEqual(normalized[0]["area_ids"]["A"], [1])

    def test_filters_inactive_ids(self):
        """Should still filter out inactive IDs."""
        raw = [{"date": "2023-10-23", "area_ids": {"A": [1, 999]}}]
        active = [1]
        areas = ["A"]
        counts = {"A": 2}
        
        normalized = normalize_multi_area_schedule_ids(raw, active, areas, counts)
        self.assertEqual(normalized[0]["area_ids"]["A"], [1])

    def test_dynamic_areas_without_predefined(self):
        """When configured area_names is empty, only AI dynamic areas should remain."""
        raw = [{"date": "2023-10-23", "area_ids": {"后花园": [1], "天台": [2]}}]
        active = [1, 2]
        areas = []
        counts = {}

        normalized = normalize_multi_area_schedule_ids(raw, active, areas, counts)
        self.assertEqual(len(normalized), 1)
        self.assertEqual(set(normalized[0]["area_ids"].keys()), {"后花园", "天台"})


class TestScheduleValidation(unittest.TestCase):
    def test_unsorted_dates_raise_error(self):
        schedule = [
            {"date": "2026-02-12", "area_ids": {"A": [1]}},
            {"date": "2026-02-11", "area_ids": {"A": [2]}},
        ]
        with self.assertRaises(ValueError):
            validate_llm_schedule_entries(schedule)

    def test_sorted_dates_pass(self):
        schedule = [
            {"date": "2026-02-11", "area_ids": {"A": [1]}},
            {"date": "2026-02-12", "area_ids": {"A": [2]}},
        ]
        validate_llm_schedule_entries(schedule)

    def test_duplicate_dates_raise_error(self):
        schedule = [
            {"date": "2026-02-11", "area_ids": {"A": [1]}},
            {"date": "2026-02-11", "area_ids": {"A": [2]}},
        ]
        with self.assertRaises(ValueError):
            validate_llm_schedule_entries(schedule)


class TestRestoreScheduleDirectDate(unittest.TestCase):
    """Test restoration using explicit dates."""

    def test_restores_basic_info(self):
        normalized = [{"date": "2023-10-23", "area_ids": {"A": [1]}, "note": "test"}]
        id_to_name = {1: "Alice"}
        areas = ["A"]
        
        restored = restore_schedule(normalized, id_to_name, areas)
        self.assertEqual(len(restored), 1)
        self.assertEqual(restored[0]["date"], "2023-10-23")
        self.assertEqual(restored[0]["day"], "Mon")
        self.assertEqual(restored[0]["area_assignments"]["A"], ["Alice"])
        self.assertEqual(restored[0]["note"], "test")

    def test_merges_existing_notes(self):
        normalized = [{"date": "2023-10-23", "note": ""}]
        existing = {"2023-10-23": "Old Note"}
        
        restored = restore_schedule(normalized, {}, [], existing)
        self.assertEqual(restored[0]["note"], "Old Note")


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

    def test_append_overwrites_existing_date(self):
        pool = [{"date": "2026-02-10", "note": "old"}]
        state = {"schedule_pool": pool}
        new_entries = [{"date": "2026-02-10", "note": "new"}]

        result = merge_schedule_pool(state, new_entries, "append", date(2026, 2, 11))
        self.assertEqual(len(result), 1)
        self.assertEqual(result[0]["note"], "new")

    def test_replace_overlap_empty_restored_keeps_existing(self):
        """replace_overlap with empty restored should keep existing pool without crashing."""
        pool = [{"date": "2026-02-10"}, {"date": "2026-02-11"}]
        state = {"schedule_pool": pool}

        result = merge_schedule_pool(state, [], "replace_overlap", date(2026, 2, 11))
        self.assertEqual([x["date"] for x in result], ["2026-02-10", "2026-02-11"])


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


class TestCallLlmPayloadIsolation(unittest.TestCase):
    def test_parse_retry_does_not_mutate_input_messages(self):
        messages = [{"role": "user", "content": "hello"}]
        config = {
            "base_url": "https://example.com",
            "model": "dummy",
            "api_key": "k",
            "llm_stream": False,
        }
        with patch(
            "llm_transport.call_llm_raw",
            return_value="no csv here",
        ), patch(
            "llm_transport.request_llm_non_stream",
            return_value="<csv>\nDate,Assigned_IDs,Note\n2023-10-10,4,ok\n</csv>",
        ):
            parsed, _ = call_llm(messages, config)
        self.assertEqual(parsed["schedule"][0]["date"], "2023-10-10")
        self.assertEqual(messages, [{"role": "user", "content": "hello"}])


class TestDynamicAreaExtractionLimit(unittest.TestCase):
    def test_dynamic_area_ids_not_capped_at_999(self):
        active = list(range(1, 1201))
        raw = [{"date": "2026-02-11", "area_ids": {"Dynamic": active[:]}}]

        normalized = normalize_multi_area_schedule_ids(raw, active, [], {})
        dynamic_ids = normalized[0]["area_ids"]["Dynamic"]
        self.assertEqual(len(dynamic_ids), 1200)
        self.assertEqual(dynamic_ids[0], 1)
        self.assertEqual(dynamic_ids[-1], 1200)


class TestMergeSchedulePoolRobustness(unittest.TestCase):
    def test_replace_future_skips_non_dict_pool_items(self):
        state = {"schedule_pool": ["bad", 123, {"date": "2026-02-10"}, {"date": "2026-02-12"}]}
        new_entries = [{"date": "2026-02-12", "note": "new"}]

        result = merge_schedule_pool(state, new_entries, "replace_future", date(2026, 2, 12))
        self.assertEqual(result, [{"date": "2026-02-10"}, {"date": "2026-02-12", "note": "new"}])

    def test_replace_overlap_preserves_invalid_date_entries(self):
        state = {
            "schedule_pool": [
                {"date": "2026-02-09", "note": "before"},
                {"date": "not-a-date", "note": "legacy"},
                {"date": "2026-02-10", "note": "old"},
            ]
        }
        new_entries = [{"date": "2026-02-10", "note": "new"}]

        result = merge_schedule_pool(state, new_entries, "replace_overlap", date(2026, 2, 10))
        notes_by_date = {entry["date"]: entry.get("note", "") for entry in result}
        self.assertEqual(notes_by_date["2026-02-09"], "before")
        self.assertEqual(notes_by_date["2026-02-10"], "new")
        self.assertEqual(notes_by_date["not-a-date"], "legacy")


class TestCreditReconciliation(unittest.TestCase):
    def test_uses_original_credit_when_llm_field_missing(self):
        result = reconcile_credit_list(
            original_credit_list=[7],
            new_credit_ids_from_llm=[],
            normalized_schedule=[],
            valid_ids={7},
            debt_list=[],
            has_llm_field=False,
        )
        self.assertEqual(result, [7])

    def test_uses_llm_credit_when_field_present(self):
        result = reconcile_credit_list(
            original_credit_list=[7, 8],
            new_credit_ids_from_llm=[8],
            normalized_schedule=[],
            valid_ids={7, 8},
            debt_list=[],
            has_llm_field=True,
        )
        self.assertEqual(result, [8])

    def test_filters_debt_and_invalid_ids(self):
        result = reconcile_credit_list(
            original_credit_list=[7],
            new_credit_ids_from_llm=[7, 9, 999],
            normalized_schedule=[],
            valid_ids={7, 9},
            debt_list=[9],
            has_llm_field=True,
        )
        self.assertEqual(result, [7])


if __name__ == "__main__":
    unittest.main()
