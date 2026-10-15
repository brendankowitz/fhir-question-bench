# Task 1: First FHIR benchmark vertical slice

## Delivered

Implemented an offline Python 3.12+ package, `fhir_question_bench`, and an
exactly 20-item synthetic developer dataset. The question set has four items
in each of five families: direct facts, temporal/status trends, cross-resource
joins, owned-local terminology mechanics, and cohort/missing evidence. All
question and review states remain provisional.

The slice includes a typed response/gold contract, immutable SHA-256 source and
dataset manifests, exact reference resolution (including Bundle-scoped
`urn:uuid`, Type/id, exact fullUrl, and contained references), source-derived
gold, pointer/literal/hash/evidence-aware grading, scoped completeness checks,
question preparation that excludes gold and review data, and an offline
oracle/self-check demo. Synthetic NDJSON and a transaction Bundle include
references, distractors, missing evidence, and local mapping mechanics. No
agent-generated code is executed.

The active roadmap target is now 300 questions, staged 20 -> 100 -> 200 -> 300.
This delivery is only the initial 20 and does not implement expansion or any
later runner/release machinery.

## Stable interfaces

- `fhir_question_bench.load_questions(path)` loads validated question records.
- `fhir_question_bench.load_corpus(path)` verifies source hashes and loads
  NDJSON, FHIR JSON, and Bundle resources.
- `fhir_question_bench.derive_gold(question, corpus, data_root=None)` derives
  private gold from the locked source resources.
- `fhir_question_bench.verify_gold(question, gold, corpus)` checks gold against
  current source data.
- `fhir_question_bench.prepare_question(question, corpus, data_root, output_dir)`
  exports the question and its agent-visible corpus/protocol assets only.
- `fhir_question_bench.grade_response(question, gold, response, corpus)` grades
  a strict `AgentResponse` and returns a `Grade`.
- `fhir_question_bench.audit_dataset(data_root=None)` validates the dataset and
  recomputes all question gold.
- `fhir_question_bench.references.resolve_reference(source, reference, corpus)`
  returns explicit `resolved`, `missing`, or `ambiguous` status.

The module CLI is `python -m fhir_question_bench` with `audit`, `prepare`,
`recompute`, `grade`, and `demo` commands. The console entry point is
`fhir-question-bench`. The demo explicitly reports `oracle/self-check`, not
measured agent performance.

## Verification

- `python -m pytest -q --durations=10` — **20 passed**.
- `python -m fhir_question_bench audit` — **20 questions**, **20 protocols**,
  **20 provisional review records**, **27 resources**, and all 20 gold records
  recomputed successfully.
- `python -m fhir_question_bench demo --out .\artifacts\offline-demo.md` —
  **20/20 strict oracle self-checks**.
- `python -m fhir_question_bench --help` — lists all five CLI commands.
- `python -m pip wheel . --no-deps --no-build-isolation --wheel-dir .\dist` —
  wheel built; checked that the question file, dataset manifest, and protocol
  file are packaged.
- `git diff --check` — clean.

The first wheel attempt failed because the active Python environment lacked the
declared setuptools build backend. After installing setuptools, the wheel build
completed successfully.

## Limitations and handoff

This is a synthetic mechanics smoke slice, not a clinically validated
benchmark. No full FHIR schema/profile validation, independent clinical or
terminology review, live Copilot CLI runner, inference, cloud API, or execution
is included. All 20 review labels are provisional. The synthetic CodeSystem and
ConceptMap do not represent authoritative clinical terminology. No
clinical-reasoning performance or safety claim should be inferred from the
oracle demo.

No design or release-plan documents were changed. A later live-code-execution
runner must enforce an actual isolation boundary; a working-directory
convention alone is not a security boundary.
