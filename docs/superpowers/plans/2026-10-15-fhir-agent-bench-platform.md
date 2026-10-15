# FHIR Agent Bench (C#) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Port and extend the Python prototype into a C#/.NET 10 bench with
Ignixa-based oracles/graders, a Copilot SDK runner with comparable run
profiles, a React UI, and a hostable container with bring-your-own token.

**Design:** `docs/superpowers/specs/2026-10-15-fhir-agent-bench-platform-design.md`
**Supersedes:** `2026-10-15-fhir-question-benchmark.md` (Python plan, now historical).
**Seed material:** `src/fhir_question_bench/**` (100 QA + 50 ops, oracles, graders).

**Tech stack:** .NET 10, xUnit, Ignixa.* (Serialization, FhirPath, Specification,
Validation, DeId, FhirMappingLanguage, SqlOnFhir, FhirFakes), GitHub Copilot
SDK for .NET, ASP.NET Core + SignalR, React + TypeScript + Vite, Docker.

## Milestone 1 — Solution and task model
- [ ] Task 1: Scaffold `FhirAgentBench.sln` per design §3 (Directory.Build.props,
  Directory.Packages.props, global.json, .editorconfig, AGENTS.md). Ignixa
  packages via NuGet or a pinned submodule/source reference — decide and record.
- [ ] Task 2: `Core` task model + YAML loader/validator for `task.yaml`
  (kind, tier, family, persona, corpus ref+hash, answer_type, budget,
  verification block), `profile.yaml`, `corpus.yaml`. Tests for invalid files.

## Milestone 2 — Corpus first
- [ ] Task 3: `Corpus` builder: pinned Synthea subset (source SHA-256
  `56cb9e49…a521`) + FhirFakes/MIMIC-inspired scenarios + owned terminology
  fixtures (CodeSystem/ConceptMap/ValueSet); deterministic seeds; per-file
  hashes in `corpus.yaml`. Port the prototype corpus/terminology as corpus `seed-v1`.
- [ ] Task 4: Corpus profiling report (resource counts, code systems, date
  ranges, reference graph) used to drive task generation.

## Milestone 3 — Oracles and graders
- [ ] Task 5: `Oracles` per family (direct, temporal/status, cross-resource,
  cohort/missing, terminology) using Ignixa FHIRPath + terminology; port the
  prototype derivations with parity tests against prototype gold.
- [ ] Task 6: `Grading` answer types (exact, set, list, quantity/UCUM, date,
  coded-equivalence, abstain) + evidence grounding; port prototype edge cases
  (unresolved refs, contained refs, excluded records, temporal eligibility).
- [ ] Task 7: Operation graders: Ignixa validation, SQL-on-FHIR row compare,
  de-id rule checks, FML output normalisation; port the 50 ops with parity.

## Milestone 4 — Tasks
- [ ] Task 8: Convert 150 prototype items into `tasks/<ID>/` folders
  (task.yaml, prompt.md, gold/, inputs/, AUTHORING.md); `admit` recomputes all
  gold, runs leakage checks, and rejects drift. Statuses: 20 provisional, 130 unverified.
- [ ] Task 9: Strengthen hard terminology set to ≥25 items reviewed for
  realism (ConceptMap translation, ambiguous local codes, inactive codes, UCUM,
  unmapped→abstain). Foundry may draft wording; oracle decides gold.

## Milestone 5 — Execution
- [ ] Task 10: Copilot SDK runner (`Mode = Empty`, per-attempt session,
  explicit tools/MCP/skills from profile, fresh workspace, gold unreachable,
  trace + usage capture). Fake runtime for tests; port prototype runner
  safeguards (input tamper detection, terminal lifecycle, bounded parsing,
  no runner-owned overrides, process cleanup).
- [ ] Task 11: Profiles `filesystem-only`, `filesystem+cli-on-fhir-mcp`,
  `filesystem+fhir-skill`; snapshot hashing; cost estimate + confirmation.
- [ ] Task 12: Results store + metrics (resolved rate + CI, pass@k, flip rate,
  cost per pass, breakdowns, paired comparison) + `report` command.

## Milestone 6 — Web and hosting
- [ ] Task 13: ASP.NET API (tasks, profiles, runs, attempts, compare,
  verification) + SignalR live traces; token handling per design §7 (memory
  only, never logged/persisted; local mode uses ambient login).
- [ ] Task 14: React UI pages per design §8.
- [ ] Task 15: Verification workflow writing `task.yaml` verification fields.
- [ ] Task 16: Dockerfile (single image), local `docker run` + hosted config;
  security tests: token never on disk/logs, workspace isolation, egress policy.

## Milestone 7 — First real comparison (gated)
- [ ] Task 17: With user-approved budget, run `filesystem-only` vs
  `filesystem+cli-on-fhir-mcp` on the 20 provisional tasks, k=3; publish report.
  Then scale to the 150.
- [ ] Task 18: Retire the Python prototype once parity tests pass.

## Gates
- Each task: implementer → reviewer; fix loop per implement-task-next.
- Live runs require user approval of the cost estimate.
- No item is marked `verified` except by a named human reviewer.
