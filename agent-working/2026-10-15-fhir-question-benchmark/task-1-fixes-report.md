# Task 1 foundation review fixes

**Review scope:** Astra's nine Important and one Minor finding against
`68bd72b..663cf90`. Each reported failure was checked against the current
implementation before changing code. Findings were reproduced with regression
tests or matched to the reviewed code path; no user-policy decision was needed.
The 20-item dataset remains provisional and unchanged in size.

## Finding-by-finding disposition

1. **NDJSON missing from the wheel — fixed.** Added an explicit
   `data/**/*.ndjson` package-data rule. The installed-wheel test builds from a
   staged source copy, checks every dataset- and corpus-manifest file is in the
   wheel, installs it, confirms imports originate from the installed target,
   then runs `audit` from outside the checkout.

2. **Relative and contained reference context — fixed.** `Type/id` resolution
   expands against an absolute source `fullUrl` base where present, stays in
   the source Bundle for Bundle-local references, and uses unique identity
   resolution only without a stronger source context. Contained references
   resolve siblings and their owner in the top-level containing resource's
   namespace. Absolute fullUrls remain exact; no network resolution was added.
   Regression coverage includes cross-base candidates, Bundle-scoped aliases,
   contained sibling and owner references, and duplicate identities.

3. **Unresolved references incorrectly certified as complete — fixed.**
   Relevant missing or ambiguous subject, diagnosis, cohort, absence, and
   medication links now stop gold derivation with `OracleError`; known
   non-matching subjects can still be excluded. Incomplete membership can no
   longer be reported as a complete empty set or count. Negative tests cover
   missing diagnoses, duplicate patient IDs, unresolved cohort/absence
   membership, and unresolved relevant medication context.

4. **Medication join compared raw references — fixed.** Encounter patient,
   medication subject, and encounter/context links are resolved in source
   context and compared by resource source location. A known non-matching
   subject or encounter is safely excluded; a relevant unresolved link fails
   derivation. A transaction-Bundle alias regression preserves the exact two
   expected medication resource identities.

5. **Evidence required byte-for-byte locator reproduction — fixed.** Grading
   accepts an exact citation, a direct parent citation, or a complete set of
   scalar leaves for an object-valued obligation. All supplied citations still
   have to resolve and match source hashes, literals, source identity/location,
   and decisive obligations; extra or wrong-patient evidence fails. Tests
   demonstrate equivalent quantity-leaf evidence and reject a wrong source.

6. **Typed and mutable response/gold bypassed validation — fixed.** The
   grading boundary reconstructs and validates both typed and dictionary
   inputs, then checks answer type against the question. Direct construction
   and post-construction nested mutation regressions verify invalid claims,
   numeric quantity payloads, and mutated gold are rejected.

7. **Series/timestamp equality was lexical — fixed.** Series entries now have
   a validated `{time, value, unit}` shape. Decimal values compare numerically,
   timezone-aware instants compare semantically, date-only values remain
   distinct from instants, units remain exact, and series order remains
   significant. The latest-result policy rejects tied latest instants; series
   ties use the published resource-identity tiebreaker. Tests cover decimal
   scale, equivalent offsets, changed units, reversed order, and tied latest
   values.

8. **Agent-visible answer/scope rules were incomplete — fixed.** Preparation
   now exports hash-locked, versioned `response-contract.json` and
   `selection-policies.json` alongside the question, source assets, and
   protocols. Policies publish both selection criteria and completeness
   rules. The cohort policy is explicitly **final Observation only** in the
   question prompts, public protocol definitions, selection policy, oracle,
   and generated scope criteria. Prepared-only regression responses for a
   complete series and medication join are independently assembled from those
   public assets, not copied from gold.

9. **Temporal missingness could crash or hide uncertainty — fixed.** Evidence
   construction includes `dataAbsentReason` when `valueQuantity` is absent and
   never fabricates a pointer to a missing quantity. A final candidate without
   either value or absence reason, a missing status, or a potentially
   qualifying missing effective time is indeterminate and raises a clear
   `OracleError`. A known nonqualifying status remains excluded, even without
   an effective time, and its available status evidence is retained. Regressions
   cover absent quantity, missing time, and nonqualifying status.

10. **Pretty-printed gold parser fallback was unreachable — fixed.** `_read_gold`
    parses a whole JSON document first and falls back to JSONL. Both an actual
    `grade` command with pretty single-object gold and malformed-input CLI
    reporting are tested.

The former length-only answer checks for `temporal-002` and `join-001` now
assert the exact, hand-calculated series entries and medication identities.

## Verification

- `python -m pytest -q` — **43 passed**. This includes the external installed
  wheel build/install/audit smoke test.
- `python -m fhir_question_bench audit` — **20 questions**, **20 protocols**,
  **20 provisional review rows**, **27 resources**, and **20 gold records
  recomputed**.
- `python -m fhir_question_bench demo --out .\artifacts\foundation-review-demo.md`
  — **20/20 oracle self-checks**, not agent performance.
- The installed-wheel regression verifies every dataset/corpus-manifest asset
  is present, including `resources.ndjson`, installs into a separate target,
  imports from that target outside the checkout, and passes the packaged audit.
- `git diff --check` — clean.

## Interfaces and remaining limits

`load_selection_policies(data_root=None)` is now public. `prepare_question`
exports the response contract and selection/completeness policies. The grader
signature is now
`grade_response(question, gold, response, corpus, data_root=None)`; callers
grading a response against a separately prepared corpus should pass the
private grading data root explicitly. `resolve_reference` retains its
`resolved` / `missing` / `ambiguous` result contract.

The slice remains 20 synthetic engineering items, all provisional. It does not
provide full FHIR/profile validation, clinician or terminology acceptance,
live inference, cloud calls, or a runner. Prepared assets are not a process
security boundary; any later live code execution still requires the separately
specified process-level containment and spending gates. The release cap remains
300, with the existing staged gates unchanged.
