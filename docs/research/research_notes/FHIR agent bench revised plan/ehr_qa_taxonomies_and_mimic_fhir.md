# EHR QA Benchmarks, Question Taxonomies, and MIMIC-IV on FHIR

Scope: question templates, complexity dimensions and answer types in existing EHR QA benchmarks, plus what MIMIC-IV FHIR data is available. The goal is to design 150 FHIR questions that can be graded deterministically. Research date: 2026-10-15.

## EHRSQL (NeurIPS 2022) and the EHRSQL 2024 shared task

### Takeaway
EHRSQL is the best source of real clinician questions over structured EHR data. It has 230 templates (174 answerable, 56 unanswerable) taken from 1,742 utterances by 222 hospital staff. The templates are organised by **patient scope** (none / single / group) and crossed with a separate **time-template grammar**: global / within / exact filters, each with an expression type, unit, interval and option. The 2024 shared task moved EHRSQL to the MIMIC-IV demo with 100 patients, the same data behind MIMIC-IV-FHIR-Demo. It added adversarial unanswerable questions and scored with an abstention-aware "Reliability Score".

### Cited Findings
**Collection**
- Questions came from a poll at Konyang University Hospital. Staff were asked to phrase what they "frequently look for in structured information in the EHR" as if asking an AI speaker. The poll gave negative examples of what not to ask: questions needing external knowledge, ambiguous or qualitative statements, and reasons behind clinical decisions. It collected 1,742 utterances from 222 valid respondents, including physicians, nurses, insurance review and health records teams. — [EHRSQL paper, arXiv 2301.07695](https://arxiv.org/abs/2301.07695) (§3, read from the PDF text)
- Utterances were sorted into **three patient scopes: single patient, group of patients, no patient**. Duplicate utterances phrased differently were merged into one template. The authors also added templates by swapping event types, e.g. "Count the number of times that a patient had a lab test" became "...received a prescription". They chained two event types to cover longitudinal statistics, e.g. "top N prescriptions after a diagnosis" became "top N lab tests after a diagnosis". — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695)
- Result: **230 question templates (174 answerable, 56 unanswerable)** over the MIMIC-III and eICU schemas. There are about **24.4K instances (22.5K answerable, 1.9K unanswerable)**. Templates have on average about 21 human paraphrases each, and about 47 per template once machine paraphrases are added. — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695)
- Table 1 groups sample questions into "question diversity" types (Demographics, Prescription, Vital sign, Cost, Survival rate, Longitudinal statistics), "time-sensitive" types (Absolute, Relative, Mixed) and "unanswerable" types (Requiring external knowledge, Beyond DB schema). Verbatim examples:
  - Demographics: "Tell me the birthdate of patient 92721?"
  - Prescription: "When was the first prescription time of patient 20000?"
  - Vital sign: "What was the last arterial bp [systolic] for patient 23042?"
  - Cost: "What is the average total hospital cost that involves non-invasive mech vent?"
  - Survival rate: "What are the five diagnoses which have the lowest four year survival rate?"
  - Longitudinal: "What are the five most commonly prescribed drugs for patients who have been diagnosed with hypotension nos earlier since 2104 within 2 months?"
  - Absolute time: "Is the heart rate of patient 2518 measured at 2105-12-31 12:00:00 less than the value measured at 2105-12-31 09:00:00?"
  - Relative time: "Tell me the last specimen test given to patient 51177 in 10/2105?" and "Tell me patient 6990's daily average gastric meds intake on the last ICU visit."
  - Mixed: "Tell me the medication that patient 3929 has been prescribed for the first time in 05/this year."
  - Unanswerable (external knowledge): "What is the name of the medication which should not be administered during the contrast arteriogram-leg treatment?"
  - Unanswerable (beyond schema): "When is the next earliest hospital visit of patient 73652?"
  — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695)
- Verbatim templates from Table 7, all answerable:
  - None scope: "What is the intake method of {drug_name}?"; "What is the cost of a procedure named {procedure_name}?"; "What does {abbreviation} stand for?"
  - Single scope: "What is the gender of patient {patient_id}?"; "What is the date of birth of patient {patient_id}?"; "What was the [time_filter_exact1] length of hospital stay of patient {patient_id}?" (assumption: only current patients)
  - Single scope, comparisons: "What is the change in the value of {lab_name} of patient {patient_id} from the [time_filter_exact2] value measured [time_filter_global2] compared to the [time_filter_exact1] value measured [time_filter_global1]?"; "Is the {vital_name} of patient {patient_id} [time_filter_exact2] measured [time_filter_global2] [comparison] than the [time_filter_exact1] value measured [time_filter_global1]?"
  - Single scope, attributes and percentiles: "What is_verb the marital status of patient {patient_id} [time_filter_global1]?"; "What percentile is the value of {lab_value} in a {lab_name} lab test among patients of the same age as patient {patient_id} [time_filter_global1]?"
  - Single scope, elapsed time: "How many [unit_count] have passed since patient {patient_id} was admitted to the hospital currently?"; "How many [unit_count] have passed since the [time_filter_exact1] time patient {patient_id} was prescribed {drug_name} on the current hospital visit?"
  - Group scope: "Count the number of hospital visits of patient {patient_id} [time_filter_global]."; "What are the top [n_rank] frequent procedures that patients received [time_filter_within] after having been diagnosed with {diagnosis_name} [time_filter_global]?"; "What are_verb the top [n_rank] frequently prescribed drugs that {gender} patients aged [age_group] were prescribed [time_filter_within] after having been diagnosed with {diagnosis_name} [time_filter_global1]?"; "What are_verb the top [n_rank] frequent intake events [time_filter_global1]?"
  - Unanswerable: "What is the effect of {drug_name}?"
  — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695) (Table 2, Table 7)
- Unanswerable topics from the poll included side effects, next check-up schedules, primary care doctor, the patient's personal information, whether a consent form was signed, and diagnoses received in other departments. — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695)
- **Time grammar.** There are three filter types: `[time_filter_global]` (overall range), `[time_filter_within]` (window between two events, e.g. "within the same hospital visit", "within the same day", "within 2 months") and `[time_filter_exact]` (e.g. "first", "last", "at 2105-12-31 09:00:00"). Each filter has an **Expression type** (absolute / relative / mixed), a **Unit** (year, month, day, or an event unit such as a hospital visit or ICU visit) and an **Interval type** (since / until / in). An optional **Option** selects first / last / this. — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695) §3.1.2
- Verbatim NL time expressions from Table 8: "since {month}/{day}/{year}", "last year", "this year", "until last year", "since last year", "until {year} year ago", "since {year} year ago", "last month", "this month", "until {month} month ago", "on the first hospital visit", "on the last hospital visit", "on the current hospital visit", "on the first ICU visit", "on the last ICU visit", "on the current ICU visit", "within the same hospital visit", "within the same day", "first". — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695) Table 3/8
- Hidden semantics are encoded in SQL. "Last hospital visit" means `dischtime IS NOT NULL ORDER BY admittime DESC LIMIT 1`, which excludes the ongoing visit. "Current hospital visit" means `dischtime IS NULL`. Templates carry explicit "Assumption" notes (only current patients / only discharged). Length of stay differs for current and discharged patients. — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695) App. B
- **Time shifting.** Admissions were shifted into 2100–2105 and "now" was fixed at "2105-12-31 23:59:00". Records after "now" were removed, so patients with a null discharge become "current" patients. The poll showed that many hospital time expressions are relative ("today, yesterday, last month"). — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695) §3.1.3
- Values were shuffled across patients for de-identification, so the condition values are untraceable. — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695); [EHRSQL GitHub](https://github.com/glee4810/EHRSQL)
- Complexity: zero-shot GAP scored 16.4% on MIMICSQL but 4.7% on the Spider-parsable subset of EHRSQL. Only about 7% (107/1,515) of answerable EHRSQL validation queries are parsable with the Spider grammar, mostly because of `strftime`, `datetime` and `NULL`. — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695) Table 6
- Error source: "In most cases, the errors do not come from generating complex, long nested queries, but from failures in schema linking (identifying references of columns, tables, and condition values)". Example: the T5 model generated `drug = 'ingestion of danazol'`. — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695) App. H.1
- The dataset is distributed under CC BY 4.0. The repo lists v1.5.0 (03/09/2026, improved annotation quality) and newer MIMIC-IV-demo data (06/22/2024). Item fields include `template`, `q_tag`, `t_tag`, `o_tag`, `department`, `importance`, `is_impossible`. Unanswerable items have `query = "nan"`. — [EHRSQL GitHub](https://github.com/glee4810/EHRSQL)
- **2024 shared task** (ClinicalNLP @ NAACL 2024). It ran on the **MIMIC-IV demo (v2.2, 100 patients)** so that participants could use commercial APIs. Splits:

  | Split | Answerable | Unanswerable | Answerable templates |
  |---|---|---|---|
  | Train | 4,674 | 450 | 100 |
  | Valid | 931 | 232 | 134 (34 unseen) |
  | Test | 934 | 233 | 134 (34 unseen) |

  Adversarial unanswerable questions were added from TrustSQL: questions that reference non-existent columns, and requests beyond SQL's capabilities. — [Overview, arXiv 2405.06673](https://arxiv.org/html/2405.06673v2)
- **Reliability Score.** A correct answer scores +1, a correct abstention on an unanswerable question +1, an abstention on an answerable question 0, and a wrong SQL or an answered unanswerable question −c. RS(0), RS(10) and RS(N) were reported. Results:
  - The top team scored RS(0) 88.17 and RS(10) 81.32.
  - No team scored positively on RS(N).
  - The Abstain-All baseline scores 20%.
  — [arXiv 2405.06673](https://arxiv.org/html/2405.06673v2)

### Inferences
- EHRSQL's structure maps directly onto a FHIR taxonomy with two parts: question template × time template × scope, plus an unanswerable/empty class. Its time grammar (first/last/current visit, since/until/in, within-window) can be reused as a ready-made difficulty dimension.
- The "current visit" and "last visit" semantics depend on a fixed "now". A FHIR benchmark must define its reference time explicitly, or relative-time questions cannot be graded deterministically.
- Answers are SQL execution results: scalars, lists, booleans and dates. These answer types can be graded deterministically.

### Gaps
- I did not extract the full 174-template list or the exact category counts per scope from Table 7. They are in the PDF appendix B.1, pages 22–24.
- The 2024 overview gives no verbatim examples of the adversarial unanswerable questions.

## Other EHR QA datasets (MIMICSQL/TREQS, emrQA, emrKBQA, EHRXQA, EHRNoteQA, EHRCon, EHR-SeqSQL, CliniQG4QA)

### Takeaway
The structured-data benchmarks are MIMICSQL/TREQS, emrKBQA, EHRSQL, EHR-SeqSQL, EHRXQA (table part), FHIR-AgentBench and the EHRAgent evaluation sets. The note-based ones are emrQA, EHRNoteQA, CliniQG4QA and the note side of EHRCon. The most common complexity axis in prior work is **the number of tables or resources joined**. EHRAgent levels I–IV show success falling from about 72% to about 49% as tables are added. Other axes are SQL nesting, time-expression type, and sequential or compositional turns.

### Cited Findings
- **MIMICSQL / TREQS** (Wang et al., 2020): 10,000 question-SQL pairs over 5 tables from MIMIC-III (more than 40K patients). Each pair has a **template question and a natural-language question**. Some questions involve more than one table. The code is at github.com/wangpinggl/TREQS. — [arXiv 1908.01839](https://arxiv.org/pdf/1908.01839)
- In EHRSQL's comparison, MIMICSQL has 1.8 tables per query, a nesting depth of 1.0 per query, and 26.6% of queries use time. GAP scored 46.7% easy / 10.5% medium / 0.0% hard on it (Spider hardness). emrKBQA has 940K examples, 9 tables, and was not publicly released at that time. — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695) Table 4/6
- **EHR-SeqSQL** (ACL Findings 2024): built on MIMIC-III EHRSQL. It has 9,195 interactions and 31,669 turns, about 3.5 turns per interaction. Question categories:

  | Category | Count | Share |
  |---|---|---|
  | Independent | 14,005 | 44.22% |
  | Referential | 11,560 | 36.50% |
  | Filtering | 5,947 | 18.78% |
  | Modifying | 507 | 1.16% |

  Interactions are built by decomposing nested SQL, WHERE/ORDER BY/HAVING clauses and aggregations into turns. "Modifying" questions are "particularly challenging". — [arXiv 2406.00019](https://arxiv.org/html/2406.00019v3)
- **EHRXQA** (NeurIPS 2023 D&B): multimodal QA that combines EHRSQL (MIMIC-IV tables) with MIMIC-CXR-VQA images. It requires uni-modal and cross-modal reasoning. Access is credentialed (MIMIC-IV v2.2, MIMIC-CXR-JPG, Chest ImaGenome). Answers are script-generated strings. The PhysioNet release is v1.0.0 (07/24/2024). — [arXiv 2310.18652](https://arxiv.org/abs/2310.18652); [EHRXQA GitHub](https://github.com/baeseongsu/ehrxqa)
- **EHRNoteQA**: 962 QA pairs over MIMIC-IV discharge summaries. Questions were generated by GPT-4 and reviewed by clinicians. Some need multiple summaries. It comes in open-ended and multiple-choice formats. It covers notes only, not structured data. — search summary of [arXiv 2402.16040](https://arxiv.org/pdf/2402.16040v3) (abstract-level only)
- **EHRCon**: checks consistency between notes and database tables. It has MIMIC-III and OMOP CDM versions, with the CheckEHR LLM framework. — search summary (abstract-level only; see the Gyubok Lee pages on [catalyzex](https://catalyzex.com/author/Gyubok%20Lee))
- **EHRAgent** (EMNLP 2024): the evaluation sets are MIMIC-III (580 Qs, 17 tables), eICU (580, 10 tables) and TREQS (996, 5 tables), all derived from EHRSQL/MIMICSQL. Complexity is defined by **the number of tables needed**: Level I–IV on MIMIC-III, I–III on the others. EHRAgent success rates by level:

  | Dataset | I | II | III | IV | Overall | Best baseline |
  |---|---|---|---|---|---|---|
  | MIMIC-III | 71.58 | 66.34 | 49.70 | 49.14 | 58.97 | 39.05 |
  | eICU | 54.82 | 53.52 | 25.00 | – | 53.10 | 40.69 |
  | TREQS | 78.94 | 61.16 | 27.27 | – | 69.70 | 40.10 |

  The paper says most errors came from failing to find the root cause of an error within the step limit. — [EHRAgent, arXiv 2401.07128v3](https://arxiv.org/html/2401.07128v3)

### Inferences
- For a FHIR benchmark, "number of distinct resource types or reference hops needed" is the natural equivalent of "number of tables". Examples: MedicationRequest→Medication, Observation→Encounter→Location.
- None of the structured benchmarks above use free-text-graded answers. They all reduce to execution results, which supports deterministic grading.

### Gaps
- I did not fetch the primary papers for emrQA (Pampari et al., 2018), emrKBQA (Raghavan et al., 2021), CliniQG4QA, EHRCon or EHRNoteQA. Their exact template counts, sizes and licences are unverified here. MedAlign's Table 1 cites Pampari et al. with "73111" questions over 303 discharge summaries, but that comes from a partially garbled table.
- I did not obtain the EHRAgent error-category percentages (Figure 6 is an image).

## MedAlign (Stanford): what clinicians actually ask

### Takeaway
MedAlign has 983 de-duplicated instructions (from 1,314 submitted) by 15 clinicians across 7 specialties, over 276 longitudinal EHRs in OMOP format. About two-thirds (66%) of instructions are "Retrieve & Summarize" and only about 17% look like exam-style (USMLE) questions. GPT-4 was wrong 35–40% of the time.

### Cited Findings
- Statistics: 1,314 instructions were collected. Near-duplicates (ROUGE-L > 0.7) were removed, leaving 983. There are 276 EHRs and 303 clinician-written gold responses. The taxonomy has 6 categories and 20 subcategories, assigned by a practicing clinician. — [MedAlign, arXiv 2308.14089](https://arxiv.org/abs/2308.14089) (App. D, Table S1)
- Category counts (Gold / All) with verbatim example instructions:

  | Category | Gold | All | Example instruction |
  |---|---|---|---|
  | Retrieve & Summarize | 223 | 667 | "Summarize the most recent annual physical with the PCP" |
  | Care Planning | 22 | 136 | "Summarize the asthma care plan for this patient including relevant diagnostic testing, exacerbation history, and treatments" |
  | Calculation & Scoring | 13 | 70 | "Identify the risk of stroke in the next 7 days for this TIA patient" |
  | Diagnosis Support | 4 | 33 | "Based on the information I've included under HPI, what is a reasonable differential diagnosis?" |
  | Translation | 0 | 2 | — |
  | Other | 41 | 75 | "What patients on my service should be prioritized for discharge today?" |

  Treemap shares: Retrieve & Summarize 66.29%, Care Planning 16.74%, Calculation & Scoring 6.77%, Other 6.47%, Diagnosis Support 3.58%. — [arXiv 2308.14089](https://arxiv.org/abs/2308.14089) Table 2, Fig. S2
- Subcategory counts (All):
  - Retrieve Medical History 335
  - Determine Plan of Care 220
  - Retrieve Imaging Studies 111
  - Retrieve Demographics 107 (described as "demographic, insurance information, code status, power of attorney, emergency contact")
  - Retrieve Medication Information 99
  - Other 85
  - Retrieve Laboratory Studies 61
  - Calculate Numerical Measures 47 ("BMI, TIMI, CHADS2VASC, ABCD2Score")
  - Provide Diagnosis Assistance 47
  - Provide Risk Assessment 42
  - Retrieve Social History 34
  - Provide Patient Education 31
  - Retrieve Vitals 24 ("blood pressure, heart rate, respiratory rate, O2 sat, input/output…")
  - Retrieve Appointment Information 23
  - Retrieve Genetic and Family History 19
  - Retrieve Institutional Policies 15
  - Retrieve Provider Referrals 7

  — [arXiv 2308.14089](https://arxiv.org/abs/2308.14089) Table S2
- "USMLE-style questions only comprise 17% of the instructions … while 68% of instructions involve retrieving and summarizing data from the EHR." — [arXiv 2308.14089](https://arxiv.org/abs/2308.14089)
- Clinician-judged correctness:

  | Model | Context | Correct |
  |---|---|---|
  | GPT-4 + MR | 32k | 65.0% |
  | GPT-4 | 32k | 60.1% |
  | GPT-4 | 2k | 51.8% |
  | Vicuña-13B | 2k | 35.0% |
  | Vicuña-7B | 2k | 33.3% |
  | MPT-7B-Instruct | 2k | 31.7% |

  More than 80% of EHRs exceed 32k tokens. — [arXiv 2308.14089](https://arxiv.org/abs/2308.14089)
- Data is in OMOP CDM, from Stanford. Access is under a research data use agreement (CITI training + DUA) at medalign.stanford.edu. It is not openly redistributable. — [arXiv 2308.14089](https://arxiv.org/abs/2308.14089)

### Inferences
- MedAlign's retrieval subcategories (demographics, medications, labs, vitals, history, encounters/appointments, social history) form a frequency-weighted list of what to cover. Calculation & Scoring (BMI, CHADS2-VASc) is a deterministically gradable "complex" tier.
- Summarize, Care Planning and Diagnosis Support cannot be graded deterministically. A deterministic FHIR benchmark should turn "summarize X" into list or set extraction (e.g. "list active medications on date D").

### Gaps
- The 7 specialties are named only partly in the extracted text (e.g. Internal Medicine). The full list was not captured.

## MIMIC-IV on FHIR: licence, resources, codes, quirks, redistribution

### Takeaway
The **demo** (100 patients, v2.1.0, 49.5 MB) is under **ODbL v1.0**: open and redistributable with attribution and share-alike for adapted databases. The **full** MIMIC-IV-FHIR (v2.1) is credentialed (PhysioNet Credentialed Health Data License 1.5.0 + CITI + DUA). Codes are mostly **local MIMIC code systems**, not LOINC, which breaks LLM-assumed LOINC queries. Concrete evidence: FHIR-AgentBench agents searched for LOINC 9279-1 for respiratory rate and missed the chartevents code.

### Cited Findings
- **Demo**: "MIMIC-IV Clinical Database Demo on FHIR" v2.1.0, released Aug 27 2025, DOI 10.13026/vphg-y548, **Open Data Commons Open Database License v1.0**. It is a 100-patient subset of full MIMIC-IV on FHIR, sourced from MIMIC-IV demo v2.2 and MIMIC-IV-ED demo v2.2. Size is 49.5 MB uncompressed. — [PhysioNet demo page](https://physionet.org/content/mimic-iv-fhir-demo/)
- The demo has 27 resource files, as gzipped NDJSON with one resource per line, e.g. `MimicPatient.ndjson.gz`:
  - Organization, Location, Patient, Encounter, EncounterED
  - Specimen
  - ObservationLabevents, ObservationMicroTest, ObservationMicroOrg, ObservationMicroSusc
  - Medication, MedicationAdministration, MedicationAdministrationICU, MedicationDispense, MedicationDispenseED, MedicationStatementED, MedicationRequest
  - ObservationChartevents, ObservationDatetimeevents, ObservationOutputevents
  - ProcedureICU, ObservationED, ObservationVitalSignsED
  - Condition, ConditionED, Procedure, ProcedureED

  All resources follow FHIR R4. — [PhysioNet demo page](https://physionet.org/content/mimic-iv-fhir-demo/); [README_DEMO](https://physionet.org/content/mimic-iv-fhir-demo/2.1.0/README_DEMO.md)
- **Full**: MIMIC-IV on FHIR v2.1, published Nov 12 2024 (v1.0 was Feb 20 2024), DOI 10.13026/rrj1-ny66. It uses the **PhysioNet Credentialed Health Data License 1.5.0**: "Only credentialed users who sign the DUA can access the files". CITI "Data or Specimens Only Research" training is required. — [PhysioNet mimic-iv-fhir](https://physionet.org/content/mimic-iv-fhir/)
- **Implementation guide**: https://kind-lab.github.io/mimic-fhir/. It has 22 profiles in Methods vs 24 in the Abstract (plus 6 ED profiles), 64 terminology resources and 2 extensions, with US Core R4 as the base where possible. The page lists 41 Location resources (one per care unit). — [PhysioNet mimic-iv-fhir](https://physionet.org/content/mimic-iv-fhir/); [demo page](https://physionet.org/content/mimic-iv-fhir-demo/)
- **Coding**:
  - Labs use the original MIMIC `itemid` values; "LOINC was not used".
  - Medications use formulary drug codes, GSN and names. NDC was not used because of missing values.
  - Diagnoses and procedures use ICD-9/ICD-10.
  - SNOMED CT is used only for the ED triage event.
  - LOINC appears in vital-sign observations (release notes mention LOINC 2708-6).
  - Named local code systems include cs-diagnosis-icd9, cs-diagnosis-icd10, cs-medication-etc and cs-unit (mimic-units).

  — [PhysioNet mimic-iv-fhir](https://physionet.org/content/mimic-iv-fhir/); [demo page](https://physionet.org/content/mimic-iv-fhir-demo/)
- **Quirks**:
  - Patient birthDate is estimated from anchor_age and transfer intime, and birth sex comes from the gender column.
  - Microbiology is split into three linked resources (Test → Org → Susc).
  - MedicationRequest takes a single Medication, so mixes are modelled as medication mixes.
  - Only inpatient encounters exist.
  - v2.1 fixed display-name mismatches, blank values, and array wrapping of medicationCodeableConcept.
  - The PhysioNet text inconsistently says the export changed to "csv" while it also describes NDJSON.

  — [PhysioNet mimic-iv-fhir](https://physionet.org/content/mimic-iv-fhir/); [demo page](https://physionet.org/content/mimic-iv-fhir-demo/)
- **ODbL v1.0 summary**: you may share, create works from and adapt the database. Conditions:
  - **Attribute** any public use and keep notices intact.
  - **Share-alike**: an adapted database that is publicly used must be offered under ODbL.
  - **Keep open**: a DRM'd version must be accompanied by an unrestricted one.

  — [ODbL summary](https://opendatacommons.org/licenses/odbl/summary/)
- **FHIR-AgentBench** (Verily/KAIST/MIT, ML4H 2025, PMLR 297) is the closest prior art.
  - **Size**: 2,931 questions derived from EHRSQL-2024, keeping single-patient questions only and dropping those needing data absent from FHIR. Answers were re-executed on raw MIMIC-IV with anchor years restored and casing un-normalised. About 24% of answers are empty, kept deliberately.
  - **Load**: questions average 14.5 ground-truth resources each, with a range of 0–2,257. About 80% need 0–2 resources. A patient averages about 9K FHIR resources, roughly 3M tokens.
  - **Data**: the experiments use the MIMIC-IV-FHIR-Demo.
  - **Grading**: answers are free text graded by an LLM judge (o4-mini), with about 97% agreement with humans on 500 samples. Retrieval precision and recall are computed over resource IDs.
  - **Best result**: answer correctness was 0.50 for multi-turn + retriever + code (o4-mini) and 0.20–0.33 for the other setups.
  - **Correctness by resource type** (o4-mini, multi-turn + code; per-group n not reported): Condition 1.00, Location 0.86, Procedure 0.81, Encounter 0.79, Empty 0.74, Patient 0.57, Observation 0.37, Medication/MedicationRequest 0.14.
  - **Verbatim examples**: "When was the first time the respiratory rate of patient 10018081 was measured to be less than 23.0 today?"; "When has patient 10018845 been given phosphate test for the last time?"; "Was patient 10018328 diagnosed with anything in the first hospital visit?"
  - **Repository**: github.com/glee4810/FHIR-AgentBench.

  — [FHIR-AgentBench, arXiv 2509.19319v2](https://arxiv.org/html/2509.19319v2); [PMLR](https://proceedings.mlr.press/v297/lee26a.html); [Verily post](https://verily.com/perspectives/Introducing-FHIR-AgentBench)

### Inferences
- **Redistribution**: under ODbL the demo NDJSON (or a FHIR server loaded with it) can be redistributed with attribution and a licence notice. A benchmark's question/answer files are probably "Produced Works", which need attribution but probably not share-alike. Any modified or cleaned dataset we publish must stay ODbL. This is not legal advice. The full ODbL text defines these terms, and I did not verify the definitions. Never mix in the credentialed full dataset.
- **Overlap with FHIR-AgentBench**: our benchmark would overlap with it, since both use EHRSQL-derived questions on the same demo. To differ, we could add deterministic grading (typed answers instead of an LLM judge), group/cohort questions (which FHIR-AgentBench excluded), and graded complexity tiers.
- **Coding quirk as a test axis**: local codes are a built-in test of "terminology grounding". Questions phrased in clinical language ("SpO2", "potassium") must be resolved to MIMIC itemid displays. FHIR-AgentBench had to add hints such as "SpO2 → oxygen saturation pulse oximetry".

### Gaps
- I did not obtain resource counts per type for the demo. We should count them locally from the NDJSON files.
- PhysioNet does not state the number of patients in full MIMIC-IV-FHIR.
- The licence of the FHIR-AgentBench repository itself was not confirmed.

## Evidence on where LLMs fail over EHR data

### Takeaway
Consistent failure points across the benchmarks:
- schema or terminology linking, i.e. finding the right code or label
- choosing the right resource type
- following references across resources (multi-hop)
- temporal semantics ("last" or "current" visit, relative time)
- large record volumes and long context
- deciding when to abstain or return an empty answer

Medication questions are the weakest category in FHIR (0.14). Correctness falls sharply as the number of tables or resources grows.

### Cited Findings
- **Schema linking** is the main error source in EHRSQL, not query complexity. — [arXiv 2301.07695](https://arxiv.org/abs/2301.07695)
- **Multi-table difficulty**: EHRAgent success drops from 71.6% (1 table) to 49.1% (4 tables) on MIMIC-III, and from 78.9% to 27.3% on TREQS (Level III). — [arXiv 2401.07128v3](https://arxiv.org/html/2401.07128v3)
- **FHIR-specific failures**:
  - searching the wrong resource type (Procedure instead of Observation for ostomy output)
  - LOINC-only queries that miss local codes
  - not following MedicationRequest→Medication references
  - not filtering for inpatient encounters
  - matching display text instead of codes
  - code bugs

  The authors name temporal reasoning, case-sensitive terminology, null answers and large record counts as challenges, but do not quantify them. — [arXiv 2509.19319v2](https://arxiv.org/html/2509.19319v2)
- **Abstention is hard**: no EHRSQL 2024 system scored positively under RS(N). A small gap between RS(0) and RS(10) predicted the top ranks. — [arXiv 2405.06673](https://arxiv.org/html/2405.06673v2)
- **Long context hurts**: GPT-4 lost 8.3 points of correctness going from 32k to 2k context. More than 80% of MedAlign EHRs exceed 32k tokens. — [arXiv 2308.14089](https://arxiv.org/abs/2308.14089)
- **Sequential follow-ups**: in EHR-SeqSQL, "modifying" questions that mention only the changed condition are the hardest. — [arXiv 2406.00019](https://arxiv.org/html/2406.00019v3)

### Inferences
- A proposed simple-to-complex taxonomy for 150 FHIR questions, graded deterministically by typed answers (scalar / boolean / date / code / list-set / count / empty):

  | Tier | Content | Example |
  |---|---|---|
  | T1 | Single-resource lookup | gender, birthDate |
  | T2 | Filtered lookup with first/last ordering | last potassium value |
  | T3 | Existence/negation, including expected-empty answers | "was patient diagnosed with X?" |
  | T4 | Aggregation | count, min/max/avg, daily average |
  | T5 | Temporal-window or relative time (EHRSQL time grammar) | — |
  | T6 | Comparison or change between two measurements | — |
  | T7 | Multi-hop across references | MedicationRequest→Medication, Observation→Encounter→Location |
  | T8 | Cohort/group | top-N after a diagnosis, percentile among same-age patients |
  | T9 | Calculation/scoring from multiple values | BMI, LOS, CHADS2-VASc |
  | T10 | Unanswerable / beyond-schema (abstain) | — |

  Orthogonal tags: terminology difficulty (LOINC vs local code), unit handling, and resource count. This taxonomy is a synthesis, not taken from any one source.

### Gaps
- None of the sources I fetched give a quantified breakdown for **unit-conversion** failures or **negation/absence** failures. FHIR-AgentBench reports 0.74 correctness on empty-answer questions, which is the closest proxy.
- I did not research MedAgentBench (Stanford, FHIR-based agent benchmark) or EHR-ChatQA/EHRSHOT in this pass, so their numbers are not included.
