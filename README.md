# FHIR Agent Bench

Clinician-voiced questions over a pinned FHIR corpus, for measuring whether an agent harness + model
answers better when you change its tools (e.g. the `cli-on-fhir` MCP), skills, prompt or model.

- Design: `docs/superpowers/specs/2026-10-15-fhir-agent-bench-platform-design.md`
- Oracle semantics and code crosswalks: `docs/questions/oracle-semantics.md`; second gold derivation: `tools/derive-gold/`
- Research behind it: `docs/research/reports/FHIR agent bench revised plan.md`
- Status: `question-sets/v1.txt` holds 106 questions (13 T1, 20 T2, 20 T3, 26 T4, 13 T5, 8 T6, 6 T7)
  across 18 oracles, including the 10 Phase 0 questions (`question-sets/phase0.txt`). Tiers are by mechanism
  (see the design spec §5.1); the `MAP` tag marks questions in clinician language, ICD-10-CM, brand names or
  drug classes that must be translated to the code system the data uses. Findings from the first real run will be written to
  `docs/superpowers/reports/` (plan Task 12); that folder does not exist yet.

## Quick start

    dotnet test
    dotnet run --project src/FhirAgentBench.Cli -- corpus fetch corpora/synthea-sample-v1
    dotnet run --project src/FhirAgentBench.Cli -- corpus verify corpora/synthea-sample-v1
    dotnet run --project src/FhirAgentBench.Cli -- admit --corpus corpora/synthea-sample-v1 --set question-sets/v1.txt
    dotnet run --project src/FhirAgentBench.Cli -- run --profile profiles/b0-oracle.yaml --corpus corpora/synthea-sample-v1 --set question-sets/v1.txt --repeats 1 --runs-dir <B0-RUNS-DIR>

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
- `fab admit [--check-searches]`: recompute gold for a question set and check it against the answer spec and gold lock; optionally cross-check declared evidence searches.
- `fab run`: run a profile over a question set, then grade and summarise it.
- `fab report <run-dir> [<compare-run-dir>] [--evidence-resolver <exe> --corpus <dir>]`: summarise a run, or compare two runs pairwise (exact
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

## Warm patient map

A profile may add a `warm_context` section (`command`, `args`, `timeout_seconds` default 120, `max_bytes` default 8000) to test a pre-computed patient overview. For each attempt of a patient-scoped question the harness runs the command once, with a minimal system-only environment and the usual timeout and kill-tree behaviour, and renders its stdout into the prompt's `{{patient_map}}` placeholder (empty for cohort questions). `command` and `args` accept `${ENV}`, and `args` also `{corpus}`, `{patient_id}` (the Patient resource id resolved from the question's MRN) and `{mrn}`. A failing, timed-out, empty or oversize map makes the attempt a `harness_error` with the reason in `attempts.jsonl`; it is never dropped silently. Maps are cached per patient within a run, and `attempts.jsonl` records `warm_bytes` and `warm_seconds` (0 for a cached map). A profile must use `warm_context` and `{{patient_map}}` together; see `profiles/p2-v5-warm.yaml` and `prompts/warm.md`.

## Query-form evidence

An agent's `evidence` entries may be resource references (`Type/id`) or the FHIR searches that returned the
resources (`Type?params`, or `Patient/<id>/Type?params`). Evidence is a diagnostic and never changes a grade.

- Each entry is classified as a reference, a search or invalid; invalid entries are kept and counted, never dropped.
- Searches are resolved to resource keys by running a pinned `cli-on-fhir` (`search <corpus> <Type> "<query>"
  [--compartment Patient/<id>] --elements id --max-bytes 51200`, paged by `offset`; a `_count` in the query limits the
  result as it did for the agent). Gold oracles never use it. Without a resolver, or when a query fails, the entry is
  recorded as unresolved with the reason; no keys are guessed.
- The resolver is `--evidence-resolver <exe>` on `run` and `admit`, else `FAB_EVIDENCE_RESOLVER` (and
  `FAB_EVIDENCE_RESOLVER_COMMIT`, recorded with the path in `run.json`). `report --evidence-resolver <exe> --corpus <dir>`
  re-resolves a finished run without changing it. Its index cache is `<run-dir>/evidence/cache`, outside the corpus.
- Limits: at most 25 entries per answer are analysed (the rest are counted as `ignored`), resolver time per answer is capped, `_offset`, `_count=0`, `_include` and `_revinclude` are removed from a cited search before it is run (recorded in `notes`), and the resolver must be an `.exe` on Windows. If any search in an attempt is unresolved, its recall and precision are null and the report counts it as excluded. A failure of the evidence analysis never changes a grade; it is recorded as `evidenceUnavailable`.
- `grades.jsonl` records per attempt the counts by kind, the resolved key set, recall and precision against the oracle's
  evidence keys, and how many cited searches were accepted ones. The report adds a short evidence section.
- A question may declare `evidence_searches: ["Encounter?class=EMER&date=gt..."]`: searches the bench accepts as
  evidence. They are written without a compartment; a patient-scoped question's compartment is applied automatically. An agent that
  cites one verbatim (parameter order and compartment spelling aside) is marked as having cited an accepted search.
- `fab admit --check-searches` (needs the resolver) resolves the union of each question's declared searches and compares it
  with the oracle's evidence: `searches-match`, `searches-superset` (extras listed) or `searches-miss` (missing listed). A miss
  or an unresolved search fails admit; gold and the lock are never touched. A cohort search usually cannot express
  "most recent value", so it is expected to be a superset.

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
