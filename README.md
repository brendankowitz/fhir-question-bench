# FHIR Agent Bench

Clinician-voiced questions over a pinned FHIR corpus, for measuring whether an agent harness + model
answers better when you change its tools (e.g. the `cli-on-fhir` MCP), skills, prompt or model.

- Design: `docs/superpowers/specs/2026-10-15-fhir-agent-bench-platform-design.md`
- Research behind it: `docs/research/reports/FHIR agent bench revised plan.md`
- Status: Phase 0 (10 questions, loop proven). See `docs/superpowers/reports/`.

## Quick start

    dotnet test
    dotnet run --project src/FhirAgentBench.Cli -- corpus fetch corpora/synthea-sample-v1
    dotnet run --project src/FhirAgentBench.Cli -- admit --corpus corpora/synthea-sample-v1 --set question-sets/phase0.txt
    dotnet run --project src/FhirAgentBench.Cli -- run --profile profiles/b0-oracle.yaml --corpus corpora/synthea-sample-v1 --set question-sets/phase0.txt --repeats 1

`admit` checks computed gold against `questions/gold.lock.json`. Pass `--update-lock` only to bootstrap
the lock for new questions or to accept intentional gold changes.

Paid runs (`harness: copilot-cli`) print a credit estimate and need `--yes`. They use your
Copilot login (`COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, or `gh auth token`); the token is never written to disk.

### Exit codes (`fab run`)

- `0`: ok
- `1`: error
- `2`: usage error, or a paid run was not confirmed with `--yes`
- `3`: run completed, but some attempts were `harness_error` or `turn_limit`

## Layout

- `corpora/<id>/corpus.yaml`: pinned source, frozen "now", MRN system; `data/` is fetched, not committed.
- `questions/*.yaml`: one question each (tier, tags, tool groups, patient context, answer spec, oracle).
- `questions/gold.lock.json`: gold hashes; `fab admit` fails on drift.
- `profiles/*.yaml`: harness, model, tools, MCP servers, prompt, budget.
- `src/FhirAgentBench`: corpus index, oracles, grader, harnesses, reports (no Ignixa dependency).
