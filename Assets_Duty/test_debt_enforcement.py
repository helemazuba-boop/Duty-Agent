#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import unittest
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))

from engine import recover_missing_debts, reconcile_credit_list


class TestDebtQueueFallback(unittest.TestCase):
    def test_unscheduled_original_debt_is_recovered(self):
        normalized_ids = [{"date": "2026-02-20", "area_ids": {"A": [1]}}]
        result = recover_missing_debts(
            original_debt_list=[1, 2],
            new_debt_ids_from_llm=[3],
            normalized_schedule=normalized_ids,
        )
        self.assertEqual(result, {2: 1, 3: 1})

    def test_scheduled_ids_removed_from_debt_even_if_llm_reports_them(self):
        normalized_ids = [{"date": "2026-02-20", "area_ids": {"A": [1, 2]}}]
        result = recover_missing_debts(
            original_debt_list=[1],
            new_debt_ids_from_llm=[1, 4],
            normalized_schedule=normalized_ids,
        )
        self.assertEqual(result, {1: 1, 4: 1})

    def test_non_dict_entries_do_not_crash_recovery(self):
        normalized_ids = ["bad", {"area_ids": {"A": [1]}}, 123]
        result = recover_missing_debts(
            original_debt_list=[1, 2],
            new_debt_ids_from_llm=[],
            normalized_schedule=normalized_ids,
        )
        self.assertEqual(result, {2: 1})


class TestCreditSemantics(unittest.TestCase):
    def test_credit_delta_is_incremental(self):
        result = reconcile_credit_list(
            original_credit_list=[10, 11],
            new_credit_ids_from_llm=[11],
            normalized_schedule=[],
            valid_ids={10, 11},
            debt_list=[],
            has_llm_field=True,
        )
        self.assertEqual(result, {10: 1, 11: 2})


if __name__ == "__main__":
    unittest.main()
