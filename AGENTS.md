# Duty scheduling via the Duty-Agent CLI (external AI harness guide)

You are an external AI taking over Duty-Agent's reasoning layer.

- `DUTY_ROOT` = `D:\projects\D\Duty-Agent`
- A backend is normally ALREADY RUNNING, managed by the standalone Duty-Agent
  client (it also serves the ClassIsland bridge). Its connection info is
  auto-discovered by the CLI via `CLASSISLAND_CONFIG_PATH`
  (`D:\ClassIsland_app_windows_x64_full_folder\data\Config`, set user-wide).
  Do NOT start a second backend unless discovery fails.

## The CLI command

Always invoke the CLI exactly like this (absolute paths, no PATH assumptions):

```
D:\projects\D\Duty-Agent\duty-cli.bat <SUBCOMMAND> [ARGS]
```

stdout is always one JSON object (UTF-8); progress goes to stderr; failures
exit non-zero with `{"status":"error",...}`. Run
`D:\projects\D\Duty-Agent\duty-cli.bat describe` for the full machine-readable
catalog. Prefer the `--*-file` flag variants over `@` literals and never use
PowerShell `>` redirection (UTF-16); write UTF-8 files instead.

## First run / sanity check

1. `D:\projects\D\Duty-Agent\duty-cli.bat doctor`
   Reports `{ready, checks, next_steps}` against the discovered backend.
   Follow the `fix` field of any failing check. It does NOT bootstrap a second
   backend while the client-managed one is reachable.
2. If the roster check fails, load one (JSON array of `{id, name, active}`):
   `D:\projects\D\Duty-Agent\duty-cli.bat replace-roster --roster-file roster.json`

## Producing a schedule (two-phase delegation)

1. Build the prompt AND persist the handle in one shot:
   `D:\projects\D\Duty-Agent\duty-cli.bat --out prompt.json plan-prompt --instruction "..." --handle-out handle.json`
2. Read `prompt.json`, reason over `prompt_text`, write your answer as a V2 INI
   file `completion.ini` (grammar below). Use numeric roster IDs, never names.
3. Ingest:
   `D:\projects\D\Duty-Agent\duty-cli.bat plan-ingest --completion-file completion.ini --handle-file handle.json`
   Confirm `"status": "success"`; on error fix `completion.ini` and re-run with
   the SAME `handle.json`. Missing roster.csv or an unparsable completion come
   back as `{"status":"error","message":...}` — read the message and fix.
4. Report the final schedule from the ingest response `snapshot`.

Alternative: to let Duty-Agent's own configured model do the reasoning instead,
run `D:\projects\D\Duty-Agent\duty-cli.bat run --instruction "..."`.
Deterministic helpers (no AI): `inspect`, `get-config`, `update-config`,
`get-roster`, `replace-roster`, `edit-entry`, `rollback`, `health`, `status`.

## V2 INI grammar (strict)

```
[areas]
A = Classroom
[schedule]
07-28 = A:1
07-29 = A:2
[state]
pointer = 2
```

- `[areas]`: `ALIAS = Area Name`, alias is UPPERCASE (e.g. A, B).
- `[schedule]`: one line per day, `MM-DD = ALIAS:<space-separated IDs>`, dates
  strictly ascending, IDs are numeric roster IDs.
- `[state]` (optional): `pointer = <int>`; `debt = <id>` or `<id>*<count>`;
  `absent = <space-separated IDs>` declares leave/absence IDs that must not be
  scheduled this round; never use `:` in `[state]`.
- Output the INI only. No prose, no markdown fences.
