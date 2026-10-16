# Rebuild the corpus so tools can matter

The fhir-question-bench cannot do its job yet. The reason is not the question count. The corpus is too small for a tool to help: 151 resources (about 49 KB) across 11 patients, which an agent can read whole. The questions also test lookup rather than reasoning, because 56 of the 100 name the exact resource id. And no real agent has ever been run against the bench. The revised plan has four parts. First, a pinned, locally generated Synthea base of roughly 100–130 patients and about 100k resources. Second, about a dozen seeded FhirFakes "needle" patients whose clinical facts are known by construction. Third, a deterministic mutation layer that adds the messiness Synthea lacks: entered-in-error statuses, unit variants, local codes, duplicates and broken references. Fourth, 150 clinician-voiced questions across seven mechanism-based tiers, each graded by an executable oracle against a typed answer. A private 50-question holdout built on a second corpus seed comes on top. Prior work shows that harness changes alone can move accuracy a lot. MedAgentBench v2 raised GPT-4.1 from about 70% to 91% by changing the harness only ([V2 paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)), and FHIR-AgentBench's best agent went from 0.20 to 0.50 once code execution was added ([FHIR-AgentBench](https://arxiv.org/html/2509.19319v2)). There is a limit, though. With 150 questions and 3–5 repeats per profile, the bench can resolve differences of about **7–10 percentage points** between closely related profiles, not 5 ([Miller 2024](https://arxiv.org/html/2411.00640v1)). The first milestone is therefore a real end-to-end agent run, not the C# platform.

## A 49 KB corpus cannot reward a tool, and prior benchmarks show what does

The audit of PR #1 explains why the current bench cannot measure what it was built for. A corpus of 151 resources fits in any modern context window. An agent that reads every file does as well as one that calls a FHIR search tool, so adding the cli-on-fhir MCP cannot show an advantage by design. The questions make this worse. More than half name a resource id, which turns reasoning into a fetch. Many use invented systems such as `urn:bench:qa:lab|renal` and toy ConceptMaps that no clinician would ever say. The prompts carry grading-contract language, which tells the agent how the grader thinks. Of the 100 QA items, 80 are unverified and none has been human-reviewed. The 50 "operational" tasks (de-identification, FML, SQL-on-FHIR, validation) are useful tests of the tool, but they are not questions about a patient corpus. Finally, every run so far has gone through a fake Copilot process, so nobody yet knows where real agents fail.

The two closest prior benchmarks show where difficulty actually comes from. **MedAgentBench** has 300 tasks, but they are only 10 physician-written templates, each repeated across 30 patients. The patients live on a HAPI FHIR server holding 100 Stanford patients and about 785k resources ([MedAgentBench](https://arxiv.org/html/2501.14654)). The task context hands the agent the local codes ("MG", "GLU", "K"), the NDCs and a frozen "now" of 2023-11-13T10:15 ([repo](https://github.com/stanfordmlgroup/MedAgentBench)). That removes terminology as a difficulty. Most failures were format and protocol errors: Gemini 2.0 Flash, for example, produced invalid actions 54% of the time. The v2 follow-up added typed FHIR tools, a calculator, a finish tool that enforces the answer format, and a planning prompt. With those, GPT-4.1 reached **91.0%**, and **98.0%** with a memory built from its own failures. The v2 authors admit that this memory partly "teaches the grader's interpretation" of ambiguous tasks ([V2 paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)). The lesson is that 10 templates saturate quickly, and that unclear conditionals reward learning the grader rather than reasoning.

**FHIR-AgentBench** is hard for different reasons. It has 2,931 EHRSQL-derived questions over MIMIC-IV-FHIR-Demo, and a single patient averages about 9k resources, or roughly 3M tokens. Putting the whole record in context "consistently failed". The data uses local MIMIC codes rather than LOINC, so agents that searched LOINC 9279-1 for respiratory rate found nothing ([FHIR-AgentBench](https://arxiv.org/html/2509.19319v2)). Architecture mattered more than model. Multi-turn retrieval *without* a code interpreter was the worst setting for every model (0.16–0.20). Adding code lifted o4-mini to **0.50**, and medication questions stayed at **0.14** because agents failed to follow `medicationReference`. A later RL-trained Qwen3-8B CodeAct agent reached 77% ([Knorr et al.](https://arxiv.org/abs/2605.14126)). EHRAgent shows the same pattern in SQL form: success fell from 71.6% with one table to 49.1% with four ([EHRAgent](https://arxiv.org/html/2401.07128v3)).

The revised bench should take the hard parts of both. From FHIR-AgentBench: records too large to read whole, codes the agent has to discover, reference traversal, and aggregation that favours code over reading. From MedAgentBench: a frozen clock, typed answers, and branches that are spelled out. It should also avoid the shared failure mode of few templates and ambiguous empty-data behaviour. MIMIC remains inspiration only. The full MIMIC-IV-FHIR is credentialed under a data use agreement ([PhysioNet](https://physionet.org/content/mimic-iv-fhir/)), and the user's licensing constraint rules out importing it in any form.

## Synthea supplies the bulk; seeded scenarios and mutations supply the truth

**Recommended corpus architecture.** The corpus has three layers. All three are produced once, committed with a sha256, and treated as the pinned artifact. The layers are never regenerated at evaluation time.

The **base layer** is a custom Synthea run with every time-derived input pinned. Synthea's seed, clinician seed, reference date and end date all default to `System.currentTimeMillis()` ([Generator.java](https://github.com/synthetichealth/synthea/blob/master/src/main/java/org/mitre/synthea/engine/Generator.java)). A reproducible invocation therefore needs explicit values, along the lines of `-s <seed> -cs <seed> -r <date> -e <now> -p 120`, a pinned jar (v4.0.0 or a commit), and keep modules such as `keep_diabetes.json`, `keep_hospice.json` or `must_have_cardiac_surgery.json`. The keep modules guarantee enough patients for specific question families ([App.java](https://github.com/synthetichealth/synthea/blob/master/src/main/java/App.java); [keep_modules](https://github.com/synthetichealth/synthea/tree/master/src/main/resources/keep_modules)). Byte-level determinism across runs is **unverified**, which is why the output files themselves, not the command, are the pinned artifact.

The published Synthea sample shows the scale this produces: 109 patients, 119,981 resources, 386 MB of JSON, a median of 624 resources per patient and a maximum of 18,551 ([synthea-sample-data](https://github.com/synthetichealth/synthea-sample-data/commits); [local profile](../synthea-source-profile.json)). A single large patient already exceeds any context window, so tools can help even on one-patient questions. The sample also contains three reference styles at once. There are 445k `urn:uuid` references, 123k conditional references such as `Practitioner?identifier=…us-npi|…` that point into separate practitioner and hospital batch files, and 19k contained `#` references. "Who is her PCP?" is therefore a real resolution problem rather than a lookup ([local profile](../synthea-source-profile.json)).

Synthea has limits as a source of hard questions. Every Observation is `final`, every Condition is `confirmed` and `encounter-diagnosis`, and no resource carries `dataAbsentReason`. Only five LOINC codes appear with more than one unit, and there is no ICD-10 or local code system anywhere ([local profile](../synthea-source-profile.json)). Its care is also unrealistically guideline-concordant. Chen et al. found **0%** of a 1.2M-patient Synthea cohort met the "controlling high blood pressure" measure, against 74.5% in Massachusetts, and concluded that Synthea does "not currently model for deviations in care" ([Chen et al. 2019](https://pmc.ncbi.nlm.nih.gov/articles/PMC6416981/)). Synthea on its own therefore yields few care-gap, conflict and data-quality questions. The other two layers exist to supply them.

The **needle layer** is about a dozen patients built with FhirFakes `new ScenarioBuilder(schemaProvider, seed)` from C#. Each patient's facts are designed so that specific question families have known, non-trivial answers. The CLI `population` and `scenario` commands cannot be seeded, and neither can `PopulationGenerator` ([PopulationGenerator.cs](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/Population/PopulationGenerator.cs); [CLI README](https://github.com/brendankowitz/ignixa-fhir/blob/main/tools/Ignixa.FhirFakes.Cli/README.md)). FhirFakes' own design doc also notes that scenario states call `new Faker()` inline without a seed ([adversarial-data-generation.md](https://github.com/brendankowitz/ignixa-fhir/blob/main/docs/features/fhir-faker/investigations/adversarial-data-generation.md)). So the rule is: generate once, commit, and hash. Each needle should be built from the predefined scenarios in the catalog ([Scenarios/Predefined](https://github.com/brendankowitz/ignixa-fhir/tree/main/src/Core/Ignixa.FhirFakes/Scenarios/Predefined)), extended where needed. A needle can equally be a custom Synthea module if that turns out easier for a particular domain.

| # | Named scenario | Source scenario(s) | Facts fixed by construction | Question families it feeds |
|---|---|---|---|---|
| N1 | Diabetes escalation to GLP-1 | DiabeticPatient + MetabolicSyndromeProgression | A1c series crossing 9%, metformin start, BMI ≥ 35, GLP-1 started on a known date | Last A1c, A1c change after a drug start, prior-auth evidence, CMS122 cohort |
| N2 | CKD with renal-risk meds | ChronicDisease (extended) | eGFR fall of more than 25% over 2 years, lisinopril + spironolactone, rising K | eGFR trend, "anything raising his potassium?", metformin in CKD 3 |
| N3 | AF anticoagulation | Cardiovascular (extended) | AF, age, HTN, DM, prior TIA, sex, apixaban dose, weight, creatinine | CHA2DS2-VASc, apixaban dose check, "anticoagulated, and who prescribed?" |
| N4 | Inpatient AKI on vancomycin | EmergencyDepartment → inpatient encounter | Vancomycin start time, creatinine +0.3 within 48 h, q6h glucose, no Mg in last 24 h, low K | KDIGO bump, 24 h glucose mean/count, empty-window magnesium |
| N5 | Untreated hypertension | HypertensivePatient (modified) | Last two BPs > 140/90, no antihypertensive ever ordered | Cohort "uncontrolled and untreated", BP classification |
| N6 | Allergy–order conflict | WellnessVisit + MedicationOrder | Penicillin allergy with reaction, later amoxicillin order | "Any penicillin allergy, and what reaction?", conflict detection |
| N7 | Pediatric asthma | PediatricAsthmaOnset / AsthmaticChild | Exacerbation encounters, controller and rescue meds, immunizations | Exacerbation count, controller history, overdue vaccines |
| N8 | Pregnancy | PregnantPatient | LMP/EDD, prenatal labs, Tdap timing | Gestational age today, missing prenatal labs, Tdap this pregnancy |
| N9 | Oncology pathway | CancerCarePathway | Diagnosis date, staging procedure, regimen start/stop | Time from diagnosis to treatment, current regimen, stopped meds |
| N10 | Serotonergic/QT polypharmacy | MentalHealthTreatment (extended) | SSRI + tramadol + ondansetron (one coded as text only), QTc observation | "Two serotonergic agents?", QT-prolonging meds, text-only med matching |
| N11 | Duplicate identity (MPI) | Patient builder ×2 | Two Patient records, same MRN identifier, split history | Questions whose answer needs both records merged |
| N12 | Elderly hospice/multimorbidity | Elderly multi-morbidity lifecycle | Hospice enrolment (CMS122 exclusion), deceased date | Cohort exclusion, "is she still alive/active?" |

**Uncertain:** the research did not check which code systems FhirFakes emits per scenario, or whether `ScenarioBuilder(seed)` output is byte-stable ([corpus notes](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/README.md)). Phase 1 has to verify that needles use real LOINC, SNOMED and RxNorm codes consistent with Synthea, and fix the scenarios where they do not.

The **mutation layer** is a pure function: `(input sha, mutation spec JSON, seed) → output files + manifest`. Targets are picked by deterministic sampling over `(patient, type, id)`. It reuses the FhirFakes `MutationRecord(Category, Path, Before, After, Description)` / `MutationManifest` pattern ([EdgeCasePipeline.cs](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/EdgeCases/EdgeCasePipeline.cs)) through custom `IEdgeCaseStrategy` registrations. The built-in strategy families (Unicode, Temporal, StringBoundary) test parsers rather than clinical reasoning ([EdgeCaseFamily.cs](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/EdgeCases/EdgeCaseFamily.cs)), so they stay secondary. Synthea's Flexporter can mutate data too, but its randomness and date transforms are not seeded ([Flexporter wiki](https://github.com/synthetichealth/synthea/wiki/Flexporter)), so a separate post-processor is safer.

| Mutation class | Example | Trap it creates |
|---|---|---|
| Status | Latest potassium set to `entered-in-error`; a Condition `refuted`; a MedicationRequest `stopped` | "Last value" must skip invalid results; "active meds" must respect status |
| Units | Some glucose results converted to mmol/L (÷18.016) with UCUM code changed | Averages and thresholds need normalising |
| Codes | A lab recoded to a site-local system (display kept); ICD-10 added beside SNOMED; one med coded as text only | Code discovery, the FHIR-AgentBench LOINC miss, the V2 ondansetron miss |
| Missing data | `valueQuantity` replaced by `dataAbsentReason`; date precision reduced to year-month | Absence vs. value; date-precision handling |
| Duplicates | Exact and near-duplicate Observations; MPI duplicate Patient (N11) | Counting and de-duplication |
| References | One dangling reference; some `urn:uuid` converted to `Type/id` | Resolution robustness; matches `folder_inspect` diagnostics |
| Conflicts | Allergy contradicted by a later same-class order | Cross-resource reasoning |

Local code systems are realistic: MIMIC itself uses local itemids. They still differ from the invented systems the audit flagged. A mutated local code always keeps a human-readable display and appears only in the *data*. A question never names an invented system, and every question can be answered in clinical language.

**Licensing.** The rules are: generate everything locally, import no third-party patient data, use real LOINC, SNOMED and RxNorm codes as identifiers, and write all question text and authored needle displays in plain clinical language. **Uncertain and open:** Synthea's own licence, and the licence status of the SNOMED and LOINC display strings that Synthea writes into its output, were not verified in this research. Since the bench republishes those strings, this has to be checked before anything is published.

**Frozen clock and layout.** A single "now" applies to the whole corpus. It equals the Synthea end date (`-e`) and is injected by the harness, never written into question text. This follows MedAgentBench's frozen timestamp and EHRSQL's fixed "2105-12-31 23:59" ([EHRSQL](https://arxiv.org/abs/2301.07695)). Keep one file layout, either transaction bundles or NDJSON, to avoid gold drift between formats. Keep Claim and ExplanationOfBenefit as realistic distractors (about 16% of resources) unless indexing cost makes runs impractical.

## 150 questions across seven mechanism tiers, voiced like a clinician

**Recommended taxonomy.** Tiers are defined by *mechanism*: how many steps, resource types, temporal operations and inference rules a question needs. This follows GAIA's step- and tool-count levels ([GAIA](https://arxiv.org/html/2311.12983)) and EHRAgent's table-count levels. Every question also carries orthogonal tags: `TEMP`, `AGG`, `TERM`, `UNIT`, `ABS` (absence), `CALC`, `MULTI`, `REF` (reference resolution) and `TOOL+`. `TOOL+` marks a mechanism that a specific part of the full cli-on-fhir surface is designed to handle ([agent-tool-surface.md](https://github.com/brendankowitz/ignixa-fhir/blob/claude/confident-turing-tph6h1/docs/features/local-cli/investigations/agent-tool-surface.md); [PR #504](https://github.com/brendankowitz/ignixa-fhir/pull/504)). Those parts are: patient-context navigation; `fhirpath_evaluate` for component and extension logic; `plan_fhir_search` with `_include`, `_has`, chains and `summary=count`; `sql_view` for cohort tabulation; and the terminology tools (`_subsumes`, `_expand`, `_translate`, `:below`/`:in` search) for class and hierarchy questions. Validating that surface is a primary purpose of the bench. Questions are therefore written assuming the complete, landed tool set, and each question's `TOOL+` tag names the tool group it exercises, so results can be broken down per tool. The weighting follows MedAlign, where about 68% of 983 clinician instructions were "Retrieve & Summarize" and about 7% were "Calculation & Scoring" ([MedAlign](https://arxiv.org/abs/2308.14089)). It is skewed toward multi-step retrieval, because that is where tools should make a difference.

| Tier | Mechanism | Count | Typical tags | Target pass@1 (strong / weak profile) |
|---|---|---|---|---|
| T1 | Direct chart lookup: one resource type, one patient, latest or "ever" | 20 | REF, ABS | 85–95% / 40–60% |
| T2 | Filtered and temporal retrieval: windows, status filtering, first/last, relative time | 30 | TEMP, ABS | 70–85% / 25–40% |
| T3 | Aggregation and trend: count, mean, min/max, change, units | 30 | AGG, TEMP, UNIT | 55–70% / 15–30% |
| T4 | Multi-resource and terminology reasoning: drug classes, reference hops, encounter scoping | 30 | TERM, MULTI, REF | 50–65% / 10–25% |
| T5 | Clinical calculation and stated-rule application | 20 | CALC, MULTI, UNIT | 40–60% / 5–20% |
| T6 | Cohort / population across the corpus | 12 | AGG, TERM, ABS, TOOL+ | 25–45% / ~0–10% |
| T7 | Abstention: unanswerable from the record | 8 | ABS | ≥ 80% / variable |
| | **Total** | **150** | | Overall ~60–75% / ~15–30% |

The target bands are judgement calls, derived from the advice to keep profiles away from floor and ceiling effects ([Anthropic evals](https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents); [Signal and Noise](https://arxiv.org/html/2508.13144v1)). They are not sourced constants, and the pilot will move them. T7 is kept to 8 items (5.3%) so that an always-abstain agent scores near 5%, avoiding the trap where "do nothing" passes unsolvable tasks ([ABC](https://arxiv.org/html/2507.02825)). Expected-empty answers, such as "no magnesium in the last 24 hours", are a different thing. They are *answered* values in T1–T3, and their gold requires the agent to show it searched. They are capped at about 10% of the bank, below FHIR-AgentBench's 24% ([FHIR-AgentBench](https://arxiv.org/html/2509.19319v2)).

**Template diversity.** Use about 60 distinct templates with at most three instances each, spread across different patients. MedAgentBench's 10 × 30 design saturated at 91–98% once the harness improved. No patient should host more than about 8 questions, which keeps clustered standard errors manageable.

**Context envelope instead of ids.** Questions arrive as they would from a SMART-style launch. The harness supplies `{now, patient: name + DOB + MRN identifier, role}`, and the question then says "she" or "this patient", following Dragon Copilot's deictic style ("What medications does the patient take?", "What is the target A1c for this patient?") ([Microsoft Support](https://support.microsoft.com/da-dk/dragon-copilot/physicians/sample-dragon-copilot-prompts); [Information Assist](https://support.microsoft.com/en-us/dragon-copilot/physicians/use-ai-prompts-for-queries-outside-of-your-note)). A resource id never appears. For cohort questions, "my panel" means every patient in the corpus, unless the user later chooses a FhirFakes Group-defined panel. Branch behaviour for missing data is always stated in clinical words where it matters ("if there isn't one, say so"). This avoids the MedAgentBench task 9/10 ambiguity ([V2 paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)).

**Sample questions per tier.** All are adapted in clinician voice. Sources of style are noted where a phrase is borrowed.

| Tier | Example question | Answer shape | What makes it hard |
|---|---|---|---|
| T1 | "When was her last A1c, and what was it?" (MedAgentBench style) | `{value: number %, date: day}` | Choosing the latest across 4548-4 and its variants |
| T1 | "Who's her primary care doctor?" | Practitioner name + NPI | Conditional `Practitioner?identifier=` reference into the batch file |
| T1 | "Does he have any drug allergies on file?" | Set of codes, or empty | Absence must be asserted, not guessed |
| T2 | "What was his last potassium?" | number mmol/L | Latest result is `entered-in-error` (mutation) |
| T2 | "Any magnesium in the last 24 hours?" (MedAgentBench task 4) | number or `none` | Empty window (N4) |
| T2 | "Which of her current meds were started in the last 90 days?" | Set of RxNorm codes | Status plus authoredOn window, both inline and `medicationReference` shapes |
| T3 | "What's her average glucose over the last 24 hours, and how many readings were above 250?" | `{mean ±0.5 mg/dL, count}` | Mixed mg/dL and mmol/L (mutation) |
| T3 | "How has his eGFR changed over the past two years — any drop of more than 25%?" | `{percent change ±1, boolean}` | Two eGFR units in Synthea; baseline choice stated |
| T3 | "How many ED visits has she had in the past year?" | integer | Encounter class filtering; `summary=count` helps |
| T4 | "Is he on anything that could be raising his potassium?" (Ely "can drug x cause y") | Set of meds | ACEi/ARB/MRA class membership from RxNorm displays (N2) |
| T4 | "Is she anticoagulated right now, and who prescribed it?" | `{med, practitioner}` | Status, class, requester reference |
| T4 | "Has she ever been on a statin?" (MedAlign verbatim) | `{boolean, first date}` | Class across RxNorm products, all of history |
| T4 | "Does he have any kind of diabetes on his problem list, including complications?" | Set of SNOMED codes | Subsumption under the diabetes hierarchy (terminology `_subsumes` / `:below`) rather than string matching |
| T4 | "What were her last three systolic readings taken standing?" | List of `{value, dateTime}` | BP panel components plus a body-position qualifier; FHIRPath over components |
| T5 | "What's her CHA2DS2-VASc, and is she anticoagulated?" | `{integer, boolean}` | Seven inputs across Condition, Patient, MedicationRequest (N3) |
| T5 | "Using today's creatinine and his latest weight, what's his CrCl by Cockcroft-Gault?" | number ±2 mL/min | Age, sex, weight units, latest valid creatinine |
| T5 | "Has his creatinine bumped 0.3 or more within 48 hours of starting vanc?" | `{boolean, peak value, time}` | Window anchored to a medication event (N4) |
| T6 | "How many of my diabetic patients 18–75 had a last A1c over 9 this year — counting those with no A1c?" (CMS122) | integer | "Missing counts as failing", hospice exclusion ([eCQI](https://ecqi.healthit.gov/ecqm/EP//CMS122v10)) |
| T6 | "Which hypertensive patients had their last two BPs over 140/90 and aren't on any antihypertensive?" | Set of MRNs | BP components, class absence, many patients |
| T6 | "Which heart-failure patients were readmitted within 30 days of discharge?" | Set of MRNs | Encounter sequencing across the corpus |
| T6 | "How many patients with any cerebrovascular disease are on an antiplatelet or anticoagulant?" | integer | Hierarchy expansion on both sides (SNOMED + RxNorm class) across the cohort; a natural `sql_view` + terminology question |
| T7 | "Why did we stop her lisinopril?" (EHRSQL negative-example style) | `insufficient_data` | No documented reason |
| T7 | "What was his A1c in 2009?" (record starts later) | `insufficient_data` | Record boundary vs. "no result" |
| T7 | "What's her home pharmacy's phone number?" | `insufficient_data` | Beyond the corpus schema ([EHRSQL](https://arxiv.org/abs/2301.07695)) |

Compound questions ("what was it and when") are scored as one item that is correct only if every field is correct. Per-field correctness is logged as a secondary metric. Care-gap and rule questions state the rule in clinical shorthand ("colonoscopy in 10 years, mammo in 2"), so that grading never depends on outside guideline knowledge.

**Splits.** On top of the 150-question test set there are two more sets. A **public dev set of 30** comes from the same template families but uses different patients, and skills and prompts may be tuned against it. A **private holdout of 50** uses the same template families on a *second corpus seed*: a new Synthea seed plus re-parameterised needles. It is never published and is run only at freeze time and at periodic re-checks. If a profile scores well above its holdout score on the test set, that is direct evidence it has overfit, which is the failure Kapoor et al. found in WebArena's hard-coded STeP policies ([AI Agents That Matter](https://arxiv.org/html/2407.01502)). Published files carry a canary GUID. Canaries only detect contamination ([BIG-bench canary note](https://alignmentforum.org/posts/kSmHMoaLKGcGgyWzs/big-bench-canary-contamination-in-gpt-4)), so the stronger defence is seed regeneration.

## Gold lives in code, answers live in a typed envelope

**Oracle.** Each question is stored as a template, parameters, an oracle function, a gold answer, the gold evidence resource ids, and the mutation-manifest entries the answer depends on. This copies MedAgentBench's executable `refsol` and FHIR-AgentBench's `true_fhir_ids` ([MedAgentBench repo](https://github.com/stanfordmlgroup/MedAgentBench); [FHIR-AgentBench repo](https://github.com/glee4810/FHIR-AgentBench)). Recording the manifest dependency means a single mutation can be switched off for an ablation and the gold recomputed.

**Recommended:** write the oracle in Python over plain JSON, independent of the Ignixa stack. cli-on-fhir is built on Ignixa search, and an oracle that shares its search code would share its bugs and inflate the MCP profile's score. The second derivation, needed for every question, comes from a different route: a second author re-derives the answer, or an independent query is written in a different style. Disagreements between the oracle and a cli-on-fhir query are worth keeping as found bugs in one or the other, but cli-on-fhir never sets the gold.

**Reachability check.** For every question, verify that the gold is reachable through each profile's tools. This means paging and result caps must not hide data that the oracle counts. MedAgentBench v2 found exactly this mismatch: the agent was capped at 200 results while the grader fetched up to 5,000 ([V2 paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)). If an item is unreachable through cli-on-fhir, that is a tool finding to report, not a reason to drop the question. Pin the cli-on-fhir version or commit in every run record.

**Answer envelope.** The agent finishes by calling a typed `submit_answer` tool with `{status: answered | insufficient_data, answer: <typed>, evidence: [refs]}`. The answer specification (type, unit, precision) is shown in a fixed, uniform harness field next to the question. It is never woven into the clinical wording. This removes the grading-contract language from prompts while still avoiding the shape failures that cost MedAgentBench points, such as returning `-1` where the grader expected `(-1,-1)`. Following ABC O.h.1, the parser assumptions are stated explicitly ([ABC](https://arxiv.org/html/2507.02825)).

| Answer type | Rule |
|---|---|
| Integer / count | Exact |
| Float | Declared tolerance per question, after UCUM normalisation of the declared unit (e.g., ±0.1 for a single lab value, ±0.5 for a mean) |
| Date / dateTime | ISO 8601 at the declared precision |
| Code | `(system, code)` tuple, display ignored |
| Reference | `Type/id` or business identifier normalised (MRN, NPI) |
| Set | Strict set equality as the primary score; precision and recall logged. Blocks "list everything" gaming ([ABC](https://arxiv.org/html/2507.02825)) |
| Boolean / enum | Exact against the allowed values |
| Composite | All fields correct; per-field logged |
| Abstention | Correct only when gold is `insufficient_data`. On answerable items it is counted as "abstained", separate from "wrong" |

Every attempt ends in one of these statuses: `correct`, `wrong`, `abstained`, `format_invalid`, `turn_limit`, `harness_error`. Harness errors are re-run, not scored, following the AgentBench status split. Evidence ids are not graded. Retrieval recall against the gold ids is reported as a diagnostic, so that "never found the data" can be told apart from "found it but reasoned wrongly". **No LLM judge in v1.** Every question reduces to a canonical answer, and judges add a calibration burden that the deterministic design avoids ([Anthropic evals](https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents)).

**Grader CI.** Each question also ships with two to four equivalent variants that must pass and two to four near-misses that must fail. The oracle's output must score 100%. An always-abstain agent must score about 5%, and a no-tools model should land near zero apart from lucky guesses ([ABC](https://arxiv.org/html/2507.02825)).

## 150 questions resolve about 8-point differences, not 5

**What the sample size can and cannot show.** One run per profile with exact McNemar on 150 paired questions detects 10 pp only if the profiles disagree on 20% of questions or fewer. A 5 pp difference would need about 626 pairs ([McNemar sizing](https://metricgate.com/docs/sample-size-mcnemar-paired)). Repeats help, with quickly diminishing returns. Under Miller's variance model, two profiles that share a model and differ only in tools (correlation of about 0.8) have a minimum detectable effect of roughly **12.3 pp at K=1, 8.2 pp at K=3 and 7.1 pp at K=5**. Profiles on different models (correlation of about 0.5) sit at roughly 10.2 pp at K=3 ([Miller 2024](https://arxiv.org/html/2411.00640v1)). These figures assume an even split between between-question and within-question variance, which the pilot must measure. They lead to four practical rules. Use K=3 by default and K=5 for pre-registered headline pairs. Treat per-tier results as descriptive, since 20–30 items per tier gives minimum detectable effects above 15 pp. Use clustered standard errors by patient, because Miller found clustered errors up to about 3× the naive ones. Reaching 5 pp means growing toward 300–600 questions with more seeds, not adding repeats.

**Recommended profile matrix.** The model is held fixed for the tool and skill comparisons, which keeps the correlation high and the detectable effect small.

| ID | Model | Tools | Skill / prompt | Role |
|---|---|---|---|---|
| B0 | — | Oracle | — | Must score 100% |
| B1 | — | Always `insufficient_data` | — | Floor ≈ 5% |
| B2 | Model A | None (question + context only) | Default | Guessing / knowledge floor |
| P1 | Model A | Generic agent shell: file read, grep, Python | Default | Baseline harness |
| P2 | Model A | P1 + full cli-on-fhir MCP (pinned commit) | Default | **Primary: tool effect (P2 − P1)** |
| P2a | Model A | P1 + cli-on-fhir with the terminology and `sql_view`/FHIRPath groups disabled | Default | Tool-group ablation: which tool groups earn their keep (read against the `TOOL+` tags) |
| P3 | Model A | P2 | FHIR skill: search playbook, status/unit/terminology checklist, calculation conventions | **Primary: skill effect (P3 − P2)** |
| P4 | Model A | cli-on-fhir MCP only, no shell or code | Default | Does the MCP replace code execution? (FHIR-AgentBench code effect) |
| P5 | Model B (smaller or cheaper) | As P2 | Default | **Primary: model effect** |
| P6 | Model A | P2 | MedAgentBench-v2-style plan/verify prompt | Prompt effect (secondary) |

The three primary contrasts are pre-registered. The rest use Holm–Bonferroni correction. **Uncertain:** P1 is a code-capable shell, and FHIR-AgentBench's evidence says such a baseline is already strong. The MCP's benefit may show up more in **cost per success, turns, tokens and consistency (pass^k, flip rate)** than in pass@1. The bench should report all of these, and an accuracy–cost Pareto plot ([Kapoor et al.](https://arxiv.org/html/2407.01502)). tau-bench shows why consistency deserves headline status: gpt-4o's 61% pass^1 in retail fell below 25% at pass^8 ([tau-bench](https://arxiv.org/html/2406.12045)).

**Pilot and calibration.** Over-generate about 220 candidates and run two contrasting profiles, weak (P5 without the MCP) and strong (P3), at K=3. Drop items that every run passes or every run fails. Read the transcripts of anything that fails every time, since that usually means a broken task, and of anything only the weak profile passes, since that usually means a shortcut ([ABC](https://arxiv.org/html/2507.02825); [Anthropic evals](https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents)). Prefer items that discriminate and have low run-to-run variance, in the spirit of Ai2's signal-to-noise filtering ([Signal and Noise](https://arxiv.org/html/2508.13144v1)). Keep a few validated items above the strong profile as headroom. Then estimate discordance, correlation and within-question variance, recompute power, and freeze the tier labels before any reported run.

## Ship a real agent run in phase 0 and defer the platform

The C# platform spec, with its React UI, container and bring-your-own token, is an 18-task plan with nothing checked off. Meanwhile no real agent has ever been run against the bench. The order should be reversed: prove the measurement loop, then build infrastructure around a bench that has shown it produces a signal.

| Keep from PR #1 | Drop or demote |
|---|---|
| Typed answer types including abstain → becomes the `submit_answer` envelope | The 151-resource corpus as the bench corpus → demote to a harness smoke-test fixture |
| Oracle-computed gold → independent Python oracle | Questions naming resource ids (56/100) → rewrite into clinician voice or drop |
| Evidence grounding → gold evidence ids + retrieval recall diagnostic | Invented systems (`urn:bench:qa:*`) and toy ConceptMaps in prompts |
| Run-profile concept and paired comparison → the profile matrix above | Grading-contract language inside prompts → moves to the harness answer-spec field |
| Runner isolation safeguards (agent cannot read gold) | The 50 operational tasks → a separate, later "FHIR operations" track, not part of the 150 |
| The Python prototype as the phase 0–4 runner | The fake Copilot process; the C# platform build until after Gate 3 |

Salvage the existing 100 questions as *template ideas*. Any that survive the rewrite go into the candidate pool and are verified like every other candidate.

| Phase | Work | Go/no-go gate |
|---|---|---|
| 0. Prove the loop | Pick the primary harness. Assume the full cli-on-fhir surface and pin its commit. Add the `submit_answer` tool. Load the pinned Synthea sample as an interim folder. Run P1 and P2 on about 10 hand-checked questions. Confirm cli-on-fhir indexes about 120k resources in practical time | Real trajectories logged for both profiles. Grader scores them. Agent provably cannot read gold. MCP launches and answers at scale. Any scale problems are logged as tool findings |
| 1. Corpus v1 | Custom pinned Synthea run (seed, dates, jar, keep modules). Needles N1–N12 via seeded ScenarioBuilder. Mutation layer with manifest. Commit files + sha256. Write the oracle library | Committed hash. Oracle confirms every needle fact and manifest entry. `folder_inspect` reports the injected duplicates and broken references. A read-everything agent cannot fit even a median patient |
| 2. Question bank | About 60 templates → about 220 candidates in clinician voice. Double derivation. Grader CI variants. Build the second seed for the 50 holdout items and the 30 dev items | 100% oracle pass. Zero unresolved derivation disagreements. Every question is tool-reachable. Wording reviewed (ideally by a clinician) |
| 3. Pilot | Weak vs. strong at K=3 on the pool. Outlier transcript review. SNR filter. Re-run the power analysis | Strong − weak ≥ 20 pp overall. No tier at 0% or 100%. Projected MDE for P2 − P1 ≤ 10 pp at K=5. **If P1 ≈ P2 with high agreement, investigate before scaling. Either the corpus is still too easy, or the tool's benefit is cost-only. The per-`TOOL+` breakdown is itself tool-validation output** |
| 4. Freeze and run | Freeze 150 + 30 + 50. Run the full matrix (K=3; K=5 for primaries). Report with clustered CIs, pass^k, flip rate, cost per success | Results reproducible from pinned corpus, tool commit and model versions |
| 5. Extend (optional) | C# platform/UI. Action tasks (cli-on-fhir's planned `draft_resource` and `asOf` clock). Grow to 300+ questions for 5 pp resolution | Driven by phase 4 findings |

| Open decision for the user | Recommendation | Confidence |
|---|---|---|
| Base corpus: pinned sample zip or custom Synthea run | Custom run with pinned seed and keep modules; the sample zip only for phase 0 (its generation parameters are unknown) | Recommended |
| Keep Claim/EOB distractors | Keep, unless phase 0 shows indexing cost is prohibitive | Uncertain |
| Synthea output and SNOMED/LOINC display licensing for publication | Verify before publishing; until then keep the repo private or publish questions only | Open, not researched |
| Clinician reviewer | Strongly preferred for wording and rule questions; otherwise label the bench "engineer-reviewed" | Recommended |
| Primary harness (Claude Code / Agent SDK, Copilot CLI, other) | One primary harness for phases 0–4; a second only after freeze | Recommended |
| Read-only scope for v1 | Yes; defer MedAgentBench-style write/order tasks | Recommended |
| Frozen "now" and panel definition | "Now" = Synthea end date; panel = whole corpus | Recommended |
| Gold publication | Public test questions with a canary; holdout private | Recommended |
| Run budget | About 8 profiles × 150 × K=3, plus K=5 for primaries, plus pilot; estimate cost in phase 0 | Uncertain |

## Conclusion

The plan changes what "difficulty" means for this bench. In PR #1, difficulty was written into the prompts as tiers and contracts. In the revised bench it lives in the data: records too large to read, references that must be resolved, results that were entered in error, and units that disagree. Those traps are injected on purpose and recorded in a manifest. As a result, every question's difficulty can be traced to a specific corpus mechanism, and every tool or skill can be judged by whether it neutralises that mechanism. Paired with per-question gold dependencies, this gives the bench diagnostic power beyond a single accuracy number.

The most important uncertainty is not statistical but competitive. A modern agent with a shell and Python is a strong baseline over a folder of JSON. On generic retrieval, the cli-on-fhir MCP may show its value mainly as lower cost, fewer turns and more consistent answers rather than higher pass@1. Its clearest accuracy gains should come from the terminology-subsumption, FHIRPath and cohort (`sql_view`) questions, where a shell has no ready answer. The per-tool-group tags and the P2a ablation exist to show exactly where the tool earns its keep, without assuming that it does. That is why phases 0 and 3 are hard gates, and why the C# platform waits until the measurement has shown it can tell profiles apart.
