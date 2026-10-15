# FHIR foundation fixes: RR4 follow-up

This narrowly scoped pass addresses the remaining RR4 exclusion/evidence
regression from the round-two verification and isolates the older typed
response negative. RR1–RR3 were left unchanged. The 20 questions remain
provisional, the release cap remains 300, and no runner, live execution,
inference, cloud call, or dataset expansion was added.

## RR4 disposition

Observation evaluation now records an explicit candidate decision with
separate `answer_eligible`, `evidence_member`, `excluded_by`, and
`indeterminate` fields. Subject identity, cutoff time, and required status
are evaluated independently. Any known decisive exclusion (different
patient, after cutoff, or known nonqualifying status) excludes a candidate
without allowing missing fields elsewhere to abort gold derivation.

Answer candidates and evidence-scope members are kept separately. Only
patient-matching records in the evidence scope are required citations;
in-scope nonfinal records retain status evidence. A nonfinal patient-matching
candidate with a missing time can still contribute its known status exclusion
evidence, while a known other-patient or post-cutoff record cannot become
mandatory evidence. If no independent exclusion decides eligibility, missing
patient membership, time, or required status remains indeterminate and fails
closed.

The requested reproduction A now returns the unchanged `4.1 mmol/L` when
`o-potassium-other` has a known other-patient subject and missing
`effectiveDateTime`. Reproduction B now excludes that other-patient
preliminary record from mandatory evidence; a demo response without its
citations grades successfully. Conversely, a patient-matching, in-cutoff
preliminary candidate remains required evidence, and omitting its evidence
fails grading.

A 27-case table-driven matrix covers patient state (match/other/unresolved),
time (before/after/missing), and status (final/nonfinal/missing). It verifies
that independently decisive exclusions win over unrelated uncertainty and
that potentially qualifying records with unresolved membership remain
errors. Existing regressions continue to cover unresolved subjects before
cutoff (error), after cutoff (excluded), and known nonqualifying status with
missing subject/time (excluded).

## Typed-response test isolation

The earlier direct-quantity response mutation now deep-copies the demo
response, snapshots gold, asserts the expected contract-validation category,
and checks that gold is unchanged. Deliberately altered gold is tested
separately using a validly typed but stale quantity value; its rejection is
asserted as a stale-gold error, with the original gold still unchanged.

## Verification

- TDD regression run against pre-fix code: the focused run failed on the
  known-other/missing-time case, nonfinal evidence leakage, and candidate
  decision combinations, confirming the new regressions detected the reviewed
  defect.
- `python -B -m pytest -q -p no:cacheprovider tests\test_foundation_review_fixes.py::test_mutated_typed_response_and_gold_are_revalidated tests\test_foundation_review_fixes.py::test_temporal_candidate_decisions_separate_eligibility_from_evidence tests\test_foundation_review_fixes.py::test_other_patient_missing_time_does_not_invalidate_or_require_evidence tests\test_foundation_review_fixes.py::test_other_patient_nonfinal_observation_is_not_mandatory_evidence tests\test_foundation_review_fixes.py::test_in_scope_nonfinal_candidate_remains_mandatory_evidence`
  — **31 passed**.
- `python -B -m pytest -q -p no:cacheprovider tests\test_foundation_review_fixes.py`
  — **61 passed**.
- `python -B -m pytest -q -p no:cacheprovider` — **81 passed**.
- `python -B -m fhir_question_bench audit` — **20 questions**, **20
  provisional review rows**, **20 protocols**, **27 resources**, and **20
  gold records recomputed**.
- `git diff --check` — clean before commit.

## Remaining limits

This validates only the synthetic offline slice and its deterministic
selection/evidence contract. It does not establish full FHIR validation,
clinical acceptance, live execution isolation, model performance, or spending
approval. The separate process-containment and spending gates still apply.
