# FHIR Question Bench

An offline, synthetic FHIR benchmark smoke slice for auditing typed answers and
source evidence. This implementation contains **20 provisional developer
questions**, not an accepted or clinically validated benchmark. It tests
resource navigation, source references, modest time/status selection,
benchmark-owned terminology mechanics, and scoped missing evidence. It does
not make diagnoses or treatment recommendations and does not claim full FHIR
validation.

## Status and scope

- The 20 questions are split evenly across direct facts, temporal/status,
  cross-resource joins, owned-local terminology, and cohort/missing evidence.
- The committed synthetic corpus includes NDJSON, a transaction Bundle with
  `urn:uuid` aliases, contained-resource and Type/id references, temporal/status
  and wrong-patient distractors, and a scoped absence case.
- The CodeSystem and ConceptMap are benchmark-owned synthetic mechanics only;
  they are not SNOMED CT, LOINC, RxNorm, or clinically authoritative mappings.
- Questions, agent-visible FHIR/protocol/terminology, and the review ledger are
  separate assets. Gold is recomputed from source bytes and is not committed in
  the agent-visible preparation directory.
- Every review row is provisional; clinical/terminology review has not been
  performed. The offline demo is an **oracle/self-check**, not measured agent
  performance.
- Paid inference, cloud calls, a Copilot CLI runner, expansion beyond this
  first 20-question slice, and full release/freeze machinery are out of scope.
  The current release target is 300 questions in staged batches.

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
# Validate locked source hashes, question contracts, review states, and all 20 gold records
python -m fhir_question_bench audit

# Export one question with only its agent-visible corpus and protocol assets
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
claims are rejected. A claim binds the complete typed answer to its supporting
evidence and a protocol rule. Evidence includes both hashes, resource identity,
relative source file, JSON Pointer, and the source literal, plus a Bundle entry
index or 1-based NDJSON line when applicable.

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
`invalid_data`. Absence/cohort claims also carry a scope check with the corpus
hash, protocol, resource type, patient scope, cutoff, criteria, an answer-claim
binding, and an explicit complete-scan assertion. The deterministic grader checks type-aware answer
equality, current source/gold hashes, resolving pointers/literals, exact
decisive evidence identities, rule binding, and required scope. It does not
judge prose as clinical reasoning.

## Python interfaces for the next runner milestone

Stable starting points:

```python
from pathlib import Path
from fhir_question_bench import (
    AgentResponse, GoldAnswer, Question, audit_dataset, derive_gold,
    grade_response, load_corpus, load_questions, prepare_question, verify_gold,
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
copied by `prepare_question`.

## Research and design

- [Benchmark design](docs/superpowers/specs/2026-10-15-fhir-question-benchmark-design.md)
- [Staged release plan](docs/superpowers/plans/2026-10-15-fhir-question-benchmark.md)
- [Pinned Synthea source profile](docs/research/synthea-source-profile.json)

The next implementation milestone can build the Copilot CLI runner on these
interfaces. Any future expansion must retain the explicit provisional status
until appropriate independent review and acceptance gates are met.
