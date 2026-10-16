# How Clinicians Phrase Questions to AI Copilots Over the Patient Chart

Research date: 2026-10-15. Labels used throughout:
- **[QUOTED]**: verbatim from the cited source.
- **[TEMPLATE]**: verbatim from a benchmark template, with placeholders.
- **[ADAPTED]**: written for this benchmark in the style of the cited source. It is not a real quote.

Tags for reasoning demands: `TEMP` = temporal reasoning (windows, most recent, trends, ordering); `AGG` = aggregation (count, average, min/max, cohort); `TERM` = terminology grouping (code hierarchies, value sets across LOINC/SNOMED/RxNorm/ICD); `UNIT` = units or conversion; `ABS` = absence of data, or the right answer is "not documented" or "never"; `CALC` = derived score or calculation; `MULTI` = joins across several resource types.

---

## 1. Microsoft Dragon Copilot (formerly DAX Copilot / Dragon Ambient eXperience): chart-query features and example questions

### Takeaway
Dragon Copilot's published prompt examples fall into three kinds. Some ask about the current encounter's note or transcript ("What medications does the patient take?"). Some are clinician self-checks ("Did I advise the patient to…?"). The rest are reference or guideline questions grounded in the patient ("What is the target A1c for this patient?"), routed through "Information Assist" to CDC, FDA or Merck/MSD Manuals. Microsoft's documentation says the prompt feature does **not** search the EHR or past sessions. Its phrasing is still a good model for short, pronoun-heavy, "this patient" questions.

### Cited Findings
- [QUOTED] Sample prompts about the note or transcript: "What medications does the patient take?", "What did the patient say about difficulty breathing?", "What medications did the patient mention during the visit for her chronic migraines?" — [Sample Dragon Copilot prompts (Microsoft Support)](https://support.microsoft.com/da-dk/dragon-copilot/physicians/sample-dragon-copilot-prompts)
- [QUOTED] Self-check prompts: "Did the patient mention taking (medication) during our discussion?", "Did I advise the patient to ____?" — [Use prompts to answer questions, make edits, and more (Microsoft Support)](https://support.microsoft.com/en-us/topic/use-note-assist-98da937e-b638-4a5a-92da-00a4161b2eb9)
- The search-result summary of the Microsoft pages says Dragon Copilot "cannot search your EHR or any past sessions for the patient", so patient questions are answered from the current encounter's note and transcript. I did not confirm this line on the fetched page itself, and the fetched "queries outside of your note" page says nothing either way about EHR access. — [Use prompts… (Microsoft Support)](https://support.microsoft.com/en-us/topic/use-note-assist-98da937e-b638-4a5a-92da-00a4161b2eb9); [Use AI prompts for queries outside of your note](https://support.microsoft.com/en-us/dragon-copilot/physicians/use-ai-prompts-for-queries-outside-of-your-note)
- Information Assist (reference queries) has to be enabled under Settings > Extensions > Information Assist, with at least one source selected (CDC, FDA, MSD/Merck Manuals named). A prompt can be prefixed with "Information Assist", "Info Assist" or "@InfoAssist". — [Use AI prompts for queries outside of your note](https://support.microsoft.com/en-us/dragon-copilot/physicians/use-ai-prompts-for-queries-outside-of-your-note)
- [QUOTED] Patient-contextual reference prompts: "What are the CDC recommendations for this patient for the shingles vaccine?"; "According to national guidelines, what blood pressure classification does this patient's blood pressure reports point to?"; "Can his medications cause orthostatic hypotension based on Merck Manuals?"; "What is the target A1c for this patient?"; "What does FDA say about Elidel?"; "Was there a recent tacrolimus recall?"; "According to the CDC - are the patient's symptoms compatible with long covid?"; "What is the next step for this patient to reduce statin related muscle pain?"; "Can this patient continue birth control pills based on guidance from Merck Manuals?"; "What are the recommended tests for Lyme disease?"; "What are the common radiographic findings in this type of arthritis?" — [Use AI prompts for queries outside of your note](https://support.microsoft.com/en-us/dragon-copilot/physicians/use-ai-prompts-for-queries-outside-of-your-note)
- The page is dated only by its footer "© Microsoft 2026". — same source

### Inferences
- The phrasing is short and deictic ("this patient", "his medications", "her chronic migraines"), with the patient context assumed. Benchmark prompts should keep the patient implicit rather than saying "patient with id X".
- Several prompts combine **chart data with a rule**: classify "this patient's blood pressure reports" against a guideline, or decide whether "his medications" cause orthostatic hypotension. In FHIR terms that is retrieving Observation (BP panel, LOINC 85354-9 with components) and MedicationRequest, then applying a threshold or drug-class rule. These are good `TERM` + `TEMP` prompts.
- "Did I advise the patient to…" and "Did the patient mention…" are absence-sensitive. The truthful answer is often "not documented" (`ABS`).

### Gaps
- I found no Microsoft documentation of a Dragon Copilot feature that queries the full longitudinal EHR, such as an Epic or Cerner FHIR chart query, as of Oct 2026. If one launched in 2026, I did not find it.

---

## 2. Epic (Art / Ask Art / Insights / Cosmos), Oracle Health Clinical AI Agent, Abridge, and others

### Takeaway
Vendors describe chart Q&A in general terms: natural-language questions answered with citations and visualizations. Almost none publish verbatim example questions. The only concrete scenarios I found are illustrative, such as pulling up a blood-pressure trend in a hypertension visit. Research benchmarks (MedAlign, EHRSQL, MedAgentBench, Ely) remain the best source of real phrasings.

### Cited Findings
- Epic: "Clinicians can ask Art questions about a patient using natural language", and answers come with citations and visualizations. Related features listed: Inpatient and Outpatient Insights (summaries that "reduce the need for manual chart review"), Medication Insights, Identify Care Gaps, Discharge Planning, Summarize Patient Problems for ED Providers, Prepare Nephrologists for Dialysis Patient Rounding, Transplant Evaluation Insights, Follow-Ups for Cardiac Patients, Identify Follow-Ups from Imaging Reports, Extract Social Drivers of Health, Cancer Staging. — [Epic: Art for Clinicians](https://www.epic.com/software/ai-clinicians/)
- Ask Art is described as letting clinicians "ask natural-language questions about a patient and receive concise answers, citations, and visualizations based on chart information." — [Imprivata knowledge hub](https://www.imprivata.com/de/node/108286)
- Tampa General was reported as the first to deploy Ask Art, with a small informatics pilot group and no formal metrics yet. The page returned 403 to my fetch, so this comes from the search summary only. — [Becker's](https://www.beckershospitalreview.com/healthcare-information-technology/ehrs/tampa-general-1st-to-deploy-ask-epic-tool/)
- Epic's roadmap lists "Ask Art with Your Voice" and "Organization protocols in Ask Art" for early adopters around Oct–Nov 2026 (search summary only). — [Becker's: 34 new Epic features](https://www.beckershospitalreview.com/healthcare-information-technology/innovation/34-new-epic-features-coming-and-when/)
- Illustrative scenario from Epic's R&D VP: during a hypertension discussion, Art "could pull up the patient's blood pressure graph, look at trends over time, highlight which medication changes would be covered by insurance…". Epic described this as future direction; currently Art is "more of a passive scribe". — [TechTarget: Epic's AI Charting goes live](https://www.techtarget.com/searchhealthit/news/366638795/Epics-AI-Charting-goes-live)
- Oracle Health Clinical AI Agent: lets physicians "ask questions in natural language to access patient details and perform frequent clinical workflows". Nurses can use natural-language voice commands to search the chart instead of navigating sections. The Aug 2026 expansion added chart review (pulling history, labs and medications across the EHR), coding and dictation. — [Oracle product page](https://go.oracle.com/LP=147219); [Techstrong](https://techstrong.ai/articles/oracles-ai-wants-to-give-nurses-something-they-rarely-have-more-time/); [Unite.AI](https://www.unite.ai/oracle-health-adds-automated-coding-dictation-and-chart-review-to-clinical-ai-agent/); [Fierce Healthcare](https://fiercehealthcare.com/ai-and-machine-learning/oracle-health-announces-expansion-clinical-ai-agent-capabilities)
- Abridge (Aug 17, 2026): extended its clinical intelligence agent to every clinician at partner health systems, with "chart-aware responses from validated medical literature, informed by the patient in the room". It also produces referral letters and handoffs "drawn from the patient record". The page gives no example questions. — [Abridge blog](https://abridge.com/blog/context-aware-clinical-intelligence-for-every-clinician-partner-systems)
- Abridge and Availity (Jan 12, 2026): real-time prior authorization using visit context and FHIR-based payer criteria. — [Abridge press release](https://abridge.com/press-release/abridge-availity-collaboration-announcement)

### Inferences
- Vendor framing points to prompt types worth covering: trend or visualization ("show me BP over time"), care gaps, medication insights, transitions of care, "summarize problems for ED", dialysis rounding prep, and prior-auth evidence ("does the chart support medical necessity for X").
- Prior-auth and payer-criteria questions are a distinctive 2026 category. They require checking whether documented conditions or failed therapies exist, which is `ABS` + `TERM` + `TEMP`.

### Gaps
- I found no verbatim Ask Art, Insights, Cosmos, Oracle or Abridge example questions. I did not research Glass Health, OpenEvidence, Google (AMIE/MedLM chart QA) or Hippocratic AI, since the tool budget went first to the sources above. OpenEvidence and Glass are mainly literature or differential tools, not chart-FHIR Q&A.
- I found no data on what clinicians actually type into Ask Art, such as usage logs or category distributions.

---

## 3. Studies of clinician information needs (Ely, MedAlign, EHRSQL, MedAgentBench, others)

### Takeaway
Research datasets give the most realistic phrasings. In **MedAlign**, about 68% of clinician instructions are "Retrieve & Summarize". **Ely** shows that point-of-care questions centre on treatment and drugs. **EHRSQL** shows that hospital staff, nurses and records teams included, ask single-patient, cohort and no-patient structured questions full of time expressions, and that some questions should be refused as unanswerable. **MedAgentBench** templates show the terse "MRN + most recent lab within last 24 hours" style that FHIR agents face.

### Cited Findings
**MedAlign (Stanford, 2023; AAAI 2024)**
- 983 instructions from 15 clinicians in 7 specialties, with clinician-written gold responses for 303, grounded in 276 longitudinal EHRs. — [MedAlign arXiv](https://arxiv.org/abs/2308.14089)
- Category distribution across all 983: Retrieve & Summarize 667 (~68%), Care Planning 136 (~14%), Calculation & Scoring 70 (~7%), Diagnosis Support 33 (~3%), Translation 2, Other 75. There are 20 subcategories, e.g. Retrieve Medical History, Determine Plan of Care, Calculate Numerical Measures. — [MedAlign HTML](https://arxiv.org/html/2308.14089v2)
- Submissions by specialty: Internal Medicine 492, Radiology 402, Neurology 320, Cardiology 71, Oncology 14, Surgery 12, Primary Care 3. Clinicians were asked to imagine a vignette typical of daily practice and write an instruction that would make work "easier, faster, and less stressful". 1,314 were collected and near-duplicates removed. — same
- [QUOTED] "Summarize the most recent annual physical with the PCP"; "Summarize the asthma care plan for this patient including relevant diagnostic testing, exacerbation history, and treatments"; "Identify the risk of stroke in the next 7 days for this TIA patient"; "Based on the information I've included under HPI, what is a reasonable differential diagnosis?"; "What patients on my service should be prioritized for discharge today?"; "Summarize from the EHR the strokes that the patient had and their associated neurologic deficits."; "Has she ever been on a statin before?" — same

**Ely et al. generic clinical questions (BMJ 1999/2000, family physicians)**
- [QUOTED] The ten most frequent generic questions (n=1396): "What is the drug of choice for condition x?" 11%; "What is the cause of symptom x?" 8%; "What test is indicated in situation x?" 8%; "What is the dose of drug x?" 7%; "How should I treat condition x (not limited to drug treatment)?" 6%; "How should I manage condition x…?" 5%; "What is the cause of physical finding x?" 5%; "What is the cause of test finding x?" 5%; "Can drug x cause (adverse) finding y?" 4%; "Could this patient have condition x?" 4%. Primary areas: diagnosis, treatment, management, epidemiology, non-clinical. — [Ely et al., BMJ (PMC27459)](https://pmc.ncbi.nlm.nih.gov/articles/PMC27459/)
- Later studies: an Australian GP study found treatment 71%, diagnosis 15%, epidemiology 9%. A systematic review found 34% of questions concerned drug treatment and 24% the causes of a symptom or finding (search summary). — [EBM BMJ](https://ebm.bmj.com/content/25/1/15)

**EHRSQL (NeurIPS 2022; shared task 2024)**
- Utterances came from 222 hospital staff (physicians, nurses, insurance review, health records teams) at a Korean university hospital, who imagined speaking to an AI speaker about structured EHR data. Questions are split by scope: single patient, group of patients, no patient. Templates are linked to MIMIC-III and eICU, with absolute and relative time expressions and held-out **unanswerable** questions that models should abstain on. — [EHRSQL arXiv](https://arxiv.org/abs/2301.07695); [GitHub](https://github.com/glee4810/EHRSQL); [Shared task 2024](https://arxiv.org/html/2405.06673v2)
- [QUOTED] Answerable: "what is the ingesting method of methimazole?"; template "what is the intake method of {drug_name}?". Unanswerable: "tell me what medicine to use to relieve a headache in hypertensive patients." — [EHRSQL GitHub](https://github.com/glee4810/EHRSQL)

**MedAgentBench (Stanford, FHIR-based, 2025)**
- 300 tasks written by two internal medicine physicians, run against a FHIR server: retrieval of patient info, labs and vitals, aggregation, documentation, and ordering tests, referrals and medications. The best original model (Claude 3.5 Sonnet v2) scored 69.67%. — [MedAgentBench arXiv](https://arxiv.org/html/2501.14654v2)
- [TEMPLATE] "What is the MRN of the patient with name {name} and DOB of {DOB}?"; "What's the most recent magnesium level of the patient {MRN} within last 24 hours?"; "What is the average [blood glucose level] of the patient {MRN} over the last 24 hours?"; "I just measured the blood pressure for patient with MRN of {MRN}, and it was 118/77 mmHg. Help me document this."; "What is the last hemoglobin A1C value in the chart for patient {MRN} and when was it recorded?"; "Order orthopedic surgery referral for patient {MRN}…"; "Check patient {MRN}'s most recent potassium level. If [below threshold], then order replacement potassium…" (the last three are truncated). — same

**Other**
- At AMIA 2025, Columbia (Tuteja et al.) compared clinician-written GenAI prompts with the Currie et al. 2003 information-needs taxonomy. About 80% fit existing categories; about 20% were new needs such as workflow optimization (search summary; not fetched). — [AMIA gallery](https://amia.secure-platform.com/symposium/gallery/rounds/82021/details/20512)
- In a Stanford inpatient AI chart-summarization pilot with 26 clinicians (physicians, APPs, nurses, case managers), clinicians especially valued **care-team identification** and **citations** (search summary). — [SHM abstract](https://education.hospitalmedicine.org/AssetListing/SHM-Converge-Scientific-Abstract-ePoster-Gallery-13901/An-LLM-Pipeline-to-Elevate-the-Clinician-Voice-Lessons-From-an-Inpatient-Ai-Chart-Summarization-Pilot-25560)

### Inferences
- A realistic mix is weighted heavily toward retrieve and summarize (about two-thirds), then care planning, then calculation and scoring. "Has she ever been on X?" (a history lookup that hinges on `TERM` + `ABS`) is a canonical shape.
- Ely's "Can drug x cause finding y?" and "What is the dose of drug x?" become chart questions once grounded: "Could any of her current meds be causing the hyponatremia?" or "What dose of apixaban is she on, and is it right for her creatinine and age?"
- EHRSQL's nurse and records-team authorship supports including operational questions (last vitals, intake/output, length of stay), cohort questions, and deliberately unanswerable questions.

### Gaps
- I did not retrieve the full EHRSQL template list or its time-expression catalogue (the PDF was not parseable). Nor did I retrieve the Currie 2003 taxonomy categories, nurse-handoff information-need studies (I-PASS), or medication-reconciliation information-need papers.

---

## 4. Population health, quality and research questions (cohorts, care gaps, eCQM-like)

### Takeaway
Quality questions are naturally phrased around eCQM or HEDIS logic: an age band, a diagnosis value set, a qualifying visit in the measurement period, exclusions, and the **most recent** result (with "missing" counting as failing). CMS122 (HbA1c > 9%) is the archetype and needs `AGG`, `TEMP`, `TERM` and `ABS` together.

### Cited Findings
- CMS122v12 (Diabetes: HbA1c Poor Control > 9%). Initial population: ages 18–75 with diabetes and a visit during the measurement period. Exclusions: hospice; palliative care; age 66+ in long-term institutional care for more than 90 days; age 66+ with frailty and advanced illness or dementia meds. Numerator: the **most recent HbA1c in the period > 9.0%, or missing/not performed**. A lower rate is better. — [Enterprise Health CMS122 spec](https://docs.enterprisehealth.com/functions/quality-of-care/measures/quality-measure-specifications-and-recommended-workflows/cms-122-diabetes-hemoglobin-a1c-hba1c-poor-control-greater9); [Sevocity CMS122v12](https://kb.sevocity.com/Content/Topics/MIPS/Quality/CMS122v12.htm); [eCQI CMS122v10](https://ecqi.healthit.gov/ecqm/EP//CMS122v10)
- EHRSQL's "group of patients" scope confirms that hospital staff naturally ask cohort-level questions of structured EHR data. — [EHRSQL arXiv](https://arxiv.org/abs/2301.07695)
- MedAlign includes panel-level instructions ("What patients on my service should be prioritized for discharge today?"). — [MedAlign HTML](https://arxiv.org/html/2308.14089v2)
- Epic lists "Identify Care Gaps" as an AI feature. — [Epic](https://www.epic.com/software/ai-clinicians/)

### Inferences
- The CMS122 "missing counts as poor control" rule is a strong `ABS` trap. A naive agent that filters to HbA1c > 9 will undercount.
- Natural phrasing usually leaves out measurement-period and exclusion details. A realistic benchmark should either state them ("this calendar year", "excluding hospice") or score how the agent handles the ambiguity.

### Gaps
- I did not fetch HEDIS phrasing or Epic Cosmos query examples. Cosmos question styles remain undocumented in these notes.

---

## 5. Example question bank (45 items) by persona and complexity

All items below are **[ADAPTED]** unless marked **[QUOTED]**. Adapted items are modelled on the sources above, as noted in brackets. Complexity: L1 = single lookup; L2 = filter, time window or simple grouping; L3 = multi-step, cohort or reasoning.

### Primary care (PCP)
1. L1 — "Has she ever been on a statin before?" **[QUOTED, MedAlign]** — `TERM` (statin class across RxNorm ingredients) `ABS` `TEMP`(ever)
2. L1 — "When was her last A1c and what was it?" [MedAgentBench style] — `TEMP` `TERM` (LOINC 4548-4, 17856-6, 4549-2)
3. L2 — "How has his blood pressure trended over the last 6 months, and is he at goal?" [Epic Art BP-trend scenario; Dragon BP classification] — `TEMP` `AGG` `UNIT`
4. L2 — "Summarize the most recent annual physical." **[QUOTED, MedAlign — "…with the PCP"]** — `TEMP`
5. L2 — "Is she due for any vaccines? When was her last Tdap and has she had Shingrix?" [Dragon CDC shingles prompt] — `TEMP` `TERM` (CVX) `ABS`
6. L2 — "What's his eGFR trend over the past two years? Any drop of more than 25%?" — `TEMP` `CALC` `UNIT`
7. L2 — "Is he on anything that could be raising his potassium?" [Ely "Can drug x cause finding y?"] — `TERM` (ACEi/ARB/MRA/K-sparing class) `MULTI`
8. L3 — "Pre-visit: what care gaps does she have — colonoscopy, mammogram, A1c, urine microalbumin, eye exam?" [Epic "Identify Care Gaps"; HEDIS] — `ABS` `TEMP` `TERM`
9. L3 — "Has she ever had a documented penicillin allergy, and what was the reaction?" — `ABS` `TERM`
10. L1 — "What medications does the patient take?" **[QUOTED, Dragon Copilot]** — `TEMP` (active only)
11. L2 — "Has he had a lipid panel since starting atorvastatin? What did his LDL do?" — `TEMP` (before vs after the start date) `TERM`
12. L3 — "What is the target A1c for this patient?" **[QUOTED, Dragon Copilot]**, grounded: age, comorbidities, hypoglycemia history — `MULTI` `ABS`

### Hospitalist / nursing
13. L1 — "What's the most recent magnesium within the last 24 hours?" **[TEMPLATE-derived, MedAgentBench]** — `TEMP` `ABS` (none in window)
14. L2 — "What's her average glucose over the last 24 hours, and how many readings were above 250?" [MedAgentBench aggregation] — `AGG` `TEMP` `UNIT` (mg/dL vs mmol/L)
15. L2 — "When did he last get a PRN opioid and how much in the last 12 hours?" [EHRSQL nursing style] — `TEMP` `AGG` `TERM` (MedicationAdministration, opioid class)
16. L2 — "What were the last set of vitals, and any fevers over 38.3 since admission?" — `TEMP` `UNIT` (°F/°C)
17. L2 — "What's the net fluid balance for the last 24 hours?" [EHRSQL intake/output style] — `AGG` `UNIT`
18. L3 — "Summarize the hospital course so far for handoff — key events, active issues, pending results." [Epic Hospital Course / Abridge handoffs] — `TEMP` `MULTI`
19. L2 — "Has she had a creatinine bump since we started vanc?" — `TEMP` `CALC` (KDIGO 0.3 rise within 48h)
20. L1 — "Who is on the care team — attending, consultants, case manager?" [Stanford SHM pilot: care-team identification] — `MULTI` (CareTeam/Encounter.participant)
21. L3 — "What patients on my service should be prioritized for discharge today?" **[QUOTED, MedAlign]** — `AGG` `MULTI`
22. L2 — "Is he on any anticoagulant or antiplatelet right now? When was the last dose?" — `TERM` `TEMP`
23. L2 — "Any blood cultures this admission? Did anything grow?" — `TERM` `ABS` `TEMP`

### Specialist
24. L2 — "Summarize from the EHR the strokes that the patient had and their associated neurologic deficits." **[QUOTED, MedAlign — neurology]** — `TEMP` `TERM` (cerebrovascular SNOMED descendants)
25. L3 — "Identify the risk of stroke in the next 7 days for this TIA patient." **[QUOTED, MedAlign]** — `CALC` (ABCD2) `ABS`
26. L3 — "What's her CHA2DS2-VASc and is she anticoagulated?" [MedAlign Calculation & Scoring] — `CALC` `TERM` `ABS`
27. L2 — "Trend his ejection fraction across all echos." [cardiology] — `TEMP` `TERM`
28. L2 — "Prep me for dialysis rounding: last 3 pre-dialysis potassiums, hemoglobin, and current ESA dose." [Epic nephrology rounding feature] — `TEMP` `AGG` `TERM`
29. L3 — "Summarize the asthma care plan for this patient including relevant diagnostic testing, exacerbation history, and treatments." **[QUOTED, MedAlign]** — `TEMP` `TERM` `MULTI`
30. L2 — "When was his last PSA, and what's the doubling time?" [oncology/urology] — `TEMP` `CALC`
31. L3 — "Does the chart support prior auth for a GLP-1? Has she tried and failed metformin, and what's her BMI?" [Abridge/Availity prior auth] — `ABS` `TERM` `TEMP` `UNIT`

### Pharmacist
32. L2 — "Med rec: what's on the outpatient list that isn't ordered inpatient?" — `MULTI` (MedicationStatement vs MedicationRequest) `TERM` (brand vs generic RxNorm)
33. L2 — "Is the apixaban dose appropriate given her age, weight and creatinine?" [Ely "dose of drug x"] — `CALC` `UNIT` `TEMP`
34. L2 — "Can his medications cause orthostatic hypotension…?" **[QUOTED, Dragon Copilot (Merck Manuals)]** — `TERM`
35. L2 — "Is she on two serotonergic agents?" — `TERM` (drug-class grouping) `AGG`
36. L3 — "Calculate CrCl with Cockcroft-Gault using today's creatinine and actual weight — does any current med need renal adjustment?" [MedAlign Calculation] — `CALC` `UNIT` (kg/lb) `MULTI`
37. L1 — "Was there a recent tacrolimus recall?" **[QUOTED, Dragon Copilot]**, chart variant: "What were her last three tacrolimus troughs, and the dose at each?" — `TEMP` `TERM`
38. L2 — "Any documented QT prolongation? What's the latest QTc, and which current meds prolong QT?" — `TEMP` `TERM` `ABS`

### Researcher / quality / population health
39. L3 — "How many of my diabetic patients aged 18–75 had a last A1c over 9 this year — counting those with no A1c?" [CMS122] — `AGG` `TEMP` `TERM` `ABS`
40. L3 — "List patients with hypertension whose last two BPs were over 140/90 and who aren't on any antihypertensive." — `AGG` `TEMP` `TERM` `ABS`
41. L3 — "How many patients with CKD stage 3 or worse are on metformin?" — `AGG` `TERM` (eGFR LOINC or SNOMED CKD hierarchy) `UNIT`
42. L2 — "What fraction of women 50–74 have a mammogram in the last 2 years?" [HEDIS BCS] — `AGG` `TEMP` `ABS`
43. L3 — "Which heart failure patients were readmitted within 30 days of discharge?" — `TEMP` `AGG` `MULTI` (Encounter)
44. L2 — "Average length of stay for pneumonia admissions this year?" [EHRSQL cohort scope] — `AGG` `TEMP` `TERM`
45. L3 — "Find patients started on an SGLT2 inhibitor; what was the mean A1c change from baseline to 6 months?" — `TEMP` `AGG` `TERM` `UNIT`

### Deliberately unanswerable / abstention probes [EHRSQL unanswerable concept]
46. "tell me what medicine to use to relieve a headache in hypertensive patients." **[QUOTED, EHRSQL unanswerable]** — needs external knowledge, not chart data
47. "Why did the attending stop her lisinopril?" [ADAPTED, EHRSQL negative example: reasons behind clinical decisions] — `ABS` unless a note documents the reason
48. "What was her A1c in 2009?" when the record starts in 2015 — `ABS`

### Inferences
- Style notes for prompt authors: use pronouns and implicit patients ("she", "this patient"); clinical shorthand ("vanc", "A1c", "K", "CrCl", "bump"); implicit time ("recent", "since admission", "this year"); compound asks ("what was it and when"); and implicit class-level terms ("anticoagulant", "statin", "serotonergic") that need code-hierarchy expansion.
- Weight the persona mix toward retrieve and summarize, per MedAlign's ~68%. Keep about 10–15% calculation and scoring and a slice of unanswerable items, per EHRSQL.

### Gaps
- The persona attributions for adapted items are my judgement, not empirical frequency data. I found no published distribution of chart-copilot questions by pharmacist or nurse persona.
