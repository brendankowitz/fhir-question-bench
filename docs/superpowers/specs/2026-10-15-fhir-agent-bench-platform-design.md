# FHIR Agent Bench: design (revision 2)

**Status:** Proposed. Supersedes revision 1 of this file (platform-first C#
port of the Python prototype) and `2026-10-15-fhir-question-benchmark-design.md`.
**Date:** 2026-10-15.
**Evidence base:** `docs/research/reports/FHIR agent bench revised plan.md` and
the notes under `docs/research/research_notes/FHIR agent bench revised plan/`.

## 1. Intent

About 150 FHIR questions, simple to complex, written the way clinicians and
researchers ask them. They test how well an **agent harness plus LLM** reasons
over a corpus of FHIR data. We change one thing at a time (tools such as the
`cli-on-fhir` MCP, skills, prompts, or the model) and measure whether accuracy,
cost and consistency change.

Validating `cli-on-fhir` (brendankowitz/ignixa-fhir#504) is a primary purpose.
Questions are written assuming its **full designed tool surface** has landed:
search, read, patient context, `fhirpath_evaluate`, `plan_fhir_search`,
`sql_view` and the terminology tools. Every run records the tool commit it used.

## 2. Principles

1. **Difficulty lives in the data, not the prompt.** The corpus is too large to
   read whole. References must be resolved. Some results are entered in error.
   Units disagree. Codes must be discovered.
2. **Gold is code.** Every question has an oracle that computes its answer from
   the pinned corpus. Oracles never call Ignixa search or `cli-on-fhir`, so a
   bug in the tool under test cannot also be in the gold.
3. **Deterministic grading.** Typed answers, explicit tolerances, strict set
   equality. No LLM judge decides pass or fail.
4. **Measure before building.** No platform or UI work until a pilot shows the
   bench separates a strong profile from a weak one (Gate 3).

## 3. Decisions

| Decision | Source |
|---|---|
| Everything in C# (.NET 10), including the oracles | user |
| Corpus generated, not imported: Synthea (licence acceptable) + FhirFakes needles + seeded mutations | user |
| Real LOINC, SNOMED CT, RxNorm, CVX codes are acceptable | user |
| Harness-agnostic where cheap; GitHub Copilot CLI/SDK is the primary harness when a tradeoff is needed | user |
| Oracles use `System.Text.Json` over raw resources, not Ignixa | coordinator: independence from the tool under test |
| Answers are returned as a file (`answer.json`), not via a bench-owned MCP tool | coordinator: works with any CLI harness, including no-MCP baselines |
| The 50 operational tasks (de-id, FML, SQL-on-FHIR, validation) leave the 150 | coordinator: they are not corpus questions; possible later track |
| Read-only questions in v1; no MedAgentBench-style write/order tasks | coordinator |

## 4. Corpus

Three layers. Each layer is produced once, its files are hashed, and the hashes
are recorded in `corpora/<corpus-id>/corpus.yaml`. Evaluation never regenerates
data.

1. **Base: Synthea R4.** Phase 0 uses the pinned public sample
   (`synthea-sample-data@9959d917`, zip sha256 `56cb9e49…a521`: 109 patients,
   119,981 resources). Phase 1 replaces it with a custom run of about 100–130
   patients. That run pins the seed, clinician seed, reference date, end date
   and jar, and uses keep modules to guarantee the patients the questions need.
2. **Needles: about 12 FhirFakes scenario patients** built with a seeded
   `ScenarioBuilder`, with facts known by construction:
   - N1: diabetes escalating to a GLP-1.
   - N2: CKD on drugs that raise potassium.
   - N3: AF on anticoagulation.
   - N4: inpatient AKI on vancomycin.
   - N5: untreated hypertension.
   - N6: allergy conflicting with an order.
   - N7: paediatric asthma.
   - N8: pregnancy.
   - N9: oncology regimen.
   - N10: serotonergic/QT polypharmacy, with one medication coded as text only.
   - N11: duplicate patient record.
   - N12: hospice/multimorbidity.

   A custom Synthea module is acceptable for any needle where it is easier.
3. **Mutations: a seeded post-processor** that writes a manifest. Each manifest
   entry records category, resource, path, before and after. Categories:
   - Status: entered-in-error, refuted, stopped.
   - Units: mg/dL ↔ mmol/L.
   - Codes: site-local code with a display; ICD-10 beside SNOMED; text-only
     medication.
   - Missing data: `dataAbsentReason`, reduced date precision.
   - Duplicates: exact, near, and duplicate Patient records.
   - References: one dangling reference; some `urn:uuid` → `Type/id`.
   - Conflicts: an allergy contradicted by an order.

The large data files are not committed to git: they are fetched or generated
into `corpora/<id>/data/` and checked against `corpus.yaml` hashes. Each corpus
has one frozen **now**, which the harness supplies. Question text never
contains it.

## 5. Questions

### 5.1 Tiers (by mechanism)

| Tier | Mechanism | Count |
|---|---|---|
| T1 | Direct lookup: one type, one patient, latest or "ever" | 20 |
| T2 | Filtered/temporal: windows, status, first/last, relative time | 30 |
| T3 | Aggregation/trend: count, mean, min/max, change, units | 30 |
| T4 | Multi-resource and terminology: drug classes, hierarchies, reference hops | 30 |
| T5 | Calculation and stated rules (CHA2DS2-VASc, Cockcroft-Gault, KDIGO) | 20 |
| T6 | Cohort/population across the corpus | 12 |
| T7 | Unanswerable from the record: correct answer is to abstain | 8 |

Tags, independent of tier:
- Data mechanism: `TEMP`, `AGG`, `TERM`, `UNIT`, `ABS`, `CALC`, `MULTI`, `REF`, `MAP`.
  `MAP` is clinician language, ICD-10-CM, brand names or drug classes that must be translated to the code
  system the data uses.
- `tool_groups`: which `cli-on-fhir` group the question exercises
  (`patient-context`, `search`, `fhirpath`, `sql-view`, `terminology`).

Template diversity: about 60 templates, at most 3 instances each, and no
patient behind more than about 8 questions.

### 5.2 Voice and context

The harness supplies a launch context: `{now, patient: name + birth date + MRN,
role}`. The question then says "she", "this patient" or "my panel", for
example: "What was his last potassium?" or "How many of my diabetics had a last
A1c over 9 this year, counting those with none?" A question never contains a
resource id or an invented code system. Where missing data matters, the
question says in clinical words what to do. Rules for care-gap and calculation
questions are stated in clinical shorthand.

### 5.3 Splits

- **Test:** 150 questions.
- **Dev:** 30 questions from the same template families but different patients;
  skills and prompts may be tuned on these.
- **Holdout:** 50 private questions on a second corpus seed, never published.
  A large gap between test and holdout scores means a profile has overfit.

### 5.4 External suite: FHIR-AgentBench

A second suite runs over an external corpus, the MIMIC-IV Clinical Database Demo
on FHIR 2.1.0 (`corpora/mimic-iv-fhir-demo-2.1.0`, ODbL), with questions and
answers adapted from FHIR-AgentBench (Lee et al., 2025; CC-BY-4.0), to test whether
a tool such as `cli-on-fhir` helps on real, messier records. Its gold is upstream:
FHIR-AgentBench derived it with SQL over MIMIC-IV, the bench does not recompute it,
and `fab import fhir-agentbench` (`suites/fhir-agentbench/`) only types it. The
importer downloads the CSV at a pinned commit, checks its sha256, infers a typed
answer spec per row from its SQL and template, and writes question YAMLs, a gold
file for the `static-answer` oracle (`true_fhir_ids` become its evidence) and
question sets, all gitignored; rows it cannot type faithfully are skipped and
counted. Grading stays deterministic, with the rules below and no LLM judge
(upstream uses one). Questions carry their own `now` and an assumption note from
upstream, rendered beside `now` in the prompt. These questions are not voiced as in
§5.2: they name the patient by MIMIC subject_id, which the corpus carries as the MRN.

## 6. Answers and grading

The agent writes `answer.json` into its output directory:

```json
{ "status": "answered" | "insufficient_data",
  "answer": <typed value>,
  "evidence": ["Observation/abc", "..."] }
```

The answer spec (type, unit, precision, fields) appears in a fixed harness
section of the prompt, never in the clinical wording.

| Type | JSON shape | Rule |
|---|---|---|
| integer | number | exact |
| decimal | number | absolute tolerance declared per question; optionally also a `relative_tolerance`, passing within the larger of the two |
| quantity | `{value, unit}` | unit must equal the declared UCUM unit after normalising equivalent spellings; value within tolerance |
| date | `"YYYY-MM-DD"` | exact at declared precision |
| code | `{system, code}` | tuple equality, display ignored |
| identifier | `{system, value}` | tuple equality (MRN, NPI) |
| name | non-empty string, declared `kind: person` or `kind: text` | equal after normalisation: case, accents, titles, name order and suffixes for a person; `&` and legal forms for an organisation or place. No abbreviation expansion. Rules in `docs/questions/oracle-semantics.md` |
| set | array of strings or of `{system, code}`; or of `items` of one declared type (text, decimal, integer, datetime, date) | strict set equality; with `items`, every gold item matches some answer item, every answer item matches some gold item under the item rule, and the answer has as many distinct items as the gold (exact repeats ignored); a nullable set treats `[]` as null; precision and recall logged |
| boolean | true/false | exact |
| composite | object of named fields, each with its own spec | all fields correct; per-field logged |
| datetime | `"YYYY-MM-DD HH:MM[:SS]"` (`T` also accepted) | same wall-clock minute; seconds and any trailing `Z`/offset are ignored, so a time converted to another zone is wrong |
| text | non-empty string | equal after trimming, collapsing runs of whitespace and ignoring case; no other normalisation |

Any type may be declared `nullable`, meaning "no such result" is a valid
answer, given as `answer: null`. This is distinct from `insufficient_data`.
Abstaining is correct only when the gold is `insufficient_data`; on answerable
questions it is counted as **abstained**, not wrong. The one exception is a spec
declaring `null_accepts_abstain` (only with `nullable`): when its gold is null,
`insufficient_data` is also correct. Imported suites use it where the upstream
gold cannot tell "nothing recorded" from "no such result" (§5.4).

Every attempt ends in exactly one status: `correct`, `wrong`, `abstained`,
`format_invalid`, `turn_limit` or `harness_error`. Harness errors are re-run
and not scored. Evidence is not graded; retrieval recall against the oracle's
evidence ids is reported as a diagnostic.

An evidence entry is a resource reference (`Type/id`) or the FHIR search that
returned the resources (`Type?params`, or `Patient/<id>/Type?params`); invalid
entries are kept and counted. Searches are resolved to resource keys by a
pinned `cli-on-fhir` (path and commit recorded in `run.json`). When no resolver is configured or a
query fails, the entry is unresolved, never guessed. Recall, precision, counts by kind and the
use of accepted searches are descriptive only and never change a grade.

**Grader CI:** each question ships 2–4 equivalent answers that must pass and
2–4 near misses that must fail. Two baselines must score as expected: the
oracle profile at 100%, and the always-abstain profile at about 5%.

## 7. Oracles

The v1 set uses 18 oracles (6 from phase 0, 4 of them extended, plus 12 new).
An external suite (§5.4) uses one more, `static-answer`, which reads upstream gold
from a file keyed by question id instead of computing it from the corpus.

An oracle is a C# class keyed by name with typed parameters from the question
file. It reads a `CorpusIndex` that the bench owns. The index:
- loads Bundles and NDJSON;
- resolves `urn:uuid` fullUrls, `Type/id`, conditional
  `Type?identifier=system|value` and contained `#id` references;
- finds patients by MRN.

`admit` runs every oracle against the pinned corpus. It fails a question when:
- the oracle throws;
- the gold is empty for a question not marked `expect_empty`;
- the gold hash differs from `gold.lock.json`, i.e. gold drift.

Oracles never use `cli-on-fhir`: gold stays independent of the tool under test. A
question may declare `evidence_searches`, searches the bench accepts as evidence;
`admit --check-searches` resolves them with the pinned `cli-on-fhir` and compares the result with
the oracle's evidence (`searches-match`, `-superset`, `-miss`), which cross-checks the oracle against the tool
without changing gold or the lock.

Gold is computed at grading time and never written into a run workspace.
`gold.lock.json` stores hashes only. Every question needs a second derivation
before it is `verified`: either an independent query written in a different
style, or a second author.

## 8. Profiles and harnesses

A profile (`profiles/<id>.yaml`) defines the harness, model, built-in tools,
MCP servers, skills directory, prompt template, per-attempt credit cap and
turn limit. Harness implementations sit behind `IAgentHarness`:

- `copilot-cli`: the primary harness. Runs the `copilot` CLI non-interactively
  with an explicit `--available-tools` list, MCP servers via
  `--additional-mcp-config`, no custom instructions, and a per-attempt
  `--max-ai-credits`.
- `oracle` (B0) and `abstain` (B1): baselines that run without cost.
- A Copilot SDK harness and other CLIs (Claude Code, etc.) are added later as
  further `IAgentHarness` implementations. Prompt-in and answer-file-out keeps
  that cheap.

Profiles:

| ID | Model | Tools | Skill / prompt | Purpose |
|---|---|---|---|---|
| B0 | – | oracle | – | must score 100% |
| B1 | – | always abstain | – | floor ≈ 5% |
| B2 | A | none | default | knowledge floor |
| P1 | A | shell: view, rg, glob, powershell | default | baseline |
| P2 | A | P1 + full `cli-on-fhir` MCP | default | **tool effect (P2−P1)** |
| P2a | A | P2 minus the terminology, sql-view and fhirpath groups | default | which tool groups matter |
| P3 | A | P2 | FHIR skill | **skill effect (P3−P2)** |
| P4 | A | `cli-on-fhir` MCP only | default | can the MCP replace code? |
| P5 | B | P2 | default | **model effect** |
| P6 | A | P2 | plan/verify prompt | prompt effect |

**Workspace isolation:**
- Each attempt runs in a fresh directory outside the repository.
- The corpus is attached read-only via `--add-dir`, and its file manifest is
  re-checked after the attempt.
- Gold is never on disk during a run.
- `--allow-all-paths` and `--allow-all-urls` are never passed.

**Cost guard:** `run` prints the number of attempts × the credit cap and
requires `--yes`.

## 9. Measurement

- **Repeats:** K=3 by default; K=5 for the three pre-registered contrasts
  (P2−P1, P3−P2, model).
- **Primary statistic:** paired per-question difference, with standard errors
  clustered by patient. Exact McNemar and a paired bootstrap serve as checks.
  The other contrasts use Holm–Bonferroni correction.
- **Also reported:** pass@1, pass^k, flip rate, cost and tokens per correct
  answer, turns, an accuracy-vs-cost Pareto plot, and per-tier and
  per-`tool_group` breakdowns. Per-tier and per-group results are descriptive
  only.
- **Expected resolution:** at 150 questions, roughly 8 pp at K=3 and 7 pp at
  K=5 for same-model profiles. 5 pp needs 300–600 questions.

## 10. Phases and gates

| Phase | Work | Gate |
|---|---|---|
| 0 | C# skeleton; corpus index; grader; 6 oracles; 10 questions; Copilot CLI harness; B0/B1/P1/P2 profiles; first real run | B0 = 100%, B1 = abstain share. Real P1 and P2 trajectories graded. Agent cannot reach gold. `cli-on-fhir` indexes the sample corpus in practical time |
| 1 | Custom Synthea run, needles, mutation layer, corpus v1 hashes | Oracle confirms every needle fact and manifest entry. A median patient exceeds a model context window |
| 2 | About 60 templates → about 220 candidates, grader CI, dev and holdout splits | 100% oracle pass. Second derivation agrees. Every question is reachable through the tools |
| 3 | Pilot: weak vs strong profile at K=3; drop questions every run passes or fails; re-run power | Strong − weak ≥ 20 pp. Projected minimum detectable effect for P2−P1 ≤ 10 pp at K=5 |
| 4 | Freeze 150/30/50 and run the profile matrix | Results reproducible from the pinned corpus, tool commit and model |
| 5 | Optional: React UI, hosting with bring-your-own token, write tasks, scale to 300+ | Driven by Phase 4 |

Each phase gets its own implementation plan in `docs/superpowers/plans/` once
the previous gate passes.

## 11. Disposition of the Python prototype

- **Keep as ideas:** typed answers including abstain, oracle-computed gold,
  evidence grounding, run profiles with paired comparison, runner isolation
  checks. The existing 100 questions become template ideas only.
- **Remove at the end of Phase 0:** the Python package, its corpus, its 150
  items and its tests. Git history keeps them.

## 12. Open items

- Clinician reviewer for question wording and rule questions. Without one, the
  bench is labelled "engineer-reviewed".
- Whether to keep Claim/ExplanationOfBenefit as distractors, depending on
  indexing cost in Phase 0.
- Run budget: about 8 profiles × 150 questions × K=3, plus the pilot. Estimated
  from Phase 0 usage data.
- Java 17+ is needed for the Phase 1 custom Synthea run; it is not installed on
  the current dev machine.
