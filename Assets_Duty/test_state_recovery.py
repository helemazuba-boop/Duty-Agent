#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Recovery-path tests for state/config persistence (HANDOFF §18).

Covers: corrupt state.json falls back to .prev; a corrupt main file does NOT
clobber the last-good backup on the next write; corrupt config.json /
host-config.json are quarantined as *.bad-<ts> and regenerated from defaults;
rollback against a corrupt .prev raises instead of writing garbage.
"""

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from state_ops import (
    Context,
    _backup_previous_atomic,
    _load_persisted_config_unlocked,
    _load_persisted_host_config_unlocked,
    load_state,
    rollback_state,
    update_state,
)


def _write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def _good_state(pointer: int = 3) -> dict:
    return {
        "schedule_pool": [{"date": "2026-08-24", "day": "Mon", "area_assignments": {"值日": ["张三"]}}],
        "next_run_note": "",
        "debt_counts": {"1": 2},
        "credit_counts": {},
        "last_pointer": pointer,
    }


class TestStateRecovery(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.data_dir = Path(self._tmp.name)

    # ---- ST-1: load_state falls back to .prev ---------------------------------
    def test_corrupt_main_recovers_from_prev(self):
        state_path = self.data_dir / "state.json"
        prev_path = self.data_dir / "state.prev.json"
        _write(state_path, "{corrupt json!!")
        _write(prev_path, json.dumps(_good_state(pointer=7), ensure_ascii=False))

        loaded = load_state(state_path)
        self.assertEqual(loaded["last_pointer"], 7)
        self.assertEqual(loaded["debt_counts"], {1: 2})
        self.assertEqual(len(loaded["schedule_pool"]), 1)

    def test_corrupt_main_without_prev_raises(self):
        state_path = self.data_dir / "state.json"
        _write(state_path, "not json at all")
        with self.assertRaises(ValueError):
            load_state(state_path)

    def test_missing_main_returns_default_even_with_prev(self):
        # No main file means fresh install; .prev must not resurrect old data.
        prev_path = self.data_dir / "state.prev.json"
        _write(prev_path, json.dumps(_good_state()))
        loaded = load_state(self.data_dir / "state.json")
        self.assertEqual(loaded["schedule_pool"], [])
        self.assertEqual(loaded["last_pointer"], 0)

    # ---- ST-4 + ST-3: backup atomicity & corruption guard ----------------------
    def test_update_skips_backup_refresh_when_main_corrupt(self):
        state_path = self.data_dir / "state.json"
        prev_path = self.data_dir / "state.prev.json"
        good_prev = json.dumps(_good_state(pointer=5), ensure_ascii=False)
        _write(prev_path, good_prev)
        _write(state_path, "{corrupt")

        ctx = Context(self.data_dir)
        update_state(
            state_path,
            lambda current: {**current, "last_pointer": 9},
        )

        # The last-good backup must survive untouched...
        self.assertEqual(json.loads(prev_path.read_text(encoding="utf-8"))["last_pointer"], 5)
        # ...and the main file must now hold the recovered+updated content.
        recovered = json.loads(state_path.read_text(encoding="utf-8"))
        self.assertEqual(recovered["last_pointer"], 9)
        self.assertIn("debt_counts", recovered)

    def test_backup_atomic_replaces_previous_generation(self):
        state_path = self.data_dir / "state.json"
        _write(state_path, json.dumps(_good_state(pointer=1)))
        _backup_previous_atomic(state_path)
        prev_path = self.data_dir / "state.prev.json"
        self.assertEqual(json.loads(prev_path.read_text(encoding="utf-8"))["last_pointer"], 1)
        # No tmp leftovers.
        self.assertFalse((self.data_dir / "state.prev.tmp.json").exists())

    # ---- rollback honesty -------------------------------------------------------
    def test_rollback_with_corrupt_prev_raises_not_writes_garbage(self):
        state_path = self.data_dir / "state.json"
        prev_path = self.data_dir / "state.prev.json"
        _write(state_path, json.dumps(_good_state()))
        _write(prev_path, "{{{broken")
        with self.assertRaises(ValueError):
            rollback_state(state_path)
        # Main file untouched by the failed attempt.
        self.assertEqual(json.loads(state_path.read_text(encoding="utf-8"))["last_pointer"], 3)

    # ---- ST-2: config quarantine --------------------------------------------------
    def test_corrupt_config_quarantined_and_defaults_returned(self):
        config_path = self.data_dir / "config.json"
        _write(config_path, "{oops")
        persisted, changed = _load_persisted_config_unlocked(config_path)
        self.assertTrue(changed)
        self.assertTrue(persisted.get("plan_presets"))
        quarantined = list(self.data_dir.glob("config.json.bad-*"))
        self.assertEqual(len(quarantined), 1)
        self.assertEqual(quarantined[0].read_text(encoding="utf-8"), "{oops")
        self.assertFalse(config_path.exists())

    def test_corrupt_host_config_quarantined_and_defaults_returned(self):
        host_path = self.data_dir / "host-config.json"
        _write(host_path, "[not an object that parses")
        persisted, changed = _load_persisted_host_config_unlocked(host_path)
        self.assertTrue(changed)
        self.assertEqual(persisted["auto_run_mode"], "Off")
        self.assertEqual(len(list(self.data_dir.glob("host-config.json.bad-*"))), 1)

    def test_valid_config_untouched(self):
        config_path = self.data_dir / "config.json"
        original = _load_persisted_config_unlocked(config_path)[0]
        config_path.write_text(json.dumps(original, ensure_ascii=False), encoding="utf-8")
        _, changed = _load_persisted_config_unlocked(config_path)
        self.assertFalse(changed)
        self.assertEqual(list(self.data_dir.glob("config.json.bad-*")), [])


if __name__ == "__main__":
    unittest.main()
