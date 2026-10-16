# cli-on-fhir for agents: what makes an MCP tool beat a shell (2026-10-16)

## Question

Can an MCP tool give a Copilot agent a consistent edge over a shell-only agent at answering clinician questions from FHIR data: fewer tokens, more correct answers, and ideally less time? Does asking the agent to "map the patient first" (the metacognitive approach) give the same benefit without a tool? Do the bench's questions and data actually test this problem?

## Setup

| | |
|---|---|
| Harness | Copilot CLI 1.0.98, `claude-sonnet-5.5`, credit cap 30 per attempt |
| Corpus | `synthea-sample-v1` (109 patients, 119,981 resources, one transaction Bundle per patient) |
| Questions | `question-sets/v1.txt`, 106 questions, split into three disjoint samples:<br>• A (`tool-eval.txt`, 24): used to tune the tool<br>• B (`tool-eval-b.txt`, 24, random per tier): also used to tune the tool<br>• C (`tool-eval-c.txt`, the other 58): **held out**, never used for tuning |
| Prompt | Evidence may be `Type/id` or a FHIR search. At most 10 entries, ungraded. The answer goes to an absolute `{{workspace}}` path |
| Tool | ignixa-fhir PR #504 (cli-on-fhir), built from source; versions V1–V7 below |

Profiles:
- **P1**: shell only. Builtins `view`, `rg`, `glob`, `powershell`, `apply_patch`.
- **P1-map**: P1, plus a prompt telling the agent to write a ≤40-line `map.md` of the patient before answering. This is the metacognitive arm.
- **P2**: P1 plus the cli-on-fhir MCP server.
- **P3**: the MCP server only, with no shell; builtins `create` and `apply_patch` for the answer file.
- **…-warm**: the bench pre-computes the compact patient map (`cli-on-fhir context <corpus> <id> --compact`) and places it in the prompt. This is the PatientIQ "warm chart" path.

## Results

Pooled over all 106 questions. P1 was run twice on each sample, giving 212 attempts. P3-warm on V7 was run twice on A and B and once on C, giving 154 attempts.

| Profile | Correct | Credits per question | Tokens per question | Time per question | Spills to file |
|---|---|---|---|---|---|
| P1 (shell only) | 95.8% | 4.70 | 67.7K | 20.4 s | 15 |
| P1-map (metacognitive; A and B only) | 95.8% | 6.27 | 67.2K | 28.0 s | 2 |
| **P3-warm on V7 (MCP only + patient map)** | **97.4%** | **3.95 (−16%)** | **55.6K (−18%)** | **12.6 s (−38%)** | **0** |

By sample:

| Sample | P1 correct | P3-warm V7 correct | Credits | Tokens | Time |
|---|---|---|---|---|---|
| A (tuned) | 91.7% | 97.9% | −5% | −5% | −40% |
| B (tuned) | 97.9% | 100% | −18% | −24% | −46% |
| **C (held out)** | 96.6% (two runs) | 94.8% | −19% | −19% | −32% |
| **C (held out), V8** | 96.6% (two runs) | **98.3%** | **−21%** | **−22%** (55.8K vs 71.1K) | **−36%** |

V8 is V7 plus one instruction line: "Before answering with a code, check it: terminology operation=lookup (or search) confirms the display matches what was asked." With it, the KDIGO miss became correct. On the held-out sample, the tool now beats the shell on all four measures. Its only miss is the E11 cohort (T6).

**Per question:** P3-warm used fewer tokens on 82 of 106 questions and was faster on 98. The expected number of correct answers is 102.5 for P3-warm against 101.5 for P1.

**By tier (fewer tokens):** T1 13/13, T2 18/20, T3 16/20, T4 18/26, T5 12/13, **T6 1/8**, T7 4/6.

The tool wins on single-patient questions: lookup, filter, trend, joins, calculations and abstention. It still loses on cohort questions across the whole corpus. There a shell agent writes one Python script over 109 files, and the tool agent needs several FHIRPath calls.

On the held-out sample, the remaining accuracy gap is one question net:
- **Shell missed two** that the tool answered: a vaccine date and a last-visit date.
- **The tool missed three:**
  - two ICD-10 → SNOMED cohort judgements, E11 and I25;
  - one KDIGO stage code: it gave the generic "CKD stage 3" code instead of G3a, and did not check the code with the terminology tool it had.

### Held-out sample, two runs per profile, three models

All runs below are on sample C (58 held-out questions) with cli-on-fhir V8. Sonnet has two runs per profile; Haiku and Luna have one.

| Model | Profile | Correct | Credits per question | Tokens per question | Time per question | Spills |
|---|---|---|---|---|---|---|
| Sonnet 5.5 | Shell only | 96.6% | 4.94 | 70K | 19.2 s | 9 |
| Sonnet 5.5 | **MCP + map** | 96.6% | **3.94** | **55K** | **12.4 s** | **0** |
| Haiku 5.5 | Shell only | 75.9% | 0.55 | 117K | 35.8 s | 7 |
| Haiku 5.5 | **MCP + map** | **96.6%** | **0.32** | **65K** | **18.1 s** | 1 |
| GPT-6 Luna | Shell only | 91.4% | 0.26 | 57K | 28.0 s | 29 |
| GPT-6 Luna | **MCP + map** | **94.8%** | **0.21** | **47K** | **22.9 s** | 1 |

- **Patient-context tiers (T1–T5, T7):** the tool used fewer tokens and less time in **every tier, for every model**. Accuracy was equal or higher in every tier and model, with one exception: Sonnet on T5, where it got 15 of 16 against the shell's 16 of 16.
- **Sonnet on the patient-context tiers:** 109 of 110 correct with the tool, against 106 of 110 with the shell.
- **The weaker the model, the bigger the gain.** Haiku with the tool matched Sonnet with a shell on accuracy (96.6% against 96.6%) at about 1/15 of the credits. Shell-only Haiku and Luna spend turns parsing large Bundles; Luna spilled 29 times.
- **Cohorts (T6) are the consistent exception.** With the tool, every model used more tokens there and got 1–2 of 3 right, against 3 of 3 with the shell. A shell agent writes one script over 109 files; the tool agent needs several FHIRPath calls. This is the PatientIQ "out of scope / SQL-on-FHIR views" route, and it needs its own tool.

## How the tool got there

Every change below came from a transcript of a run that went wrong. All were measured on samples A and B.

| Version | Change | Effect |
|---|---|---|
| fix | Resolve conditional references (`Practitioner?identifier=…`); report every reference that cannot be followed | Join questions became answerable; previously some includes came back empty with no warning |
| V1 | `patient_list` query; server instructions; 12 KB responses with concrete `next` calls | File spills went from 30 to 2. `patient_list` had returned 26 KB, and the agent grepped the spill file |
| V2 | `columns` (FHIRPath with `resolve()`) and `fhirpath` expression mode; reject unknown arguments | A prescriber join takes 2 calls instead of 7; covers both medication shapes |
| V3 | Menu from 17 tools to 9 (`terminology` and `patient_context` with an `operation`, plus `help`); descriptions trimmed | Menu went from about 12K to about 3.4K tokens; input per request from 28K to 16–18K |
| V4 | NLM RxClass drug classes (`class-search`, `class-members`, folder-scoped) | Statin cohort went from 19.6 to 8.0 credits |
| V5 | Fast patient lookup (no index loads); persisted compartments; **compact patient map**; per-version cache; tx-server text search | Lookup went from 6.8–32 s to 0.35 s; map is 11 KB → 5.5 KB |
| V6 | Map trimmed to about 4 KB | Fewer tokens on every request in warm runs |
| V7 | `perPatient=latest` for cohorts; MCP responses capped at 16 KB (Copilot spills above about 14–20 KB); `next` suggests a whole-answer `fhirpath` first | The LDL cohort takes 1 call instead of 15; 0 spills |

Two things outside the tool mattered as much as the tool itself:
1. **Index cache thrash.** Two tool builds shared one cache directory, so almost every attempt rebuilt its indexes. That made `patient_list` average 18 s. Giving each build its own cache directory, and pre-warming it, removed the time penalty entirely.
2. **The answer-file contract.** A shell agent ran `cd` into the corpus and wrote `answer.json` there, so the tamper check aborted the run. A tools-only agent could not overwrite a wrong first answer with `create`. Both were fixed in the bench: an absolute workspace path in the prompt, and `apply_patch` for P3.

## The metacognitive approach

Asking the shell agent to write a patient map first did not improve accuracy: 95.8% against 95.8%, on A and B. It cost 33% more credits and 37% more time, because the agent spends turns and output tokens building the map.

The same idea pays off when the map is **pre-computed and handed over**. That is P3-warm: the map arrives in the prompt for free, and the agent goes straight to one or two targeted calls. Requests per question fall from about 5.9 to 3.8. In short, the metacognitive insight is right, but the map should be derived, not generated by the model on every question. This matches the PatientIQ design's warm root digest.

## Do the questions and data test the problem?

Partly. The fit is good where the tool is meant to help, and weaker in three places.

**Good fit**
- The questions are clinician-voiced and patient-scoped (T1–T5). Most need code knowledge: 40 MAP questions, two creatinine LOINCs, both medication shapes. Several need joins (T4) or have an abstain answer (T7).
- These are the PatientIQ routes Lookup, Explain, and Filter/Count/Trend, and the tool wins on them.

**Weaker fit**
1. **The data layout favours the shell.** Synthea writes one Bundle file per patient, named after the patient, so `grep name` plus PowerShell is a strong baseline. In the PatientIQ setting the agent has no files at all; it reaches a FHIR store through tools. The P3 (tools-only) result is therefore the realistic one. An NDJSON-by-type copy of the corpus, with the same answers, would remove the file-per-patient shortcut and should be the next corpus variant.
2. **Cohort questions are a different route.** PatientIQ marks population questions "Out of scope (SQL-on-FHIR views)". The bench has 8 of them, and they are the only tier where the tool still loses on tokens. They belong on a SQL-on-FHIR or analytics route, such as the `duckdb-analytics` candidate in the cli-on-fhir design docs. They should be reported separately, not averaged into the patient-context score.
3. **No narrative content.** The bench has no questions over notes, though Synthea ships DocumentReference notes. The PatientIQ "Narrative" route is untested.

Other caveats:
- Synthea data quirks (A1c values below 4%, creatinine LOINCs that disagree with each other) limit which questions are clinically sound.
- Cohorts are small: about 100 living patients.
- Samples A and B were used for tuning. Only C (58 questions) is held out, and it was run once (K=1) on the tool side.
- Most tuning used Sonnet 5.5. Haiku 5.5 and GPT-6 Luna were each run once, on the held-out sample only.

## Recommendation

Build the agent surface the way PatientIQ describes, and the way the V7 results support:

1. **Expose the FHIR data through the MCP tool only**, without a general shell, for patient-context questions. A shell and an MCP tool together (P2) cost more tokens than either alone. The agent pays for two tool menus and mixes the two approaches.
2. **Warm the patient map** (the root digest, about 4 KB) and give it to the agent before the first question. Do not ask the model to build it. This is the biggest single saving: fewer requests and about 40% less time.
3. **Keep the tool's answers small and exactly shaped:**
   - `columns` with `resolve()` for lists and joins;
   - `fhirpath` for single values;
   - `perPatient` for "latest per patient";
   - responses under the client's spill threshold (16 KB for Copilot), with `next` calls that answer the question rather than page through rows.
4. **Make terminology first-class**: RxClass drug classes, SNOMED hierarchy through a tx server, and code verification. The remaining held-out errors are terminology judgements (ICD-10 → SNOMED, choosing the G3a code). Adding a "verify a code before you answer with it" line to the instructions is cheap.
5. **Route cohorts elsewhere**: a SQL-on-FHIR view or analytics tool. Do not stretch patient-context search to cover them.

With 1–3 in place (cli-on-fhir V7 plus the warm map, MCP only), the agent used 18% fewer tokens, was 38% faster and answered slightly more questions correctly over all 106 questions. With the step 4 instruction line added (V8, the head of ignixa-fhir#507), the held-out 58 questions came out at:
- 98.3% correct, against 96.6% for the shell;
- 22% fewer tokens;
- 36% less time;
- no spills.

## Next steps

1. Add a cohort route (SQL-on-FHIR views or DuckDB over SQL-on-FHIR output), so T6 also beats the shell.
2. Build an NDJSON-by-type corpus variant (same answers, no file-per-patient shortcut).
3. Report T6 separately from the patient-context score.
4. Run K=3 on the held-out sample, and freeze a dev split for further tool tuning.
5. Add a few narrative (DocumentReference note) questions.
