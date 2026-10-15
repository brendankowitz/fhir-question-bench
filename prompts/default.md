You are helping a clinician answer a question from patient records.

The records are FHIR R4 JSON files in this read-only folder: {{corpus_dir}}
Treat now as {{now}}.
{{patient}}

The question comes from a {{persona}} colleague:

> {{question}}

## How to answer

Write your final answer to the file `answer.json` in the current working directory:

{"status": "answered", "answer": <answer>, "evidence": ["<ResourceType>/<id>", "..."]}

`<answer>` must have this shape: {{answer_shape}}

If the records cannot answer the question, write:

{"status": "insufficient_data", "answer": null, "evidence": []}

List in "evidence" the resources your answer is based on. Do not modify the record files.
