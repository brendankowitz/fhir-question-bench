# Task 3 — 100-item QA expansion

## Delivery

Implemented directly, without delegating implementation or decisions. Exactly
**100 QA records** are committed: the original 20 plus 80 new records. The
overall release target is **100 QA + 50 operations = 150**, not the historical
300-item plan. Operations and runner implementation remain separately owned.

- Original question content, IDs, difficulty labels and provisional review
  states are preserved. Added metadata does not promote them to verified.
- Original 20: `verification_status: provisional`; new 80:
  `verification_status: unverified`. Every row has `tier`,
  `capabilities_advantaged`, and the existing family field `primary_family`.
- Legacy question JSON remains parseable with metadata defaults.
- Gold remains private and independently recomputable from exported source
  bytes. No gold file was added to the agent tree.
- No Copilot/Foundry/cloud inference or live execution was performed.
- No push was performed.

Implementation commit:
`969c5fa3a9f368755a3cc4e7ecfaf9cf44a5f541`
(`Expand QA benchmark to 100 source-derived questions`).
This report is committed separately; its introducing commit is discoverable
with `git log --oneline -- agent-working/2026-10-15-fhir-question-benchmark/task-3-report.md`.
Both commits carry the requested coauthor/session trailers.

## Distribution

| Family | Tier 1 | Tier 2 | Tier 3 | Tier 4 | Tier 5 | Total |
|---|---:|---:|---:|---:|---:|---:|
| direct_facts | 14 | 0 | 0 | 0 | 0 | 14 |
| temporal_status_trends | 1 | 18 | 0 | 0 | 0 | 19 |
| cross_resource_joins | 0 | 0 | 18 | 0 | 0 | 18 |
| cohort_missing_evidence | 0 | 0 | 0 | 20 | 0 | 20 |
| owned_local_terminology | 0 | 2 | 2 | 0 | 25 | 29 |
| **Total** | **15** | **20** | **20** | **20** | **25** | **100** |

New items: 10 direct, 15 temporal/status, 14 joins, 16 cohort/missingness,
25 hard terminology. Personas across all 100: nursing 26, specialist 29,
researcher 35, informatics 7, primary-care 3. Legacy `difficulty` is preserved;
`tier` is the new scope rubric, so original cohort items retain L3 but are tier 4.

## Terminology item list

The original `term-001` through `term-004` remain, covering lookup, equivalent
mapping, multiple non-equivalent candidates and an explicitly unmatched result.
The 25 new tier-5 items are:

| Item | Capability/trap |
|---|---|
| hard-001 | Owned local-to-SNOMED-identifier synthetic translation |
| hard-002 | Owned local-to-LOINC-identifier synthetic translation |
| hard-003 | Owned local-to-RxNorm-identifier synthetic translation |
| hard-004 | No supplied ICD-10-CM target: abstain, never invent a code |
| hard-005 | Explicit reverse LOINC-identifier map group |
| hard-006 | Explicit reverse RxNorm-identifier map group |
| hard-007 | Wider target is not exact equivalence |
| hard-008 | Narrower target is not exact equivalence |
| hard-009 | Two equivalent candidates: ambiguous, return both |
| hard-010 | Unmapped local code: insufficient evidence |
| hard-011 | Transitive subsumption through owned local hierarchy |
| hard-012 | Reverse hierarchy direction is false |
| hard-013 | Separate hierarchy branches do not imply subsumption |
| hard-014 | Observation display conflicts with authoritative owned code display |
| hard-015 | Condition display borrowed from another system |
| hard-016 | Inactive concept despite familiar display: invalid_data |
| hard-017 | Reused local code X, version 1 specimen meaning |
| hard-018 | Same system/code, version 2 medication meaning |
| hard-019 | Unversioned X across conflicting releases: ambiguous/null |
| hard-020 | Same code string X in a different system |
| hard-021 | Missing specimen dependsOn context: ambiguous/null |
| hard-022 | Serum context selects serum target, excluding urine |
| hard-023 | UCUM g to kg; contradictory display unit ignored |
| hard-024 | UCUM g/dL to g/L with exact Decimal arithmetic |
| hard-025 | Mass-to-molar conversion unsupported without analyte factor: abstain |

All relationships/displays/hierarchy are owned synthetic mechanics, **not
clinically authoritative standard-vocabulary equivalence**. Source and target
system/version, map version, relation strength and dependencies are explicit.
The UCUM fragment is a small owned linear dimension/factor table, not a general
UCUM parser or an analyte-specific conversion service.

## Corpus, provenance and derivations

- Added `agent/corpus/qa-expansion.ndjson`: eight deterministic patient scenarios,
  status/time/threshold/age/encounter distractors, referenced and contained
  medications, specimens and DiagnosticReports.
- Added `agent/terminology/qa-fixtures.json`: owned CodeSystems and ConceptMap,
  version/context ambiguity, inactive/display conflicts, UCUM factor fragment,
  and a separate Bundle-scoped terminology example patient.
- Added private provenance `review/qa-provenance.json`; refreshed the corpus
  manifest, dataset hash lock, 100 review rows, protocols and selection policies.
- Added independent `qa_terminology.py` derivations and oracle operations for
  report/specimen/result provenance and repeated outpatient research cohorts.
  Unresolved or inconsistent decisive references stop derivation. Unsupported
  conversions/non-equivalent mappings abstain rather than guess.
- Null ambiguous answers are an additive response-contract extension for
  missing release/context; evidence and a bound claim are still mandatory.
- Reproduction recipe:
  `python agent-working\2026-10-15-fhir-question-benchmark\build_qa_expansion.py`.
  It generates no private gold and does not modify operations.

The session ZIP was read only to verify three already-present public identifiers.
Its SHA-256 was checked:
`56cb9e49f7ba6ad4e61c40aa80999f8c10a710823fed1becdf2502053777a521`.
Member:
`Nilda678_Wiza601_e503e9b3-d149-653b-92df-ca4a8cb68e16.json`;
member SHA-256:
`cda35fe3f92305a32f38be77864c5dcb03fd373065205ba68dd50ca6e9c1a45e`.

Observed Coding pointers within that member:

- SNOMED `390906007`: `/entry/1/resource/type/0/coding/0`
- LOINC `34117-2`: `/entry/3/resource/category/0/coding/0`
- RxNorm `310325`: `/entry/38/resource/medicationCodeableConcept/coding/0`

No source patients or standard descriptions were copied. No ICD-10-CM codes
were observed during the archive scan, so the ICD-10 translation item deliberately
tests unmapped abstention rather than fabricating a clinical crosswalk.

## Verification commands and output

Initial red run:

```powershell
python -B -m pytest -q -p no:cacheprovider tests\test_qa_expansion.py --basetemp .test-work-qa-red
```

Result: **27 failed** as expected before expansion (20 versus 100, absent
metadata/new items). Initial fixture integration exposed Bundle-local reference
scope and ambiguous-null contract mismatches; those were corrected at the
fixture/contract boundaries, not by weakening reference resolution.

Owned regression run:

```powershell
python -B -m pytest -q -p no:cacheprovider tests\test_qa_expansion.py tests\test_cli.py tests\test_contracts.py tests\test_oracle_grading.py tests\test_corpus_references.py tests\test_foundation_review_fixes.py --basetemp .test-work-qa-owned
```

Output: **123 passed in 81.47s (0:01:21)**, exit 0. Includes installed-wheel
audit, hand-derived answers and all eight complete cohort sets, version/system
boundaries, preparation privacy, plausible wrong-answer rejection, and decisive
map/hierarchy/unit/inactive mutations rejecting stale gold.

Post-commit read-only checks also passed:

```text
PASS: all original 20 question contents preserved except additive metadata.
Rebuilt 100 QA questions and 100 review rows; no gold exported.
PASS: isolated recipe rebuild preserves all 14 QA asset files byte-for-byte.
```

The recipe was executed against a temporary copy excluding operations; the
shared source data was not rewritten during these checks. A fresh post-commit
audit returned exactly the hashes and counts below.

Final audit:

```powershell
python -m fhir_question_bench audit
```

Exit 0, exact JSON output:

```json
{
  "corpus_hash": "014a3684fe4ea930b7e42465055648a2c7229cf533b275e68bddd8abe8a35977",
  "corpus_id": "fhir-question-bench-synthetic-v1",
  "dataset_hash": "e9d36716b068b1df1a79e6480be29b26c1cd5d2e92c587ce38ec47250f7ed9cc",
  "family_counts": {
    "cohort_missing_evidence": 20,
    "cross_resource_joins": 18,
    "direct_facts": 14,
    "owned_local_terminology": 29,
    "temporal_status_trends": 19
  },
  "gold_recomputed": 100,
  "protocol_count": 27,
  "question_count": 100,
  "resource_count": 170,
  "review_count": 100,
  "review_state": "provisional",
  "tier_counts": {
    "1": 15,
    "2": 20,
    "3": 20,
    "4": 20,
    "5": 25
  },
  "verification_counts": {
    "provisional": 20,
    "unverified": 80
  }
}
```

Final full-suite command:

```powershell
python -B -m pytest -q -p no:cacheprovider --basetemp .test-work-qa-full-final
```

Exit 1, final output:

```text
FAILED tests/test_runner.py::test_runner_selects_only_the_original_initial_twenty_when_data_expands
1 failed, 300 passed in 375.93s (0:06:15)
```

The remaining failure is in the concurrently owned new runner regression:
it correctly asserts 20 planned slots, then compares the episode ID set to
**all 100** loaded question IDs (`tests/test_runner.py:667`). It must compare
to the initial20 ID set instead. This file was not edited. The runner owner
was notified. An earlier full run had 47 failures/199 passes while the
runner's global initial20 guard and obsolete QA count assertions were still
present; all but the named new runner assertion were resolved by the final run.

## Ownership, rollout and limitations

- Did not edit `runner.py`, `processes.py`, `tests/test_runner.py`,
  `tests/fake_copilot.py`, operations modules, or anything under
  `data/operations`. Shared `cli.py` changes were not staged or committed.
- `configs/smoke.json` is explicitly pinned to the original 20 IDs, retaining
  60 default B/C/D episodes instead of silently planning 300.
- README QA/overall-count hunks were staged separately from peers' runner
  lifecycle and appended operations hunks. No peer files/hunks were committed.
  Explicit pathspecs were used; no add-all, stash, reset, checkout or push.
- New corpus bytes invalidate previous corpus/gold/comparison snapshots.
  In particular, unchanged question `direct-002` now recomputes **66**
  Observations, not 14; the test expectation was independently updated.
- Eight new patient scenarios and threshold/interval variants are correlated
  development fixtures. They are not 100 independent clinical tasks, a
  large-corpus stress test, or a sealed evaluation set.
- No clinician/terminologist acceptance, general FHIR validation, imported
  standard terminology release, real clinical cross-system equivalence,
  general UCUM implementation, or native specialist-tool capability is claimed.
- Verification transitions and professional review remain future work.
  The full-suite runner assertion above remains an integration concern at the
  time of this report. Live execution stays blocked.
