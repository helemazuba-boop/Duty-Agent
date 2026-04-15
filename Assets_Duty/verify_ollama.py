#!/usr/bin/env python
"""
Ollama Prompt Validator for Duty-Agent tool_loop.

Runs a single tool-loop scheduling conversation against a local Ollama model
and prints EVERYTHING: system prompts, LLM streaming output, tool results.

Usage:
    python verify_ollama.py
    python verify_ollama.py --model qwen3.5:9b --rounds 5 --instruction "安排4月1日到4月5日"
    python verify_ollama.py --model Qwen-3.5-9B-uncensored:latest --no-stream
    python verify_ollama.py --hints off  # hide @keep/@drop/@finalize from system prompt
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import time
import urllib.error
import urllib.request
from datetime import date, datetime, timedelta
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Set, Tuple

# ---------------------------------------------------------------------------
# Path bootstrap
# ---------------------------------------------------------------------------
_ASSETS = Path(__file__).parent
if str(_ASSETS) not in sys.path:
    sys.path.insert(0, str(_ASSETS))

from tool_loop.ini_handler import (
    ExecutionCtx, PollingFlags, ScheduleUnit, parse_and_apply,
)
from tool_loop.tool_prompt import build_tool_system_prompt, build_hints_prompt

# ---------------------------------------------------------------------------
# ANSI colour helpers
# ---------------------------------------------------------------------------
try:
    _HAS_COLOR = sys.stdout.isatty()
except Exception:
    _HAS_COLOR = False

_C = type("", (), {})()
for _k, _v in {
    "R": "\033[91m", "G": "\033[92m", "Y": "\033[93m", "B": "\033[94m",
    "M": "\033[95m", "C": "\033[96m", "W": "\033[97m", "D": "\033[90m",
    "BOLD": "\033[1m", "RST": "\033[0m",
}.items():
    setattr(_C, _k, _v if _HAS_COLOR else "")

def divider(label: str = "", char: str = "─", width: int = 100) -> None:
    if label:
        half = (width - len(label) - 4) // 2
        print(f"{_C.D}{char * half} {_C.BOLD}{label} {char * half}{_C.RST}")
    else:
        print(f"{_C.D}{char * width}{_C.RST}")

def cprint(tag: str, color: str, text: str) -> None:
    col = getattr(_C, color, "")
    print(f"{col}[{tag}]{_C.RST} {text}")

# ---------------------------------------------------------------------------
# Ollama HTTP client
# ---------------------------------------------------------------------------

TOOL_DEFINITION: List[dict] = [
    {
        "type": "function",
        "function": {
            "name": "fill_schedule",
            "description": (
                "Submit INI-formatted schedule for validation and persistence. "
                "Pass the full INI text as the 'schedule' argument."
            ),
            "parameters": {
                "type": "object",
                "properties": {
                    "schedule": {"type": "string",
                                 "description": "Complete INI text."},
                },
                "required": ["schedule"],
            },
        },
    }
]

# Module-level storage: the raw JSON text from the last non-streaming Ollama response.
# Needed because Ollama puts tool_calls inside the JSON body, not in the streamed content.
_last_non_stream_json: Optional[str] = None


def _http_post_streaming(url: str, payload: dict, api_key: str) -> Tuple[List[str], str, Optional[dict]]:
    """Call Ollama with streaming=True.

    Returns (content_chunks, raw_sse_text, final_response_obj).
    final_response_obj holds the parsed JSON from the last data: line.
    """
    stream_payload = dict(payload)
    stream_payload["stream"] = True
    data = json.dumps(stream_payload).encode("utf-8")
    req = urllib.request.Request(
        url=url, data=data, method="POST",
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {api_key}"},
    )
    content_chunks: List[str] = []
    raw_sse_lines: List[str] = []
    final_obj: Optional[dict] = None
    try:
        with urllib.request.urlopen(req, timeout=300) as resp:
            for raw_line in resp:
                decoded = raw_line.decode("utf-8", errors="ignore")
                raw_sse_lines.append(decoded)
                stripped = decoded.strip()

                if not stripped or stripped.startswith(":"):
                    continue

                if stripped.startswith("data:"):
                    data_text = stripped[5:].strip()
                    # SSE chunk — parse delta for content tokens
                    try:
                        event = json.loads(data_text)
                    except Exception:
                        continue
                    final_obj = event  # keep overwriting; last valid one wins
                    delta = event.get("choices", [{}])[0].get("delta", {})
                    token = delta.get("content", "")
                    if token:
                        content_chunks.append(token)
                else:
                    # Non-SSE line in a streaming response.
                    # qwen3.5 returns tool_calls as non-SSE JSON between SSE chunks.
                    try:
                        event = json.loads(stripped)
                    except Exception:
                        continue
                    final_obj = event
                    # Extract content from message if present (streaming delta case)
                    msg = event.get("choices", [{}])[0].get("message", {})
                    msg_content = msg.get("content", "")
                    if msg_content:
                        content_chunks.append(msg_content)
                    # tool_calls are stored for extraction via _last_non_stream_json
                    if msg.get("tool_calls"):
                        content_chunks.append("[TOOL_CALL]")

                if stripped == "[DONE]":
                    break

    except urllib.error.HTTPError as ex:
        detail = ex.read().decode("utf-8", errors="ignore")
        raise RuntimeError(f"HTTP {ex.code}: {detail}") from ex
    except Exception as ex:
        raise RuntimeError(f"Ollama request failed: {ex}") from ex

    return content_chunks, "".join(raw_sse_lines), final_obj


def call_ollama(url: str, payload: dict, api_key: str,
                on_token: Optional[Callable[[str], None]]) -> str:
    """Single API entry point. Uses streaming internally; falls back to non-stream for tool calls."""
    chunks, raw_sse, final_obj = _http_post_streaming(url, payload, api_key)
    content = "".join(chunks)

    # Store the full response JSON for tool_call extraction
    global _last_non_stream_json
    _last_non_stream_json = None
    if final_obj is not None:
        _last_non_stream_json = final_obj  # Store as dict

    # qwen3.5 doesn't properly stream tool_calls — fall back to non-stream
    if not content.strip() and payload.get("tools"):
        non_stream_payload = dict(payload)
        non_stream_payload["stream"] = False
        non_stream_payload.pop("think", None)  # avoid duplicate
        req2 = urllib.request.Request(
            url=url,
            data=json.dumps(non_stream_payload).encode("utf-8"),
            method="POST",
            headers={"Content-Type": "application/json", "Authorization": f"Bearer {api_key}"},
        )
        try:
            with urllib.request.urlopen(req2, timeout=60) as resp2:
                result2 = json.loads(resp2.read().decode("utf-8"))
                _last_non_stream_json = result2
                msg = result2.get("choices", [{}])[0].get("message", {})
                content = msg.get("content") or ""
                if msg.get("tool_calls"):
                    content = "[TOOL_CALL]"
        except Exception:
            pass

    if not content.strip() and _last_non_stream_json is None:
        raise RuntimeError("LLM returned empty content.")
    return content


# ---------------------------------------------------------------------------
# Tool call extraction
# ---------------------------------------------------------------------------

_FILL_CALL_RE = re.compile(
    r'fill_schedule\s*\(\s*(?P<json>\{.{0,4000}\})', re.DOTALL,
)


def _normalize(content: str) -> str:
    text = str(content or "").replace("\ufeff", "").strip()
    text = re.sub(
        r"<(?P<tag>think|thinking|reasoning)\b[^>]*>.*?</(?P=tag)>",
        "", text, flags=re.DOTALL | re.IGNORECASE,
    )
    return text.strip()


def extract_tool_calls(raw_content: str) -> List[Dict[str, Any]]:
    """Extract fill_schedule tool calls from LLM response.

    Handles:
    - "[TOOL_CALL]" marker (qwen3.5 streaming fallback -> uses _last_non_stream_json)
    - choices[].message.tool_calls  (Ollama non-streaming /v1/chat/completions)
    - top-level tool_calls          (standard OpenAI)
    - SSE reassembled content with JSON
    - plain text: fill_schedule(...)
    """
    results: List[Dict[str, Any]] = []
    cleaned = _normalize(raw_content)
    if not cleaned:
        return []

    # [TOOL_CALL] marker means tool was called; extract from stored JSON
    if cleaned == "[TOOL_CALL]":
        if _last_non_stream_json is not None:
            try:
                obj = _last_non_stream_json if isinstance(_last_non_stream_json, dict) else json.loads(_last_non_stream_json)
                tc_list: List[dict] = []
                for choice in obj.get("choices", []):
                    tc_list.extend(choice.get("message", {}).get("tool_calls", []))
                tc_list.extend(obj.get("tool_calls", []))
                for tc in tc_list:
                    fn = tc.get("function", tc)
                    name = str(fn.get("name", "")).strip()
                    if name == "fill_schedule":
                        raw_args = fn.get("arguments", "{}")
                        if isinstance(raw_args, str):
                            try:
                                args = json.loads(raw_args)
                            except Exception:
                                args = {"schedule": raw_args}
                        else:
                            args = raw_args or {}
                        results.append({"name": name, "arguments": args})
            except Exception:
                pass
        return results

    def _from_obj(obj: dict) -> bool:
        tc_list: List[dict] = []
        for choice in obj.get("choices", []):
            tc_list.extend(choice.get("message", {}).get("tool_calls", []))
        tc_list.extend(obj.get("tool_calls", []))
        for tc in tc_list:
            fn = tc.get("function", tc)
            name = str(fn.get("name", "")).strip()
            if name == "fill_schedule":
                raw_args = fn.get("arguments", "{}")
                if isinstance(raw_args, str):
                    try:
                        args = json.loads(raw_args)
                    except Exception:
                        args = {"schedule": raw_args}
                else:
                    args = raw_args or {}
                results.append({"name": name, "arguments": args})
        return bool(results)

    # 1. Direct parse
    try:
        if _from_obj(json.loads(cleaned)):
            return results
    except Exception:
        pass

    # 2. Stored non-streaming JSON (may be dict or JSON string)
    if _last_non_stream_json is not None:
        try:
            obj = _last_non_stream_json if isinstance(_last_non_stream_json, dict) else json.loads(_last_non_stream_json)
            if _from_obj(obj):
                return results
        except Exception:
            pass

    # 3. Fenced JSON block
    fenced = re.search(r'```json\s*(.*?)\s*```', cleaned, re.DOTALL | re.IGNORECASE)
    if fenced:
        try:
            if _from_obj(json.loads(fenced.group(1))):
                return results
        except Exception:
            pass

    # 4. Plain fill_schedule(...)
    for m in _FILL_CALL_RE.finditer(cleaned):
        raw = m.group("json").strip()
        try:
            args = json.loads(raw)
        except Exception:
            try:
                args = json.loads(raw.strip())
            except Exception:
                continue
        if "schedule" in args:
            results.append({"name": "fill_schedule", "arguments": args})
            break

    return results


# ---------------------------------------------------------------------------
# Date range parsing
# ---------------------------------------------------------------------------
_DATE_RANGE_RE = re.compile(
    r"(\d{1,2})[月/-](\d{1,2})(?:\s*日?\s*[-~至到]\s*(\d{1,2})[月/-](\d{1,2})(?:\s*日?))?",
)
_DATE_SINGLE_RE = re.compile(r"(\d{1,2})[月/-](\d{1,2})")
DEFAULT_AREA = "值日"


def parse_required_slots(instruction: str, start: date) -> List[ScheduleUnit]:
    filled: Set[str] = set()
    year = start.year
    m = _DATE_RANGE_RE.search(instruction or "")
    if m:
        try:
            s = date(year, int(m.group(1)), int(m.group(2)))
            e = date(year, int(m.group(3)), int(m.group(4)))
            if s > e:
                e = date(year + 1, int(m.group(3)), int(m.group(4)))
            units = []
            cur = s
            while cur <= e:
                if cur.isoformat() not in filled:
                    units.append(ScheduleUnit(cur.isoformat(), DEFAULT_AREA, ""))
                cur += timedelta(days=1)
            if units:
                return units
        except (ValueError, TypeError):
            pass
    sm = _DATE_SINGLE_RE.search(instruction or "")
    if sm:
        try:
            d = date(year, int(sm.group(1)), int(sm.group(2)))
            if d.isoformat() not in filled:
                return [ScheduleUnit(d.isoformat(), DEFAULT_AREA, "")]
        except ValueError:
            pass
    return [
        ScheduleUnit((start + timedelta(days=i)).isoformat(), DEFAULT_AREA, "")
        for i in range(1, 8)
    ]


# ---------------------------------------------------------------------------
# Pretty-print helpers
# ---------------------------------------------------------------------------

def print_system(prompt: str) -> None:
    divider(" SYSTEM PROMPT ", char="═")
    for line in prompt.splitlines():
        print(f"  {_C.D}{line}{_C.RST}")
    divider()


def print_user(msg: str) -> None:
    divider(" USER → LLM ", char="─")
    for line in msg.splitlines():
        print(f"  {_C.Y}{line}{_C.RST}")
    divider()


def print_stream(token: str) -> None:
    sys.stdout.write(_C.G + token + _C.RST)
    sys.stdout.flush()


def print_stream_done() -> None:
    print()


def print_tool_calls_detected(tcs: List[Dict[str, Any]]) -> None:
    divider(" TOOL CALL DETECTED ", char="─")
    for i, tc in enumerate(tcs):
        sched = tc.get("arguments", {}).get("schedule", "(empty)")
        cprint(f"call#{i + 1}", "M", f"fill_schedule — {len(sched)} chars")
        print(f"  {_C.D}{'-' * 50}")
        lines = sched.splitlines()
        for ln in lines[:50]:
            print(f"    {_C.M}{ln}{_C.RST}")
        if len(lines) > 50:
            print(f"    {_C.D}... ({len(lines) - 50} more lines){_C.RST}")
    divider()


def print_tool_result(result_ini: str, ctx: ExecutionCtx) -> None:
    divider(" PYTHON TOOL RESULT → LLM ", char="─")
    print(f"  {_C.B}Round {ctx.round_num} | finalized={ctx.finalized} | "
          f"accepted={len(ctx.accepted)} | rejected={len(ctx.rejected)} | "
          f"remaining={len(ctx.remaining)}{_C.RST}")
    print(f"  {_C.D}{'-' * 60}")
    for ln in result_ini.splitlines():
        print(f"  {_C.C}{ln}{_C.RST}")
    if ctx.rejected:
        print(f"  {_C.R}{'-' * 60}{_C.R}")
        print(f"  {_C.R}Rejections:{_C.RST}")
        for d, code, detail in ctx.rejected:
            print(f"  {_C.R}  {d}: [{code}] {detail}{_C.RST}")
    if ctx.invalid_cmds:
        print(f"  {_C.Y}{'-' * 60}{_C.Y}")
        print(f"  {_C.Y}Invalid commands:{_C.RST}")
        for ic in ctx.invalid_cmds:
            print(f"  {_C.Y}  {ic}{_C.RST}")
    divider()


def print_final(ctx: ExecutionCtx, dates: List[str], rounds: int, tcs: int, elapsed: float) -> None:
    divider(" FINAL RESULT ", char="═")
    cprint("status", "G", "FINALIZED" if ctx.finalized else "STOPPED (no @finalize)")
    print(f"  {_C.B}Elapsed:     {_C.W}{elapsed:.1f}s")
    print(f"  {_C.B}Rounds:      {_C.W}{rounds}")
    print(f"  {_C.B}Tool calls:  {_C.W}{tcs}")
    print(f"  {_C.B}Dates:       {_C.W}{len(dates)}")
    print(f"  {_C.B}Pointer:     {_C.W}{ctx.last_pointer}")
    if ctx.debt_counts:
        print(f"  {_C.B}Debt:       {_C.W}{ctx.debt_counts}")
    if ctx.credit_counts:
        print(f"  {_C.B}Credit:     {_C.W}{ctx.credit_counts}")
    print()
    print(f"  {_C.B}{'Date':<14}  Schedule{_C.RST}")
    print(f"  {_C.D}{'-' * 60}{_C.RST}")
    by_date: Dict[str, dict] = {}
    for e in ctx.schedule_pool:
        d = e.get("date", "?")
        by_date.setdefault(d, {"area_ids": {}})
        for area, ids in e.get("area_ids", {}).items():
            by_date[d]["area_ids"].setdefault(area, []).extend(ids)
    for d in sorted(by_date):
        e = by_date[d]
        parts = [f"{a}:{' '.join(str(i) for i in ids)}" for a, ids in sorted(e["area_ids"].items())]
        print(f"  {_C.W}{d:<14}  {' | '.join(parts)}{_C.RST}")
    divider(" END ", char="═")


# ---------------------------------------------------------------------------
# Main polling loop
# ---------------------------------------------------------------------------

def run_validation(
    model: str, instruction: str, max_rounds: int, hints_on: bool,
    ollama_url: str, roster: dict, init_state: dict, duty_rule: str,
) -> Tuple[ExecutionCtx, int, int, List[str]]:
    all_ids = roster["all_ids"]
    inactive_ids = set(roster["inactive_ids"])
    debt = dict(init_state.get("debt_counts", {}))
    credit = dict(init_state.get("credit_counts", {}))
    ptr = int(init_state.get("last_pointer", 0) or 0)
    pool: List[dict] = list(init_state.get("schedule_pool", []) or [])
    start = date(2026, 4, 5)  # Use current date to avoid year boundary issues
    start_iso = start.isoformat()
    now_str = datetime.now().strftime("%Y-%m-%d %H:%M")
    required = parse_required_slots(instruction, start)
    flags = PollingFlags(hints_on=hints_on, max_rounds=max_rounds)
    alias_map: Dict[str, str] = {}

    sys_prompt = build_tool_system_prompt(
        instruction=instruction, duty_rule=duty_rule,
        all_ids=all_ids, inactive_ids=list(inactive_ids),
        debt_counts=debt, credit_counts=credit,
        last_pointer=ptr, start_date=start_iso, current_time=now_str,
    )
    if hints_on:
        h = build_hints_prompt()
        if h:
            sys_prompt = sys_prompt.rstrip() + "\n\n" + h

    messages: List[dict] = [{"role": "system", "content": sys_prompt}]
    print_system(sys_prompt)

    seen_dates: Set[str] = set()
    ctx_exec: Optional[ExecutionCtx] = None
    total_tcs = 0

    for rnd in range(1, max_rounds + 1):
        cprint(f"ROUND {rnd}/{max_rounds}", "BOLD", f"→ {model}")
        divider(f" Round {rnd} ", char="─")

        remaining = ", ".join(
            f"{u.date_iso[5:]} {u.area_name}" for u in required if u.date_iso not in seen_dates
        )
        user_msg = (
            f"Schedule request: {instruction}\n"
            f"Required dates (MM-DD area): {remaining or 'none'}\n"
            f"Current state: debt={debt}, credit={credit}, pointer={ptr}\n"
            f"Generate the INI schedule now using the fill_schedule tool."
        )
        messages.append({"role": "user", "content": user_msg})
        print_user(user_msg)

        payload = {
            "model": model, "messages": list(messages),
            "temperature": 0.1,
            "tools": TOOL_DEFINITION,
            "tool_choice": {"type": "function", "function": {"name": "fill_schedule"}},
            "think": False,
        }

        cprint("llm", "D", "Streaming response:")
        try:
            raw = call_ollama(ollama_url, payload, "ollama", on_token=print_stream)
            print_stream_done()
        except Exception as ex:
            cprint("ERROR", "R", f"Ollama failed: {ex}")
            raise

        messages.append({"role": "assistant", "content": raw})
        tcs = extract_tool_calls(raw)

        if not tcs:
            cprint("warn", "Y", "No fill_schedule tool call this round.")
            if any(m in raw.lower() for m in ["done", "complete", "完成了", "all dates"]):
                cprint("done", "G", "LLM signalled completion.")
                break
            messages.append({
                "role": "user",
                "content": "You did not call fill_schedule. Call it now with your INI template.",
            })
            continue

        for tc in tcs:
            total_tcs += 1
            sched = str(tc.get("arguments", {}).get("schedule", "")).strip()
            print_tool_calls_detected([tc])
            if not sched:
                messages.append({
                    "role": "tool",
                    "tool_call_id": f"call_{total_tcs}",
                    "content": "fill_schedule called with empty schedule.",
                })
                continue
            try:
                result_ini, ctx_exec = parse_and_apply(
                    raw_ini=sched,
                    all_ids=all_ids, debt_counts=debt, credit_counts=credit,
                    inactive_ids=list(inactive_ids), last_pointer=ptr,
                    start_date=start, schedule_pool=pool,
                    required_slots=required, flags=flags,
                    round_num=rnd, alias_map=alias_map,
                )
            except Exception as ex:
                tb_ex = __import__("traceback").format_exc()
                messages.append({
                    "role": "tool", "tool_call_id": f"call_{total_tcs}",
                    "content": f"Python error parsing your INI:\n  {type(ex).__name__}: {ex}\nFix and retry.",
                })
                cprint("ERROR", "R", f"parse_and_apply: {ex}")
                continue

            debt = ctx_exec.debt_counts
            credit = ctx_exec.credit_counts
            ptr = ctx_exec.last_pointer
            pool = ctx_exec.schedule_pool
            alias_map = ctx_exec.alias_map
            for e in ctx_exec.accepted:
                d = e.get("date", "")
                if d:
                    seen_dates.add(d)

            print_tool_result(result_ini, ctx_exec)

            if ctx_exec.finalized:
                content = (
                    "[TOOL RESULT]\nSchedule finalized. Authoritative [state]:\n\n"
                    + result_ini + "\n\nThe schedule is complete."
                )
            else:
                content = (
                    "[TOOL RESULT]\nINI processed. Authoritative [state]:\n\n"
                    + result_ini
                    + "\n\nContinue or call fill_schedule with @finalize."
                )
            messages.append({"role": "tool", "tool_call_id": f"call_{total_tcs}", "content": content})
            if ctx_exec.finalized:
                break

        if ctx_exec is not None and ctx_exec.finalized:
            break
        if ctx_exec is not None and not ctx_exec.remaining:
            cprint("done", "G", "All slots filled.")
            break

    return ctx_exec or ExecutionCtx(
        debt_counts=debt, credit_counts=credit, inactive_ids=inactive_ids,
        last_pointer=ptr, schedule_pool=pool, remaining=[],
        round_num=max_rounds, flags=flags, start_date=start,
        accepted=[], rejected=[], invalid_cmds=[], finalized=False,
        alias_map={}, _valid_ids=set(all_ids),
    ), rnd, total_tcs, sorted(seen_dates)


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main() -> None:
    p = argparse.ArgumentParser(
        description="Ollama validator for Duty-Agent tool_loop.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    p.add_argument("--model", "-m", default="qwen3.5:9b")
    p.add_argument("--url", "-u",
                   default="http://localhost:11434/v1/chat/completions")
    p.add_argument("--rounds", "-r", type=int, default=8)
    p.add_argument("--instruction", "-i", default="安排4月1日到4月7日")
    p.add_argument("--hints", choices=["on", "off"], default="on")
    p.add_argument("--duty-rule", default="")
    args = p.parse_args()
    hints_on = args.hints == "on"

    divider(f" OLLAMA VALIDATOR ", char="=", width=100)
    cprint("model", "B", args.model)
    cprint("url", "B", args.url)
    cprint("instruction", "B", args.instruction)
    cprint("hints", "B", f"{'ON' if hints_on else 'OFF'}")
    cprint("max_rounds", "B", str(args.rounds))
    print()

    roster = {
        "all_ids": [1001, 1002, 1003, 1004],
        "inactive_ids": [1004],
        "id_to_name": {1001: "张三", 1002: "李四", 1003: "王五", 1004: "赵六"},
    }
    init_state = {
        "debt_counts": {1002: 1},
        "credit_counts": {1001: 1},
        "last_pointer": 0,
        "schedule_pool": [],
    }

    t0 = time.time()
    try:
        ctx, rounds, tcs, dates = run_validation(
            model=args.model, instruction=args.instruction,
            max_rounds=args.rounds, hints_on=hints_on,
            ollama_url=args.url,
            roster=roster, init_state=init_state,
            duty_rule=args.duty_rule,
        )
        elapsed = time.time() - t0
        print_final(ctx, dates, rounds, tcs, elapsed)
    except Exception as ex:
        divider(" ERROR ", char="═")
        cprint("ERROR", "R", str(ex))
        __import__("traceback").print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
