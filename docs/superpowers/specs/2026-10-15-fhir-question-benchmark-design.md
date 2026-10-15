# FHIR Question Bench: investigation and proposed design

**Status:** Proposed; research completed, implementation not started.
**Research date:** 2026-10-15 UTC (2026-10-14 in the user's local time).
**Scope:** 1,000 original, professionally relevant questions over synthetic FHIR
folders, with executable answer keys and auditable supporting evidence.

## User-directed delivery update: reviewed increments and operational tasks

On 2026-10-15 UTC, the user requested Astra review, expansion from the initial
20-item engineering slice to 100 questions, evaluation and fixes, then expansion
by 100 questions per iteration until 1,000. The last 100 items are operational.
This update supersedes the original read-only-only scope and 1,000-question
category allocation below; the original allocation remains research history,
not the active dataset quota.

The active target is **900 patient/cohort question-answering items plus 100
operational items**. Proposed primary-family counts for the first 900 are:
orientation/navigation 70; exact retrieval 90; longitudinal/temporal 130;
medication/allergy 110; terminology 160; cohorts/aggregates 130;
research phenotypes 70; quality/provenance 50; missing evidence/abstention 40;
professional synthesis 50. These sum to 900. Difficulty/persona/source-layer
cross-tabs must be rebalanced and frozen before the relevant batch is evaluated;
the original 1,000-item cross-tabs are not simultaneously binding.

Delivery totals are 20, 100, 200, 300, 400, 500, 600, 700, 800, 900, and 1,000.
At each gate, inspect new items and the cumulative set, evaluate the runnable
agent arms, review failures, fix the dataset/harness/tools as appropriate, and
rerun affected cases plus a fixed regression panel before adding the next batch.
Astra reviews the initial implementation and consequential subsequent changes.
No unresolved important correctness or evidence-integrity finding may silently
carry into the next gate. Expert review and paid-run approvals remain separate
gates; generated volume does not satisfy either.

The final operational batch is proposed as 25 de-identification tasks, 25 FML
transform tasks, 25 SQL-on-FHIR ViewDefinition tasks, and 25 validation tasks.
Each is an **execute-and-verify** episode, not a question about what command to
run. Supply explicit configuration/maps/views/profiles and required capabilities
equally across arms. Verify actual capability support before scheduling runs.
Do not imply the current three-tool MCP prototype exposes these operations.

Operational gold includes deterministic artifact/invariant checks:

- **De-identification:** configured identifier transformations, removal of
  specified synthetic identifiers, consistent reference rewriting, and preserved
  required relationships. Passing the fixture is not a general HIPAA or
  re-identification-risk certification.
- **FML:** expected mapped fields, relationships, cardinalities, diagnostics, and
  failed-input handling; do not rely only on successful process exit.
- **SQL-on-FHIR:** exact typed rows, multiplicity, null behavior, complete input
  coverage, and ordering only when explicitly specified; do not compare CSV
  strings as a proxy for equivalent data.
- **Validation:** independently expected rule/profile violations and severity,
  with conformant and intentionally invalid cases. A validator must not grade
  its own diagnostics as truth.

Source folders remain immutable. Writes are restricted to a fresh per-episode
output/scratch location, with explicit source-integrity checks and artifact path
validation. Destructive edits, external writes, and real patient data remain out
of scope. Gold artifact checks and operational success rates are reported
separately from question-answering accuracy, not blended into a misleading score.

Repeated evaluation makes those cases development/adaptive evaluation material.
Do not claim the original 800-item sealed-test design remains untouched if its
answers or failures guide fixes. Maintain an exposure ledger and versioned
evaluation snapshots; any genuinely sealed final subset must remain uninspected
until preregistration and its one-time final run.

## 1. Intent and the claim worth testing

Doctors, nurses, advanced practitioners, specialists, and researchers need more
than plausible answers. They need the right patient, complete relevant records,
correct interpretation of terminology and time, and a visible source of truth.
Large folders of JSON make each of those difficult even for capable agents.

**Hypothesis:** specialist FHIR navigation, search, and terminology tools improve
the rate of correct, fully supported answers, especially as record volume,
reference depth, temporal complexity, and terminology ambiguity increase.

The benchmark must be capable of disproving that hypothesis. Do not curate the
final set around questions on which MCP happened to win. Report small-record
controls, strong scripting baselines, losses, unavailable capabilities, and
unsupported answers as well as successes. Demonstrating advantage honestly is
more persuasive to professionals than handicapping the comparison.

Success means fewer missed qualifying records, fewer invented codes or mappings,
more verifiable claims, and appropriate uncertainty. It does not mean producing
more diagnoses or recommending more treatments.

### Goals, non-goals, and anti-goals

| Kind | Requirement |
|---|---|
| Goal | Measure patient-level and cohort-level question answering with reproducible clinical rules and source-level citations. |
| Goal | Separate navigation benefit, terminology benefit, computational correctness, and tool-interface overhead. |
| Goal | Show understandable failure examples: what was missed or guessed, why it matters, and what evidence was available. |
| Goal | Produce 1,000 distinct question instances across diverse task families, not 1,000 trivial paraphrases. |
| Non-goal | Autonomous diagnosis, prescribing, order entry, or deploying a bedside assistant. |
| Non-goal | Claiming synthetic-data performance establishes real-world safety or population validity. |
| Anti-goal | LLM-generated answer keys certified only by another LLM, unsupported exact code mappings, hidden truncation, or silently incomplete cohorts. |
| Anti-goal | Calling agent review clinician review, or selecting cases after seeing final model scores. |

All allocations, stack choices, gates, and thresholds below are proposed design
decisions, not decisions already approved by the user.

## 2. What the investigation established

### 2.1 Pin the tool, not the PR description

Investigated [brendankowitz/ignixa-fhir#504][S1] at
`29ff3c76158322067184408910de2bca2e2899e4`. Its description still emphasizes P0,
but the code/README at that revision already includes cached search and reads.

| Capability | Observed at the pinned revision | Benchmark implication |
|---|---|---|
| `folder_inspect` / `inspect` | Implemented | Corpus discovery and diagnostics can be measured now. |
| `search_fhir_resources` / `search` | Implemented; default page 10, maximum 50, count-only option, offset pagination, element projection | Test completeness, token matching, date behavior, counts, and pagination. |
| `read_resources` / `get` | Implemented; accepts `ResourceType/id`, at most 50 references per call | Test exact source retrieval and evidence traceability. |
| Response envelope | `facts`, `evidence`, `omitted`, `absent`, `issues`, `freshness`; null derived facts omitted; serialized MCP cap 50 KB | Evaluate warnings, truncation, missing evidence, and stale data explicitly. |
| Unsupported search | `_sort`, `_include`, chains, and `_has` fail as unsupported; ignored parameters can produce warnings | An agent must not treat an ignored filter as applied. Client-side joins remain possible, not native joins. |
| Terminology operations | Proposed in the investigation, not implemented in these three MCP tools | No current-tool terminology accuracy claim. Add a separately gated terminology track. |
| Patient context tree, FHIRPath, SQL-on-FHIR analytics | Proposed tool surface, not exposed by these three tools | Label future capabilities; current agents may calculate or join results themselves. |
| Bundle reference aliases | Investigation explicitly identifies missing `fullUrl` to `Type/id` resolution; catalog entries retain resource bytes/identity, not a fullUrl alias map | A blocker for reliable native Synthea reference navigation; must be a conformance test, not silently normalized away. |
| Underlying subsumption | Upstream investigation flags a success-shaped `not-subsumed` fallback in another service | Verify actual behavior before enabling hierarchy questions; never treat the fallback as a gold answer. |

These are source-inspection findings, not results from running the MCP here.
Rediscover `tools/list`, tool schemas, package versions, and behavior at every
evaluated revision. A PR can change after this snapshot.

### 2.2 The requested Synthea archive is already a useful scale challenge

Downloaded the requested sample's repository-pinned bytes, not an unrecorded
moving `latest`. Full measurements and method are in
[`synthea-source-profile.json`](../../research/synthea-source-profile.json).

| Measurement | Observed |
|---|---:|
| Archive / uncompressed JSON bytes | 29,595,199 / 386,150,705 |
| Files | 111 JSON Bundles: 109 transaction, 2 batch |
| Resources / resource types / patients | 119,981 / 24 / 109 |
| Observations / encounters / conditions | 46,305 / 5,635 / 3,540 |
| Medication requests / administrations | 3,858 / 1,026 |
| Resources per patient Bundle: min / median / max | 47 / 624 / 18,551 |
| `urn:uuid` reference occurrences | 445,174 |
| Missing resource ids / duplicate top-level `(type,id)` pairs | 0 / 0 |
| Observation statuses | All 46,305 are `final` |
| Observations lacking every `code.coding.display` | 0 |

Source commit: `9959d9178ea28f4ec10f17ee238b6fabe6eb0de5`.
Archive SHA-256:
`56cb9e49f7ba6ad4e61c40aa80999f8c10a710823fed1becdf2502053777a521`.

**Implications:** do not describe this as just 109 simple charts. Conversely,
unmodified Synthea is weak for testing absent displays, preliminary/corrected
labs, messy imports, and specialized ICU physiology. Its many coding displays
make a "guess the display" challenge especially unconvincing. Preserve it as a
natural-data control and add clearly labeled controlled fixtures.

The archive was structurally counted with Python's standard library. No schema
validator was run; profiles and file naming alone do not prove FHIR R4 or US Core
conformance. Validate a selected release explicitly before publishing questions.

### 2.3 FhirFakes is a scenario foundation, not a clinical gold oracle

Inspected Ignixa main at `6a0fc0eab9e69c9e94fe4859a1ca21150dd92354` [S4].
The CLI supports R4 and other versions, scenario parameter overrides, resolved
references, populations, NDJSON population output, and schema validation.

Verified built-in examples include `DiabeticPatient`, `AsthmaticChild`,
`UrinaryTractInfection`, and `AcuteMyocardialInfarction`. The source tree also
contains cardiovascular, cancer-care, surgical, emergency-department, pregnancy,
mental-health, preventive-care, and metabolic-progression scenario factories.
Discover exact callable IDs using `ignixa-fakes help scenarios` or
`ScenarioCatalog.GetAll()`; a class filename is not necessarily a scenario ID.

Example commands documented upstream (not executed in this investigation):

```powershell
ignixa-fakes help scenarios
ignixa-fakes r4 scenario DiabeticPatient --out .\data\diabetes --resolved-references --param age=60 --param severity=3 --validate
ignixa-fakes r4 population --out .\data\population --from Boston --count 100 --ndjson --validate
```

Important reproducibility constraint: the README exposes `--seed` for the
**resource** command, not universally for scenario/population commands. The
library has a seeded `ScenarioBuilder` and explicit patient start dates, but
built-in factories may use unseeded construction and default clocks. Do not
invent `scenario --seed`. Freeze exact output bytes first; a custom deterministic
scenario exporter must explicitly control clocks, identifiers, all random
sources, references, and values, then pass a two-run byte-equivalence test.

Read generated code and validate the clinical logic. For example, a diabetes
factory's comments describe an A1c-dependent progression, but its visible
escalation state is severity-driven. That is not independent clinical evidence
that treatment was appropriate or that a guideline trigger was satisfied.

### 2.4 MedAgentBench should shape the evaluation, not be copied blindly

[MedAgentBench's official repository][S5] describes a virtual FHIR EHR environment,
agent execution, and reference-solution-based evaluation. It links the user's
[NEJM AI publication][S6], uses Docker for its EHR, and explicitly describes its
code as research-oriented. The NEJM page returned HTTP 403 during this
investigation; no inaccessible article-specific numerical result is relied on.

Adopt its professional workflow framing, environment-bound task execution, and
task-specific verification. Adapt from interaction with a server to a read-only
local folder. Do not import state-changing tasks such as ordering medication
into this benchmark. Do not claim leaderboard comparability or reuse questions,
patient records, or reference solutions without a separate licensing and
contamination review.

[EHRSQL][S7] supplies complementary precedent: questions collected from 222
hospital staff, executable SQL, explicit temporal expressions, and unanswerable
questions. Use these ideas, not its patient data or question text.
[EHRXQA][S8] is a genuine multimodal benchmark with executable SQL/NeuralSQL and
credentialed underlying datasets. It is relevant future work, but image
interpretation is outside this JSON-folder v1. General AgentBench is not evidence
that a separate "FHIR AgentBench" dataset exists.

### 2.5 MIMIC-inspired and MIMIC-derived must stay separate

The official [MIMIC-IV Clinical Database Demo on FHIR 2.1.0][S9] already exists;
no speculative CSV-to-FHIR conversion is needed to investigate its structure.
Its documentation describes real-world artifacts and atypical workflows that
Synthea is not designed to reproduce, as well as local CodeSystems, ValueSets,
profiles, and release-specific validation fixes.

For the public core, synthesize **new patients and values** inspired by ICU
workflow patterns and published clinical definitions. Do not reconstruct actual
MIMIC patients, reuse restricted notes, or send real-patient records to Foundry.
The demo is deidentified real data, not synthetic data and not automatically
cleared for cloud transmission by being downloadable.

Optional future external-validity work may use MIMIC-FHIR under the exact chosen
release's access, license, ethics, and cloud-processing conditions. Keep it
separate from the 1,000-question synthetic core. This investigation downloaded
no MIMIC patient records and makes no blanket commercial-use claim about MIMIC.

## 3. The 1,000-question blueprint

### 3.1 Primary question-family allocation

Each item has exactly one `primary_family` from the ten rows below and one
difficulty label. Other tags are cross-cutting and must not inflate the total.
A separate `task_family_id` identifies the semantic question template/protocol:
the requested operation, inclusion/exclusion rules, and output obligations.
Changing a patient's name, seed, code instance, threshold instance, or wording
does not create a new task family. A different clinical purpose or rule/output
structure can. Review this assignment before generating paraphrases.

| Primary family | L1 | L2 | L3 | L4 | L5 | Total |
|---|---:|---:|---:|---:|---:|---:|
| Corpus orientation and source navigation | 40 | 25 | 10 | 5 | 0 | 80 |
| Patient facts and exact record retrieval | 50 | 35 | 15 | 0 | 0 | 100 |
| Longitudinal trends and temporal reconstruction | 10 | 30 | 50 | 40 | 10 | 140 |
| Medication and allergy reconciliation | 5 | 25 | 40 | 40 | 10 | 120 |
| Terminology, value sets, hierarchy, translation | 30 | 40 | 45 | 45 | 20 | 180 |
| Cohort selection, denominators, aggregates | 5 | 20 | 40 | 50 | 25 | 140 |
| Explicit research phenotypes and calculated criteria | 0 | 5 | 15 | 35 | 25 | 80 |
| Data quality, contradictory records, provenance | 10 | 10 | 15 | 15 | 10 | 60 |
| Missing evidence and justified abstention | 0 | 10 | 10 | 10 | 10 | 40 |
| Evidence-grounded professional synthesis | 0 | 0 | 10 | 30 | 20 | 60 |
| **Total** | **150** | **200** | **250** | **270** | **130** | **1,000** |

**Primary persona quotas:** nursing 180; primary care/advanced practitioner 220;
specialist 250; researcher 200; clinical informatics/data-quality professional
150. Personas change the question's purpose and requested output, not the gold
truth.

**Primary source quotas:** unmodified pinned Synthea 450; purpose-built,
clinically coherent Fakes/custom scenarios 350; controlled perturbation
fixtures 200. A perturbation has a separate derived corpus hash and provenance
back to its parent. These are question quotas, not patient counts.
Assign `primary_source_layer` in this order: any item whose decisive evidence
depends on an intentional mutation is `perturbation`; otherwise a newly authored
specialist scenario is `specialist`; otherwise it is `synthea`. Preserve the
parent origin separately. Questions combining layers must specify their decisive
fixture at authoring time; they cannot be relabeled after seeing results.

**Split:** 100 development, 100 validation, 800 sealed test questions. Use the
100 development questions as the first pilot, spanning all levels (20 each).
Group related templates, patient lineages, terminology-map variants, and
counterfactuals into one split before paraphrasing. Fit exact primary quotas
across the whole 1,000; publish the actual split cross-tabs. If connected
families cannot satisfy exact split sizes, add independently generated families,
not leaky splitting. Seal test wording and keys before comparative runs.

At least 100 distinct `task_family_id` values; no task family contributes more
than 20 items (the ten broad primary families are not subject to that cap).
Use at least 200 distinct patient lineages overall, drawing the additional
lineages from custom scenarios. All variants of a patient stay together.
Assign whole preconstructed cohort worlds to splits; do not let patient Bundles,
shared distractor patients, or mixed-patient cohort fixtures leak across splits.
Public Synthea is not a secret holdout, so new private synthetic test worlds
also matter. Shared public terminology versions are allowed and disclosed.
Define the dependence cluster as the connected component of shared task
family, patient lineage, counterfactual parent, or cohort world. Use that same
component for split assignment and paired bootstrap resampling; do not treat
different semantic templates over the same patient as independent samples.

### 3.2 Complexity is structural, not longer wording

| Level | Required operations | Example success condition |
|---|---|---|
| L1: direct | One resource, explicit concept, no join or inference | Correct fact plus exact supporting field. |
| L2: selection | Filter/status/time choice or verified single terminology operation | Chooses the correct result among plausible alternatives. |
| L3: relational | At least two resource types or a temporal series with normalization | Correct join and arithmetic, complete supporting records. |
| L4: compositional | Multi-domain or cohort question with several interacting temporal/terminology predicates | Correct included and excluded sets with a reproducible method. |
| L5: expert-auditable | Multiple interacting ambiguities, missingness, mappings, and/or phenotype windows over a substantial corpus | Correct result or justified partial/insufficient result, no invented certainty. |

Record a difficulty vector: candidate resources inspected, reference hops,
resource types joined, temporal predicates, terminology operations, conversions,
ambiguities, and completeness obligations. Record clinical reasoning complexity
separately from corpus size. Empirical success rates may calibrate the rubric on
development data, but must not change the sealed test set to favor one arm.

Scale tiers are exact resource-count manifests, not approximate patient counts:
small <=1,000; medium 1,001-25,000; large 25,001-150,000; stress >150,000 up to
a proposed 1,000,000. Single-patient complexity and cohort scale are orthogonal.
Stress reruns and format permutations are robustness evaluations, not extra
questions toward 1,000.

### 3.3 Question examples to instantiate, not accepted gold items

Every example below requires actual fixture evidence, a fixed index time,
appropriate terminology assets, and oracle approval before entering the set.

| ID | Persona / level | Representative question | Key pitfall / capability |
|---|---|---|---|
| Q01 | Nurse / L1 | What is this patient's recorded birth date, and where is it stored? | Identity and source field; current read tools. |
| Q02 | Informatics / L1 | How many Observation resources are in this folder, excluding Bundle containers? | Count entries, not JSON files. |
| Q03 | Practitioner / L2 | What is the latest final potassium before the stated encounter cutoff? | Clinical time, status, units; paginate then select client-side today. |
| Q04 | Nurse / L2 | Is an allergy explicitly documented, explicitly assessed absent, or simply not recorded? | Empty search is not "no known allergies." |
| Q05 | Specialist / L3 | What is the HbA1c trajectory across these encounters, excluding entered-in-error observations? | Corrected/status semantics; source time ordering. |
| Q06 | Pharmacist/specialist / L3 | Which medications were ordered, and which were actually administered during this admission? | Requests are not administrations; no adherence claim. |
| Q07 | Nurse / L3 | Which final culture results belong to this collection episode and DiagnosticReport? | Specimen/report/observation references. |
| Q08 | Practitioner / L3 | Which medications appear active at discharge under the supplied reconciliation policy? | Status, intended duration, cancellation, contradictory sources. |
| Q09 | Informatician / L2 | What display and status does the pinned terminology release assign this recorded code? | Lookup, not model recall; future terminology track. |
| Q10 | Specialist / L3 | Are these two lab codes comparable given specimen, property, timing, scale, and method? | Similar display does not establish equivalence. |
| Q11 | Researcher / L4 | Find patients with a condition in the supplied diabetes ValueSet, including its defined descendants. | Versioned expansion; no guessed code lists. |
| Q12 | Informatician / L4 | Translate this local lab code using the supplied ConceptMap and report match strength and dependencies. | Contextual mapping, not assumed one-to-one translation. |
| Q13 | Practitioner / L4 | Is there an exact target-system equivalent, or only a broader, narrower, ambiguous, or absent map? | Correct refusal of false exactness. |
| Q14 | Researcher / L4 | Count adults with two qualifying outpatient results at least 90 days apart under this protocol. | Unique patients, encounter class, interval endpoints. |
| Q15 | Researcher / L4 | Which discharged patients returned within 30 days, excluding transfers under the supplied rule? | Encounter linkage; resource counts are not patient counts. |
| Q16 | Researcher / L5 | Build an eligible cohort using diagnoses, administered exposure, lab timing, and explicit exclusion criteria; explain exclusions. | Multi-stage denominator and evidence completeness. |
| Q17 | Nephrologist / L4 | Does this admission meet the supplied creatinine-only AKI screening rule, and at what first timestamp? | Fixed baseline/window; screening, not complete diagnosis. |
| Q18 | Intensivist / L5 | Can the supplied sepsis research phenotype be evaluated, and which prerequisites are missing? | No imputing missing SOFA components as normal. |
| Q19 | Nurse / L3 | What was the documented urine volume over this interval, and is coverage adequate to calculate mL/kg/h? | Interval overlap, weight time, chart gaps. |
| Q20 | Specialist / L4 | Which lab result was corrected, and how does the final evidence change the trend? | Distinguish revision history from separate observations. |
| Q21 | Informatician / L3 | Resolve this transaction Bundle's patient and encounter references without crossing patients. | fullUrl alias map; current prototype gap. |
| Q22 | Informatician / L4 | Do duplicate identities or broken references prevent a trustworthy count? | Report ambiguity, not arbitrary first-wins certainty. |
| Q23 | Practitioner / L4 | What supports and contradicts the recorded problem-list diagnosis before the cutoff? | Retrieval with two-sided evidence, not new diagnosis. |
| Q24 | Researcher / L5 | Return the numerator, denominator, excluded IDs, and unresolved cases for this phenotype. | Full set comparison; denominator accounting. |
| Q25 | Specialist / L5 | Summarize the ICU-to-ward medication/laboratory timeline, separating observed facts, calculated facts, and unknowns. | Claim-level evidence, no invented causal relationship. |
| Q26 | Practitioner / L2 | Does absence of a result in this export establish that the test was never performed? | Answer no; bound conclusions to the available export. |
| Q27 | Informatician / L4 | Has this result changed since the index snapshot, and is an absence claim still defensible? | Controlled freshness test, not silent cache trust. |
| Q28 | Specialist / L5 | Recompute the cohort when one mapping is non-equivalent, a result is corrected, and another site's units are incompatible. | Interaction effects, uncertainty propagation. |

## 4. Data construction and clinical validity

### 4.1 Three complementary corpus layers

**Natural control:** preserve the entire pinned Synthea archive as a provenance
source. Build split-specific patient/cohort subsets with the required shared
practitioners/organizations. Keep source-to-subset mappings; never silently
rewrite an original reference or strip displays. The entire archive can also
serve as a disclosed public scale-only diagnostic, not a sealed holdout.

**Specialist scenarios:** use Fakes for scaffolding, then author clinically
correlated timelines from an explicit latent scenario specification. Proposed
packs: diabetes/CKD and medication escalation; heart failure and diuretic
exposure; ACS/PCI chronology; COPD/asthma exacerbation; infection/culture and
antibiotic administrations; postoperative course; oncology with CBC trends;
maternal/pediatric histories; ICU kidney-injury screening; ICU perfusion and
organ-dysfunction evaluation. Acute kidney injury and ICU sepsis packs are
**new custom work**, not verified built-in CLI scenarios.

For every pack, define encounter boundaries, event times, specimen relationships,
drug route/dose/administration status, expected observations and units, plausible
clinical ranges, and explicit missingness. Do not generate every lab
independently and later assert a coherent syndrome. Resource validity and
clinical plausibility are different gates. Mortality/disease prevalence in these
fixtures is designed, not an estimate for a real population.

**Controlled perturbations:** positive/negative near-boundary pairs; final vs
preliminary or entered-in-error labs; local codes without displays; wrong or
misleading displays; valid non-equivalent maps; incomplete ValueSets; missing
units; incompatible specimens; same numeric code in different systems; broken
references; conflicting duplicate IDs; paginated cohorts; large resources;
stale indexes; and clinically irrelevant distractors.

Some perturbations intentionally violate structural or semantic rules. Mark
them `expected_invalid` with an expected diagnostic, rather than claiming all
fixtures must pass validation. Strip a display only in the labeled perturbation
track, and do not add the answer to filenames, narrative, metadata, or generator
scenario names accessible to the tested agent.

### 4.2 Terminology must be a verifiable asset

FHIR core and HL7 Terminology packages do not contain complete SNOMED CT, LOINC,
RxNorm, or clinically authoritative cross-system maps. A terminology operation
is usable only if the required contents, release, hierarchy, map direction, and
dependencies are actually present.

| Task | Required truth | What must never substitute |
|---|---|---|
| Display lookup | Pinned CodeSystem release and concept record | `Coding.display` alone or an LLM's remembered display |
| Code-system validation | Correct system, version, concept status | Numeric-string resemblance |
| ValueSet membership | Canonical URL/version, includes/excludes, expansion parameters and timestamp/hash | A convenient manually guessed list |
| Subsumption | Versioned hierarchy and direction | Success-shaped `not-subsumed` when hierarchy is unavailable |
| Lab comparison | LOINC attributes plus appropriate clinical context | Same name, different specimen or method |
| Medication grouping | Ingredient/strength/form relationships and documented class policy | Brand-name string matching |
| Translation | ConceptMap/version, source and target context, equivalence, dependencies, all applicable candidates | A fabricated one-to-one SNOMED-to-ICD or local-to-LOINC map |
| Conversion | UCUM-compatible dimensions; analyte-specific factor when mass-to-molar conversion is needed | Generic numeric unit rescaling |

For redistribution, check [LOINC][S12], [SNOMED CT][S13], [RxNorm][S14], HL7
package, and map licenses individually. Do not download licensed content or
commit releases without the required entitlement and redistribution review.
Use benchmark-owned local CodeSystems/ConceptMaps for an open reproducible
mapping track; label it **synthetic mapping mechanics**, not real clinical map
accuracy. Licensed standard-terminology extensions get separate manifests and
leaderboards. Missing entitlement is a blocked extension, not a made-up asset.

The [FHIR R4 translate contract][S10] permits multiple matches and explicitly
requires inspecting equivalence. Do not port R5 relationship labels into an R4
gold contract. Preserve inactive-code, map-version, hierarchy-direction,
one-to-many, unmatched, disjoint, and dependency-missing cases.

### 4.3 Phenotypes need versioned operational definitions

Use the [KDIGO 2012 AKI guideline][S15] as an explicitly named research reference,
not an unqualified "latest guideline": KDIGO's site currently describes a 2026
update still being prepared after public review. Pin the final published rule
chosen at authoring time. A creatinine-only screen may use the specified
0.3 mg/dL increase in 48 hours or 1.5-times baseline within seven days, but must
state the baseline-selection algorithm, known data coverage, and excluded urine
criteria. Without adequate baseline/coverage, return insufficient evidence or a
partial criterion result, not "no AKI."

[Sepsis-3][S16] supports careful infection/organ-dysfunction research tasks, not
labeling sepsis from lactate alone. A septic-shock operationalization needs its
full defined context, including adequate resuscitation assumptions. SOFA tasks
must define all components, timing, treatment support, baseline, and missingness.
The benchmark is not permitted to infer absent treatment or normal physiology
from a missing record.

Each phenotype card includes the exact source and rule version, clinical purpose,
inclusion/exclusion criteria, reference time, units, missingness policy,
boundary cases, oracle tests, and reviewer signoff. Guideline use does not turn a
synthetic phenotype into a validated bedside diagnosis.

## 5. Evidence and gold-answer contracts

### 5.1 Separate the agent's world from the answer-key world

The agent receives the question, persona, explicit protocol/reference time, data
mount, available tool schemas, and the arm's authorized terminology assets.
It never receives gold answers, oracle code, latent scenario labels, hidden
inclusion flags, or expected evidence IDs.

Keep four proposed artifacts separate:

| Artifact | Contents |
|---|---|
| `questions.jsonl` | ID, family/lineage/split, persona, wording, difficulty vector, corpus ID, cutoff, protocol, required capabilities, answer type |
| `gold.jsonl` | Typed answer, accepted alternatives/tolerances, complete member sets, required claim/evidence obligations, expected unknowns and errors |
| `corpora.lock.json` | Source/hash, FHIR release, generation recipe, subset/derivation map, terminology versions, known defects, expected validation outcome |
| `review-ledger.jsonl` | Generator, deterministic checks, reviewers' roles, decisions, disagreements, corrections, acceptance state |

The actual JSON Schemas are an implementation task. Validate all fields; a
missing review state or terminology version is not a pass.

### 5.2 Typed outputs, including uncertainty

Answer states: `answered`, `partial`, `insufficient_evidence`, `ambiguous`,
`unsupported_capability`, and `invalid_data`. They are not interchangeable.
An unsupported-capability label is legitimate only when the task truly cannot
be completed through other permitted operations; lacking a native join is not
enough if the arm can retrieve and join the necessary data.

Answer values are boolean, coded concept, quantity, timestamp, ordered series,
patient/resource set, aggregate with numerator/denominator, or bounded narrative.
Each asserted clinical fact must bind to evidence. Each calculated fact must
also identify its rule and input evidence. Missing-data and completeness claims
need a checked scope, not a fictitious resource proving a negative.

Each evidence object records corpus hash, resource identity, source-file hash
and relative path, Bundle entry or NDJSON line/byte span, JSON Pointer, and
version when present. Include the actual source literal or its deterministic
comparison representation. Preserve duplicate identities by source location;
`Observation/x` alone is not an adequate identity when duplicates exist.

Example contract shape, illustrative only:

```json
{
  "question_id": "dev-potassium-001",
  "status": "answered",
  "answer": {"kind": "quantity", "value": 4.2, "unit": "mmol/L"},
  "claims": [{
    "id": "c1",
    "text": "Latest qualifying final potassium before the supplied cutoff",
    "evidence_ids": ["e1"],
    "rule_id": "latest-final-potassium-v1"
  }],
  "evidence": [{
    "id": "e1",
    "corpus_id": "fixture-potassium-v1",
    "resource_reference": "Observation/o17",
    "file": "observations.ndjson",
    "line": 18,
    "pointer": "/valueQuantity/value"
  }],
  "limitations": []
}
```

Production evidence additionally requires the hashes and source locator fields
described above; this shape does not establish an actual patient result.

### 5.3 Independent truth construction

1. Author a latent clinical scenario/protocol and boundary examples.
2. Export FHIR and provenance, keeping latent labels out of agent-visible data.
3. Build a standard-library JSON reference resolver independently of Ignixa:
   Bundle-scoped fullUrl aliases, allowed absolute references, contained
   references, and explicit unresolved/ambiguous targets.
4. Compute typed answers from exported bytes using an independent Python/SQL
   oracle, not by asking the MCP under test. Retain query/rule and code hash.
5. Cross-check against latent intent and hand-calculated boundary fixtures;
   mismatches block acceptance. Both generator and oracle can be wrong.
6. Verify terminology against pinned assets, not the model. For tractable
   sets/counts, use a second independent calculation or manual enumerations.
7. Run clinician/domain review of clinical wording, plausibility, and rules.
   Require relevant clinician approval for all 80 phenotype items, all L4/L5
   clinical synthesis items, and high-risk medication/allergy interpretations.
8. Validate evidence pointers against original bytes; run mutation tests that
   change the decisive value, status, time, map relation, or patient link.

All 1,000 accepted items need independent semantic review appropriate to their
domain (clinical professional or terminology/informatics expert). Model/agent
review may triage but must retain its actual identity. Without the necessary
professional reviewers, publish only a **provisional research set**, never a
"clinician-validated benchmark." Reviewer staffing remains unconfirmed.

## 6. Fair comparisons and capability-gated tracks

### 6.1 Experimental arms

| Arm | Access | Purpose |
|---|---|---|
| A: raw-context diagnostic | A deterministic serialization if it fits; record exact coverage if not | Illustrate context limits, not the headline competitor. No truncation disguised as full access. |
| B: general agent | Read/list/search files, run Python or equivalent scripts; no specialist FHIR CLI/MCP | Strong baseline: agent can genuinely figure out the folder and build indexes. |
| C: specialist CLI | B plus the pinned cli-on-fhir CLI | Tests specialist implementation benefit independent of MCP transport. |
| D: specialist MCP | B plus identical capabilities exposed through MCP | Measures benefit with a normal agent tool interface. |

Headline comparison is **D vs B**, with **C vs D** exposing transport effects.
Same underlying model deployment/version, reasoning setting, prompts, machine,
data bytes, time/token/output budgets, and network policy per paired comparison.
All substantive medical knowledge, terminology releases, and protocol documents
available to D must also be readable by B and C. Do not precompute answers or
give an expansion table only to MCP. Tool documentation and implementation are
the intervention; agent-facing generic instructions otherwise stay identical.

Disable evaluation-time external browsing/live terminology for every arm, or
define a separate equal-access network track. Separate optional RAG baselines
from the filesystem baseline and publish their preprocessing costs.

**Terminology ablation:** repeat matched items with terminology assets present
versus absent and specialist terminology operations enabled versus disabled.
Within each condition, match knowledge access across arms. A same-assets
comparison isolates interface/computation; an assets-absent comparison measures
reliance on memorization, not pure MCP benefit. Missing versions are legitimate
abstention cases with changed gold expectations, never silently reused keys.

### 6.2 Tracks and the current prototype

- **Current-core:** only capabilities demonstrated by pinned
  inspect/search/read and allowed client-side reasoning. Establish real counts
  and retrieval behavior before assuming Synthea reference navigation works.
- **Terminology-ready:** the relevant lookup/validation/hierarchy/translation
  behavior passes independent conformance tests with complete assets.
- **Navigation/analytics-ready:** context trees, FHIRPath, or view tools actually
  exist and pass behavior tests; complex questions need not wait if they can
  already be answered through core retrieval and client computation.
- **Robustness:** native Bundle vs equivalent NDJSON/single-resource layouts,
  controlled missingness, diagnostics, injected distractors, stale data.

Every item carries minimal capability requirements and answerability. Before
running, conformance discovery creates an eligibility manifest. Report eligible
count/total count, unsupported/blocked counts, and successful answers against
the **fixed full denominator** as well as capability-eligible accuracy.
Do not advertise a score "on 1,000 questions" if most are excluded.

For native Synthea, first pin a failing/passing test for fullUrl aliases.
Prefer an upstream resolver fix. If a normalized derivative is used for
development, rewrite only resolvable references through a documented transform,
retain an original-to-derived evidence map, apply the identical bytes to every
arm, and report native versus normalized results separately. Normalization is
not proof the native-folder problem is solved.

## 7. Scoring trust and transparency

**Primary endpoint: strict grounded task success.** The answer is semantically
correct, all required evidence obligations hold, completeness/limitations are
correct, and there is no material unsupported clinical claim. A persuasive
narrative with a wrong count or fabricated code fails.

| Dimension | Measurement |
|---|---|
| Answer correctness | Typed equality; predefined quantity/time tolerances; set precision/recall/exact match; exact numerator/denominator |
| Citation validity | Referenced source exists at the pinned hash and pointer resolves |
| Citation support | Source actually entails the associated claim, including patient, status, time, code, and unit |
| Evidence coverage | Every required decisive input/clinical claim is supported; allow equivalent sufficient source sets |
| Terminology accuracy | System, code, version, membership, mapping direction/relation/dependencies correct; invented code rate |
| Completeness | All pages/candidates considered; no silent omissions or ignored-filter claims |
| Abstention/calibration | Correct insufficient/ambiguous calls, answerability precision/recall, coverage vs error; over-abstention remains a failure |
| Safety-relevant errors | Wrong-patient evidence, false absence, order-as-administration, unsupported equivalence, missed contradictory facts |
| Efficiency | Input/output/reasoning tokens when reported, measured spend, total calls, wall time, tool bytes, peak memory |
| Transparency | Reproducible evidence trail, explicit rules and limitations, no hidden truncation |

Deterministic graders cover facts, sets, calculations, locators, and known claim
obligations. Bounded narratives use blinded domain review; an LLM judge is
secondary triage and cannot overrule a failing exact calculation or fabricated
citation. Do not request hidden chain-of-thought. A concise calculation,
source trail, and limitation statement are sufficient.

Report paired differences with 95% confidence intervals, clustering bootstrap
resampling by scenario/template/cohort world rather than treating sibling
questions as independent. Preregister the primary comparison; disclose
secondary subgroup/multiple-comparison treatment. Publish results by difficulty,
scale, primary family, persona, source layer, and answerability, including
negative and tied results.

No guaranteed minimum uplift is assumed. A proposed practical target is at least
10 percentage points improvement in strict grounded success on preregistered
L4/L5 tasks, with uncertainty reported. It is a target, not a claimed result or
a basis for removing questions. Estimate power after the development pilot
using family counts and within-family correlation; 1,000 correlated items do
not imply 1,000 independent samples.

Use fresh per-question sessions by default. Separate cold-start indexing,
warm per-question execution, and batch-amortized total cost; include preparation
for every arm, including baseline-built indexes and terminology loading.
Enforce common budgets and record budget-exhausted and tool-crash outcomes.
Repeat a registered number of runs with independently recorded seeds where
supported; never imply deterministic model inference just because temperature
is zero. Gold corrections invalidate and rerun all affected arms together.

## 8. Foundry-assisted authoring and validation

The user authorized investigating the existing Olympus Foundry resource and
using it to assist question generation/validation. Read-only Azure management
discovery found `olympus-foundry-eus-resource` in `eastus2`, kind `AIServices`.
No access settings, deployments, credentials, or resource configuration changed.

Text-model deployments observed:

| Deployment | Observed model version | SKU |
|---|---|---|
| `gpt-5.6-sol` | `2026-07-09` | GlobalStandard |
| `gpt-6.1-sol` | `2026-09-29` | GlobalStandard |
| `grok-4.7` | `1` | GlobalStandard |

Discovery establishes existence, not API compatibility, authorization for
inference, structured-output/tool support, quotas, pricing, or clinical quality.
No GlobalBatch/DataZoneBatch deployment appeared in the returned list; do not
submit standard deployments as Batch jobs or create a deployment automatically.
Verify the chosen model's supported endpoint and parameters in a separately
approved, cost-bounded smoke call before scaling.

The [Olympus project]'s `AGENTS.md:150-168,190-192` supplies useful patterns:
environment-configured deployment/endpoint, Entra authentication, export before
execution, printed cost gates, resumable request IDs, bounded retries, and strict
refusal/truncation/parse handling. Its private KB and curated examples are not
part of this benchmark and must not be copied.

Proposed stack: Python 3.12+, pytest, JSON/JSONL contracts, standard-library
SQLite for independent tabular oracles where useful, .NET 10 for the pinned
Ignixa tool/scenario exporter, and the official OpenAI/Azure identity clients
for supported Foundry endpoints. Start with deterministic fixtures and no
vector store; add dependencies only when the owning task needs them.

Proposed authoring pipeline:

1. Human/expert authors protocol cards and independent scenario specifications.
2. Deterministic generators create FHIR, terminology fixtures, and evidence maps.
3. Oracle derives exact answers; authoring packets include only bounded,
   synthetic evidence and cited clinical rules.
4. A configured Foundry writer proposes natural clinician-style questions and
   paraphrases. It may flag evidence gaps; it cannot invent codes or set gold.
5. A separately configured critic checks ambiguity, unsupported assumptions,
   clinical plausibility, and semantic difficulty. Prefer a different model
   where available; model diversity does not constitute independent clinical
   verification.
6. Deterministic gates, expert review, split/decontamination checks, and mutation
   tests accept or reject records. Preserve rejected records and reasons.
7. Freeze test packets; run evaluated agents without gold or authoring transcripts.

Generation, critique, and evaluation are separate role configurations, even
when only one endpoint is available. Record model family overlap and judge
bias. Never select a writer/model because it made a particular evaluation arm
look better.

Use the approved resource only for approved synthetic evidence, not arbitrary
private files or MIMIC records. Keep endpoints/credentials in local environment
configuration, never source or request logs. Use the current SDK/API's documented
Entra audience and deployment-name semantics [S17]; do not assume legacy token
audiences, a portal URL as an inference endpoint, or universal v1 support across
all providers.

**Spending gate:** export requests and print token/currency estimates before
submitting inference, matching Olympus' workflow. Obtain approval for an explicit
maximum request count, total output-token allowance, and cost ceiling. Current
permission identifies a resource and purpose, not a numeric unlimited budget.
No inference has been submitted in this investigation.

Start with 20 authoring candidates; expand and independently review the
development set to 100 accepted questions before the development pilot.
The proposed pilot deployment is `gpt-5.6-sol`, with B/C/D and one run:
**300 agent episodes**, not 300 completions. Proposed final evaluation is B/C/D
on both `gpt-5.6-sol` and `gpt-6.1-sol`, three independent runs per sealed test
item: **14,400 episodes** for 800 test questions, before optional diagnostics or
ablations. This is a proposed scope for costing, not authorization to spend.
The inference smoke test must establish endpoint capability; funding approval
may reduce this panel before preregistration, with the resulting narrower claim
stated explicitly. Each episode may involve many inference calls. Fix exact
per-episode turn, token, tool-output, and wall-time limits after the smoke
estimate and preregister before the pilot. Scale only after reviewing yield,
errors, costs, and clinical-review capacity.

## 9. Delivery stages and acceptance gates

| Stage | Deliverable | Gate before advancing |
|---|---|---|
| 0: protocol and source lock | Reproducible corpus profile, capability manifest, comparison protocol, contracts | Native-reference failure understood; no future capabilities mislabeled implemented |
| 1: gold-first vertical slice | 20 independently checked questions, including unanswerable and exact-code traps | Every answer/evidence locator recomputes; mutation and boundary tests work |
| 2: development pilot | 100 questions, 20 at each difficulty, B/C/D on one model | Equal access/budgets; no gold leakage; reviewer decision and measured cost |
| 3: scenario/terminology expansion | Full source and family coverage, release-specific assets | Clinical and licensing gates met; every required capability conformance-tested |
| 4: 1,000-item freeze | 100 development / 100 validation / 800 sealed test; quotas audited | 100% accepted records have required oracle, evidence, and review; no cross-split lineages |
| 5: comparative report | Paired runs, confidence intervals, cost curves, error atlas | All denominators and exclusions disclosed; failed/unsupported outcomes retained |

Do not promise an implementation calendar before reviewer availability,
terminology entitlement, and upstream reference/terminology readiness are known.
The benchmark harness and source-independent gold can progress while upstream
features are missing; those questions stay authored-but-blocked for that track.

Outstanding approvals before execution: this design; clinical/terminology
reviewer staffing; access and redistribution scope for standard terminologies;
the cost-bounded Foundry pilot. Open defaults for an implementer: Python
module internals, deterministic fixture serialization, and packaging details
that preserve these contracts. Do not create a new server, UI, database service,
or model-training pipeline for v1.

## 10. Sources and evidence limits

External pages and repository snapshots were checked during this investigation.
Search-generated summaries were used only to find sources: two proposed
EHRXQA links were wrong, and a supposed "FHIR AgentBench" relationship was not
substantiated. The sources below are the verified references actually relied on.
No published model score, clinical effect size, or benchmark superiority is
claimed here.

- **S1** [cli-on-fhir PR][S1]; pinned [README][S2],
  [ResourceTools][S3], and
  [local tool investigation](https://github.com/brendankowitz/ignixa-fhir/blob/29ff3c76158322067184408910de2bca2e2899e4/docs/features/local-cli/investigations/agent-tool-surface.md).
- **S4** [FhirFakes CLI and source at the inspected main revision][S4];
  [ScenarioCatalog](https://github.com/brendankowitz/ignixa-fhir/blob/6a0fc0eab9e69c9e94fe4859a1ca21150dd92354/src/Core/Ignixa.FhirFakes/Scenarios/ScenarioCatalog.cs).
- **S5-S6** [MedAgentBench official repository][S5] and [NEJM AI article][S6]
  (article body inaccessible here; repository and linked abstract used).
- **S7** [EHRSQL official repository][S7], including answerability, source roles,
  and executable-query design.
- **S8** [EHRXQA official repository][S8], including modality and access limits.
- **S9** [MIMIC-IV Clinical Database Demo on FHIR 2.1.0][S9], especially Methods,
  Background, release notes, and Ethics.
- **S10** [FHIR R4 ConceptMap $translate][S10].
- **S11** [UCUM specification][S11]; dimensional conversion is not arbitrary
  analyte mass-to-molar conversion.
- **S12-S14** [LOINC license][S12], [SNOMED CT access][S13],
  [RxNorm release and terms information][S14].
- **S15** [KDIGO AKI guideline page][S15] and its linked
  [2012 guideline](https://kdigo.org/wp-content/uploads/2016/10/KDIGO-2012-AKI-Guideline-English.pdf).
- **S16** [Sepsis-3, open-access original publication][S16].
- **S17-S18** [Azure/OpenAI endpoint and authentication guidance][S17] and
  [Azure batch guidance][S18]. Model-specific compatibility still needs probing.
- **Local reference** [Olympus project], README and targeted AGENTS.md excerpts
  only; no private KB or credentials were accessed.
- **Measured data** [Pinned Synthea archive][S19] and the local profile JSON;
  independent structural scan, not clinical certification.

[S1]: https://github.com/brendankowitz/ignixa-fhir/pull/504
[S2]: https://github.com/brendankowitz/ignixa-fhir/blob/29ff3c76158322067184408910de2bca2e2899e4/tools/Ignixa.CliOnFhir/README.md
[S3]: https://github.com/brendankowitz/ignixa-fhir/blob/29ff3c76158322067184408910de2bca2e2899e4/tools/Ignixa.CliOnFhir/Mcp/ResourceTools.cs
[S4]: https://github.com/brendankowitz/ignixa-fhir/tree/6a0fc0eab9e69c9e94fe4859a1ca21150dd92354/tools/Ignixa.FhirFakes.Cli
[S5]: https://github.com/stanfordmlgroup/MedAgentBench
[S6]: https://ai.nejm.org/doi/full/10.1056/AIdbp2500144
[S7]: https://github.com/glee4810/EHRSQL
[S8]: https://github.com/baeseongsu/ehrxqa
[S9]: https://physionet.org/content/mimic-iv-fhir-demo/2.1.0/
[S10]: https://hl7.org/fhir/R4/conceptmap-operation-translate.html
[S11]: https://ucum.org/ucum
[S12]: https://loinc.org/license/
[S13]: https://www.snomed.org/get-snomed
[S14]: https://www.nlm.nih.gov/research/umls/rxnorm/docs/rxnormfiles.html
[S15]: https://kdigo.org/guidelines/acute-kidney-injury/
[S16]: https://pmc.ncbi.nlm.nih.gov/articles/PMC4968574/
[S17]: https://learn.microsoft.com/en-us/azure/ai-foundry/openai/how-to/switching-endpoints
[S18]: https://learn.microsoft.com/en-us/azure/ai-foundry/openai/how-to/batch
[S19]: https://raw.githubusercontent.com/synthetichealth/synthea-sample-data/9959d9178ea28f4ec10f17ee238b6fabe6eb0de5/downloads/latest/synthea_sample_data_fhir_latest.zip
[Olympus project]: C:\src\olympus-coding-flash-model
