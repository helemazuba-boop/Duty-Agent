#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import unittest
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))

from core import recover_missing_debts, reconcile_credit_list


class TestDebtQueueFallback(unittest.TestCase):
    def test_unscheduled_original_debt_is_recovered(self):
        normalized_ids = [{"date": "2026-02-20", "area_ids": {"A": [1]}}]
        result = recover_missing_debts(
            original_debt_list=[1, 2],
            new_debt_ids_from_llm=[3],
            normalized_schedule=normalized_ids,
        )
        self.assertEqual(result, [2, 3])

    def test_scheduled_ids_removed_from_debt_even_if_llm_reports_them(self):
        normalized_ids = [{"date": "2026-02-20", "area_ids": {"A": [1, 2]}}]
        result = recover_missing_debts(
            original_debt_list=[1],
            new_debt_ids_from_llm=[1, 4],
            normalized_schedule=normalized_ids,
        )
        self.assertEqual(result, [4])

    def test_non_dict_entries_do_not_crash_recovery(self):
        normalized_ids = ["bad", {"area_ids": {"A": [1]}}, 123]
        result = recover_missing_debts(
            original_debt_list=[1, 2],
            new_debt_ids_from_llm=[],
            normalized_schedule=normalized_ids,
        )
        self.assertEqual(result, [2])


class TestCreditSemantics(unittest.TestCase):
    def test_llm_remaining_credit_is_source_of_truth(self):
        result = reconcile_credit_list(
            original_credit_list=[10, 11],
            new_credit_ids_from_llm=[11],
            normalized_schedule=[],
            valid_ids={10, 11},
            debt_list=[],
            has_llm_field=True,
        )
        self.assertEqual(result, [11])


if __name__ == "__main__":
    unittest.main()
