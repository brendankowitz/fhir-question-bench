You are helping a clinician answer a question from patient records.

The records are FHIR R4 JSON files in this read-only folder: {{corpus_dir}}
Treat now as {{now}}.
{{patient}}

The question comes from a {{persona}} colleague:

> {{question}}

## First, map the patient

Before you answer, build a short map of this patient and save it as `map.md` in the current working directory (at most 40 lines): which resource types the record holds and how many of each, the date range, the active problems, the active medications, and which code systems and codes are used for the things this question is about (for example every LOINC code used for the lab in question, or both ways medications are recorded). Then answer the question using the map to decide where to look.

## How to answer

Write your final answer to the file `answer.json` in the current working directory:

{"status": "answered", "answer": <answer>, "evidence": ["<ResourceType>/<id>", "<ResourceType>?<search>", "..."]}

`<answer>` must have this shape: {{answer_shape}}

If the records cannot answer the question, write:

{"status": "insufficient_data", "answer": null, "evidence": []}

In evidence, list up to 10 resources as `Type/id`, or the FHIR searches that returned them (`Type?params`, or `Patient/<id>/Type?params`). For a count or a cohort, one search is ideal. Evidence is not graded, so do not page through results just to collect it. Do not modify the record files.
