# FHIR Question Bench

> **Direction change:** the bench is being rebuilt in C#/.NET with Ignixa, a
> React UI and a hostable container (bring-your-own Copilot token). See
> [design](docs/superpowers/specs/2026-10-15-fhir-agent-bench-platform-design.md)
> and [plan](docs/superpowers/plans/2026-10-15-fhir-agent-bench-platform.md).
> The Python package below is the reference prototype and seed data.

An offline, synthetic FHIR benchmark for auditing typed answers and source
evidence. The active target is **150 items: 100 QA questions plus 50 operational
tasks**, not an accepted or clinically validated benchmark. The QA set contains
the original **20 provisional** items plus **80 unverified** generated items.
It tests resource navigation, source references, time/status selection,
benchmark-owned terminology mechanics, and scoped missing evidence. It does
not make diagnoses or treatment recommendations and does not claim full FHIR
validation.

## Status and scope

- Exactly 100 QA questions span tiers 1–5: **15 / 20 / 20 / 20 / 25**.
  Family counts are direct facts **14**, temporal/status **19**, cross-resource
  joins **18**, owned-local terminology **29**, cohort/missing evidence **20**.
  Tier is an expansion rubric; the original 20 retain their legacy `difficulty`
  labels unchanged (for example, their cohort items are now tier 4).
- The committed synthetic corpus includes NDJSON, a transaction Bundle with
  `urn:uuid` aliases, contained-resource and Type/id references, temporal/status
  and wrong-patient distractors, and a scoped absence case.
- The 25 tier-5 questions exercise pinned ConceptMap translation and relation
  strength, local hierarchy direction/transitivity, display/code disagreement,
  inactive codes, system/version/context disambiguation, UCUM normalization and
  unmapped/unsupported abstention. CodeSystems, displays and maps are owned
  synthetic mechanics, **not clinical equivalence or standard-release coverage**.
  Three public identifiers were observed in the pinned Synthea archive
  (SNOMED/LOINC/RxNorm); no standard descriptions or patient records were copied.
  No ICD-10 identifiers were found: its translation case deliberately abstains.
- Questions, agent-visible FHIR/protocol/terminology, the versioned response
  contract, selection/completeness policies, and the review ledger are separate
  assets. Gold is recomputed from source bytes and is not committed in the
  agent-visible preparation directory.
- Every review row remains provisional; clinical/terminology review has not
  been performed. Additive `verification_status` is `provisional` for the
  original 20 and `unverified` for the new 80. `tier` and
  `capabilities_advantaged` are present on every question; `primary_family` is
  the family field. Legacy question records remain parseable with defaults.
  No expert-verification transition is implemented.
- The offline demo is an **oracle/self-check**, not measured agent performance.
  No paid inference or cloud calls were made; live execution stays blocked.
  The former **300-item plan is historical**, superseded by the 150-item cap.
  Eight new owned patient scenarios and related cohort variants are correlated
  development fixtures, not independent sealed samples.

## Install and test

Requires Python 3.12+.

```powershell
python -m pip install -e ".[test]"
python -m pytest
```

The package also exposes the module CLI and a console script:

```powershell
python -m fhir_question_bench --help
fhir-question-bench --help
```

## Offline commands

```powershell
# Validate locked source hashes, review metadata, tier/family counts and all 100 gold records
python -m fhir_question_bench audit

# Export one question with its corpus, protocol, response contract, and policies
python -m fhir_question_bench prepare --question-id temporal-001 --out .\prepared\temporal-001

# Recompute private gold. Keep this output outside any agent-visible directory.
python -m fhir_question_bench recompute --out .\private\gold.jsonl

# Grade a strict response JSON against recomputed gold; writes JSON and Markdown
python -m fhir_question_bench grade --question-id temporal-001 `
  --response .\responses\temporal-001.json `
  --gold .\private\gold.jsonl `
  --out .\grading\temporal-001.json

# Exercise the oracle and grader without model inference or cloud access
python -m fhir_question_bench demo --out .\artifacts\offline-demo.md
```

When overriding the package data directory, place `--data-root <path>` before
the subcommand. `prepare` refuses to overwrite a non-empty destination.
`recompute` refuses to write gold under the agent-visible `data\agent` tree.
No command downloads or extracts archives.

## Strict response format

The response is JSON with exact fields; unsupported extra fields and extra
claims are rejected. Prepared packages contain `response-contract.json` and
`selection-policies.json`, which specify the versioned output shape, selection
rules, and completeness obligations without revealing gold answers. A claim
binds the complete typed answer to its supporting evidence and a protocol rule.
Evidence includes both hashes, resource identity, relative source file, JSON
Pointer, and source literal, plus a Bundle entry index or 1-based NDJSON line
when applicable. Equivalent evidence can use a parent citation or sufficient
leaf citations; source identity, decisive fields, and exact units remain
checked. Decimal answers compare numerically and aware timestamps compare as
instants; series order and exact units remain significant.

```json
{
  "question_id": "direct-004",
  "status": "answered",
  "answer": {
    "kind": "quantity",
    "value": {"value": "72.5", "unit": "kg"}
  },
  "claims": [{
    "id": "claim-1",
    "answer_pointer": "/answer",
    "evidence_ids": ["e1"],
    "rule_id": "direct-quantity-v1"
  }],
  "evidence": [{
    "id": "e1",
    "corpus_hash": "<64 lowercase hex characters>",
    "resource_identity": "Observation/o-weight-ada",
    "resource_reference": "Observation/o-weight-ada",
    "source_hash": "<64 lowercase hex characters>",
    "file": "corpus/resources.ndjson",
    "line": 15,
    "pointer": "/valueQuantity",
    "source_value": {"value": "72.5", "unit": "kg"}
  }],
  "limitations": [],
  "scope_checks": []
}
```

The six accepted answer states are `answered`, `partial`,
`insufficient_evidence`, `ambiguous`, `unsupported_capability`, and
`invalid_data`. An ambiguous answer can be null when system/version/context
cannot be selected, or a typed candidate set when requested; decisive evidence
and a bound claim are still required. Absence/cohort claims also carry a scope check with the corpus hash, protocol,
selection policy/version, resource type, patient scope, cutoff, criteria, an
answer-claim binding, and an explicit complete-scan assertion. Unresolved
references that could change membership stop gold derivation rather than being
counted as non-matches. The deterministic grader checks type-aware answer
equality, current source/gold hashes, resolving pointers/literals, sufficient
decisive evidence from the required source identities, rule binding, and
required scope. It does not
judge prose as clinical reasoning.

## Python interfaces for the next runner milestone

Stable starting points:

```python
from pathlib import Path
from fhir_question_bench import (
    AgentResponse, GoldAnswer, Question, audit_dataset, derive_gold,
    grade_response, load_corpus, load_questions, load_selection_policies,
    prepare_question, verify_gold,
)

questions = load_questions(Path(".../data/questions.jsonl"))
corpus = load_corpus(Path(".../data/agent/corpus-manifest.json"))
question = questions[0]
gold = derive_gold(question, corpus)
verify_gold(question, gold, corpus)
prepared = prepare_question(question, corpus, Path(".../data"), Path(".../prepared"))
grade = grade_response(question, gold, AgentResponse.from_dict(response_json), corpus)
```

`Question`, `GoldAnswer`, `AgentResponse`, `EvidenceRef`, and `Grade` are
validated dataclasses in `fhir_question_bench.contracts`; `Corpus` and
`ResolvedResource` are in `fhir_question_bench.corpus`. `load_corpus` verifies
immutable SHA-256 manifests. `resolve_reference(source, reference, corpus)` is
available from `fhir_question_bench.references` and returns explicit
`resolved`, `missing`, or `ambiguous` outcomes. Gold and review data are not
copied by `prepare_question`. `grade_response` accepts an optional `data_root`
argument so an external grader can use private gold derivation inputs while
responses themselves are authored only from the prepared public assets.

## Research and design

- [Benchmark design](docs/superpowers/specs/2026-10-15-fhir-question-benchmark-design.md)
- [Staged release plan](docs/superpowers/plans/2026-10-15-fhir-question-benchmark.md)
- [Pinned Synthea source profile](docs/research/synthea-source-profile.json)

## Initial20 offline runner

Milestone 2 prepares actual Copilot CLI argv and runs **trusted fake-process
engineering checks only**. Live inference is **blocked**, including when
credentials and spending approval are present. There is no uncontained opt-in.
A copied workspace, permission flags, Windows Job Object cancellation, and a
successful fake test are **not** a read/write/private-gold/network boundary.
The selected 20 items remain provisional adaptive engineering material, not
accepted clinical questions. `configs/smoke.json` explicitly pins their IDs so
the larger QA set does not silently expand the 60-episode smoke plan.
The historical 20 -> 100 -> 200 -> 300 sequence is superseded by the target
**100 QA + 50 operations = 150 total**. QA authoring and runner/live acceptance
are separate gates.

From an installed development checkout (Python 3.12+ and the existing test
extra), prepare the default B/C/D plan:

```powershell
python -m fhir_question_bench run --config configs\smoke.json --output .\runs\smoke
```

This **does not launch any subprocess or send inference**. It creates 60
episodes (20 questions × `filesystem`, `cli`, `mcp`), fresh per-episode homes,
public workspaces, MCP config when configured, exact Copilot argv, and
`report.json` / `report.md`. Use a **new, empty output directory** each time.
The sample plans a 60-second wall limit and **30 AI credits per episode,
1,800 total soft-cap credits**; this is not a dollar budget or a hard guarantee.
The installed CLI requires at least 30 credits and an in-flight call can
overshoot. Unknown pricing, usage, actual model version and observed tool
counts are `null`, never invented zero.

Config is strict JSON. Required fields are `copilot_command` (argv array, not
a shell string), `copilot_version` (expected pin, not proof of the installed
version), fixed `model` (not `auto`), `wall_timeout_seconds`, and
`max_ai_credits`. Optional fields are `reasoning_effort`, `max_trace_bytes`,
`max_answer_bytes`, `auth_env`, `question_ids`, `arms`, and `tool`.
For example, add `"question_ids": ["direct-001"]` and
`"arms": ["filesystem"]` to prepare one slot. Paths containing spaces remain
single argv elements. Windows `.cmd` / `.bat` wrappers may appear in a plan
but are **not shell-executed**: configure a native `.exe` or an explicit
interpreter argv (for example `node.exe`, absolute script path) for eventual
execution. The runner never concatenates prompts into `cmd.exe` commands.

The specialist tool is not bundled or installed automatically. Add:

```json
"tool": {
  "command": ["C:\\FHIR tools\\cli-on-fhir.exe"],
  "expected_revision": "<pinned-source-commit>",
  "mcp_tools": ["<exact-supported-tool-name>"]
}
```

Replace placeholders with the real executable/revision and independently
discovered supported tool names; no terminology/operational tool is assumed.
The CLI arm gets command argv, the `./corpus` resource folder and help
instructions. MCP uses that **same** command with `mcp <resourcefolder>`,
fresh `COPILOT_HOME`, built-in MCPs disabled, explicit extra stdio config and
the pinned tool allowlist. An executable file hash and declared source SHA
are recorded separately: the SHA remains **unverified**, not a source-build
attestation. Dry-run performs no version/behavior/handshake probes; actual CLI
tool-name integration, search behavior, model version, and tool provenance
remain unverified. Missing specialist configuration does not remove its slots.
Generic scripting remains a permitted fallback in every arm; specialist
native support and end-to-end feasibility are separate report fields.

All arms receive identical public corpus/protocol/terminology assets,
`response-contract.json` and `selection-policies.json`. Runner `question.json`
omits family, difficulty, persona and review labels; private gold and oracle
code are never copied. Agents must write **`answer.json`** using the public
typed response and exact required/forbidden `scope_template` bindings, not a
JSON object embedded in a trace. Private grading uses source-derived gold
and the explicit `grade_response(..., data_root=...)` contract.

### Offline subprocess conformance

```powershell
python -B -m pytest -q -p no:cacheprovider --basetemp .test-work-runner tests\test_runner.py
```

These tests invoke the real `run` CLI and real Python child processes, not a
model. Their explicitly oracle-derived response fixtures validate harness
mechanics, **not independent agent performance**. For a separately authored
trusted fixture, the test-only injection syntax is:

```powershell
python -m fhir_question_bench run --config configs\smoke.json --output .\runs\fake --fake-process .\fixtures\trusted-fake.py --fake-tool-process .\fixtures\trusted-mcp.py
```

Supply those files yourself; do not point this interface at model-authored
code or a real provider wrapper. The fake script receives the prepared Copilot
arguments and writes `answer.json` and optional usage JSON. `--fake-tool-process`
is optional, requires fake mode and tool configuration, and must implement
JSON-lines MCP initialize / initialized / tools/list. Failed/missing preflight
blocks the specialist slots with no fallback substitution. Exact tool-list
conformance proves only the **fixture** handshake, not real search behavior or
real source revision. The fake command identity/hash is recorded, but external
fixture dependencies are unverified and its outcomes cannot be reused as
benchmark evidence. Fake mode is always labeled `offline-fake-process`,
`measured_agent_performance: false`.

Every execution bounds combined stdout/stderr and answer/usage bytes, records
exit status and elapsed time, and cancels fixture descendants on timeout,
overflow **and normal parent exit**. Windows uses suspended creation, Job
Object assignment, then resume, then waits for job accounting to report zero
active descendants before collection; a cleanup deadline failure is reported
and stops advancement. POSIX uses a new process session/group. These are
trusted-fixture lifecycle controls, not
a hostile-code sandbox. Preparation freezes complete original data-root and
protected workspace inventories. Those exact file bytes, directory membership
and link safety are
checked before each dispatch and after collection; no changed source or later
workspace becomes a new baseline. This is change detection, not write
prevention. The runner remains pinned to the original initial20 question IDs
and per-item SHA-256 values. Later unverified QA rows cannot enter its
denominator.
Configured Copilot argv may not override runner-owned prompt, session, model,
budget, permission, MCP/config or output options; reported permission metadata
is derived from the effective generated argv.

Final collection requires zero process exit, a final lifecycle `session.idle`
for the same session when IDs are supplied, no later lifecycle activity without
another terminal transition, a regular single-link answer, a strict response
and a passing evidence-aware grade. Bounded JSON-depth failures in answers,
usage or traces are failed episodes, with the planned denominator still reported.
Unknown live CLI event schemas are not certified by these fixtures.
Symlinks/reparse points, hardlinked answers, malformed/oversize JSON,
unsupported citations and source mutation cannot pass. Source changes stop
all remaining slots; hashes detect changes but **do not prevent writes**.

Reports retain planned/completed/failed/blocked/not-run denominators and per-arm
status counts. Wrong answers are failed observations; unavailable infrastructure
is blocked, not a measured wrong answer. Snapshot hashes cover question,
corpus/public assets, private derived gold, Python grader/oracle/runner source,
prompts, requested model/reasoning, config, permissions, budgets, runtime and
tool identity. Relevant changes invalidate dependent results: **no historical
pooling**, even if a regression suite passes. No cumulative model score,
statistical significance or clinical acceptance is claimed.

### Future live gate (currently blocked)

```powershell
python -m fhir_question_bench run --config configs\smoke.json --output .\runs\blocked --execute --approve-total-credits 1800
```

Approval must match the configured full planned soft cap exactly; it does
**not** enable submission. This records blocked slots and exits 1. Missing
auth is reported before submission; invalid configuration/approval exits 2.
Only standard auth **names** (`COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_TOKEN`)
may be configured. Never put token values in config, argv, logs or reports,
and never copy personal auth caches. Fake children receive no auth/provider
environment. Plans include `--secret-env-vars` for configured auth names, so
future credential forwarding must keep them out of agent shell/MCP children.
Live auth forwarding and preflight await a verified boundary implementation;
no model command is launched by this milestone.

Before any live pilot, independently verify a descendant-covering boundary
with read-only corpus, private gold inaccessible, episode-only writes and
enforced network policy, plus negative access/write tests. Then verify CLI
version/tool-name integration, real MCP handshake and representative search
behavior, tool build/source identity, actual model/usage schema, and approved
spending. Independent Astra review of this bounded offline milestone comes
before live evaluation; clinical acceptance and live budget approval remain separate.

## Operational tasks (separate offline track)

Exactly **50 unverified operational tasks** live under
`src/fhir_question_bench/data/operations/`: 10 de-identification, 10
StructureMap transforms, 10 SQL-on-FHIR v2 ViewDefinition materializations,
10 resource/profile validation, and 10 mixed/owned-terminology operations.
Each category has two tasks at each tier 1–5. These are generated adaptive
development fixtures, **not professionally verified tasks or measured agent
performance**. There are no live runs or claims of specialist-tool support.

```powershell
python -m pytest tests/test_operations.py -q
python -m fhir_question_bench operations-audit
```

The audit checks the independent operations manifest and implementation hashes,
recomputes all 50 private expected outputs, requires the whole-output grader to
accept gold, and rejects six constructed wrong outputs per task. It does not
change `verification_status`. `--operations-root <path>` overrides the operations
tree independently of the QA `--data-root`.

### Public inputs, private gold, and output contracts

`operations/agent/tasks.json` contains realistic prompts, tiers, capabilities
that could help (not assertions of native support), ordered operation steps,
input inventories and versioned output contracts. Inputs include owned synthetic
records/Bundles, policy JSON, **R4 StructureMap JSON** (the chosen mapping-language
representation, not a general textual FML parser), ViewDefinitions,
StructureDefinition differentials and a version-pinned owned terminology Bundle.
`operations/private/expected/<task-id>/` contains expected JSON artifacts. It must
never be exposed to an evaluated agent, nor may the Python reference/grader code
be mounted into that agent's boundary.

Export a single task without gold:

```python
from pathlib import Path
from fhir_question_bench.operations import prepare_operation

prepare_operation("mixed-08", Path("prepared-operations/mixed-08"))
```

This copies only the selected public inputs and `task.json` into a new directory.
Private grading uses `load_expected`, `recompute_task`, `grade_artifacts` and
`grade_output_tree` from `fhir_question_bench.operations`; operational success
is distinct from scalar QA accuracy. The collector hashes the **entire** output
tree and rejects undeclared files (including logs/raw copies), empty extra
directories, escaping/noncanonical paths, links/reparse points and hardlinks.
It has an 8 MiB output budget. It is trusted offline collection, not race-free
hostile-process containment.

Contracts enforce exact artifact inventories, IDs, required/forbidden fields,
preserved relationships and allowed loss, typed table values, nulls and row
multiplicity, and validation severity/code/FHIRPath-expression tuples.
Only declared arrays are order-insensitive **multisets**; other arrays retain
order. JSON object key order is irrelevant. JSON numbers compare numerically,
never as booleans or numeric strings. Declared aware timestamps compare as
instants with **zero-second tolerance**; dates and all other strings are exact.
Issue diagnostic prose may vary; issue codes, severity, expressions and coded
constraint identities may not. No partial success, auxiliary artifacts, extra
issues or undeclared generated IDs are accepted.

### Reference semantics and limits

`operations_reference.py` is deterministic, standard-library-only and independent
of the tool under test. Tests cross-check hand-derived privacy, mapping,
tabulation, validation and terminology cases. Its supported subset is explicit:

- **De-identification:** recursive exact-key suppression, year-only birth dates,
  fixed/per-patient clinical date offsets, exact supplied ID/alias/reference maps,
  configured ZIP3 suppression and an owned auxiliary age topcode fixture.
  These are Safe-Harbor-style rule exercises, **not HIPAA certification or a
  de-identification-risk assessment**. One scoped policy deliberately preserves
  `code.text`; unlisted public external references remain intact.
- **StructureMap:** one group/source/target, nested rules, source min/max and
  conditions, variables, copy/create/append/evaluate. Required-source failure
  produces only an OperationOutcome, never a partial target.
- **ViewDefinition:** resource/where, column, scalar/collection columns, nested
  select, forEach/forEachOrNull, sibling cross products and unionAll. The FHIRPath
  subset is navigation, literals, `$this`, `where`, `first`, `count`, `exists`,
  `empty` and the fixture comparisons; unsupported expressions fail explicitly.
- **Validation:** only supplied differential cardinality/choice, primitive and
  complex-type shape, fixed/pattern values, constraints and pinned owned binding
  rules, plus an explicit local-reference policy where requested. This is **not
  base-R4 conformance, full profile/slicing validation or clinical acceptance**.
  Conformant cases return one information/informational issue.
- **Terminology:** owned versioned CodeSystem/ConceptMap/ValueSet mechanics for
  `$translate`/`$validate-code`/`$expand`-style results: one-to-many and dependent
  matches, missing-context abstention, inactive/mismatched-version rejection,
  exact display checks and filtered/paged expansions. There is no licensed
  SNOMED text or clinical equivalence claim. Expansion timestamps are fixed,
  not the wall clock.

To intentionally regenerate this owned release, run
`python -m fhir_question_bench.operations_authoring`, then rerun tests/audit and
review every change. Inputs, keys, implementation hashes or contract changes
invalidate dependent comparison snapshots. The existing runner does **not**
execute these tasks. Live execution remains blocked pending the independently
verified descendant-covering read/write/private-gold/network boundary described
above; no cloud or model calls are made by this track.
