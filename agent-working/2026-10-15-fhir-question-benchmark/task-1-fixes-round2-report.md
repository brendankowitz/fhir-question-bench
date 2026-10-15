# FHIR foundation fixes: scoped corrective review

**Fix commit:** `9171d7ac2c03667918c20d619fc247a2a99d1d8d`

The bounded corrective pass addressed RR1–RR4 from Astra's scoped review of
`0ae8e16`. The 20 synthetic questions remain provisional; no questions or
runner capability were added, and the release cap remains 300. No live
inference, cloud calls, or model-authored process execution occurred.

## Finding dispositions

1. **RR1 — stable patient binding.** Patient-scoped temporal, condition, and
   absence operations now resolve the question's bare `Patient/id` once from
   the corpus. Missing or duplicate source locations raise an explicit
   question-patient scope error. Candidate subjects are resolved in their own
   source context and compared to that fixed source location; the requested
   patient is no longer rebound in each candidate's namespace. Cohort
   membership still resolves each candidate's subject in context and rejects
   duplicate qualifying Patient identities across distinct source locations.
   New two-Bundle regressions cover a temporal series, active conditions, and
   cohort deduplication.

2. **RR2 — contained-resource context.** A contained source's actual `#`
   owner reference now resolves to its containing resource. References from a
   contained resource inherit the owner's Bundle and REST `fullUrl` context,
   while the contained wrapper keeps its own identity and evidence pointer,
   source hash, file, and entry location. The regression uses an Observation
   at `a.test` with a contained Specimen and same-identity Patient candidates
   at `a.test` and `b.test`; `#` and `Patient/p` resolve to the owner and
   `a.test` Patient, respectively.

3. **RR3 — public scope contract.** Every public selection policy now declares
   a `scope_template`. Each template explicitly states whether a scope check
   is required or forbidden. Required templates specify resource type,
   patient reference, cutoff source, and exact criteria fields bound to
   question parameters or public policy criteria. Gold generation materializes
   its scope from that template; grading's source-recomputed gold comparison
   validates the same generated obligation. The response contract now points
   responders to these exact templates instead of the ambiguous instruction
   to combine all policy criteria and parameters. Prepared-asset regressions
   construct scopes from templates for every scope-bearing question/policy
   and confirm a scope is rejected for policies declaring it forbidden.

4. **RR4 — exclusions before membership resolution.** Observation code
   filtering is followed by decisive status exclusion and effective-time
   cutoff handling before subject membership is required. Known
   nonqualifying-status candidates remain available for status evidence, but
   missing subject/time cannot invalidate a result when status alone excludes
   the record. Out-of-cutoff records are skipped before subject resolution.
   Potentially qualifying in-scope records with unresolved subjects still
   raise `OracleError`. Paired regressions cover unresolved-subject candidates
   before and after cutoff and a known nonqualifying status with missing time.

Negative series responses now use deep copies. Tests assert specific
answer-mismatch errors and verify that grading negative cases leaves gold
unchanged.

## TDD and verification

The review reproductions were first exercised against the unfixed code:

- RR1 duplicate-Bundle patient could still produce a temporal gold series.
- RR2 `resolve_reference(specimen, "#", corpus)` returned `missing`.
- RR3 policies had no public scope template, so generic prepared-template
  construction failed because the declared contract was absent.
- RR4 a post-cutoff unresolved subject and a nonqualifying-status candidate
  both raised for missing subject; the before-cutoff case correctly remained
  indeterminate.

After implementing the changes:

- `python -B -m pytest -q -p no:cacheprovider` — **51 passed**.
- `python -B -m pytest -q -p no:cacheprovider tests\test_foundation_review_fixes.py::test_installed_wheel_audits_from_outside_checkout`
  — **1 passed**. The smoke builds a wheel, installs to an isolated target,
  verifies import origin outside the checkout, and audits packaged assets.
- `python -B -m fhir_question_bench audit` — **20 questions**, **20
  provisional review rows**, **20 protocols**, **27 resources**, and **20
  gold records recomputed**.
- `python -B -m fhir_question_bench demo --out .round2-final.md` — **20/20**
  oracle/self-checks (not measured agent performance); generated outputs were
  removed.
- `git diff --check` — clean before commit.

## Interfaces and remaining limits

The externally relevant contracts remain `resolve_reference(...)`,
`load_selection_policies(data_root=None)`, `derive_gold(question, corpus,
data_root=None)`, and `grade_response(question, gold, response, corpus,
data_root=None)`. Prepared policy assets now expose `scope_template.required`,
`resource_type`, `patient_reference`, `cutoff`, and `criteria` bindings for the
next runner milestone.

Bare duplicate `Patient/id` values intentionally remain ambiguous; this slice
does not introduce a source-qualified patient parameter. Cohort identity
deduplication rejects a qualifying identity that resolves to multiple source
locations. The dataset remains a synthetic, provisional engineering fixture;
there is no full FHIR/profile or clinical validation, no live runner, and no
process-level isolation claim. Live execution remains blocked by the separate
containment and spending gates.
