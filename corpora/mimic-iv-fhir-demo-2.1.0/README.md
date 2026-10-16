# MIMIC-IV Clinical Database Demo on FHIR 2.1.0

The corpus for the FHIR-AgentBench suite (`suites/fhir-agentbench/`): 100 de-identified MIMIC-IV demo patients converted
to FHIR R4 by the MIMIC-on-FHIR project, as gzipped NDJSON per resource profile.

    fab corpus fetch corpora/mimic-iv-fhir-demo-2.1.0
    fab corpus verify corpora/mimic-iv-fhir-demo-2.1.0

`fetch` downloads PhysioNet's 2.1.0 zip (`source.url`), checks it against the pinned `source.sha256`, and stores each
`fhir/*.ndjson.gz` decompressed as `data/*.ndjson` (read-only), so the bench's index, `cli-on-fhir` and shell agents all
read plain NDJSON. `data.manifest.json` pins the size and sha256 of every stored file; `verify` and `fab run` check it.
The data is never committed.

- `now` is 2202-12-31T23:59:00-05:00, the end of the year of the latest record (2202-08-26), so no record is in the
  future. Questions that state a time ("Assume the current time is …", at most 2201-12-31) carry their own `now`; the
  corpus `now` applies only to questions without one, whose SQL gold does not depend on it.
- `mrn_system` is `http://mimic.mit.edu/fhir/mimic/identifier/patient`: `Patient.identifier` carries the MIMIC
  `subject_id` there, which is how FHIR-AgentBench questions name patients ("patient 10018081").
- Times are MIMIC's shifted local times with America/New_York offsets (`-04:00`/`-05:00`); records run from 2102 to 2202.

## Licence and attribution

Open access under the [Open Data Commons Open Database License v1.0](https://opendatacommons.org/licenses/odbl/1-0/)
(ODbL). The bench downloads the database from PhysioNet; it does not redistribute it. Anything derived from it and
published must stay under the ODbL and keep this attribution.

Please cite the resource and PhysioNet:

- Bennett, A., Ulrich, H., Wiedekopf, J., Szul, P., Grimes, J., & Johnson, A. (2025). *MIMIC-IV Clinical Database Demo on
  FHIR* (version 2.1.0). PhysioNet. RRID:SCR_007345. https://doi.org/10.13026/vphg-y548
- Pollard, T., Moody, B. E., Lehman, L., Gow, B., Fernandes, C., Xie, C., Johnson, A., Mark, R. G., & Heldt, T. (2026).
  PhysioNet as a global platform for biomedical research. *Nature Health*. https://doi.org/10.1038/s44360-026-00096-z

Source page: https://physionet.org/content/mimic-iv-fhir-demo/2.1.0/
