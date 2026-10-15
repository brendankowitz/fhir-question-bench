# FHIR Question Benchmark Implementation Plan

> **Historical (2026-10-15):** superseded by `2026-10-15-fhir-agent-bench-platform.md`
> (C#/.NET, Ignixa, React UI, hosted container with bring-your-own token).

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an auditable 150-item synthetic FHIR benchmark that measures
whether specialist CLI/MCP tools improve professional question answering and
operational workflows: 100 QA items plus 50 operational tasks.

**Architecture:** Immutable FHIR corpora and terminology assets feed independent
answer/evidence oracles. A Python harness runs equal-access agent arms through
filesystem, CLI, or MCP adapters and grades typed answers plus supporting
evidence. Foundry assists authoring and critique, never establishes truth alone.

**Tech Stack:** Python 3.12+, pytest, JSON Schema/JSONL, standard-library SQLite
when appropriate, .NET 10 for Ignixa scenario/tool builds, supported official
Foundry/OpenAI and Azure identity clients.

**Spec:** [Investigation and proposed design](../specs/2026-10-15-fhir-question-benchmark-design.md).
**Status:** Proposed work; commands below are future acceptance checks, not
commands already implemented or run. Do not begin implementation until design
approval. Each task is a reviewable milestone, not a claimed completed feature.

## Active execution update

The binding scope brief revises the release to **100 QA + 50 operations = 150
total**, then stop. The previous **300-item plan** and 20 -> 100 -> 200 -> 300
sequence are historical. Generate now, verify later; no live inference calls.
The QA expansion is 20 original provisional items plus 80 new `unverified`
items. Independent clinician/terminologist acceptance remains a separate gate.

QA tiers 1–5 contain **15/20/20/20/25** records. The five implemented primary
families contain direct facts 14, temporal/status 19, joins 18, owned terminology
29, and cohort/missing evidence 20. Keep `tier`, `verification_status`,
`primary_family` and capability rationales on every record; legacy difficulty
labels stay unchanged. Recompute gold privately from source bytes and validate
exact evidence. The 25 tier-5 cases exercise synthetic mapping/hierarchy,
system/version/context disambiguation, inactive/display conflicts, UCUM and
abstention. Owned mechanics are not standard terminology release coverage.

`configs/smoke.json` explicitly selects the original 20 IDs: QA authoring must
not silently expand the runner's 60 planned B/C/D slots. Operations remain a
separately owned, artifact-graded track; do not force them into the scalar QA grader.

For every batch, record corpus/question/gold/config/tool hashes, exposure state,
actual model/version, arm availability, review findings, fixes, and rerun results.
Keep failed/blocked denominator slots. Repeatedly inspected items are adaptive
development evaluation, not an untouched sealed test. Update dataset-audit
expectations and split/cross-tab rules before scaling; the original 1,000-QA
matrix and 800-item sealed allocation below are historical proposed defaults.
The user has not authorized unlimited inference spending or waived independent
clinical review requirements.

### Review gates applying to every implementation handoff

The design's Astra amendments are binding: comparison snapshots invalidate
dependent results after any relevant system/configuration change; regression
success alone does not refresh cumulative scores. Batch freezes and exposure
are component-level, with adaptive results labeled as such. Native capability,
permitted fallback feasibility, blocked execution, and observed failure are
separate fields. Operational acceptance checks the full artifact inventory.

Runner/grader offline conformance starts at 20 provisional engineering items.
Live agent-authored execution requires verified process-level containment and
the spending gate. Until containment passes negative access/write tests for
all descendants, use offline fake-process validation only; a trusted-local
acknowledgement is not a substitute for a publication-quality boundary.

## Global constraints

- Final target: 100 QA plus 50 operational items, 150 total, with no further
  expansion. Acceptance class, verification status and exposure status are
  explicit; provisional engineering items are not professionally accepted items.
- Freeze active batch quotas before evaluation; original sealed split and
  1,000-QA matrix are historical. The former 100-family/200-lineage diversity
  quotas are historical too, not valid assertions about this smaller owned
  development set. Disclose shared patient/scenario/cohort-world variants.
- Audit patient/template/variant/cohort-world grouping at every stage; final-only
  diversity constraints must not reject the deliberately small initial slice.
- Default FHIR release R4, subject to explicit corpus validation; no silent
  mixed-version ingestion or assumed US Core conformance.
- Pin tool/source/terminology revisions; native source data is read-only.
- Core uses synthetic data only. Real MIMIC records and licensed terminology
  releases require separate review; never fabricate missing terminology.
- No inference submissions before a printed estimate and bounded approval.
- Credentials stay in environment configuration; no private Olympus KB reuse.
- The tested MCP must never supply its own gold answers.
- Report native/reference-normalized and cold/warm results separately.
- Keep the implemented inspect/search/read surface distinct from proposed tools.
- Document every accepted clinical rule and required review authority.
- Source-layer assignment follows mutation-dependent > specialist > Synthea;
  retain the parent origin separately, fixed before results are seen.
- Use connected task-family/patient/counterfactual/cohort-world components for
  split isolation and uncertainty estimates.

## Review focus

- Bundle-scoped aliases or contained references resolve to the wrong patient:
  Task 2 must test exact source identity and cross-Bundle collisions.
- Quantity conversion looks plausible but mismatches analyte/specimen:
  Task 3 must reject incompatible dimensions and unsupported molar factors.
- A count is correct on the first page but incomplete overall: Tasks 5 and 7
  must exercise pagination, cap overflow, ignored filters, and complete sets.
- Sibling question templates or whole-cohort worlds cross split boundaries:
  Task 6 must reject connected lineage/template/world components split apart.
- Provider retry/truncation or missing capability becomes apparent success:
  Tasks 4, 5, and 7 must distinguish infrastructure failure from valid abstention.

## Proposed file map

| Area | Files to create | Responsibility |
|---|---|---|
| Package and contracts | `pyproject.toml`, `src\fhir_question_bench\contracts.py`, `schemas\question.schema.json`, `schemas\gold.schema.json`, `schemas\corpus.schema.json`, `schemas\response.schema.json` | Typed, validated records and CLI entry point |
| Corpus | `src\fhir_question_bench\corpora.py`, `src\fhir_question_bench\references.py`, `corpora.lock.json` | Locked downloads/derivations and independent source identity |
| Truth | `src\fhir_question_bench\oracle.py`, `src\fhir_question_bench\terminology.py`, `protocols\*.json` | Executable rules and versioned terminology semantics |
| Scenarios | `tools\Benchmark.Scenarios\*`, `scenarios\*.json` | Only when Fakes needs a custom deterministic exporter |
| Authoring | `src\fhir_question_bench\authoring.py`, `src\fhir_question_bench\foundry.py` | Export/estimate/submit/collect with review states |
| Agent runner | `src\fhir_question_bench\runner.py`, `src\fhir_question_bench\adapters.py`, `configs\pilot.json` | Equal-access B/C/D runs and capability preflight |
| Freeze | `src\fhir_question_bench\dataset.py`, `dataset\questions.jsonl`, `dataset\review-ledger.jsonl` | Quotas, split groups, contamination checks, acceptance |
| Scoring/report | `src\fhir_question_bench\grading.py`, `src\fhir_question_bench\report.py` | Typed evidence-aware grading and paired reports |
| Tests | `tests\test_contracts.py`, `tests\test_corpora.py`, `tests\test_references.py`, `tests\test_oracle.py`, `tests\test_terminology.py`, `tests\test_authoring.py`, `tests\test_foundry.py`, `tests\test_runner.py`, `tests\test_dataset.py`, `tests\test_grading.py`, `tests\test_report.py` | Offline unit, mutation, integration tests |

Gold/oracle material and test keys belong outside the agent-visible mount.
Keep sealed keys private until the intended release; public questions and gold
can be released together after evaluation, with provenance and licenses.
Raw downloads, caches, provider results, and run traces are ignored by Git.
Do not add a UI, vector store, persistent server, or orchestration platform.

## Task 1: Validated contracts and an offline vertical fixture

**Files:** package/contracts/schemas and `tests\test_contracts.py`.
**Dependencies:** design approval.
**Interfaces produced:** validated `Question`, `GoldAnswer`, `CorpusManifest`,
`AgentResponse`, `RunConfig`, `RunResult`, `EvidenceRef`, `ResolvedResource`,
`Grade`, and `CostEstimate` records; `validate_record(kind: str, value: dict) ->
None`. Use `Decimal` for clinical quantities and timezone-aware datetimes.

- [ ] Write tests rejecting absent corpus hashes, missing cutoff/protocol,
  unknown answer states, invalid evidence locators, and unsupported tolerances.
  Assert the six answer states exactly match the design.
- [ ] Run `python -m pytest tests\test_contracts.py -q`; expect failures because
  the package/contracts do not exist.
- [ ] Create package/setup and strict contracts plus a minimal synthetic
  potassium fixture. Support typed scalar, series, set, aggregate, and narrative
  answers; keep questions separate from gold.
- [ ] Run the same command; expect all contract assertions to pass.
- [ ] Commit this self-contained milestone with the required session trailers.

## Task 2: Locked corpora and trustworthy reference resolution

**Files:** corpus/reference modules, lockfile, related tests.
**Depends on:** Task 1.
**Consumes:** `CorpusManifest`, `EvidenceRef`, `ResolvedResource`.
**Produces:** `load_corpus(manifest: CorpusManifest) ->
list[ResolvedResource]`; `resolve_reference(source: ResolvedResource,
reference: str, corpus: list[ResolvedResource]) -> list[ResolvedResource]`.
Return zero/multiple matches explicitly; never pick an arbitrary duplicate.

- [ ] Write tests for the design's pinned SHA-256; altered ZIP bytes; Bundle,
  single-resource, NDJSON input; `urn:uuid` aliases; contained targets; unresolved
  absolute refs; identical aliases in different Bundles; duplicate type/id.
- [ ] Run `python -m pytest tests\test_corpora.py tests\test_references.py -q`;
  expect missing-implementation failures.
- [ ] Implement hash-checked acquisition and independent JSON enumeration with
  exact source file/hash, entry/line/byte, pointer, and derivation identity.
  Do not extract archives through unchecked paths. Add subset manifests that
  retain referenced shared resources without leaking patients across worlds.
- [ ] Run the tests and `python -m fhir_question_bench corpus inspect --locked`;
  expect counts to match the committed Synthea profile, or an explicit
  source-hash mismatch, never silent acceptance of a new `latest`.
- [ ] Commit corpus/resolver changes with the required trailers.

## Task 3: Independent clinical and terminology truth

**Files:** oracle/terminology/protocol/scenario files and related tests.
**Depends on:** Tasks 1-2.
**Consumes:** validated corpora and protocol cards.
**Produces:** `derive_gold(question: Question, corpus: list[ResolvedResource])
-> GoldAnswer`; `verify_gold(question: Question, gold: GoldAnswer) -> None`;
`TerminologySnapshot` record; `lookup_code(snapshot: TerminologySnapshot,
system: str, code: str) -> dict`; `translate_code(snapshot:
TerminologySnapshot, map_id: str, system: str, code: str, context: dict) ->
list[dict]`. Define membership/hierarchy methods only when their first protocol
needs them, with equivalent version-locked tests.

- [ ] Write failing tests for the 20-item vertical slice: exact latest-final
  selection; wrong-patient exclusion; temporal boundary equality; corrected
  results; orders vs administrations; insufficient baseline; incompatible
  specimen/units; mapping unmatched/disjoint/one-to-many/dependencies.
  Include hand-computed sets and decisive-field mutations.
- [ ] Run `python -m pytest tests\test_oracle.py tests\test_terminology.py -q`.
- [ ] Implement independent exported-byte oracles and owned terminology
  fixtures. Import standard assets only after license approval. Add a small
  .NET Fakes exporter only where CLI factories cannot meet explicit scenario
  values, seeded randomness, stable identifiers, and fixed dates.
- [ ] Run oracle/terminology tests; if the exporter exists, run
  `dotnet test .\tools\Benchmark.Scenarios.Tests` and compare two complete exports
  byte-for-byte. Expect deterministic outputs and gold mutation failures.
- [ ] Obtain appropriate expert decisions for these first 20 items; save actual
  reviewer roles and unresolved issues. Commit only approved redistributable
  fixtures/protocols, with required trailers.

## Task 4: Cost-gated Foundry authoring and collection

**Files:** authoring/Foundry modules and related tests; environment example names
only, no endpoint secrets or credentials.
**Depends on:** Tasks 1-3; live submission additionally needs a spending approval.
**Consumes:** bounded synthetic authoring packets, `CostEstimate`.
**Produces:** `export_candidates(questions: list[Question], output: Path) ->
Path`; `estimate_requests(requests: Path, pricing: dict) -> CostEstimate`;
`submit_requests(requests: Path, config: RunConfig, approval: dict) -> Path`;
`collect_candidates(results: Path) -> list[dict]`. Candidate state is always
`review_required`, never implicitly accepted. This milestone expands the
development slice after its first offline runner/grader gate; it does not block
Task 5 or the minimal Task 7 implementation at 20 engineering items.

- [ ] Write offline tests proving estimate-only sends nothing; wrong approval
  hash/budget rejects; retries obey limits; 401/403/404 stop; 429/5xx remain
  bounded; refusals/truncation/invalid JSON are rejected; resume does not
  resubmit completed IDs; no credentials or real-patient data enter logs.
- [ ] Run `python -m pytest tests\test_foundry.py tests\test_authoring.py -q`.
- [ ] Implement official supported endpoint clients with deployment names and
  roles from configuration. Use direct mode initially; no existing batch SKU
  was observed. Import workflow patterns, not private Olympus data.
- [ ] Run the same tests; print a 20-candidate estimate. Only after approval,
  execute a bounded smoke call and authoring run. Capture actual usage; no
  "clinical validation" claim from model review.
- [x] Expand the gold-first QA slice to exactly 100 development questions,
  tiers 15/20/20/20/25, with 25 hard terminology mechanics cases. Recompute gold
  and exact evidence and run decisive mutations. Every new question is
  `unverified`; original 20 remain provisional.
- [ ] Obtain required independent expert review and a later verification
  transition system. Preserve shared task-family/lineage/cohort-world groupings;
  generated fixtures and offline conformance are not accepted agent results.
- [ ] Commit transport/pipeline changes with required trailers, not credentials
  or provider trace dumps.

## Task 5: Fair B/C/D runner and capability manifest

**Files:** runner/adapters/pilot configuration and tests.
**Depends on:** Tasks 1-3 for offline conformance; approved transport, verified
containment, and spending authorization before live model execution.
**Consumes:** `Question`, `CorpusManifest`, `RunConfig`.
**Produces:** `discover_capabilities(config: RunConfig) -> dict`;
`run_episode(question: Question, arm: str, config: RunConfig) -> RunResult`.
Arm values are `filesystem`, `cli`, `mcp`; raw context is a separate diagnostic.

- [ ] Write tests for exact resource reads, mocked MCP tools/list, CLI/MCP
  parity, 51+ references, 50 KB overflow, pages beyond 50 results, unsupported
  joins, ignored filters, stale indexes, native Bundle aliases, and tool crashes.
  Prove all arms can read the same terminology/protocol assets and cannot see
  gold, exporter labels, sibling run traces, or out-of-mount private files.
- [ ] Run `python -m pytest tests\test_runner.py -q`.
- [ ] Implement isolated fresh-episode workspaces and filesystem/code access;
  add CLI subprocess and MCP stdio adapters over the same pinned tool build.
  Put exact turn/token/tool-output/wall-time budgets into the pilot config
  after estimate review. Discover and test capabilities before eligibility.
- [ ] Integrate minimal Task 7 typed grading/reporting at the initial 20-item
  gate. Run offline tests and local tool-only conformance with no inference.
  Retain native-reference failures; file upstream work separately or label the
  track blocked. Do not rewrite sources to make the test appear to pass.
- [ ] After spending approval, run the 100-development-question B/C/D pilot:
  300 episodes on the proposed `gpt-5.6-sol` deployment/version. Account for all
  inference calls and preparation costs. Commit runner/config, not transient
  workspaces.

## Task 6: Review and freeze the capped 150-item set

**Files:** dataset module, accepted public questions/review ledger, tests.
**Depends on:** Tasks 1-5; expert staffing and required terminology access.
**Consumes:** candidates, verified gold, reviewer decisions, corpus manifests.
**Produces:** `audit_dataset(questions: list[Question], gold:
list[GoldAnswer], reviews: list[dict]) -> dict`;
`freeze_dataset(input_dir: Path, output_dir: Path) -> str` (manifest hash).

- [ ] Write stage-parameterized tests for current QA/operational quotas,
  acceptance class, exposure, batch membership and frozen manifests. At final
  release test missing required clinical review, missing terminology
  license/version, duplicates, and the declared active diversity limits.
  The previous 100-family/200-lineage quotas are historical.
  Split connected lineage/template/counterfactual/cohort components;
  reject leakage even when wording differs.
- [ ] Run `python -m pytest tests\test_dataset.py -q`.
- [ ] Expand scenario packs and native Synthea subsets, use bounded authoring
  and expert review, and accept records only after deterministic evidence
  verification and mutation checks. Maintain explicit blocked/provisional
  states; do not lower review gates to hit 150.
- [ ] Implement an explicit stage-target audit argument and document its actual
  syntax before use. The implemented `audit` command now checks 100 QA records,
  tier/family/verification counts and recomputes all private gold. Operations
  use their separate audit; overall target is 150, not a 150-row QA file.
  Freeze
  wording, keys, terminology, splits, tool revision, budgets, and protocol.
- [ ] Commit redistributable dataset/manifests and review provenance with
  required trailers; sealed keys remain out of agent-visible/public mounts
  until the intended release.

## Task 7: Evidence-aware grading and honest comparative report

**Files:** grading/report modules and tests.
**Depends on:** Tasks 1-3 for the first offline grader; Task 5 supplies episode
results. Minimal reporting precedes expansion; final reporting needs Task 6.
**Consumes:** gold, responses/traces, eligibility/freeze manifests.
**Produces:** `grade_response(question: Question, gold: GoldAnswer,
response: AgentResponse) -> Grade`; `build_report(results: list[RunResult],
grades: list[Grade], frozen_manifest: dict) -> dict`.

- [ ] Write tests where the answer is right but evidence is fabricated,
  wrong-patient, stale, contradictory, incomplete, or from an ignored filter.
  Assert strict success fails. Test correct absence scope, justified partial
  answers, over-abstention, correct count/wrong membership, alternate sufficient
  evidence, budget exhaustion, and denominator inclusion of unsupported items.
  Test bootstrap resampling of whole family/world groups and paired deltas.
- [ ] Run `python -m pytest tests\test_grading.py tests\test_report.py -q`.
- [ ] Implement typed deterministic graders, separate domain-review judgments,
  fixed/full and eligible denominators, capability/terminology ablations,
  cold/warm/preprocessing accounting, subgroup reports, and error examples.
  No invented spend when provider token/pricing data is missing.
- [ ] Run the tests, then run the frozen comparison only within approved
  budgets and verified containment. Proposed model panel: `gpt-5.6-sol` and
  `gpt-6.1-sol`; recompute cost/episode estimates for each active batch and
  repetition policy, not the historical 800-item sealed proposal. Approval may
  narrow that panel before preregistration; record the changed scope and do not
  imply unrun models/repeats. Inference settings must also be preregistered.
  Report all arms affected by any post-freeze gold correction as invalidated
  until rerun against a newly versioned key.
- [ ] Commit reproducible aggregate report artifacts and limitations with
  required trailers. Publish the narrow conclusion supported by observed
  intervals, even if it is no MCP advantage.

## Completion criteria

The benchmark is accepted only when all 150 records pass their actual
acceptance gates and the harness independently reproduces facts and evidence.
Authored 100-QA/50-operation volume is not professional verification or a live
evaluation. The former 300-item release plan is historical. Tool capability completion is
separate from benchmark authoring completion. Clinical effectiveness remains
unproven without an appropriately governed real-world study.
