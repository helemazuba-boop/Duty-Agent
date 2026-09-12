"""
Tests for conversation history compaction (conversation.py).

Uses Python's built-in unittest module (no pytest required).
Run with: python test_conversation.py
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from conversation import compact_conversation_history


def _round(round_num: int, ini_chars: int = 100, tool_call: bool = False) -> list:
    """One polling round: assistant message plus its feedback messages."""
    assistant = {"role": "assistant", "content": f"INI round {round_num} " + "x" * ini_chars}
    if tool_call:
        assistant["tool_calls"] = [{
            "id": f"call_{round_num}",
            "type": "function",
            "function": {"name": "fill_schedule", "arguments": "{}"},
        }]
        return [
            assistant,
            {"role": "tool", "content": f"result {round_num}", "tool_call_id": f"call_{round_num}"},
        ]
    return [
        assistant,
        {"role": "user", "content": f"feedback {round_num}"},
    ]


class TestCompactConversationHistory(unittest.TestCase):

    def _large_conversation(self, rounds: int = 8, chars: int = 6000) -> list:
        messages = [{"role": "system", "content": "system prompt"}]
        for round_num in range(1, rounds + 1):
            messages.extend(_round(round_num, ini_chars=chars))
        return messages

    def test_small_conversation_unchanged(self):
        messages = self._large_conversation(rounds=3)
        result = compact_conversation_history(messages, "summary")
        self.assertIs(result, messages)

    def test_large_conversation_compacted(self):
        messages = self._large_conversation(rounds=8, chars=6000)
        result = compact_conversation_history(messages, "summary text", char_limit=100)
        self.assertIsNot(result, messages)
        joined = "\n".join(str(m.get("content")) for m in result)
        self.assertIn("summary text", joined)
        # Old rounds folded, newest kept verbatim
        self.assertNotIn("INI round 1 ", joined)
        self.assertIn("INI round 8 ", joined)
        self.assertIn("INI round 6 ", joined)

    def test_system_head_preserved(self):
        messages = self._large_conversation(rounds=8, chars=6000)
        result = compact_conversation_history(messages, "summary", char_limit=100)
        self.assertEqual(result[0]["role"], "system")
        self.assertEqual(result[0]["content"], "system prompt")
        self.assertEqual(result[1], {"role": "user", "content": "summary"})

    def test_tool_call_pairing_never_split(self):
        messages = [{"role": "system", "content": "s"}]
        for round_num in range(1, 8):
            messages.extend(_round(round_num, ini_chars=6000, tool_call=True))
        result = compact_conversation_history(messages, "summary", char_limit=100)
        # Every tool message must directly follow an assistant declaring its id
        for index, message in enumerate(result):
            if message.get("role") == "tool":
                prev = result[index - 1]
                self.assertEqual(prev.get("role"), "assistant")
                declared_ids = {tc.get("id") for tc in prev.get("tool_calls", [])}
                self.assertIn(message.get("tool_call_id"), declared_ids)

    def test_no_mutation_of_input(self):
        messages = self._large_conversation(rounds=8, chars=6000)
        snapshot = [dict(m) for m in messages]
        compact_conversation_history(messages, "summary", char_limit=100)
        self.assertEqual([dict(m) for m in messages], snapshot)

    def test_few_groups_below_threshold_unchanged(self):
        messages = [{"role": "system", "content": "s"}]
        for round_num in range(1, 4):  # 3 groups == keep_groups
            messages.extend(_round(round_num, ini_chars=6000))
        result = compact_conversation_history(messages, "summary", char_limit=100)
        self.assertIs(result, messages)


if __name__ == "__main__":
    unittest.main(verbosity=2)
