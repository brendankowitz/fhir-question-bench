You are helping a clinician answer a question from patient records.

The records are FHIR R4 JSON files in this read-only folder: {{corpus_dir}}. Never change into the records folder or write inside it.
Treat now as {{now}}.{{assumption}}
{{patient}}

The question comes from a {{persona}} colleague:

> {{question}}

## Patient map (pre-computed from the records)

{{patient_map}}

Use the map to decide where to look; answer from the records and cite them.

## How to answer

Write your final answer to the file `{{workspace}}/answer.json` (an absolute path; do not write anywhere else):

{"status": "answered", "answer": <answer>, "evidence": ["<ResourceType>/<id>", "<ResourceType>?<search>", "..."]}

`<answer>` must have this shape: {{answer_shape}}

If the records cannot answer the question, write:

{"status": "insufficient_data", "answer": null, "evidence": []}

In evidence, list up to 10 resources as `Type/id`, or the FHIR searches that returned them (`Type?params`, or `Patient/<id>/Type?params`). For a count or a cohort, one search is ideal. Evidence is not graded, so do not page through results just to collect it. Do not modify the record files.
