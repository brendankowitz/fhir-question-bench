# Oracle semantics and crosswalks

This document specifies how each gold oracle in `src/FhirAgentBench/Oracles/` interprets its parameters (shared definitions, extensions to the original oracles, and the newer oracles), and the crosswalk tables behind the code lists used as oracle parameters (ICD-10-CM and SNOMED CT, clinician language, lab names and LOINC, drug classes and RxNorm, vaccines and CVX). It is the reference for reviewing a question's `oracle` block or writing a new question. It deliberately contains no gold answers; gold stays out of the repo except as hashes in `questions/gold.lock.json`. A crosswalk target code or code list is inherently the answer space for the questions that use it. An independent re-derivation lives in `tools/derive-gold/`.

## Oracle specification

Six existing oracles (four of them extended) plus twelve new ones. All parameters come from the question's `oracle.parameters` map. Code values are always quoted strings; numbers are plain.

### 2.0 Shared definitions (apply to every oracle)

| Term | Definition |
|---|---|
| now | Harness-frozen `2026-08-17T00:00:00Z`. |
| patient | Patient whose identifier (system `http://hospital.smarthealthit.org`) equals `patient.mrn`. |
| window | When `window_days` (integer) is present, an event is in the window iff `now - window_days days < t <= now`. When it is absent, the window is `t <= now` (all history). |
| date answer | The calendar date `YYYY-MM-DD` of the instant **in its recorded UTC offset** (not converted to UTC). Whole-day differences (`days_since_latest`) subtract these calendar dates. The corpus only uses `+00:00`, so the two readings agree on this data. |
| latest / earliest | Order by instant; **tie-break on the smaller resource `id` (ordinal string compare)** for both directions. |
| list parameter | A parameter documented as `code-or-list` accepts a single string or a list. A resource matches when **any** of its codings (in the stated system) equals **any** listed code. |
| valid observation reading | `Observation.status` in {final, amended, corrected}; instant = `effectiveDateTime`, else `effectivePeriod.start`, else `issued`; instant `<= now`; and a listed LOINC is carried with a numeric quantity, either (a) on `Observation.code` with numeric `valueQuantity.value`, or (b) on a `component.code` with numeric `component.valueQuantity.value`. Check (a) first, then components in array order; the first match supplies the value. A resource yields at most one reading. Unit = `valueQuantity.code`, else `valueQuantity.unit`. |
| unit conversion | When `unit` is given and differs from the reading's unit, convert. Supported: `kg -> [lb_av]` (x 2.2046226218); `cm -> [in_i]` (/ 2.54); `Cel -> [degF]` (x 9/5 + 32); glucose LOINCs 2339-0/2345-7 `mg/dL <-> mmol/L` (/ or x 18.016, as existing code); creatinine LOINCs 38483-4/2160-0 `mg/dL -> umol/L` (x 88.4). For eGFR LOINC 33914-3 the corpus spells the same unit two ways; `mL/min` is treated as identical to `mL/min/{1.73_m2}` (MDRD output is always per 1.73 m²). Any other mismatch throws. |
| countable order | `MedicationRequest.status` not in {entered-in-error, cancelled, draft}; instant = `authoredOn <= now`; RxNorm codes from `medicationCodeableConcept`, else from the `Medication` resolved through `medicationReference` (`Medication.code`). The order's "code" is its **first** RxNorm coding. |
| active order | Countable order with `status = active`. ("Current" in question text means this; it is the FHIR status, not a recency rule.) |
| considered condition | `Condition.verificationStatus` not refuted / entered-in-error, with an onset (`onsetDateTime`, else `onsetPeriod.start`). Onset is the condition's instant. |
| active condition | Condition whose `verificationStatus` is not refuted / entered-in-error, whose `clinicalStatus` is not inactive / resolved / remission, whose onset is `<= now` **or absent** (unlike a considered condition, no onset is required), with no `abatementBoolean = true` and no `abatementDateTime` / `abatementPeriod.end <= now`. |
| living | `Patient.deceasedDateTime` absent or `> now`, and `deceasedBoolean` not true. |
| age | Full years between `Patient.birthDate` and the reference date (birthday not yet reached → minus one). |
| rounding | "round n" = round half away from zero to n decimals. Unrounded values are compared with the question's tolerance. |
| no data | Unless a rule says otherwise, an oracle returns `null` when nothing qualifies; a `count` returns 0. |

### 2.1 Existing oracles and their extensions

**latest-observation** (extended). Parameters: `loinc` code-or-list (**now a list allowed**: the question means the analyte, so Blood and Serum/Plasma LOINCs are both listed); `window_days` int optional (**new**); `unit` string optional (**new**, conversion per 2.0); `return` one of `value_date` (default), `value`, `code`, `components` (**new**); `components` map name → LOINC (required for `return: components`).
- `value_date`: latest valid reading → `{value: {value, unit}, date}`.
- `value`: latest valid reading → `{value, unit}` (quantity).
- `code`: among observations with valid status, instant in window and a listed LOINC on `Observation.code` **and** a `valueCodeableConcept`, take the latest → its first coding `{system, code}`.
- `components`: among observations with valid status, instant in window and `loinc` on `Observation.code`, keep those where **every** named component LOINC has a numeric `component.valueQuantity`; take the latest → `{<name>: {value, unit}, ..., date}`.
- Used by: t1-latest-lab-003, t1-latest-bp-001/002, t1-smoking-status-001/002, t1-latest-analyte-001/002, t2-latest-lab-window-001/002, t3-unit-convert-001/002/003, t4-lay-lab-001.

**observation-window-stats** (extended). Parameters: `loinc` code-or-list (**list allowed**; component-carried LOINCs such as systolic 8480-6 now count, per 2.0); `window_days` int; `unit` string; `statistic` in count | mean | max | min | change (**new**) | percent_change (**new**); `comparator` (gt | ge | lt | le) + `threshold` number, optional (**new**): applied to the converted value before the statistic.
- count → integer (0 when none). mean → round 2. max / min → unrounded. change → value(latest) − value(earliest), round 2. percent_change → (latest − earliest) / earliest × 100, round 2. change / percent_change need at least 2 readings, else null; percent_change is also null when the earliest reading is 0.
- Used by: t2-window-max-lab-002/003, t3-window-mean-lab-002/003, t3-window-count-lab-002/003, t3-observation-change-001/002/003.

**ever-on-medication** (extended). Parameters: `rxnorm` list; `window_days` int optional (**new**; filters on `authoredOn`); `return` in ever_first (default) | first_date (**new**) | latest_date (**new**) | order_count (**new**). Matching set = countable orders in window with a listed RxNorm code.
- ever_first → `{ever: bool, first: date|null}` (unchanged). first_date / latest_date → date of the earliest / latest matching order. order_count → number of matching MedicationRequest resources (refills are separate orders and each counts).
- Used by: t2-med-order-date-001/002/003, t3-med-order-count-001, t4-ever-on-class-002/003.

**medications-started-within** (unchanged). Used by: t2-meds-started-window-002.

**cohort-lab-above-or-missing** (extended). Parameters: `snomed` list; `loinc` code-or-list (**list allowed**); `comparator` gt (default) | ge | lt | le (**new**); `threshold`; `window_days`; `age_min`, `age_max` (inclusive, age at now); `unit`. Counts living patients in the age band with an active condition in `snomed` whose latest valid reading in the window satisfies `value <comparator> threshold`, **or** who have no reading in the window.
- Used by: t6-ldl-not-at-goal-001.

**unanswerable** (unchanged). Returns the `insufficient_data` status with no answer. Used by: t7-goal-target-001, t7-family-history-001, t7-pack-years-001, t7-code-status-001.

### 2.2 New oracles

**condition-status** — patient-level questions about diagnoses.
- Parameters: `snomed` list (not used with `active_count`); `window_days` optional (filters on onset); `return` in first_onset | latest_onset | count | age_at_first_onset | active_count; `exclude_snomed` list (only with `active_count`).
- Matching set = considered conditions (2.0) with a listed SNOMED code and onset in window.
- first_onset / latest_onset → date of the earliest / latest onset. count → number of matching Condition **resources** (each recorded episode counts once). age_at_first_onset → age on the earliest onset date. active_count → number of **distinct** SNOMED codes (first coding) among all active conditions, minus codes in `exclude_snomed`.
- Answer: date, integer. Null when no match (except count/active_count → 0).
- Used by: t1-first-diagnosis-001, t2-first-diagnosis-002/003, t2-latest-episode-001, t3-condition-episodes-001/002, t4-problem-count-001, t5-age-at-diagnosis-001/002.

**condition-crosswalk** — "which code should I bill" translation questions.
- Parameters: `source_snomed` list (every corpus SNOMED code that falls under the target code; see appendix A); `target_system`; `target_code` (the standard crosswalk value, justified in appendix A).
- Semantics: if the patient has an active condition with a code in `source_snomed`, answer `{system: target_system, code: target_code}`; otherwise null. Evidence = the matching Condition(s).
- Answer: code. Used by: t4-icd-crosswalk-001/002/003.

**active-medications** — current orders within a drug class.
- Parameters: `rxnorm` list; `return` optional, only `codes` is accepted (and is the default).
- Semantics: active orders (2.0) whose code is listed → set of distinct `{system: RxNorm, code}` using each order's first RxNorm coding. Empty set when none.
- Answer: set. Used by: t4-current-class-001/002/003.

**medication-reason** — reference hop MedicationRequest.reasonReference → Condition.
- Parameters: `rxnorm` list.
- Semantics: walk countable orders with a listed code from latest to earliest (tie-break 2.0); the first order with a `reasonReference` that resolves to a Condition supplies the answer = that Condition's first `code` coding `{system, code}`. Null when none resolves.
- Answer: code. Used by: t4-med-indication-001/002.

**observation-before-medication** — baseline lab before a drug start.
- Parameters: `loinc` code-or-list; `rxnorm` list; `unit` optional.
- Semantics: anchor = `authoredOn` of the earliest countable order with a listed code. Answer = latest valid reading with instant `<= anchor` (same-instant counts) → `{value: {value, unit}, date}`. Null if there is no order or no reading.
- Used by: t4-lab-before-med-001/002.

**immunization-history**
- Parameters: `cvx` list; `window_days` optional; `return` in latest_date | first_date | count | days_since_latest.
- Matching set: `Immunization.status = completed`, `occurrenceDateTime` in window, a `vaccineCode` coding with system `http://hl7.org/fhir/sid/cvx` in the list.
- latest_date / first_date → date. count → number of Immunization resources. days_since_latest → `now.date − latest.date` in whole days.
- Used by: t1-vaccine-date-001/002, t2-vaccine-date-003, t2-vaccine-in-window-001, t3-vaccine-count-001/002, t4-vaccine-brand-001/002, t5-days-since-001.

**procedure-history**
- Parameters: `snomed` list; `window_days` optional; `return` in latest_date | count.
- Matching set: `Procedure.status = completed`, instant = `performedDateTime` else `performedPeriod.start`, in window, SNOMED code listed.
- Used by: t2-last-procedure-001/002/003, t3-procedure-count-001.

**encounter-query**
- Parameters: `class` list of v3-ActCode codes (optional); `type_snomed` list (optional); `window_days` optional; `return` in count | latest_date.
- Matching set: `Encounter.status` not cancelled / entered-in-error; instant = `period.start` in window; `class.code` listed (if given); any `type` coding SNOMED-listed (if given).
- Answer: integer, date. Used by: t2-last-visit-type-001/002, t3-encounter-count-001/002.
- A clinician, facility or other far resource of an encounter is reached with `reference-path`, not here.

**reference-path** — select one resource of the patient, walk reference hops, project one value.
- Parameters:
  - `start`: `{type, <criterion>: <code-or-list>, ..., window_days?, pick}`.
  - `path`: a list of steps, possibly empty or absent.
  - `project`: one projection, or a map of field name to projection.
- **Start selector.** The candidates are the patient's resources of `type` (those in the patient's bundle). A candidate qualifies when it passes the type's validity rule, has an instant that is `<= now` and inside `window_days` when given (2.0), and, for **each** criterion given, carries any listed code (list rule, 2.0). `pick` is `latest` or `earliest` by instant, with the 2.0 tie-break on the smaller `id` in both directions.

| Type | Validity | Instant | Criteria and the codes compared |
|---|---|---|---|
| MedicationRequest | countable order (2.0) | `authoredOn` | `rxnorm`: RxNorm codes of `medicationCodeableConcept`, else of the Medication resolved through `medicationReference` |
| Encounter | status not cancelled / entered-in-error | `period.start` | `class`: `class.code`; `type_snomed`: SNOMED codings of any `type` |
| DiagnosticReport | status final / amended / corrected | `effectiveDateTime`, else `effectivePeriod.start`, else `issued` | `loinc`: LOINC codings of `code` |
| Observation | status final / amended / corrected | as the valid observation reading (2.0) | `loinc`: LOINC codings of `code` (components are not searched) |
| Condition | considered condition (2.0) | onset | `snomed`: SNOMED codings of `code` |
| Procedure | status completed | `performedDateTime`, else `performedPeriod.start` | `snomed`: SNOMED codings of `code` |
| Immunization | status completed | `occurrenceDateTime` | `cvx`: CVX codings of `vaccineCode` |

  Practitioner, PractitionerRole, Organization, Location, Medication and Patient have no validity rule or instant. They are valid hop targets only, and a hop to them may not carry criteria. An unknown type, a start type without an instant, or a criterion not listed for its type is a configuration error: the oracle throws and `admit` fails.
- **Path steps**, applied in order to the current resource:
  - Forward, `{ref: <element path>, type?: <Type>, <criterion>?: <codes>}`. The element path is dot-separated plain element names (`requester`, `participant.individual`, `result`). Walk it, flattening arrays in document order, and collect the Reference objects (objects with a `reference` string). Try them in that order. Skip a reference that is unresolved, whose target has a different `resourceType` than `type` (when given), or whose target fails its type's validity rule, instant `<= now` or the step's criteria. The first target not skipped becomes current. If none is left the answer is null.
  - Reverse, `{rev: <Type>.<element path>, pick: latest | earliest, <criterion>?: <codes>}`. The candidates are the patient's resources of `Type` with a Reference at the element path that resolves to the current resource itself. They qualify exactly as start candidates (validity, instant `<= now`, criteria, no window) and `pick` chooses among them with the 2.0 tie-break. No qualifying candidate gives null.
- **Reference resolution** for a reference string `r` found in a resource `R`:
  1. `#id` is the resource in `R.contained` with that id; none means unresolved.
  2. `r` equal to a Bundle entry `fullUrl` anywhere in the corpus is that entry's resource.
  3. `Type?identifier=system|value` is the resource of `Type`, anywhere in the corpus, with an `identifier` whose `system` and `value` both match exactly. None means unresolved. **More than one match is a corpus error: the oracle throws.**
  4. `Type/id` is the resource of that type and id; none means unresolved.
  5. Anything else (absolute URLs, other search parameters, a Reference with only `identifier` or `display`) is unresolved.
  The `display` of a reference is never used.
- **Projections** on the final resource:

| Projection | Result |
|---|---|
| `name` | Practitioner / Patient: the first `name` with `use = official`, else the first `name`; its `given` entries then `family`, joined by single spaces. `prefix` and `suffix` are omitted, and `text` is used only when there is neither given nor family. Organization / Location: the `name` string. |
| `value` | `valueQuantity` as `{value, unit}`, unit = `code`, else `unit`. Null when the value is not numeric or there is no unit. |
| `date` | the type's instant as a date answer (2.0). |
| `address.<field>` | the field of the first `address` entry (or of the single address object of a Location), for example `address.city`. |
| `identifier:<system>` | the first identifier with that system as `{system, value}`. |
| `code:<system>` | the first coding of `code` (or `vaccineCode`) in that system as `{system, code}`. |

  A map projection gives a composite. If any field projects to null the whole answer is null, because composites have no partial gold.
- **Missing data.** There is no fallback to an older start resource. Once the start is picked, a failed hop or a null projection gives null, and `admit` rejects a null gold on a question not marked `expect_empty`. That catches a broken path at authoring time instead of silently answering about a different visit. (`medication-reason` keeps its own walk-back semantics; it is a different oracle.)
- **Evidence.** The start resource and every resource reached by a hop, in path order.
- Answer: any type the projection produces; `name` for a person or organisation. Used by: t4-med-prescriber-001, t4-class-prescriber-001, t4-prescription-facility-001, t4-panel-result-001/002, t4-admission-facility-001, t4-admission-med-prescriber-001, t4-encounter-clinician-001.

**allergy-check**
- Parameters: `substances` list of `"system|code"` strings (optional; absent = all); `category` (optional: food | medication | environment | biologic); `return` in any | codes.
- Matching: `AllergyIntolerance.clinicalStatus` active or absent; verificationStatus not refuted / entered-in-error; `category` contains the parameter (if given); a `code` coding equal to a listed (system, code) pair (if given).
- any → boolean. codes → set of distinct `{system, code}` of matching codings.
- Used by: t1-drug-allergies-001, t4-drug-class-allergy-001.

**clinical-calculation** — stated-rule calculations. `formula` selects one of:
- `bmi`: latest valid weight (29463-7, kg) and latest valid height (8302-2, cm), chosen independently; `kg / (cm/100)²`, round 1. Null if either is missing.
- `ckd-epi-2021`: Scr = latest valid creatinine over 38483-4 and 2160-0 (mg/dL); age = age on Scr's date; female iff `Patient.gender = female`. eGFR = 142 × min(Scr/κ,1)^α × max(Scr/κ,1)^−1.200 × 0.9938^age × (1.012 if female); κ = 0.7 (F) / 0.9 (M); α = −0.241 (F) / −0.302 (M). Round 1.
- `kdigo-g-stage`: eGFR from `source: observed` (latest valid reading of **LOINC 33914-3 only**, unrounded; `mL/min` and `mL/min/{1.73_m2}` treated as the same unit per 2.0) or `source: ckd-epi-2021` (unrounded formula above). Map: ≥90 → 431855005 (CKD stage 1); 60–<90 → 431856006 (stage 2); 45–<60 → 700378005 (stage 3A); 30–<45 → 700379002 (stage 3B); 15–<30 → 431857002 (stage 4); <15 → 433146000 (stage 5). Answer `{system: http://snomed.info/sct, code}`.
- `cockcroft-gault`: CrCl = (140 − age at now) × latest weight kg / (72 × latest Scr mg/dL), × 0.85 if female. Scr and weight as above. Round 1.
- `acc-aha-2017-bp`: parameters `readings` (int) and `window_days` (int, optional, **new in revision 1**). Take the `readings` most recent valid 85354-9 panels that have numeric 8480-6 and 8462-4 components and whose instant is in the window (latest first, tie-break 2.0). Mean systolic and mean diastolic → each round 1; `stage1_or_higher` = unrounded mean systolic ≥ 130 **or** unrounded mean diastolic ≥ 80. Null if fewer qualifying panels than `readings`. Answer composite `{systolic, diastolic, stage1_or_higher}`.
- `cha2ds2-vasc`: +1 active condition in `chf`; +1 active in `hypertension`; +2 if age at now ≥ 75, else +1 if ≥ 65; +1 active in `diabetes`; +2 any considered condition (ever) in `stroke_tia`; +1 any considered condition (ever) in `vascular`; +1 if female. Integer.
- `lab-gap`: latest valid reading over `loinc` (in `unit`) minus `target`, round 2. Negative means below target.
- Used by: t5-bmi-calc-001/002, t5-ckd-epi-001/002, t5-kdigo-stage-001 (`source: observed`; `source: ckd-epi-2021` is implemented but currently unused), t5-cockcroft-gault-001/002, t5-bp-class-001, t5-cha2ds2-vasc-001, t5-ldl-gap-001.

**cohort-count** — generic AND-of-criteria count over living patients.
- Parameters (all optional, all must hold): `age_min` / `age_max` (inclusive, age at now); `conditions_any` list (≥ 1 active condition listed); `medications_active_any` list (≥ 1 active order listed); `immunization_any` `{cvx: list, window_days?: int}` (≥ 1 matching completed immunization, per immunization-history); `lab` `{loinc, window_days, comparator, threshold, unit, missing?}`: the latest valid reading in the window satisfies the comparison; a patient with no reading is excluded unless `missing: count`.
- Answer: integer. Used by: t6-icd-cohort-001/002, t6-class-cohort-001, t6-vaccine-cohort-001/002, t6-lab-cohort-001.

## Answer type: name

JSON shape: a non-empty string. The answer spec declares `kind: person` (a Practitioner or Patient) or `kind: text` (an organisation, location or town). Prompt wording: a person is `"<given name(s)> <family name>"` (a JSON string; titles such as Dr. are optional), text is `"<name as recorded>"` (a JSON string). The gold is the oracle's `name` projection. Both gold and agent strings are normalised, then compared token by token.

Common step: Unicode NFKD, drop combining marks, lower-case, and map U+2019 to `'`.

`kind: person`:
1. Split on commas into parts. Tokenise each part by replacing every character other than a letter, digit, underscore, whitespace, `'` or `-` with a space, then splitting on whitespace.
2. While there are at least two parts and the last part is only suffix tokens, drop it. Suffixes: md, do, phd, np, pa, rn, dds, dmd, jr, sr, ii, iii, iv.
3. Exactly two parts are read as `Family, Given` and reordered to given then family. One part keeps its order. Three or more are concatenated in order.
4. Drop leading title tokens (dr, doctor, mr, mrs, ms, miss, mx, prof, professor), then trailing suffix tokens.
5. Strip trailing digits from each token and drop tokens left empty. Synthea appends digits to every name; they are ignored.
6. Match iff the token lists are equal. Every given name and the family name must be present, in order. A reversed order without a comma fails.

`kind: text`:
1. Replace `&` and `+` with ` and `, and delete `'` and `"`.
2. Replace any other non-word character with a space and split on whitespace.
3. Drop one leading `the`, then drop trailing legal-form tokens (inc, incorporated, llc, llp, ltd, corp, corporation, co, pc, pllc, lp).
4. Match iff the token lists are equal. Digits are kept. There is no abbreviation expansion: `Rehab` does not match `Rehabilitation`.

Accepted consequence: because digits are ignored, a person's name passes with different Synthea digits. That is safe only while no two corpus Practitioners collide once digits are stripped; re-check this when the corpus is regenerated. Organisation names repeat in the corpus, so a question must not have a duplicated name as its answer.

## Crosswalk appendix

Every list below is used verbatim as an oracle parameter. General rules:
- Lists are **closed over the corpus**: they contain every code in the pinned corpus that falls under the clinical concept, plus a few standard codes for the same concept that do not occur (harmless, and they keep the list correct if the corpus is regenerated). Codes marked † do not occur in the corpus.
- RxNorm class lists were built by ingredient: an RxNorm product is in a class when its ingredient belongs to the stated ATC class. Brand names are mapped to their RxNorm ingredient, then to every corpus product with that ingredient (the corpus holds generic SCDs, so brand text never appears).
- ICD-10-CM targets use system `http://hl7.org/fhir/sid/icd-10-cm`. SNOMED to ICD-10-CM direction follows the NLM SNOMED CT to ICD-10-CM map and the ICD-10-CM Official Guidelines. Where the map is one-to-many, every corpus SNOMED code under the ICD-10 code is listed.

### A. ICD-10-CM ↔ SNOMED CT (conditions)

| ICD-10-CM | Meaning | SNOMED codes in scope (corpus) | Excluded on purpose (and where they map instead) | Used by |
|---|---|---|---|---|
| I10 | Essential (primary) hypertension | 59621000 Essential hypertension; 38341003 Hypertensive disorder † | Hypertension with CKD codes to I12.- and with heart failure to I11.- (ICD-10-CM guideline I.C.9.a presumes the link), so the I10 answer is only used for a patient with neither (Susan422 Barrows492: no CKD, no heart failure) | t1-first-diagnosis-001, t4-icd-crosswalk-001 |
| E78.5 | Hyperlipidemia, unspecified | 55822004 Hyperlipidemia | 302870006 Hypertriglyceridemia → E78.1; E78.0 (pure hypercholesterolemia) is the near miss for the lay phrase "high cholesterol" because the record says hyperlipidemia | t4-icd-crosswalk-002 |
| R73.03 | Prediabetes | 714628002 Prediabetes | 80394007 Hyperglycemia → R73.9 | t4-icd-crosswalk-003 |
| E11.- | Type 2 diabetes mellitus (any) | 44054006 Diabetes mellitus type 2 (E11.9); 127013003 Disorder of kidney due to diabetes mellitus (E11.29; type unstated defaults to E11 per guideline I.C.4.a.2); 90781000119102 Microalbuminuria due to T2DM (E11.29); 157141000119108 Proteinuria due to T2DM (E11.29); 368581000119106 Neuropathy due to T2DM (E11.40); 1551000119108 Nonproliferative diabetic retinopathy due to T2DM (E11.329) | 714628002 Prediabetes → R73.03; 80394007 Hyperglycemia → R73.9; 427089005 Diabetes due to cystic fibrosis → E08.-; 237602007 Metabolic syndrome → E88.81 | t2-first-diagnosis-002, t5-age-at-diagnosis-002, t5-cha2ds2-vasc-001 (D), t6-icd-cohort-001 |
| I25.- | Chronic ischemic heart disease | 414545008 Ischemic heart disease (I25.9); 399211009 History of myocardial infarction (I25.2 Old MI) | Acute MI 401314000 NSTEMI → I21.4, 401303003 STEMI → I21.3, 22298006 MI → I21.9 (guideline I.C.9.e: acute within 4 weeks); 274531002 Abnormal findings on diagnostic imaging of heart → R93.1; 399261000 History of CABG → Z95.1. | t6-icd-cohort-002, t6-ldl-not-at-goal-001 |

### B. Clinician language → SNOMED CT (conditions, procedures, encounter types)

| Phrase | Codes | Justification | Used by |
|---|---|---|---|
| "high blood pressure" | 59621000 | Lay term for essential hypertension; the only hypertension concept in the corpus | t5-age-at-diagnosis-001 |
| "coronary artery disease", "coronary disease" | 414545008 Ischemic heart disease | The corpus records coronary disease only as 414545008 (Synthea's coronary heart disease module); 53741008 Coronary arteriosclerosis † is a descendant that never occurs | t2-first-diagnosis-003, t5-ldl-gap-001 (context only) |
| "diabetes, not prediabetes" / "type 2 diabetes" | E11 list (A) | Prediabetes is a separate concept (R73.03) | t2-first-diagnosis-002, t5-age-at-diagnosis-002 |
| "sinus infection", not chronic | 444814009 Viral sinusitis; 75498004 Acute bacterial sinusitis; 36971009 Sinusitis | Acute sinusitis concepts; 40055000 Chronic sinusitis excluded as the question says | t2-latest-episode-001 |
| "strep throat" | 43878008 Streptococcal sore throat | Direct concept; 195662009 Acute viral pharyngitis is not strep | t3-condition-episodes-001 |
| "bladder infection" | 307426000 Acute infective cystitis; 38822007 Cystitis † | Cystitis = bladder infection. 197927001 Recurrent UTI is a chronic label recorded on the same day as an episode, so counting it would double-count | t3-condition-episodes-002 |
| "social-determinant ... and administrative 'Medication review due'" (excluded from problem count) | 314529007, 160903007, 160904001, 73438004, 741062008, 224299000, 473461003, 224295006, 73595000, 422650009, 423315002, 706893006, 424393004, 160968000, 266948004, 105531004, 32911000, 266934004, 713458007, 10939881000119105, 361055000, 446654005, 1187604002 | Synthea SDOH findings ; obesity (162864005), chronic pain findings and prediabetes are clinical and stay in | t4-problem-count-001 |
| heart failure (CHA2DS2-VASc C) | 88805009 Chronic congestive heart failure; 84114007 Heart failure †; 42343007 Congestive heart failure † | Standard CHF concepts | t5-cha2ds2-vasc-001 |
| stroke / TIA (S2) | 230690007 Cerebrovascular accident; 266257000 TIA †; 422504002 Ischemic stroke † | S2 also covers systemic thromboembolism in the full score; the question scopes S2 to recorded stroke/TIA explicitly (the corpus's only thromboembolic code is DVT 132281000119108, which is not systemic arterial embolism) | t5-cha2ds2-vasc-001 |
| vascular disease (V: prior MI) | 22298006, 401314000, 401303003, 399211009 | CHA2DS2-VASc V = prior MI, PAD or aortic plaque; no PAD or aortic-plaque codes exist in the corpus, and the question scopes V to recorded MI explicitly | t5-cha2ds2-vasc-001 |
| "colonoscopy" | 73761001 | Only colonoscopy concept in corpus | t2-last-procedure-001 |
| "an echo of any kind" | 40701008 Echocardiography; 433236007 Transthoracic echocardiography; 105376000 Transesophageal echocardiography | Echocardiography and its descendants | t2-last-procedure-002 |
| "dialysis session" | 265764009 Renal dialysis; 302497006 Hemodialysis †; 71192002 Peritoneal dialysis † | Renal dialysis and its subtypes | t2-last-procedure-003, t3-procedure-count-001 |
| "ED visit" / "inpatient admission" | Encounter.class EMER / IMP (v3-ActCode) | FHIR encounter class | t2-last-visit-type-001/002, t3-encounter-count-001, t4-encounter-clinician-001, t4-admission-facility-001, t4-admission-med-prescriber-001 |
| "urgent care visit" | Encounter.type 702927004 Urgent care clinic | Synthea files urgent care as class AMB with this type, so a class-based (EMER) search is wrong | t3-encounter-count-002 |
| KDIGO G category → SNOMED | G1 431855005; G2 431856006; G3a 700378005; G3b 700379002; G4 431857002; G5 433146000 | SNOMED CT "Chronic kidney disease stage N" concepts; 3A/3B have their own concepts below 433144002 (stage 3) | t5-kdigo-stage-001 |

### C. Lab names → LOINC

| Phrase | LOINC | Justification | Used by |
|---|---|---|---|
| creatinine | 38483-4 (Blood), 2160-0 (Serum/Plasma) | Same analyte, two specimen LOINCs in the corpus. In five patients the two LOINCs disagree, so creatinine questions use patients with only plausible single-LOINC values | t3-unit-convert-003, t5-ckd-epi-001/002, t5-cockcroft-gault-001/002 |
| sodium | 2947-0 (Blood), 2951-2 (Serum/Plasma) | Same analyte; values agree whenever both are drawn together | t1-latest-analyte-001 |
| "blood sugar", glucose | 2339-0 (Blood), 2345-7 (Serum/Plasma) | Same analyte | t1-latest-analyte-002, t3-window-mean-lab-003 |
| potassium, "K" | 6298-4 (Blood), 2823-3 (Serum/Plasma) | Same analyte | t2-latest-lab-window-001 |
| LDL | 18262-6 (direct), 13457-7 † (calculated), 2089-1 † | LDL cholesterol in serum/plasma | LDL questions |
| "kidney function number - the filtration rate" (eGFR) | 33914-3 (MDRD, corpus), 48642-3 †, 48643-1 †, 62238-1 † (CKD-EPI), 98979-8 † (CKD-EPI 2021), 69405-9 †, 50044-7 †, 88293-6 †, 88294-4 †, 77147-7 † | eGFR LOINC family | t4-lay-lab-001, t2-window-max-lab-003; t5-kdigo-stage-001 uses 33914-3 only |
| "urine microalbumin ... albumin-to-creatinine ratio" | 14959-1 Microalbumin/Creatinine [Mass Ratio] in Urine | The corpus's only urine albumin measure; 14957-5 (microalbumin mass concentration, †) is the naive near miss | t2-latest-lab-window-002 |
| systolic / diastolic BP | 8480-6 / 8462-4 as components of panel 85354-9 | Corpus has no standalone 8480-6 observations | BP questions |
| smoking status | 72166-2 (value SNOMED: 266919005 never, 8517006 ex-smoker, 449868002 daily smoker) | | t1-smoking-status-001/002 |
| "lipid panel" | counted as one LDL 18262-6 result per panel | Each Lipid panel DiagnosticReport 57698-3 carries exactly one LDL | t3-window-count-lab-002 |

### D. Drug classes and brands → RxNorm (corpus products)

| Phrase | RxNorm codes | Justification (ingredient → ATC class) | Used by |
|---|---|---|---|
| statin | 312961, 314231 (simvastatin); 904475 (pravastatin); 859751, 859747 (rosuvastatin); 617310, 617311 (atorvastatin) | ATC C10AA HMG-CoA reductase inhibitors (same list as existing t4-ever-statin-001) | t4-current-class-003, t4-lab-before-med-002, t6-class-cohort-001 |
| "Lipitor" | 617310, 617311 | Brand Lipitor → ingredient atorvastatin → corpus atorvastatin products | t2-med-order-date-003 |
| "Glucophage" | 860975 (24 HR metformin ER 500 mg) | Brand Glucophage → ingredient metformin; the only metformin product in the corpus | t2-med-order-date-001 |
| "blood pressure medicines" | ACE inhibitors 314076, 314077 (lisinopril); ARB/ARNI 979492 (losartan), 1656356 (sacubitril/valsartan); beta blockers 866412, 866436 (metoprolol succinate), 866511, 866924, 866514 (metoprolol tartrate), 200033 (carvedilol); CCB 308136 (amlodipine), 897685 (verapamil); diuretics 310798 (HCTZ), 313988, 1719286 (furosemide) | ATC C09, C07, C08, C03 | t4-current-class-001 |
| "water pill" | 310798 (HCTZ), 313988, 1719286 (furosemide) | Lay term for diuretic, ATC C03 | t2-med-order-date-002 |
| anticoagulant ("blood thinner such as warfarin or heparin, not an antiplatelet") | 855332 (warfarin); 1659263, 1361048, 1361226 (heparin); 854235, 854252 (enoxaparin) | ATC B01AA / B01AB. Alteplase 1804799 (thrombolytic, B01AD) and antiplatelets excluded, as the question says | t4-ever-on-class-002 |
| antiplatelet | 309362, 749196 (clopidogrel); 243670, 2563431 (aspirin 81 mg), 212033 (aspirin 325 mg); 855812 (prasugrel) | ATC B01AC | t4-current-class-002 |
| opioid | 245134, 1735006 (fentanyl); 856987, 857005, 1860491 (hydrocodone); 1049221, 1049625, 1860154 (oxycodone); 861467 (meperidine); 835603 (tramadol); 1723208 (alfentanil); 1729584 (remifentanil); 993770 (codeine); 1728805 (morphine); 351266 (buprenorphine/naloxone) | ATC N02A opioid analgesics, N01AH opioid anaesthetics, and buprenorphine (a partial opioid agonist, ATC N07BC51). Buprenorphine is a deliberate near miss | t3-med-order-count-001, t4-ever-on-class-003 |
| insulin | 106892 (Humulin 70/30), 311034 (regular human insulin) | ATC A10A | t4-med-indication-001, t4-lab-before-med-001 |
| alendronate | 904419 | Named generic | t4-med-indication-002 |
| penicillin allergy | RxNorm ingredients 7984 (penicillin V), 7980 (penicillin G) †, 723 (amoxicillin) †, 733 (ampicillin) †, 70618 (penicillin) † | Penicillin-class ingredients as recorded on AllergyIntolerance.code; the corpus records drug allergies by RxNorm ingredient | t4-drug-class-allergy-001 |

### E. Vaccine names and brands → CVX

| Phrase | CVX | Justification | Used by |
|---|---|---|---|
| "tetanus shot", "tetanus-containing vaccine" | 113, 115, 09, 138, 139, 196, 20, 01, 22, 50, 102, 106, 107, 110, 120, 146, 28, 35, 112, 142, 130, 132, 170 | Every CVX code whose vaccine contains tetanus toxoid (Td, Tdap, DTaP, DT, DTP and combinations); corpus has 113 (Td), 115 (Tdap), 20 (DTaP) | t1-vaccine-date-001, t5-days-since-001 |
| "shingles shot" | 121 (zoster live, Zostavax), 187 (recombinant, Shingrix) , 188 † (unspecified) | All zoster CVX codes; corpus patients have 121 | t1-vaccine-date-002, t6-vaccine-cohort-002 |
| "Pneumovax 23" | 33 (PPSV23) | Brand → CVX; 133 (PCV13, Prevnar 13) is the near miss | t4-vaccine-brand-001 |
| "pneumococcal vaccine of any type" | 33, 133, 100 †, 109 †, 152 †, 215 †, 216 † | All pneumococcal CVX codes | t6-vaccine-cohort-001 |
| "Gardasil" | 62 (HPV quadrivalent, Gardasil), 165 † (HPV9, Gardasil 9) | Brand → CVX; Cervarix (118) is not Gardasil | t4-vaccine-brand-002 |
| "flu shot" | 140, 141, 150, 158, 88, 135, 153, 155, 166, 168, 171, 185, 186, 197, 205 | Influenza CVX codes; corpus has 140 only | t2-vaccine-in-window-001, t3-vaccine-count-001 |
| "COVID shot" | 207, 208, 212, 210, 211, 213, 217, 218, 219, 221, 228, 229, 230, 300, 301, 302, 309, 310, 311, 312, 313 | SARS-CoV-2 CVX codes; corpus has 207 (Moderna), 208 (Pfizer), 212 (Janssen) | t2-vaccine-date-003 |
| "hepatitis B vaccine" | 43, 08, 44 †, 45 †, 189 †, 220 † | Hep B CVX codes (adult, pediatric, dialysis, unspecified, Heplisav-B, PreHevbrio) | t3-vaccine-count-002 |
