# FHIR-AgentBench suite

A second, external suite: questions and gold answers adapted from
[FHIR-AgentBench](https://github.com/glee4810/FHIR-AgentBench), asked over the MIMIC-IV Clinical Database Demo on FHIR
2.1.0 (`corpora/mimic-iv-fhir-demo-2.1.0`). It tests whether a tool such as `cli-on-fhir` helps on real, messier
records than Synthea. Nothing here holds gold: `fab import fhir-agentbench` writes questions, gold and question sets
into `generated/`, which is gitignored.

    fab corpus fetch corpora/mimic-iv-fhir-demo-2.1.0
    fab corpus verify corpora/mimic-iv-fhir-demo-2.1.0
    fab import fhir-agentbench --split test
    fab admit --corpus corpora/mimic-iv-fhir-demo-2.1.0 --questions-dir suites/fhir-agentbench/generated/questions --set suites/fhir-agentbench/generated/test-80.txt --update-lock
    fab run --profile profiles/b0-oracle.yaml --corpus corpora/mimic-iv-fhir-demo-2.1.0 --questions-dir suites/fhir-agentbench/generated/questions --set suites/fhir-agentbench/generated/test-80.txt --repeats 1 --runs-dir <B0-RUNS-DIR>

(`fab` is `dotnet run --project src/FhirAgentBench.Cli --`.) The first `admit` needs `--update-lock` to create
`generated/questions/gold.lock.json`; later ones check for drift. Use `test-all.txt` for the whole imported test split.

## What the importer does

`import.yaml` pins the upstream commit, the CSV path and its sha256, the corpus, and the default split, sample size and
seed. The importer downloads `final_dataset/questions_answers_sql_fhir.csv` at that commit (or reads `--csv <file>`),
refuses it unless the sha256 matches, and caches it as `generated/upstream.csv`. For the chosen split it writes:

- `generated/questions/fa-<question_id>.yaml`: one question per typeable row. `patient` is the corpus Patient whose MRN
  (`Patient.identifier`, system `http://mimic.mit.edu/fhir/mimic/identifier/patient`) is the question's subject_id, so
  the prompt's patient line renders; the importer skips a row whose `patient_fhir_id` is not that Patient.
- `now`: the assumption's "Assume the current time is …" as MIMIC local time with its America/New_York offset. Questions
  without one use the corpus `now` (2202-12-31T23:59:00-05:00, after every record); their gold does not depend on it.
- `assumption`: the rest of the upstream assumption (e.g. "Search for 'Daily Weight' to find weight."), rendered after
  `now` in the prompt. Some of these hints speak of SQL ("the dose_val_rx column"); they are kept verbatim.
- `generated/gold-<split>.json`: gold per question id for the `static-answer` oracle, with `true_fhir_ids` as evidence
  (so evidence recall works).
- `generated/<split>-all.txt` and `generated/<split>-<N>.txt`: every imported question, and a sample of N stratified by
  `main_table_name` (proportional quotas, largest remainders, seeded Fisher-Yates per stratum).
- `generated/import-report-<split>.txt`: rows skipped by reason, and answer types.

## Answer types

The spec is inferred from the row's SQL and template, never from its gold value, so it does not reveal whether the gold
is null (rules in `src/FhirAgentBench/Import/AnswerTyping.cs`):

| Upstream shape | Spec | Gold |
|---|---|---|
| `SELECT COUNT(*)>0` | boolean | 0/1 as false/true |
| `SELECT COUNT(` | integer | the count |
| "Is the … than …" comparisons | boolean, nullable | 0/1, or null |
| aggregates, `valuenum`, `value`, `dose_val_rx`, date arithmetic | decimal, nullable; tolerance 0.01 absolute or 0.1% relative, whichever is larger | the number |
| a selected `*time` column | datetime, nullable; same wall-clock minute | `YYYY-MM-DD HH:MM:SS` |
| care unit | text, nullable; case and whitespace ignored | the name |
| drug, specimen, organism, test names (`SELECT DISTINCT`) and "List the …" | set of text, decimal or datetime, nullable | the distinct rows |
| gender | text | MIMIC M/F as FHIR `male`/`female` |
| marital status | code (v3-MaritalStatus), nullable | MARRIED/SINGLE/WIDOWED/DIVORCED as M/S/W/D |

`[]` and `[[None]]` become a null gold ("no such result"). Every nullable spec here also sets `null_accepts_abstain`:
SQL cannot tell "nothing recorded" from "no such result", so on a null gold both `null` and `insufficient_data` are
correct, and the always-abstain baseline scores exactly the share of null golds.

Skipped and counted: rows not mappable to FHIR, admission-type questions (MIMIC's admission_type text is not in the FHIR
conversion, only lossy Encounter class and priority codes), single-valued questions whose gold has several rows, golds
that do not parse as their type (e.g. a dose of "1-2"), and SQL shapes with no rule.

## Attribution

- Questions and answers: FHIR-AgentBench, © its authors, [CC-BY-4.0](https://creativecommons.org/licenses/by/4.0/),
  adapted (answers re-typed for deterministic grading; questions filtered and sampled). Lee, G., Bach, E., et al. (2025).
  *FHIR-AgentBench: Benchmarking LLM Agents for Realistic Interoperable EHR Question Answering.* arXiv:2509.19319;
  Proceedings of the 5th Machine Learning for Health Symposium, PMLR 297 (2026). https://arxiv.org/abs/2509.19319
- Data: MIMIC-IV Clinical Database Demo on FHIR 2.1.0, ODbL v1.0; see `corpora/mimic-iv-fhir-demo-2.1.0/README.md`.
