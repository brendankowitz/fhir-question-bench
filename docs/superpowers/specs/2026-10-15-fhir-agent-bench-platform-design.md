# FHIR Agent Bench: platform design (C# revision)

**Status:** Proposed — supersedes the Python harness direction in
`2026-10-15-fhir-question-benchmark-design.md`. The Python package on this
branch is kept as a **reference prototype**: its 100 QA + 50 operational items,
oracles, and grading rules are seed material to port, not the product.
**Date:** 2026-10-15.

## 1. Intent

Measure whether an agent answering clinician/researcher questions over a folder
of FHIR data does better — more correct, better grounded, cheaper — when it is
given specialist FHIR tooling (the `cli-on-fhir` MCP/CLI from
brendankowitz/ignixa-fhir#504, skills, or prompt changes) than when it only has
a filesystem. Results must be convincing to clinicians and professionals:
every answer is shown next to the gold answer and the exact source resources
that justify it.

The bench is modelled on two prior works:

- **olympus-swe-bench** (brendankowitz/olympus-swe-bench#1): C#/.NET solution,
  one folder per task (`task.yaml`, `problem.md`, hidden grading assets,
  `AUTHORING.md`), harness/profile YAML, admission checks, paired comparison
  metrics, report generation.
- **MedAgentBench** (Stanford, NEJM AI 2025): realistic clinician-voiced
  instructions over a patient population, deterministic reference solutions
  (`refsol`) that compute the gold answer from the FHIR data, task families of
  increasing difficulty, and query vs. action tasks.

## 2. Goals, non-goals, anti-goals

**Goals**
- Corpus first: build pinned, hash-locked synthetic corpora; generate tasks *from* them.
- 150 tasks: 100 QA of scaled complexity (tiers 1–5, ≥25 hard terminology:
  translation, equivalence, subsumption, display/code disagreement, inactive
  codes, ambiguous local codes, UCUM normalisation, unmapped → abstain) and
  50 operational tasks (de-identification, FML transforms, SQL-on-FHIR views,
  validation, terminology operations).
- Everything in C# (.NET 10) using Ignixa libraries for parsing, FHIRPath,
  terminology, validation, de-id, FML, SQL-on-FHIR, and FhirFakes generation.
- Run profiles that vary the prompt template, skills, MCP servers and model,
  with side-by-side, per-task paired comparison.
- A polished React web UI to browse tasks, launch runs, watch traces, inspect
  answer vs. gold with cited resources, and compare runs.
- One container image, hostable; users bring their own GitHub Copilot token.

**Non-goals**
- Clinical decision support or treatment recommendations.
- Real PHI. Licensed terminology content (no SNOMED CT descriptions copied).
- LLM-as-judge for pass/fail.

**Anti-goals (failure even if it "works")**
- Gold produced by the tool under test, or by an LLM without an oracle.
- Tasks whose answer leaks through the prompt or file names.
- A user's token persisted, logged, or shared across sessions.
- Claiming clinical validation for items not verified by a professional.

## 3. Repository layout (mirrors olympus-swe-bench)

```
FhirAgentBench.sln
src/
  FhirAgentBench.Core/          task model, loader, selector, results, metrics
  FhirAgentBench.Corpus/        corpus builder (Synthea subset + FhirFakes scenarios), manifests/hashes
  FhirAgentBench.Oracles/       per-family reference solutions (Ignixa-based)
  FhirAgentBench.Grading/       answer, evidence, artifact graders
  FhirAgentBench.Execution/     Copilot SDK runner, workspace isolation, profiles
  FhirAgentBench.Cli/           admit | build-corpus | run | grade | report | verify
  FhirAgentBench.Web/           ASP.NET Core API + SignalR, hosts the React build
web/                            React + TypeScript (Vite) UI
corpora/<corpus-id>/            built corpus + corpus.yaml (sources, seeds, hashes)
tasks/<TASK-ID>/                one folder per task (see §4)
profiles/<profile-id>/          run profiles (see §6)
skills/, mcp/                   assets profiles can enable
docs/superpowers/{specs,plans,research}/
tests/                          xUnit
```

## 4. Task folder ("what you see")

```
tasks/FAB-QA-T5-017/
  task.yaml        id, title, kind (qa|operation), tier 1-5, family, persona,
                   corpus id + hash, answer_type, budget, tags,
                   capabilities_advantaged, verification {status, by, at, notes}
  prompt.md        clinician/researcher-voiced ask + required answer format
  gold/answer.json hidden: typed answer + decisive evidence references
  gold/expected/   hidden (operations): expected artifacts
  inputs/          visible operation inputs (FML map, ViewDefinition, profile, rules)
  AUTHORING.md     provenance, oracle used, leakage review, why tools help
```

The oracle for a task is code in `FhirAgentBench.Oracles` keyed by family +
parameters in `task.yaml`; `admit` re-runs it against the pinned corpus and
fails if gold drifts. Gold is never mounted into the agent workspace.

## 5. Grading ("how we decide it's right")

Each attempt produces four results; **pass = correct ∧ grounded** (QA) or
**pass = artifact contract satisfied** (operations).

1. **Correct** — typed comparison by `answer_type`: exact, set, ordered list,
   number/quantity with UCUM-normalised tolerance, date/period, coded value
   (equivalence via owned terminology fixtures), and **abstain** (correct
   refusal when data is missing/ambiguous/unmapped).
2. **Grounded** — cited references resolve in the corpus and include the
   decisive evidence; extra citations allowed, fabricated ones fail.
3. **Artifact contract** (operations) — Ignixa-based checks: validate output,
   compare SQL-on-FHIR rows (order-insensitive, typed), de-id rule checks
   (no identifiers left, consistent date shift, references preserved), FML
   output equivalence after normalisation.
4. **Efficiency** — turns, tool calls by tool, tokens, premium requests, wall time.

An optional LLM rationale review may *annotate* traces; it never changes pass/fail.
Metrics follow olympus: resolved rate with CI, pass@k / pass-all-k, flip rate,
cost per pass, breakdowns by tier/family/persona, and **paired per-task
comparison** between profiles.

## 6. Run profiles and runs

A **profile** = `profile.yaml` (model, prompt template, enabled skills, MCP
servers, tool allow-list, budget) plus assets. Baselines shipped:
`filesystem-only`, `filesystem+cli-on-fhir-mcp`, `filesystem+fhir-skill`.
A **run** = profile × task selection × repetitions, with a snapshot hash of the
profile, corpus, task set and runtime versions; changing any of them is a new
snapshot (no silent comparison across changed inputs).

## 7. Execution and security

- Uses the **GitHub Copilot SDK for .NET** in `Mode = Empty`, one session per
  attempt with explicit `AvailableTools`, MCP servers and skills from the
  profile; no ambient host tools.
- Each attempt gets a fresh copy of the corpus as its workspace (read-only
  source; separate output dir); gold and other tasks are not reachable.
- **Token: bring-your-own.** Hosted mode accepts a user's Copilot-entitled
  GitHub token per browser session, held in server memory only, passed as the
  per-session `GitHubToken`, never written to disk, logs, traces or results;
  cleared on sign-out/idle timeout. GitHub OAuth device/web flow is the
  preferred way to obtain it. **Local mode** may use the logged-in Copilot
  CLI / environment token.
- Cost guard: per-run premium-request estimate shown and confirmed before start;
  per-run concurrency and budget caps.
- Container: single image (ASP.NET + built React + Copilot runtime + the
  `cli-on-fhir` tool), corpora and tasks baked in or mounted read-only, results
  volume for runs. Network egress limited to Copilot endpoints.

## 8. Web UI (React + TypeScript)

- **Tasks** — filter by tier/family/persona/kind/verification; task detail
  shows prompt, inputs, and (toggle) gold + evidence rendered as FHIR resources.
- **Run builder** — choose/clone a profile, edit prompt template, toggle skills
  and MCP servers, pick model and tasks, see cost estimate, launch.
- **Live run** — streaming trace (tool calls, MCP calls, messages).
- **Attempt view** — agent answer vs gold, pass/fail per grader, cited
  resources highlighted in a FHIR resource viewer, efficiency stats.
- **Compare** — two or more runs side by side: score deltas with CIs,
  per-task win/loss/flip grid, tool-usage and cost differences.
- **Verify** — reviewer workflow to mark tasks verified/rejected with notes,
  writing to `task.yaml` verification fields (git-tracked).

## 9. Verification lifecycle

`generated → unverified → under-review → verified | rejected`.
All 130 newly generated items start `unverified`; the original 20 are
`provisional`. Only a named human reviewer can set `verified`; reports
separate verified vs unverified scores.

## 10. Decisions and open items

| Decision | Who |
|---|---|
| All C#/.NET, Ignixa libraries for FHIR semantics | user |
| BYO Copilot token in hosted mode; local token allowed locally | user |
| React UI, comparable runs with editable prompt/skills/MCP | user |
| Corpus first, tasks generated from it; MedAgentBench-style tasks, olympus-style layout | user |
| React + TypeScript is the only non-C# part (UI) | coordinator's call |
| Copilot SDK (.NET) instead of shelling out to the CLI | coordinator's call |
| Deterministic graders decide pass/fail; LLM review is annotation only | coordinator's call |
| Python prototype kept on branch as reference until the C# port reaches parity | coordinator's call |

Open: hosting target (Azure Container Apps vs. App Service), whether Foundry
is used for question-drafting assistance in the C# pipeline, and who the
clinical/terminology reviewers are.
