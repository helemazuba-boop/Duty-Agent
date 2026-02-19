
import unittest
from core import force_insert_debts

class TestDebtEnforcement(unittest.TestCase):
    def test_force_insert_debts_prepends(self):
        schedule_raw = [{"date": "2026-02-20", "area_ids": {"A": [3, 4]}}]
        debt_list = [1, 2]
        area_names = ["A", "B"]
        
        force_insert_debts(schedule_raw, debt_list, area_names)
        
        # Debts should be at the START of the list
        expected = [1, 2, 3, 4]
        self.assertEqual(schedule_raw[0]["area_ids"]["A"], expected)

    def test_force_insert_debts_handles_empty_schedule(self):
        schedule_raw = [{"date": "2026-02-20"}]
        debt_list = [1]
        area_names = ["A"]
        
        force_insert_debts(schedule_raw, debt_list, area_names)
        
        self.assertEqual(schedule_raw[0]["area_ids"]["A"], [1])

    def test_debt_recalculation_logic(self):
        # Simulation of main loop logic
        # Scenario: Debt [1, 2]. LLM says New Debt [3].
        # Schedule has [1]. (2 was dropped).
        
        normalized_ids = [{"area_ids": {"A": [1]}}]
        debt_list = [1, 2]
        new_debt_list = [3]
        
        scheduled_set = set()
        for entry in normalized_ids:
            for ids in entry.get("area_ids", {}).values():
                scheduled_set.update(ids)
        
        final_debt_set = set(new_debt_list)
        for old_pid in debt_list:
            if old_pid not in scheduled_set:
                final_debt_set.add(old_pid)
        final_debt_set = final_debt_set - scheduled_set
        
        # Expected: 1 is scheduled, so removed. 2 is not, so kept. 3 is new.
        # Result: {2, 3}
        self.assertEqual(final_debt_set, {2, 3})

if __name__ == '__main__':
    unittest.main()
