# Task 2 — Astra runner fixes report

## Scope and disposition

Implemented the Astra runner review findings I1–I5 and M1, plus the requested initial-20 selection guard needed to coexist with the independently expanded 100-question QA dataset. The runner only accepts the original 20 approved IDs with their fixed per-item SHA-256 values; the other 80 rows remain unverified and cannot enter this runner denominator. Live execution remains unconditionally blocked. No Copilot, inference, cloud, or actual specialist-tool calls were made.

Runner implementation commit: `26ee357` — `Fix Astra runner integrity and lifecycle findings`.

## Finding-to-fix map

| Finding | Correction | Regression coverage |
|---|---|---|
| I1 — protected inputs/workspaces could change during execution | Preparation freezes inventories for the original source root and each workspace, including membership, content, directories, and link/hardlink safety. The frozen baselines are compared before dispatch and after collection; any change is detected, never rebaselined. Expected generated `answer.json` is excluded from the protected workspace inventory and separately checked as a regular single-link file. | Source and workspace tamper/membership/link regressions in `tests/test_runner.py`. |
| I2 — final lifecycle/session association was permissive | Collection now requires a final terminal `idle` and rejects later lifecycle activity unless another valid terminal transition follows. Supplied session IDs must associate consistently; only explicit telemetry may trail terminal idle. | Added idle-then-tool, idle-then-new-session, and session-association lifecycle regressions. |
| I3 — deeply nested JSON could escape bounded episode handling | Answer, usage, and trace parsing handle `RecursionError` as bounded failed episodes. CLI-level handling still writes the planned-denominator JSON/Markdown report, preserving `planned = completed + failed + blocked + not_run`. | Deep nesting coverage for answer/usage/trace and persisted CLI report behavior. |
| I4 — configured command could override runner-owned protections | Configured `copilot_command` is rejected if it supplies runner-owned overrides, including alternate/equal argument forms. Permission metadata and effective-argv hashes are derived from the actual prepared argv, not an independently reconstructed command. | Override rejection and effective permission/argv metadata regressions. |
| I5 — Windows descendants might survive parent exit | Windows Job Object accounting is checked after termination; the helper waits, boundedly, for zero active processes before returning. Cleanup failure is reported and prevents collection/advancement rather than treating the episode as quiescent. | Parent-exits-before-child and injected cleanup-failure regressions. |
| M1 — specialist preflight failure blocked independent baseline | Copilot/version preflight errors and specialist MCP startup exceptions are separated. Specialist startup failure is recorded without suppressing eligible filesystem-only baseline work. | Specialist preflight failure with baseline continuation regression. |
| Expanded dataset guard | Removed the global “dataset must contain exactly 20” assumption. The runner validates/selects only the frozen initial-20 ID set and each matching per-item hash, rejecting other question IDs; default execution remains limited to those 20. | Expanded-100 input selection/hash guard; pinned `configs/smoke.json` remains initial20. |

Labels and existing output behavior are preserved except for explicit, bounded error reporting required by the findings.

## Verification

All commands below were run from the repository root. Tests/audits are offline; audit output explicitly reports provisional/unverified data and blocks live execution.

- `python -B -m pytest -q -p no:cacheprovider tests\test_runner.py --basetemp .test-work-report-runner` — **80 passed** (257.71s).
- `python -B -m pytest -q -p no:cacheprovider tests\test_runner.py::test_all_twenty_through_sixty_fake_subprocesses_are_only_engineering_evidence --basetemp .test-work-report-fake60` — **1 passed** (67.55s). The regression exercised all20/all60 fake subprocess handling; these are engineering fixtures, not measured agent performance.
- `python -B -m pytest -q -p no:cacheprovider --basetemp .test-work-report-full` — **305 passed** (298.85s).
- `python -B -m fhir_question_bench audit` — **100 questions, 100 gold recomputed**, 20 provisional + 80 unverified; 170 resources. Dataset SHA-256 `e9d36716b068b1df1a79e6480be29b26c1cd5d2e92c587ce38ec47250f7ed9cc`.
- `python -B -m fhir_question_bench operations-audit` — **50 tasks recomputed/accepted**, 300 incorrect outputs rejected; `live_execution: blocked`, `measured_agent_performance: false`.
- `git diff --cached --check` — passed before the runner-fix commit.

Fresh run snapshots (new IDs; older reports were not relabeled):

- Dry-run snapshot: `bf2763b55e388cb3fa6fee7217e05379de78b5a79d91f55d222b3588bace6804` — 60 planned, 0 completed/failed/blocked, 60 not run.
- Fake-process snapshot: `3401b5771ad76237a0d37318e28b7feabc73dffa8172995ab003786fee242716` — 60 completed strict fixture successes, 0 failed/blocked; not measured agent performance.
- Historical fake report `runs\milestone2-fake60\report.json` remains unchanged at snapshot `4eb065b66d83924f3fc368e7ee27b4810a6fb4c2639594d32c445f6fbbea0703`.

## Remaining limits

No live Copilot or specialist evaluation occurred. Live remains blocked. Windows Job Object
quiescence confirms the managed job has exited; it is not hostile-process containment or proof of an
OS security boundary. Fake subprocesses and oracle-derived answers validate runner mechanics only,
not model quality. The 100 QA items are 20 provisional plus 80 unverified; operational task
verification is unverified. No clinical/professional acceptance, terminology certification, or
clinical efficacy claim is made. Scope remains capped at the approved 100 QA + 50 operational items;
no expansion was performed here. `progress.md` was not edited.
