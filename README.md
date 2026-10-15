# FHIR Agent Bench

Clinician-voiced questions over a pinned FHIR corpus, for measuring whether an agent harness + model
answers better when you change its tools (e.g. the `cli-on-fhir` MCP), skills, prompt or model.

- Design: `docs/superpowers/specs/2026-10-15-fhir-agent-bench-platform-design.md`
- Research behind it: `docs/research/reports/FHIR agent bench revised plan.md`
- Status: Phase 0 (10 questions, loop proven). Findings from the first real run will be written to
  `docs/superpowers/reports/` (plan Task 12); that folder does not exist yet.

## Quick start

    dotnet test
    dotnet run --project src/FhirAgentBench.Cli -- corpus fetch corpora/synthea-sample-v1
    dotnet run --project src/FhirAgentBench.Cli -- corpus verify corpora/synthea-sample-v1
    dotnet run --project src/FhirAgentBench.Cli -- admit --corpus corpora/synthea-sample-v1 --set question-sets/phase0.txt
    dotnet run --project src/FhirAgentBench.Cli -- run --profile profiles/b0-oracle.yaml --corpus corpora/synthea-sample-v1 --set question-sets/phase0.txt --repeats 1 --runs-dir <B0-RUNS-DIR>

Replace `<B0-RUNS-DIR>` with an absolute path outside the repository, written for your shell:
`"$env:TEMP\fab-b0"` in PowerShell, `"$TEMP/fab-b0"` in bash, `%TEMP%\fab-b0` in cmd. A form your shell
does not expand (e.g. `%TEMP%` in PowerShell) becomes a literal folder inside the repository, full of gold.

`admit` checks computed gold against `questions/gold.lock.json`. Pass `--update-lock` only to bootstrap
the lock for new questions or to accept intentional gold changes.

Caution: run the B0 oracle baseline into a separate `--runs-dir` (as above). Its `answer.json` files
are the gold answers, and an agent run must never be able to browse them.

## Commands

- `fab corpus fetch <corpus-dir>`: download the pinned source, check its sha256, extract it read-only.
- `fab corpus verify <corpus-dir>`: check every data file against `data.manifest.json`.
- `fab corpus stats|patients|codes <corpus-dir>`: authoring helpers for exploring the corpus.
- `fab admit`: recompute gold for a question set and check it against the answer spec and gold lock.
- `fab run`: run a profile over a question set, then grade and summarise it.
- `fab report <run-dir> [<compare-run-dir>]`: summarise a run, or compare two runs pairwise (exact
  McNemar). Runs can only be compared when their `run.json` records the same corpus manifest,
  question and gold-lock hashes (a run without a gold-lock hash is compared with a warning).

Before any attempt, `fab run` refuses to start if the corpus does not match its manifest, if any
question's gold has drifted from (or is missing in) `questions/gold.lock.json`, or, for Copilot
profiles, if the Copilot CLI is older than 1.0.96.

## Where runs go

- Run folders: `%LOCALAPPDATA%\fab\runs\<run-id>` by default; override with `fab run --runs-dir`.
  Each holds `run.json` (profile, corpus, question and gold-lock hashes, harness version),
  `attempts.jsonl`, `grades.jsonl` and `attempts/<question>/r<n>/`.
- Agent workspaces: `%LOCALAPPDATA%\fab\ws\<guid>`, one per attempt, deleted afterwards.

Keeping both outside the repository is a mitigation, not isolation: it keeps questions, gold and other
runs out of the agent's working directory, but the agent's shell runs with your full user rights and
the prompt names the corpus path, so a determined agent could still find them. An OS-level sandbox for
attempts is a planned follow-up.

## Copilot CLI runs

Paid runs (`harness: copilot-cli`) print a credit estimate and need `--yes`. `max_ai_credits` must be
at least 30 (Copilot's minimum). Attempts run with `--no-auto-update`, which makes the WinGet
`copilot.exe` use its bundled version; if that is older than 1.0.96, run `winget upgrade GitHub.Copilot`.

Environment variables:

- `COPILOT_GITHUB_TOKEN`: the Copilot token passed to the CLI (falls back to `GH_TOKEN`, then
  `gh auth token`). It is passed only through the child environment and never written to disk.
- `FAB_CLI_ON_FHIR`: path to the `cli-on-fhir` executable, used as the MCP server command in
  `profiles/p2-shell-cli-on-fhir.yaml`.
- `FAB_CLI_ON_FHIR_COMMIT`: optional `cli-on-fhir` commit recorded in `run.json` for reproducibility.

### Exit codes (`fab run`)

- `0`: ok
- `1`: error
- `2`: usage error, or a paid run was not confirmed with `--yes`
- `3`: run completed, but some attempts were `harness_error` or `turn_limit`

## Layout

- `corpora/<id>/corpus.yaml`: pinned source, frozen "now", MRN system; `data/` is fetched, not committed.
- `corpora/<id>/data.manifest.json`: size and sha256 of every data file.
- `questions/*.yaml`: one question each (tier, tags, tool groups, patient context, answer spec, oracle).
- `questions/gold.lock.json`: gold hashes; `fab admit` and `fab run` fail on drift.
- `question-sets/*.txt`: question ids per run.
- `profiles/*.yaml`: harness, model, tools, MCP servers, prompt, budget.
- `prompts/*.md`: prompt templates.
- `src/FhirAgentBench`: corpus index, oracles, grader, harnesses, reports (no Ignixa dependency).
- `src/FhirAgentBench.Cli`: the `fab` command line.
