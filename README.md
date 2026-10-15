# FHIR Question Bench

A proposed 1,000-question benchmark for evidence-grounded medical agents working
with local folders of FHIR data.

The central question is not whether an LLM knows medicine. It is whether specialist
FHIR tools help an agent find the right records, interpret codes correctly, avoid
unsupported conclusions, and show a clinician exactly what supports its answer.

## Plan and investigation

- [Research, benchmark design, and question blueprint](docs/superpowers/specs/2026-10-15-fhir-question-benchmark-design.md)
- [Staged implementation plan](docs/superpowers/plans/2026-10-15-fhir-question-benchmark.md)
- [Measured Synthea source profile](docs/research/synthea-source-profile.json)

The design builds on [MedAgentBench](https://github.com/stanfordmlgroup/MedAgentBench)
and investigates
[cli-on-fhir, brendankowitz/ignixa-fhir#504](https://github.com/brendankowitz/ignixa-fhir/pull/504).
It distinguishes the prototype's implemented inspect/search/read tools from
proposed terminology, patient-context, and analytics capabilities.

**Status:** research and proposed plan, not an implemented benchmark. The 1,000
questions, executable answer keys, evaluation harness, and clinician review have
not yet been produced. Azure resource/deployment discovery was read-only; no
question-generation or evaluation inference was submitted.

This is research infrastructure using synthetic patient data, not a validated
clinical decision-support product.
