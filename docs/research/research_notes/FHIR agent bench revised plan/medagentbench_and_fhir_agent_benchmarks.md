# MedAgentBench (v1/v2) and Related FHIR Agent Benchmarks: How They Are Built

Research date: 2026-10-15. Primary sources: the GitHub repos (files downloaded and inspected directly), arXiv papers and the PSB 2026 proceedings PDF.

Source keys used below:
- [MAB-repo] https://github.com/stanfordmlgroup/MedAgentBench (files read: `README.md`, `data/medagentbench/test_data_v1.json`, `test_data_v2.json`, `funcs_v1.json`, `src/server/tasks/medagentbench/__init__.py`, `eval.py`, `utils.py`, `configs/tasks/medagentbench.yaml`)
- [MAB-paper] https://arxiv.org/html/2501.14654 (arXiv v2 of the paper; published as NEJM AI 2(9):AIdbp2500144, 2025, https://ai.nejm.org/doi/full/10.1056/AIdbp2500144)
- [V2-paper] https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf (Chen, Postelnik, Black, Jiang, Chen, "MedAgentBench v2: Improving Medical LLM Agent Design", PSB 2026)
- [V2-repo] https://github.com/ericoericochen/medagentbenchv2 (files read: `README.md`, `.../data/medagentbench/new_tasks.json`, `.../eval_desctiptions.json` [sic])
- [FAB-paper] https://arxiv.org/html/2509.19319v2 (Lee et al., FHIR-AgentBench; PMLR 297, ML4H 2026: https://proceedings.mlr.press/v297/lee26a.html)
- [FAB-repo] https://github.com/glee4810/FHIR-AgentBench (files read: `README.md`, `final_dataset/questions_answers_sql_fhir.csv`)
- [Medmarks] https://arxiv.org/pdf/2605.01417
- [RL-FHIR] https://arxiv.org/abs/2605.14126

---

## 1. MedAgentBench v1: tasks, data, environment, interface, grading and results

### Takeaway
MedAgentBench is 300 tasks: 10 physician-written templates, each with 30 patient-specific instances. Half are query (GET-only) tasks and half are action tasks (POST, usually after GETs). They run against a HAPI FHIR Docker image holding 100 deidentified Stanford patients and about 785k resources. The agent speaks a raw-text `GET url` / `POST url\n{json}` / `FINISH([...])` protocol, with a maximum of 8 rounds. Grading is pass@1 through a hidden `refsol.py`. For query tasks it recomputes the gold answer live from the FHIR server. For action tasks it checks the POST payloads in the agent's history field by field. The best v1 model (Claude 3.5 Sonnet v2) scored 69.67%.

### Cited Findings

**Size, split and authorship**
- There are 300 tasks in 10 categories, written by two internal medicine physicians (KB, JHC). They are grouped into 7 broad categories: patient information retrieval, lab result retrieval and patient data aggregation (query), plus recording patient data, test ordering, referral ordering and medication ordering (action). 150 tasks are query and 150 are action. — [MAB-paper](https://arxiv.org/html/2501.14654)
- In the repo, `test_data_v2.json` holds 300 items, exactly 30 per template (`task1`…`task10`). The older `test_data_v1.json` holds 100 items, 10 per template. The task config `configs/tasks/medagentbench.yaml` points `medagentbench-std` at `test_data_v2.json` and `funcs_v1.json`, with `max_round: 8` and `fhir_api_base: "http://localhost:8080/fhir/"`. Here "v1/v2" means the **data-file version** (100 vs 300 instances). It is not the "MedAgentBench v2" paper. — [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench)
- Each task item has the keys `id`, `instruction`, `context`, `sol` and `eval_MRN`. Only task1 items carry a static `sol`, for example `"sol": ["S6534835"]`. All other tasks compute gold at grading time. — [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench) (`test_data_v2.json`)

**The 10 task templates, quoted from `test_data_v2.json`** — [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench)

| id | Type | Instruction (verbatim) | Context (verbatim) |
|---|---|---|---|
| task1 | query | "What's the MRN of the patient with name Peter Stafford and DOB of 1932-12-29? If the patient does not exist, the answer should be \"Patient not found\"" | "" (static `sol`) |
| task2 | query | "What's the age of the patient with MRN of S2874099?" | "It's 2023-11-13T10:15:00+00:00 now, and the answer should be rounded down to an integer." |
| task3 | action | "I just measured the blood pressure for patient with MRN of S2380121, and it is \"118/77 mmHg\". Help me record it." | "It's 2023-11-13T10:15:00+00:00 now. The flowsheet ID for blood pressure is BP." |
| task4 | query | "What's the most recent magnesium level of the patient S3032536 within last 24 hours?" | "...The code for magnesium is \"MG\". The answer should be a single number converted to a unit of mg/dL, and it should be -1 if a measurement within last 24 hours is not available." |
| task5 | action (conditional) | "Check patient S6315806's last serum magnesium level within last 24 hours. If low, then order replacement IV magnesium according to dosing instructions. If no magnesium level has been recorded in the last 24 hours, don't order anything." | "...The NDC for replacement IV magnesium is 0338-1715-40. Dosing instructions: (1) Mild deficiency (eg, serum magnesium 1.5 to 1.9 mg/dL): IV: 1 g over 1 hour. (2) Moderate deficiency (eg, serum magnesium 1 to <1.5 mg/dL): IV: 2 g over 2 hours. (3) Severe deficiency (eg, serum magnesium <1 mg/dL): IV: 4 g over 4 hours." |
| task6 | query (aggregation) | "What is the average CBG of the patient S6307599 over the last 24 hours?" | "...The code for CBG is \"GLU\". The answer should be a single number converted to a unit of mg/dL, and it should be -1 if a measurement within last 24 hours is not available." |
| task7 | query | "What is the most recent CBG of the patient S2823623?" | "...The code for CBG is \"GLU\"" |
| task8 | action | "Order orthopedic surgery referral for patient S2016972. Specify within the free text of the referral, \"Situation: acute left knee injury, Background: radiology report indicates ACL tear. Assessment: ACL tear grade II. Recommendation: request for Orthopedic service to evaluate and provide management recommendations.\"" | "...The SNOMED code for orthopedic surgery referral is 306181000000106." |
| task9 | action (conditional, 2 orders) | "Check patient S3241217's most recent potassium level. If low, then order replacement potassium according to dosing instructions. Also pair this order with a morning serum potassium level to be completed the next day at 8am." | "...The code for potassium is \"K\". The NDC for replacement potassium is 40032-917-01. Dosing instructions: for every 0.1 mEq/L (or mmol/L) below threshold, order 10 mEq potassium oral repletion) to reach a goal of 3.5 serum level. The LOINC code for serum potassium level is 2823-3." |
| task10 | query+conditional action | "What's the last HbA1C (hemoglobin A1C) value in the chart for patient S6227720 and when was it recorded? If the lab value result date is greater than 1 year old, order a new HbA1C lab test." | "...The code for HbA1C is \"A1C\". The LOINC code for ordering HbA1C lab is: 4548-4. The answer should be [-1] if a measurement is not available." |

- Within each template, the 30 instances differ **only** in patient MRN (and name/DOB for task1). The instruction text, the codes and the frozen "now" of 2023-11-13T10:15:00+00:00 are identical across instances. The task3 BP value is always "118/77 mmHg". — [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench) (`test_data_v2.json`, inspected)
- The context string **gives the agent the local codes** ("MG", "GLU", "K", "A1C", "BP"), the NDCs, the LOINC/SNOMED order codes and the reference time. Terminology lookup is therefore removed as a difficulty. — [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench)

**Patient data sourcing**
- The data come from the STARR (STAnford Research Repository) deidentified clinical data warehouse. 100 patients were randomly sampled from those with an inpatient sodium lab ordered on the morning of 2023-11-13. Only the last 5 years of records were kept (cutoff 2018-11-13). Timestamps were jittered at the patient level. — [MAB-paper](https://arxiv.org/html/2501.14654)
- Table 2: 100 patients, age 58.15 ± 19.82, 47% female. 785,207 total records: 563,426 Observation, 124,969 Procedure, 74,821 Condition and 21,991 MedicationRequest. — [MAB-paper](https://arxiv.org/html/2501.14654)
- MRNs are fake, written as "S" plus 7 digits. Names, phones and addresses were generated with Faker. The Observations cover labs plus 6 vital types (HR, SpO2, RR, FiO2, BP, temperature). PRN orders were removed. — [MAB-paper](https://arxiv.org/html/2501.14654)

**FHIR server setup**
- The server is HAPI FHIR JPA with a persistent H2 database, shipped as a Docker image. The authors ran it on a GCP c2d-standard-2 VM, where startup takes about 90 s. The authors say it "should not be directly used in a production setting". — [MAB-paper](https://arxiv.org/html/2501.14654)
- Setup commands: `docker pull jyxsu6/medagentbench:latest; docker tag ... medagentbench; docker run -p 8080:8080 medagentbench`. The harness is built on AgentBench (THUDM). Running it means `python -m src.start_task -a` (20 task workers on ports 5000–5015) and then `python -m src.assigner`. Results land in `outputs/MedAgentBenchv1/<model>/medagentbench-std/overall.json`. — [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench) (README)

**Agent interface (prompt and protocol)**
- The prompt template (`MedAgentBench_prompt` in `src/server/tasks/medagentbench/__init__.py`) reads verbatim: "You are an expert in using FHIR functions to assist medical professionals... 1. If you decide to invoke a GET function, you MUST put it in the format of `GET url?param_name1=param_value1&...` 2. ... POST ... `POST url` `[your payload data in JSON format]` 3. If you have got answers for all the questions and finished all the requested tasks, you MUST call to finish the conversation in the format of (make sure the list is JSON loadable.) `FINISH([answer1, answer2, ...])` Your response must be in the format of one of the three cases, and you can call only one function each time. You SHOULD NOT include any other text in the response. Here is a list of functions in JSON format... {functions} Context: {context} Question: {question}". The whole thing is injected as a single user message. — [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench)
- `funcs_v1.json` defines 9 functions in a BFCL-style JSON schema:
  - GET: `{api_base}/Condition` (category, patient); `/Observation` labs (code, date, patient); `/Observation` vitals (category, date, patient); `/MedicationRequest` (category, date, patient); `/Procedure` (code, date, patient); `/Patient` (address…, birthdate, family, gender, given, identifier, name, telecom).
  - POST: `/Observation` (vitals create); `/MedicationRequest`; `/ServiceRequest`.
  - The descriptions are copied from Epic-style FHIR docs, for example "Observation.Search (Labs) The Observation (Labs) resource returns component level data for lab results." — [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench)
- **Important harness detail:** GET calls are executed for real, and `&_format=json` is appended. **POST calls are NOT sent to the FHIR server.** The harness only JSON-parses the body and replies "POST request accepted and executed successfully. Please call FINISH if you have got answers...". Any output that does not start with GET/POST/FINISH( ends the episode as `AGENT_INVALID_ACTION`. Running out of rounds returns `TASK_LIMIT_REACHED`. Both count as failures. — [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench) (`__init__.py`)
- The function-calling design was inspired by BFCL. Temperature was 0 for all models except o3-mini. — [MAB-paper](https://arxiv.org/html/2501.14654)

**Grading (refsol.py)**
- `refsol.py` is **not in the public repo**. Users must download it from a Stanford Box link (https://stanfordmedicine.box.com/s/fizv0unyjgkb1r3a83rfn5p3dc673uho) and save it as `src/server/tasks/medagentbench/refsol.py`. `eval.py` dispatches `getattr(refsol, task_id)(case_data, results, fhir_api_base)` by template id. Any exception counts as a fail. `calculate_overall` returns `{'success rate': correct/total}`. — [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench)
- The paper says query tasks are compared against reference-solution answers and action tasks get "rule-based sanity checks on POST payloads". The grader and reference solutions were manually curated. — [MAB-paper](https://arxiv.org/html/2501.14654)
- The v2 authors' `eval_desctiptions.json` describes per task what the refsol grader does. — [V2-repo](https://github.com/ericoericochen/medagentbenchv2):
  - **task1:** Zero POSTs allowed. `json.loads(result)` must equal `sol` exactly.
  - **task2:** `GET Patient?identifier={MRN}`, then birthDate is converted to integer age. The answer must be exactly `[age]`.
  - **task3:** Exactly one POST, to `Observation`, with category `vital-signs` (system `http://hl7.org/fhir/observation-category`), `code: {"text":"BP"}`, `effectiveDateTime` 2023-11-13T10:15:00+00:00, `status: final`, `valueString: "118/77 mmHg"` and `subject.reference: Patient/{MRN}`.
  - **task4:** No POST. `GET Observation?patient={MRN}&code=MG`, filtered to the 24 h before the cutoff. Gold is the latest `valueQuantity.value`, or -1. The answer must be identical to `[value]`.
  - **task5:** Same retrieval as task4. No POST if the value is missing or > 1.9. Otherwise exactly one MedicationRequest with NDC 0338-1715-40, authoredOn containing 2023-11-13T10:15, route IV, dose and rate by band (4 g/4 h, 2 g/2 h or 1 g/1 h), status active, intent order and the correct subject.
  - **task6:** No POST. Mean of GLU values in [2023-11-12 10:15, cutoff]. The answer must be a 1-element list within **±0.1** (string or number accepted), or [-1].
  - **task7:** No POST. Latest GLU across all time, and the answer must be identical to `[value]`.
  - **task8:** Exactly one ServiceRequest with SNOMED 306181000000106, authoredOn, status active, intent order, priority stat, subject, and `note.text` containing the exact SBAR string.
  - **task9:** If the latest K is missing or ≥ 3.5, no POSTs are allowed. If < 3.5, **exactly two POSTs in order** are required: a MedicationRequest (NDC 40032-917-01, route oral, dose = ((3.5 − K)/0.1)×10 mEq ±0.1) and then a ServiceRequest (LOINC 2823-3, occurrenceDateTime containing 2023-11-14T08:, priority stat). The FINISH answer can be `[K]`, `[-1]` or `[]` (tolerated).
  - **task10:** If the latest A1C is missing **or** older than 2022-11-13 10:15, exactly one ServiceRequest for LOINC 4548-4 is required. Otherwise no POST. FINISH must be `[value, ISO_date]` or `[-1]` (or `[]`).
- So the gold for query tasks is **recomputed live from the server at grade time**, using code+patient searches with date windows relative to a frozen "now". Action grading inspects the **POSTs recorded in the agent's history**, because they never touch the server. — [V2-repo](https://github.com/ericoericochen/medagentbenchv2); [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench)

**Success rates (Table 3, pass@1)** — [MAB-paper](https://arxiv.org/html/2501.14654)

| Model | Overall | Query | Action |
|---|---|---|---|
| Claude 3.5 Sonnet v2 | 69.67% | 85.33% | 54.00% |
| GPT-4o | 64.00% | 72.00% | 56.00% |
| DeepSeek-V3 | 62.67% | 70.67% | 54.67% |
| Gemini-1.5 Pro | 62.00% | 52.67% | 71.33% |
| GPT-4o-mini | 56.33% | 59.33% | 53.33% |
| o3-mini | 51.67% | 54.67% | 48.67% |
| Qwen2.5 | 51.33% | 38.67% | 64.00% |
| Llama 3.3 | 46.33% | 50.00% | 42.67% |
| Gemini 2.0 Flash | 38.33% | 34.00% | 42.67% |
| Gemma2 | 19.33% | 38.67% | 0.00% |
| Gemini 2.0 Pro | 18.00% | 25.33% | 10.67% |
| Mistral v0.3 | 4.00% | 8.00% | 0.00% |

**Reported failure modes**
- The dominant failures were instruction-following and format errors. Gemini 2.0 Flash produced invalid actions in 54% of cases, often wrapping output in `tool_code` or JSON code fences; the harness now strips ```` ```tool_code ```` for this reason. Models answered in full sentences or returned `["value": 5.4]` where `[5.4]` was expected. There were also invalid GET syntax errors. — [MAB-paper](https://arxiv.org/html/2501.14654); [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench)
- The authors' limitations:
  - The benchmark does not capture team coordination.
  - Patients from Stanford are "not representative of the general population".
  - Coverage of clinical tasks is limited (no surgical or nursing tasks).
  - The reliability of repeated actions was not examined.
  - The baseline agent is simple. — [MAB-paper](https://arxiv.org/html/2501.14654)
- The paper's count is inconsistent: it says 11 models are evaluated in the results text but lists 12 in Table 3. — [MAB-paper](https://arxiv.org/html/2501.14654)

### Inferences
- MedAgentBench's difficulty comes mostly from **protocol and format compliance plus conditional action logic**, not from hard retrieval. Each template needs at most 1–3 searches, and the codes are given in context. Once v2 fixed the harness, GPT-4.1 reached 91% (see §2). The benchmark is close to saturated as an "LLM" test, but it is still useful as a harness-compliance test.
- The 30-instances-per-template design means the effective diversity is 10 tasks. Per-template variance (patients with no data in the window, low vs normal values) is the only thing that exercises branching. For a 150-question benchmark, more distinct templates with fewer instances each would measure more.
- Live-recomputed gold is a good pattern for our corpus: store the gold as a *program* over the FHIR store rather than as a static string. It must be pinned to a frozen "now" and to a fixed server snapshot.
- Grading POSTs from the transcript, rather than server state, avoids state reset between tasks. It also means an agent that "verifies" its write with a follow-up GET sees nothing.

### Gaps
- I could not read `refsol.py` itself, because it is on Stanford Box behind a download link. The grader logic above comes from the v2 authors' natural-language descriptions in `eval_desctiptions.json`, not from the code.
- I did not check whether the NEJM AI published version has updated model results compared with arXiv v2. The NEJM page was not fetched.

---

## 2. MedAgentBench v2 (PSB 2026) and other follow-ups

### Takeaway
MedAgentBench v2 is a 2026 Pacific Symposium on Biocomputing (PSB) paper from MIT, Binghamton and Stanford. It changes the harness rather than the model. It adds typed FHIR tools instead of raw HTTP, a calculator, a finish tool that enforces output format, a planning/CoT system prompt and few-shot good/bad examples. With these, GPT-4.1 goes from the v1 harness's roughly 70% ceiling to 91.0%, and to 98.0% with a failure-derived memory. The paper also adds 300 harder multi-step tasks (10 templates × 30 MRNs), where GPT-4.1 scores 88.67%.

### Cited Findings
- **Harness changes:**
  - New system prompt: paraphrase the instruction, make conditional logic explicit, plan step by step, validate preconditions before acting.
  - 3–4 few-shot examples of correct vs incorrect output format.
  - Structured tools such as `fhir_patient_search`, `fhir_observation_search` and `fhir_service_request_create` replace raw HTTP.
  - A calculator tool: Python `eval()` with `math`, `datetime` and `decimal`.
  - A dedicated finish tool that enforces typing.
  - — [V2-paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)
- **Results:** GPT-4.1 scores 91.0% without memory and 98.0% with memory. On tasks with 3 or more tool calls (tasks 5, 6, 9 and 10), success is 96.67% with memory, and tasks 9 and 10 reach 100%. Task 6 (averaging) improved most because of the calculator. — [V2-paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)
- **Memory:** Each memory entry is synthesized from the instruction, the agent's wrong output and the expected response, then appended to the system prompt. All memories are given every time; there is no retrieval. To build memory, 5 failed tasks per category were used, and the rest were held out for testing. Example memory for task10: "treat no existing HbA1C result as equivalent to >1 year old: return [-1] for the value/date AND create a new HbA1C ServiceRequest with LOINC 4548-4 before finishing." — [V2-paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)
- **Ambiguity in v1 tasks named by the v2 authors:**
  - Task 9's "Also pair this order with a morning serum potassium level" has unclear conditional scope. Agents ordered the follow-up lab even when K was normal; "it's even plausible that a human misunderstands".
  - Task 10 does not say what to do when no HbA1C exists. The grader requires an order.
  - Memory partly "teaches the grader's interpretation".
  - — [V2-paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)
- **Evaluation mismatch:** Agent GETs were capped at 200 items to save tokens, while the evaluators fetch with `_count` up to 5000. Some failures may reflect truncation rather than reasoning. — [V2-paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)
- **How the new 300 tasks were built:**
  - o3 was given the full EHR records of 2 patients, the tool set and the 10 reference tasks, and asked for 10 novel tasks per patient.
  - Physician KB rated each task as poor, intermediate or high, then picked the top 10.
  - Each of the 10 was varied 30 times by substituting a random MRN.
  - A blinded co-author ran the evaluation.
  - Each task got a custom evaluation function covering temporal logic, branches, dose thresholds and FHIR field validity (resourceType, intent, status, subject.reference).
  - The authors describe the new tasks as "more complex, require more decision steps... but exhibit less ambiguity".
  - — [V2-paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)
- **New v2 task templates (verbatim `new_tasks.json` excerpts)** — [V2-repo](https://github.com/ericoericochen/medagentbenchv2):
  1. "Find the date of the most recent CT Abdomen/Pelvis procedure for patient S6315806. If the study was performed more than 12 months ago, order a new CT Abdomen/Pelvis with IV contrast indication 'Renal mass follow-up'." (codes IMGCT0491/IMGIL0001, new CPT 74177)
  2. "Verify that patient S6315806 has exactly one active DVT prophylaxis order. If there are zero orders, create one. If there are multiple orders, discontinue duplicates keeping only the newest." (any anticoagulant counts as a duplicate)
  3. "Calculate the average heart rate over the past 6 hours and the past 12 hours..." (code 'HEARTRATE')
  4. "Check if patient S6315806 has a urinary catheter that has been in place for more than 48 hours without a documented removal order. If so, create a service request to remove the catheter." (procedure code 'NUR1373')
  5. "Confirm patient ... has a malignant neoplasm of left kidney diagnosis [C64.2]. If true and the last CT Abdomen/Pelvis is more than 3 months old, order a new CT and referral to Interventional Radiology."
  6. "Retrieve the most recent TSH and free T4 values... Protocol: If TSH > 10 uIU/mL on two separate instances... separated by at least one month..., start levothyroxine 25 mcg PO daily and order repeat TSH/FT4 in 6 weeks. If TSH is 4.5–10 and free T4 is normal (0.8–1.8 ng/dL), order repeat TSH/FT4 in 3 months"
  7. "Check the latest QTc interval... If prolonged [>500 ms], discontinue QT-prolonging medications and order a follow-up ECG for 24 hours later." (ondansetron, prochlorperazine and most antipsychotics; SNOMED 445118002)
  8. "Verify that every active opioid analgesic order... has a matching naloxone rescue prescription. If an opioid order is active without naloxone, create a naloxone order."
  9. "Determine the date of the last influenza vaccine... If >365 days ago, order a new influenza vaccine... Please also search for any allergies to this vaccine"
  10. "Review COVID-19 vaccination status... code 'COVIDVACCINE' or MedicationRequest with text containing 'COVID-19 VAC'... order a COVID booster (CPT 91320)".
- **Error analysis on the new tasks:**
  - Task 2: the agent did not know that discontinuing an order means a MedicationRequest with status cancelled/completed.
  - Task 7: the agent missed an ondansetron order that had no code and appeared only in free text (medication normalization and coded-vs-text matching).
  - Task 3: the agent returned `-1` where the grader expected the pair `(-1, -1)`. The clinical logic was right but the response shape was wrong, so it was graded incorrect.
  - — [V2-paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)
- The v2 authors argue that agents need >95% accuracy for clinical use. They recommend reporting success rate together with per-task cost and latency. — [V2-paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)
- **Medmarks:**
  - Medmarks packages "MedAgentBench v2" as a `verifiers` RL environment (tool-based), but evaluated only the original V1 question set.
  - GLM 4.7 narrowly beat GPT-5.1 (medium) and significantly beat Claude Sonnet 4.5. Qwen3 235B-A22B Thinking significantly underperformed Qwen3 30B-A3B Thinking.
  - Failure taxonomy for GLM 4.7's wins over GPT-5.1: "Wrong search scope/category" 25 cases (67.6%) and "Winner placed order; loser abstained" 7 (18.9%). Other categories: tool/query formatting error, wrong patient reference.
  - The authors caution that with 300 cases, "future evaluations should average multiple runs".
  - — [Medmarks](https://arxiv.org/pdf/2605.01417)
- The community has MCP wrappers for the benchmark, such as `udapy/medagentbench-fhir-mcp-server` and `mike-holcomb/fhir-mcp-server-medagentbench`, plus AgentBeats "green/purple" agent leaderboards and `abasit/FhirAgentEvaluator` ("Based upon MedAgentBench and FHIRAgentBench"). — [GitHub repo search](https://github.com/search?q=medagentbench&type=repositories)

### Inferences
- The 70% → 91% gain from harness changes alone, with the model held fixed (GPT-4.1 v1 baseline vs v2 harness, per V2 Fig. 5), is the strongest evidence that a **harness** benchmark is meaningful. Typed tools, a calculator and a format-enforcing finish each targeted a specific failure class. A benchmark meant to discriminate harnesses should include questions that specifically reward:
  - arithmetic and aggregation
  - temporal-window filtering
  - exact output schemas
  - explicit handling of empty results.
- The memory result shows that ambiguous tasks reward "learning the grader". Our benchmark should avoid underspecified conditionals, or state the empty-data behaviour explicitly, the way v1 task 5 does ("If no magnesium level has been recorded... don't order anything").
- Response-shape mismatches cost real points. We should fix the answer schema per question (scalar, list, tuple, date, empty) and grade with type-tolerant comparators, such as numeric tolerance and string-or-number.

### Gaps
- Per-task success numbers for v2 (Figures 2–5) are only in figures. I did not extract the exact per-category numbers.
- The v2 repo's test file naming conflicts:
  - README: "new tasks are located in .../test_data_v2.json".
  - The repo also has `new_tasks.json` (10 templates), `new_patient_tasks.json` and `new_data_v1.json`.
  - I did not open the latter two to confirm which holds the 300 instances.
- The F1000 poster version reports "about 89%" in one place and 88.67% elsewhere. The PSB paper states 88.67% (266/300).

---

## 3. FHIR-AgentBench (Verily / KAIST / MIT, 2025–2026)

### Takeaway
FHIR-AgentBench has 2,931 single-patient questions. They come from EHRSQL-2024 templates re-grounded onto MIMIC-IV-FHIR (Demo, R4), and each comes with gold answers *and* gold FHIR resource IDs. The best baseline, a multi-turn ReAct agent with a resource retriever plus a Python code interpreter, reached only about 50% answer correctness (o4-mini). Architecture mattered more than model: across models, top-architecture scores were 44–50%. Later work (May 2026) used RL on a Qwen3-8B CodeAct agent to reach 77%.

### Cited Findings
- **Data:**
  - Patients come from MIMIC-IV Clinical Database Demo on FHIR (PhysioNet `mimic-iv-fhir-demo/2.1.0`), loaded into a **Google Cloud Healthcare API FHIR store (R4)**, not HAPI.
  - Patients average about 9K FHIR resources, and a full record is about 3M tokens of FHIR JSON. Putting the whole record in context "consistently failed".
  - — [FAB-paper](https://arxiv.org/html/2509.19319v2); [FAB-repo](https://github.com/glee4810/FHIR-AgentBench)
- **Question source:**
  - Questions are from EHRSQL-2024, keeping only single-patient questions; multi-patient ones were dropped because of retrieval latency.
  - Anchor years were restored. Relative-time questions get an assumption such as "Assume the current time is 2133-12-31 23:59:00."
  - Normalized terms were mapped back to raw MIMIC strings.
  - The SQL was re-executed on raw MIMIC-IV.
  - The NLQ/answer pairs were manually and iteratively reviewed, and clarifying context was added (e.g., "SpO2" → "oxygen saturation pulse oximetry").
  - About 24% of answers are intentionally **empty**.
  - — [FAB-paper](https://arxiv.org/html/2509.19319v2)
- **Dataset file:** `final_dataset/questions_answers_sql_fhir.csv` has 2,931 rows. — [FAB-repo](https://github.com/glee4810/FHIR-AgentBench) (inspected)
  - Columns: `split, question_id, question, sql_query, true_answer, assumption, patient_fhir_id, template, val_dict, proc_query, main_table_name, mappable_to_fhir, true_fhir_ids`.
  - Splits: train 2,098, valid 424, test 409. The paper says there is no training set, so the split column appears to be inherited from EHRSQL.
  - Source-table mix: prescriptions 602, labevents 570, chartevents 555, microbiologyevents 334, admissions 317, outputevents 166, procedures_icd 133, icustays 121, transfers 54, patients 44, diagnoses_icd 35.
- **Example rows (verbatim):**
  - Q: "When was the first time the respiratory rate of patient 10018081 was measured to be less than 23.0 today?"
    - `template`: "When was the [time_filter_exact1] time that the {vital_name} of patient {patient_id} was [comparison] than {vital_value} [time_filter_global1]?"
    - `true_answer`: `[['2133-12-31 02:00:00']]`
    - `true_fhir_ids`: `{'Observation': ['b2c4828b-...']}`
  - Q: "Has docusate sodium (liquid), glucose gel, or heparin been prescribed since 2157 to patient 10036156?"
    - `true_answer` `[[1]]`
    - assumption: "...account for all variations in letter case and surrounding whitespace. When searching for heparin, use the prescriptions records."
    - gold IDs span both `Medication` and `MedicationRequest`.
  - — [FAB-repo](https://github.com/glee4810/FHIR-AgentBench)
- **Other example questions:** "When has patient 10018845 been given phosphate test for the last time?"; "Was patient 10018328 diagnosed with anything in the first hospital visit?" Answer types are counts, timestamps, yes/no (as 1/0), lists and empty results. — [FAB-paper](https://arxiv.org/html/2509.19319v2)
- **Gold resources:** The SQL was rewritten to return all supporting rows, which were mapped to FHIR IDs. Questions need 0–2,257 resources each; 80% need 0–2, and the remaining 20% average about 70. — [FAB-paper](https://arxiv.org/html/2509.19319v2)
- **Metrics:**
  - Retrieval precision and recall over resource IDs, with edge-case rules: no gold but predictions present gives P=0; gold present but no predictions gives R=0; neither gives both=1.
  - Answer correctness is judged by o4-mini. Validation on 500 manual samples gave 97% agreement.
  - — [FAB-paper](https://arxiv.org/html/2509.19319v2)
- **Architectures compared:**
  - Tools: (1) FHIR Query Generator, which writes raw query strings; (2) Retriever, which fetches all resources of a type for a patient, or one by ID; (3) Code Interpreter.
  - Settings: single-turn {QueryGen, Retriever, Retriever+Code} and multi-turn ReAct {Retriever, Retriever+Code}.
  - Models: o4-mini, Gemini-2.5-Flash, Qwen3-32B and Llama-3.3-70B, all with a 32k context.
  - Agent classes in the repo: `single_turn_request_agent.py`, `single_turn_resource_agent.py`, `single_turn_code_resource_agent.py`, `multi_turn_resource_agent.py`, `multi_turn_code_resource_agent.py`.
  - — [FAB-paper](https://arxiv.org/html/2509.19319v2); [FAB-repo](https://github.com/glee4810/FHIR-AgentBench)
- **Results (o4-mini; precision / recall / answer correctness):** — [FAB-paper](https://arxiv.org/html/2509.19319v2)

  | Architecture | Precision | Recall | Answer correctness |
  |---|---|---|---|
  | Single-turn + QueryGen | 0.46 | 0.43 | 0.25 |
  | Single-turn + Retriever | 0.41 | 0.58 | 0.22 |
  | Single-turn + Retriever + Code | 0.41 | 0.58 | 0.33 |
  | Multi-turn + Retriever | 0.33 | 0.71 | **0.20** |
  | Multi-turn + Retriever + Code | 0.35 | 0.68 | **0.50** |

  Under Multi-turn + Retriever + Code, the other models scored 0.44 (Gemini-2.5-Flash), 0.47 (Qwen3) and 0.46 (Llama-3.3). Multi-turn *without* code was the worst setting for every model (0.16–0.20). On the "Empty" answer group, o4-mini's best setting scored 0.7368.
- **Failure modes:**
  - Wrong resource type: ostomy output was searched in Procedure instead of Observation (answered 0 vs 68).
  - Over-specific queries: LOINC 9279-1 was used for respiratory rate, but the data uses the MIMIC itemid code 220210.
  - The agent could not join Encounter and Procedure in single-turn mode.
  - Medication names were missed behind `medicationReference` → Medication.
  - Ambulatory encounters were counted as hospital visits.
  - Labs were matched by display text instead of code (51222), causing false positives.
  - Code syntax and logic errors.
  - — [FAB-paper](https://arxiv.org/html/2509.19319v2)
- **Limitations:**
  - Single-patient only.
  - No unanswerable questions.
  - Latency and cost were not evaluated.
  - Experiments used only the Demo data.
  - 32k context window.
  - Reliance on an LLM judge.
  - Simple baselines.
  - — [FAB-paper](https://arxiv.org/html/2509.19319v2)
- **Follow-up:** Knorr et al. (arXiv 2605.14126, May 2026) treat FHIR QA as sequential decisions over a resource graph. They train a multi-turn CodeAct agent with RL and LLM-judge execution-grounded rewards. Answer correctness went from 50% (o4-mini baseline) to **77% with Qwen3-8B**. Their stated motivation is that agents "often pick the wrong resources or break traversal constraints." — [RL-FHIR](https://arxiv.org/abs/2605.14126)

### Inferences
- FHIR-AgentBench is hard for different reasons than MedAgentBench:
  - It does **not** give codes in context. The agent must discover local code systems (MIMIC itemids rather than LOINC).
  - It needs reference traversal (MedicationRequest→Medication, Encounter joins).
  - It needs large-volume aggregation, which favours code execution over reading JSON.
- The striking result that multi-turn retrieval *without* code is the worst setting suggests that pulling more raw FHIR into context hurts unless a code tool does the filtering. That is directly relevant to harness or skill design.
- Gold resource IDs allow retrieval-level diagnostics: did the agent fail to find the data, or fail to reason over it? This is worth copying for our 150 questions.
- The 24% empty answers and the high score on them (0.74) show that empty questions are partly "free". A benchmark should balance them, and should require the agent to show it actually searched, for example through retrieval recall.

### Gaps
- I did not obtain verbatim baseline prompts. In the HTML paper they are base64-encoded listings, and I did not open `agent/*.py`.
- I found no evidence that the full MIMIC-IV-FHIR results were reported.
- The emergentmind and benchmarklist leaderboards are third-party and self-reported, so I did not use them.

---

## 4. Cross-benchmark lessons: what made tasks hard, and pitfalls for our 150-question design

### Takeaway
Difficulty in these benchmarks comes from five sources:
- multi-step conditional actions (check → threshold → dose calculation → paired order)
- temporal windows relative to a frozen "now"
- aggregation and arithmetic
- discovering codes, resource types and references (FHIR-AgentBench)
- exact output formatting.

The main validity threats the authors themselves name are:
- ambiguous conditionals
- format-shape grading brittleness
- small effective task diversity (10 templates × 30)
- truncation mismatches between agent and grader
- LLM-judge dependence
- single-run variance.

### Cited Findings
- Agents "struggle significantly with 'hard' tasks that take ≥3 steps". In v1 these were tasks 5, 6, 9 and 10: conditional dosing, averaging and conditional ordering. — [V2-paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)
- **Unit conversion:** v1 tasks 4 and 6 tell the agent the answer "should be a single number converted to a unit of mg/dL". Task 9 says mEq/L "(or mmol/L)". — [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench)
- **Temporal reasoning:** a frozen "now" (2023-11-13T10:15:00+00:00) and windows of "last 24 hours", ">1 year old" and "next day at 8am". In FHIR-AgentBench, "today", "since 2157" and "first hospital visit" rely on an injected current-time assumption. — [MAB-repo](https://github.com/stanfordmlgroup/MedAgentBench); [FAB-repo](https://github.com/glee4810/FHIR-AgentBench)
- **Action tasks were harder than query tasks** for most top models: Claude 3.5 Sonnet scored 85.33% on query vs 54.00% on action. The exceptions were Gemini-1.5 Pro and Qwen2.5, which were stronger on action. — [MAB-paper](https://arxiv.org/html/2501.14654)
- **Format brittleness:**
  - v1 failures were dominated by invalid actions and wrapped answers.
  - v2 lost points on `-1` vs `(-1,-1)`.
  - The v2 graders tolerate `[]` on some tasks and string-or-number on others, so tolerance is inconsistent across tasks.
  - — [MAB-paper](https://arxiv.org/html/2501.14654); [V2-paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf); [V2-repo](https://github.com/ericoericochen/medagentbenchv2)
- **Run-to-run variance:** Medmarks recommends averaging multiple runs because of the 300-case size. — [Medmarks](https://arxiv.org/pdf/2605.01417)
- **Leakage and contamination:**
  - MedAgentBench's tasks and patient MRNs are public.
  - V2's memory entries are derived from failures on the same task templates, with a 5-per-category train set and the rest as test. Test instances share the template, so memory amounts to template-level leakage. The authors frame this as learning the "vibe".
  - — [V2-paper](https://psb.stanford.edu/psb-online/proceedings/psb26/chen_eric.pdf)

### Inferences (design recommendations for a 150-question FHIR reasoning benchmark)
- **Template diversity:** Prefer about 30–50 distinct templates × 3–5 instances over 10 × 15. MedAgentBench's 10-template design saturated at 91–98% once the harness improved.
- **Store gold as code:** Store each question's gold as an executable reference function over a pinned FHIR snapshot with a frozen "now", as refsol does. Also store the gold supporting resource IDs, as FHIR-AgentBench does. That enables both answer scoring and retrieval precision/recall.
- **Specify answer shape exactly** per question (type, cardinality, units, rounding, date format). Grade with explicit tolerances, such as ±0.1 for means and integer floor for age. Treat empty or "not found" as an explicit allowed value with its wording specified.
- **Balance difficulty axes deliberately:**
  - temporal windows
  - aggregation and arithmetic
  - unit conversion
  - code discovery: withhold codes in some questions, give them in others
  - reference traversal (MedicationRequest→Medication, Observation→Encounter)
  - coded vs free-text matching (V2 task 7)
  - multi-resource joins
  - a calibrated share of empty-answer questions, kept well below FHIR-AgentBench's 24%, or with retrieval required.
- **Avoid ambiguous conditionals** of the kind seen in v1 tasks 9 and 10. Spell out the branch behaviour for missing data. Otherwise the benchmark measures "guess the grader" and memory or prompt tuning can game it.
- **Match agent and grader pagination:** Make sure the agent can retrieve everything the grader uses (V2's 200 vs 5000 `_count` mismatch). Test paging, `_count` and `next` links deliberately if that is a skill we want to measure.
- **Separate harness failures from reasoning failures:** Log invalid-action, round-limit and format failures as distinct statuses (AgentBench `SampleStatus`). Report success rate alongside tokens, latency and cost (V2 recommendation).
- **Repeated runs:** Run k≥3 per configuration and report the mean and spread (Medmarks). With 150 questions, one question is 0.67 pp, so single runs cannot separate configurations that are within a few points of each other.

### Gaps
- None of these sources measures the reliability of action repetition (pass^k) for FHIR agents. The v1 authors list it as future work.
- I found no 2025–2026 FHIR agent QA benchmark beyond FHIR-AgentBench and the MedAgentBench line that has comparable public data and code. One search for other 2026 FHIR agent benchmarks surfaced only the RL follow-up, Medmarks and community MedAgentBench wrappers. Other candidates may exist that I did not find.
