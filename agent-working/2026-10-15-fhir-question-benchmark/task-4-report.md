# Task 4 — 50 unverified operational tasks

## Delivery and ownership

- Exactly **50** operations, all `verification_status: "unverified"`, separate
  from the 100-item QA dataset. No expert acceptance, clinician review, measured
  agent performance, live tool support, cloud inference or paid run is claimed.
- Original synthetic content under the repository MIT license. No licensed
  SNOMED descriptions or external terminology releases.
- Owned paths: `src/fhir_question_bench/operations.py`,
  `operations_reference.py`, `operations_authoring.py`,
  `data/operations/**`, `tests/test_operations.py`, this report, and only the
  appended operations README section.
- `cli.py`: minimal additive changes only: register `operations-audit` and
  `--operations-root`, then dispatch before loading the QA dataset. Re-read the
  shared file immediately before editing. Its concurrent `RecursionError`
  change is **not** mine and is excluded from my staged patch.
- Did not edit runner.py, processes.py, test_runner.py, fake_copilot.py,
  contracts.py, oracle.py, QA questions/data/manifest/review ledger or design/plan.
  No implementation/review was delegated to another agent.

## Decisions and contract

**Ruling:** the latest binding brief/request sets 100 QA + **50** operations,
overriding older 200/100 allocations in the historical design/plan.

**Ruling:** use actual owned **R4 StructureMap JSON**, not a new textual FML
parser. This keeps the reference small and the map representation explicit.
Cost: textual FML engine/parser equivalence is not tested.

**Ruling:** the task-specific, documented differential/terminology/reference
policy defines validation truth, not a full base-R4 validator. The public
contract specifies severity, code, FHIRPath expression, coded constraint
identity and successful-validation representation. Cost: full R4, slicing and
inherited profile conformance require a separate independently reviewed track.

The main failure risk is permissive acceptance of incomplete or leaked output.
Every task therefore has a versioned whole-output contract, complete public
input inventory with SHA-256s, exact output inventory, preservation/loss rules,
failure/partial semantics and public normalization rules. Contracts expose
mechanics, not expected outputs. The operations manifest hashes all 50 task
records/fixtures/private outputs plus reference and grader implementation bytes.

- **55** private output JSON artifacts across the 50 tasks.
- `operations/agent/`: public tasks, policies, record/folder fixtures,
  StructureMaps, ViewDefinitions, StructureDefinitions, owned terminology.
- `operations/private/expected/<task-id>/`: private gold, never copied by
  `prepare_operation`. Neither private files nor Python oracle/grader code
  belong in an agent-visible execution boundary.
- Whole values compare, not selected fields. IDs are exact; declared timestamp
  paths compare equivalent aware instants with **zero-second tolerance**.
  Declared arrays are multisets, preserving duplicates. Other arrays stay
  ordered. Object key order is ignored. JSON numbers use exact Decimal-aware
  parsing/comparison; booleans/numeric strings/null remain distinct.
- Diagnostic prose alone may vary. Severity/code/expression/coded constraint
  identity and finding multiplicity remain mandatory.
- Entire output-tree collection hashes every file, rejects extras including
  raw copies/logs, unexpected empty directories, unsafe paths, links/reparse
  points and hardlinks; 8 MiB total budget. No partial success.
- Six constructed bad outputs per task: undeclared copy, missing artifact,
  category-specific wrong value, partial wrapper, duplicate row/entry/finding/
  match, and unauthorized ID. All **300** are rejected by the audit.

State/concurrency/failure semantics: the reference is synchronous and uses
local in-memory state, copies transformed values and does not mutate source
objects. Steps run in declared order; mixed-task consumers use prior outputs.
There are no processes, queues, background workers, network, retries, random
IDs or wall-clock dependencies. Map required-source failures discard the
partial target and return only an OperationOutcome. Unsupported reference
grammar fails explicitly, not as successful empty results. Input/implementation
hash mismatches fail closed. Live cancellation/backpressure/containment remain
the runner's separately owned future boundary, not claims of this collector.
Changes can be rolled back as one separate operations release, without
changing the QA dataset. Regeneration intentionally overwrites only owned
operations assets and refreshes the independent manifest; changed hashes
invalidate dependent comparison snapshots.

## Category and complexity distribution

| Category | Tier 1 | Tier 2 | Tier 3 | Tier 4 | Tier 5 | Total |
|---|---:|---:|---:|---:|---:|---:|
| De-identification | 2 | 2 | 2 | 2 | 2 | 10 |
| StructureMap mapping | 2 | 2 | 2 | 2 | 2 | 10 |
| SQL-on-FHIR ViewDefinition | 2 | 2 | 2 | 2 | 2 | 10 |
| Resource/profile validation | 2 | 2 | 2 | 2 | 2 | 10 |
| Mixed/owned terminology | 2 | 2 | 2 | 2 | 2 | 10 |
| **Total** | **10** | **10** | **10** | **10** | **10** | **50** |

Tiers are author-assigned engineering complexity, not measured tool difficulty.
Harder cases compose reference rewriting, per-patient/date policies, nested
mapping, null/collection/union/cross-product/multiset semantics, multiple
validation rules and version/context-dependent terminology. Fixtures are
deliberately small for auditable independent truth, not a scale/performance test.

## Complete task list

| ID | Tier | Task |
|---|---:|---|
| deidentification-01 | 1 | Safe-Harbor-style demographic minimization |
| deidentification-02 | 1 | Preserve nonidentifier clinical codes |
| deidentification-03 | 2 | Uniform leap-day shift |
| deidentification-04 | 2 | Consistent negative date shifts |
| deidentification-05 | 3 | Bundle aliases and request references |
| deidentification-06 | 3 | Two-patient offset isolation |
| deidentification-07 | 4 | Contained resources and local references |
| deidentification-08 | 4 | ZIP threshold and 90-plus topcoding |
| deidentification-09 | 5 | Reference conservation with unlisted external links |
| deidentification-10 | 5 | Nested privacy policy without clinical loss |
| mapping-01 | 1 | Patient active projection |
| mapping-02 | 1 | Observation status normalization |
| mapping-03 | 2 | Component selection into scalar value |
| mapping-04 | 2 | Nested HumanName projection |
| mapping-05 | 3 | Identifier synthesis |
| mapping-06 | 3 | Patient-to-Observation reference construction |
| mapping-07 | 4 | Cross-type Condition projection |
| mapping-08 | 4 | Atomic required-source failure |
| mapping-09 | 5 | Repeated nested component transform |
| mapping-10 | 5 | Evaluate typed collection count |
| view-01 | 1 | Patient identifiers and booleans |
| view-02 | 1 | Final-observation filter |
| view-03 | 2 | Typed missing values |
| view-04 | 2 | Inner name unnest |
| view-05 | 3 | Outer name unnest |
| view-06 | 3 | Nested panel projections |
| view-07 | 4 | Collection-valued columns |
| view-08 | 4 | Union of scalar and component measurements |
| view-09 | 5 | Filtered nested panel multiset |
| view-10 | 5 | Sibling-selection cross product |
| validation-01 | 1 | Conformant projected Observation |
| validation-02 | 1 | Required status absent |
| validation-03 | 2 | Fixed-code violation |
| validation-04 | 2 | Maximum cardinality violation |
| validation-05 | 3 | Multiple independent profile failures |
| validation-06 | 3 | Typed value and unit pattern |
| validation-07 | 4 | Choice cardinality conflict |
| validation-08 | 4 | Required terminology binding failure |
| validation-09 | 5 | Warning invariant plus required binding |
| validation-10 | 5 | Unresolved reference with conformant binding |
| mixed-01 | 1 | Translate a single equivalent code |
| mixed-02 | 1 | Validate display and version |
| mixed-03 | 2 | Expand active membership |
| mixed-04 | 2 | Preserve multi-target translation |
| mixed-05 | 3 | Context-dependent translation |
| mixed-06 | 3 | Missing-context abstention |
| mixed-07 | 4 | Inactive-code rejection and paged expansion |
| mixed-08 | 4 | Sanitize then materialize a research view |
| mixed-09 | 5 | Map then validate using a pinned owned binding |
| mixed-10 | 5 | Wrong-version translation plus deidentification and validation |

## Verification evidence

All commands run locally with Python 3.12; no inference was invoked.

1. Initial RED: `python -m pytest tests/test_operations.py -q`:
   import/collection failure because the operations module did not exist.
   After implementation: 63 passed, one expected missing-CLI failure, then
   64 passed after adding the CLI dispatch.
2. Independent checks were expanded, not just gold-vs-self comparisons:
   hand-derived rows for all 10 views, expected severity/code/location tuples
   for all 10 validation tasks, mapped resources and nested components,
   privacy/date/reference invariants, exact owned terminology behavior and
   mixed-step composition.
3. Additional RED→GREEN regressions: optional diagnostic-only details text,
   row-duplication mutation (rather than a duplicated schema column), decimal
   precision preservation, StructureMap wrong source type, and task input-hash
   enforcement. Final fixture checks also require explicit owned codes on
   synthesized Observations and preserved contained-staff references. Missing
   optional FHIR source fields are omitted rather than represented as empty
   arrays or null. A transient Windows PermissionError during one regeneration
   resolved on retry; no permission or unrelated-file changes were made.
4. Latest focused command: `python -m pytest tests/test_operations.py -q`:
   **102 passed in 5.57s**, exit 0.
5. `python -m fhir_question_bench operations-audit`, exit 0:

   ```json
   {
     "category_counts": {
       "deidentification": 10, "mapping": 10, "mixed": 10,
       "validation": 10, "view": 10
     },
     "gold_accepted": 50,
     "live_execution": "blocked",
     "manifest_sha256": "4adec24798aef44cb10f3ff36209ce00f6c7beaf21251a196187338ea4e4823d",
     "measured_agent_performance": false,
     "mode": "offline-reference-and-grader-self-check",
     "recomputed": 50,
     "task_count": 50,
     "tier_counts": {"1": 10, "2": 10, "3": 10, "4": 10, "5": 10},
     "verification_status": "unverified",
     "wrong_outputs_rejected": 300
   }
   ```

6. First full `python -m pytest -q` while concurrent QA/runner edits were in
   progress: **47 failed, 199 passed in 143.40s**, exit 1. No operations test
   failed. Concurrent failures, left to their owners:
   - `tests/test_cli.py::test_cli_audit_prepare_recompute_and_offline_demo`
   - `tests/test_foundation_review_fixes.py::test_installed_wheel_audits_from_outside_checkout`
   - `tests/test_foundation_review_fixes.py::test_every_prepared_scope_template_generates_a_gradable_scope`
   - `tests/test_oracle_grading.py::test_exactly_twenty_original_questions_and_hand_calculated_gold`
   - 43 runner cases, chiefly the old global `len(questions) != 20` guard at
     runner.py:472 rejecting the intentionally expanded 100-item QA dataset.
     Includes parametrized fake failures, 60-episode checks, lifecycle,
     deep-JSON, preflight mutation and original-terminology mutation cases.
7. Next full `python -m pytest -q` after concurrent integration fixes:
   **1 failed, 301 passed in 406.68s (0:06:46)**, exit 1. Sole failure:
   `tests/test_runner.py::test_runner_selects_only_the_original_initial_twenty_when_data_expands`.
   Its expected episode-ID set included all 100 QA items despite asserting a
   planned count of 20. This runner-owned test was left untouched.
8. Final full `python -m pytest -q` against the finished operations release:

   ```text
   FAILED tests/test_runner.py::test_unconfirmed_windows_cleanup_blocks_collection_and_advancement
   1 failed, 304 passed in 388.37s (0:06:28)
   ```

   Exit 1. The preceding initial-twenty test was fixed concurrently by its
   owner. This new runner cleanup test expected
   `['cleanup_failed', 'blocked_cleanup']` but got
   `['blocked_preflight', 'blocked_preflight']`. This is a separate concurrent
   runner/preflight integration failure; **no operations tests failed** and no
   runner-owned path was edited to hide or fix it.
9. `git -c core.whitespace=cr-at-eol --no-pager diff --cached --check`: no
   output, exit 0. Default `diff --check` treats native CRLF source lines as
   trailing whitespace; the CRLF-aware check above is the meaningful staged
   whitespace check. Implementation hashes bind the actual committed bytes.
10. Offline packaging command:
    `python -m pip wheel --no-index --no-deps --no-build-isolation . --wheel-dir <temporary>`
    succeeded. Python zipfile/hash checks then verified **all 157 locked data
    assets**, including all **55 private expected artifacts**, and both
    implementation hashes inside the wheel. Actual output:

    ```text
    Successfully built fhir-question-bench
    Wheel verified: all 157 locked data assets, including 55 private expected artifacts; implementation hashes match.
    ```

    This used no package index/network. The temporary wheel was inspected and
    removed, not published. Exact command wrapper used `tempfile`, `subprocess`,
    `zipfile` and `hashlib`; no source or gold was submitted externally.

## Limitations and rollout

- All 50 remain unverified. Self-review by the implementation author is weaker
  than an independent terminology/standards/expert review.
- The reference supports the documented owned subsets only. No general FML
  parser, SQL compiler, full FHIRPath evaluator, terminology server or complete
  R4 profile validator is claimed. Invalid/auxiliary fixture fields are
  intentional and identified by prompts.
- De-identification is policy-scoped, not HIPAA certification. The ZIP suppression
  list is owned synthetic policy; age is an auxiliary fixture field.
- The output collector is for quiescent trusted offline artifacts and is not a
  race-free hostile-process boundary. Live execution stays blocked; the existing
  runner is not extended to execute operations.
- No specialist product was invoked or certified. Capability labels describe
  potential advantage only; independent native-support/conformance/entitlement
  checks and a verified boundary are required before future comparisons.
- Generator reruns are release-authoring actions, not evaluation. Outputs never
  come from the tested tool. The operations manifest is separate from the QA
  dataset manifest, so it does not revise QA truth or the review ledger.

## Commits

- `fd6f86e2fb674e3f4179cd24795271adc5384d12` — Add 50 unverified
  operational tasks and whole-output audit. **164 owned paths/hunks only**:
  158 JSON assets, three implementation/authoring modules, the tests, seven
  additive CLI lines and the 101-line appended README operations section.
- This report is committed separately after the implementation commit; its
  commit identity is available from the path's git history and final handoff.
- Both commits use the requested coauthor/session trailers. No push.

Staging used explicit owned pathspecs. The ignored private expected directory
was explicitly force-added by its exact owned pathspec so all 55 expected
artifacts are actually tracked. Shared README/CLI hunks were synthesized
against HEAD and applied to the index with explicit path includes; peer README
runner-lifecycle edits and the CLI RecursionError change remained unstaged.
The index was checked for foreign paths before committing. No stash/reset/
checkout, wholesale add or changes to peer work were used.
