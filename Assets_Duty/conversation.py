"""Conversation history compaction for polling executors.

Polling loops (tool_loop, orchestrator direct mode) append the model's full
INI output plus Python's authoritative feedback every round. Left alone the
conversation grows linearly and crowds the context of small local models
(default: qwen3.5:9b). This helper folds old rounds into a single summary
user message while never splitting an assistant message from its tool/user
feedback, so OpenAI-strict providers still see a valid message sequence.
"""
from __future__ import annotations

from typing import List, Optional

# Compact only when the conversation actually grew past this budget; small
# runs must keep their full history verbatim.
HISTORY_CHAR_LIMIT = 24000

# Newest assistant groups kept verbatim when compacting.
HISTORY_KEEP_GROUPS = 3


def _group_rounds(messages: List[dict]) -> List[List[dict]]:
    """Split non-system messages into groups: one assistant message plus the
    feedback messages that follow it (a user message before any assistant
    forms its own group)."""
    groups: List[List[dict]] = []
    current: Optional[List[dict]] = None
    for message in messages:
        if message.get("role") == "assistant":
            current = [message]
            groups.append(current)
            continue
        if current is None:
            current = [message]
            groups.append(current)
        else:
            current.append(message)
    return groups


def compact_conversation_history(
    messages: List[dict],
    summary_text: str,
    keep_groups: int = HISTORY_KEEP_GROUPS,
    char_limit: int = HISTORY_CHAR_LIMIT,
) -> List[dict]:
    """Fold rounds older than the newest ``keep_groups`` into ``summary_text``.

    Leading system messages are always preserved. Returns the input list
    unchanged when the total content size is under ``char_limit`` or there is
    nothing meaningful to fold. A new list is returned when compaction
    happens; the input list is never mutated.
    """
    total_chars = sum(len(str(message.get("content") or "")) for message in messages)
    if total_chars <= char_limit:
        return messages

    head: List[dict] = []
    start = 0
    while start < len(messages) and messages[start].get("role") == "system":
        head.append(messages[start])
        start += 1

    groups = _group_rounds(messages[start:])
    if len(groups) <= keep_groups + 1:
        return messages

    folded = [message for group in groups[:-keep_groups] for message in group]
    kept = [message for group in groups[-keep_groups:] for message in group]
    summary_message = {"role": "user", "content": summary_text}
    return head + [summary_message] + kept
