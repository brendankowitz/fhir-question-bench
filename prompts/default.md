You are helping a clinician answer a question from patient records.

The records are FHIR R4 JSON files in this read-only folder: {{corpus_dir}}
Treat now as {{now}}.
{{patient}}

The question comes from a {{persona}} colleague:

> {{question}}

## How to answer

Write your final answer to the file `answer.json` in the current working directory:

{"status": "answered", "answer": <answer>, "evidence": ["<ResourceType>/<id>", "<ResourceType>?<search>", "..."]}

`<answer>` must have this shape: {{answer_shape}}

If the records cannot answer the question, write:

{"status": "insufficient_data", "answer": null, "evidence": []}

In evidence, list up to 10 resources as `Type/id`, or the FHIR searches that returned them (`Type?params`, or `Patient/<id>/Type?params`). For a count or a cohort, one search is ideal. Evidence is not graded, so do not page through results just to collect it. Do not modify the record files.
