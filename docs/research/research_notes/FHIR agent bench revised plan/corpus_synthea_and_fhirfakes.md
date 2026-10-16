# Corpus building: Synthea R4 sample data and Ignixa.FhirFakes

Method note: Synthea facts come from three places. The first is a fresh scan of the pinned archive (`synthea_sample_data_fhir_latest.zip` at synthea-sample-data commit `9959d9178e`, sha256 `56cb9e49…a521`, which matches the local profile). The scan was a Python stdlib pass over every bundle entry, written by this researcher and not committed; the numbers below are its output. The second is Synthea source and config on `master` (commit `d9d07a6eef`, 2026-08-18). The third is the wiki. FhirFakes and cli-on-fhir facts come from source read through `gh api` on `brendankowitz/ignixa-fhir` (`main`, and PR #504 head `claude/confident-turing-tph6h1`, still OPEN as of 2026-10-15T17:32Z).

## 1. What the pinned Synthea R4 sample provides, and its pitfalls

### Takeaway
The pinned sample is large and realistic in shape: 109 patients, 119,981 resources, 24 types, and US Core 6.1-style profiles. It is clinically "too clean" for edge-case questions. Every Observation is `final`, every Condition is `confirmed`, there are no `entered-in-error` statuses and no `dataAbsentReason`, and only 5 LOINC codes carry more than one unit. It also uses three reference styles at once (`urn:uuid`, conditional `?identifier=`, and contained `#`), and any loader or tool must resolve all three.

### Cited Findings
**Provenance**
- The pinned sample-data commit `9959d9178e` (2026-08-18) is titled "Update from synthetichealth/synthea@d9d07a6eef". Synthea releases are v4.0.0 (2026-03-05) and v3.4.0 (2025-11-05), plus a rolling `master-branch-latest` tag (2026-08-18) — [synthea-sample-data commits](https://github.com/synthetichealth/synthea-sample-data/commits), [synthea releases](https://github.com/synthetichealth/synthea/releases)
- Archive: 29.6 MB compressed and 386 MB of JSON in 111 files. 109 are per-patient `transaction` Bundles. The other 2 are `batch` Bundles: `practitionerInformation1787069559744.json` (556 entries) and `hospitalInformation1787069559744.json` (557 entries) — local scan; [local profile](../../synthea-source-profile.json)
- Resources per patient bundle: minimum 47, median 624, maximum 18,551, so patient size is very skewed — [local profile](../../synthea-source-profile.json)

**Bundle and reference structure**
- Every `entry.fullUrl` is `urn:uuid:` (119,981). Every entry is `request.method=POST`. Only Location, Practitioner and Organization entries carry `request.ifNoneExist`, which makes them conditional creates. PractitionerRole is a plain POST — local scan
- Reference occurrences:
  - 445,174 are `urn:uuid:` (intra-bundle).
  - 122,715 are conditional references. These are `Practitioner?identifier=http://hl7.org/fhir/sid/us-npi|…` (59,305), `Location?identifier=https://github.com/synthetichealth/synthea|…` (38,806) and `Organization?identifier=https://github.com/synthetichealth/synthea|…` (24,604).
  - 18,986 are contained `#` references (Claim and EOB).
  - The local profile counts the conditional references as "relative". They are in fact conditional search URLs, which only resolve if the practitioner and hospital batch files are loaded first or resolved by identifier.
  — local scan
- Resource counts by type: Observation 46,305; Procedure 16,063; DiagnosticReport 11,013; Claim and EOB 9,493 each; Encounter and DocumentReference 5,635 each; MedicationRequest 3,858; Condition 3,540; SupplyDelivery 2,242; Immunization 1,562; Medication and MedicationAdministration 1,026 each; Device 532; ImagingStudy 418; CareTeam and CarePlan 352 each; Location 279; Practitioner, PractitionerRole and Organization 278 each; Patient and Provenance 109 each; AllergyIntolerance 105 — [local profile](../../synthea-source-profile.json)
- Claim and EOB make up about 16% of resources. They are billing noise for clinical questions, but they are realistic distractors — local scan

**Code systems (codings counted recursively)**
- CMS BlueButton adjudication (188k; only on Claim and EOB), LOINC (134,944), SNOMED CT (101,366), UCUM (40,615), RxNorm (12,610), CVX (4,686) — local scan
- DICOM CID 29 (`http://dicom.nema.org/medical/dicom/current/output/chtml/part16/sect_CID_29.html`, 418) is used for ImagingStudy modality, with SNOMED for body site — local scan
- Also present: NUCC provider taxonomy, NUBC discharge disposition (15), CDC race/ethnicity `urn:oid:2.16.840.1.113883.6.238`, v3-ActCode (Encounter.class), v2-0074 (DiagnosticReport LAB), us-core-documentreference-category, IHE formatcode — local scan
- **No ICD-10, CPT or local/proprietary code systems appear.** Every Condition code is SNOMED and every Procedure code is SNOMED — local scan

**Observations**
- Categories: laboratory 29,936; vital-signs 11,176; survey 3,540; social-history 1,368; exam 212; imaging 46; procedure 22; therapy 5 — local scan
- value[x]: valueQuantity 36,082; valueCodeableConcept 7,896; component-only 2,267 (BP panel `85354-9` and others); valueString 60 — local scan
- 219 distinct Observation codes. The most frequent are pain 72514-3, weight 29463-7, BP panel 85354-9, HR 8867-4, RR 9279-1, height 8302-2, smoking 72166-2, BMI 39156-5, eGFR 33914-3, PRAPARE 93025-5, then the basic metabolic panel (2339-0, 38483-4, …) and HbA1c 4548-4 — local scan
- Unit variety is tiny: 45 distinct units overall. Only 5 codes have more than one unit: 788-0 {fL, %}, 33914-3 {mL/min, mL/min/{1.73_m2}}, 5811-5 {{nominal}, {SG}}, 5803-2 {pH, [pH]}, 89579-7 {pg/mL, ng/L} — local scan

**Statuses (no diversity)**
- Observation 100% `final`, DiagnosticReport 100% `final`, Encounter 100% `finished`, Procedure 100% `completed`, Immunization 100% `completed` — local scan
- Condition clinicalStatus: resolved 2,646, active 894. verificationStatus is `confirmed` on all 3,540. category is `encounter-diagnosis` on all; there is no `problem-list-item` — local scan
- MedicationRequest: completed 3,590, active 268. intent is always `order` — local scan
- DocumentReference: superseded 5,526, current 109 (one current per patient). CarePlan: active 150, completed 202 — local scan
- 9 patients have `deceasedDateTime`. No resource carries `dataAbsentReason`. There are no duplicate type/id pairs and no missing ids — local scan; [local profile](../../synthea-source-profile.json)

**MedicationRequest vs Medication**
- 2,832 MedicationRequests use inline `medicationCodeableConcept` (RxNorm). 1,026 use `medicationReference` to a standalone `Medication` resource. Medication count is exactly 1,026, which equals the MedicationAdministration count, so standalone Medication appears tied to administered (inpatient or hospice-type) meds — local scan
- So a "what meds is X on" question must handle both shapes. Some MedicationRequests also carry a SNOMED coding (555), which comes from reasonCode — local scan

**Profiles**
- `meta.profile` uses US Core profiles (us-core-observation-lab, us-core-encounter, and others) plus pediatric growth-chart profiles. Synthea `master` defaults to `exporter.fhir.use_us_core_ig = true` with `exporter.fhir.us_core_version = 6.1.0`. The older wiki says default `false` / `5.0.1`, which is a stale-docs conflict — [synthea.properties](https://github.com/synthetichealth/synthea/blob/master/src/main/resources/synthea.properties); contradicted by [Common Configuration wiki](https://github.com/synthetichealth/synthea/wiki/Common-Configuration)

**Known realism critiques**
- Chen et al. 2019 (BMC Med Inform Decis Mak) tested a 1.2M-patient Massachusetts Synthea cohort against 4 quality measures:
  - Controlling high blood pressure: Synthea 0% vs 74.5% MA and 69.7% national.
  - Hip/knee complications: 0% vs 2.9%.
  - COPD 30-day mortality: 0.7% (strict) vs 7.0%.
  - Colorectal screening: 68.7% vs 77.3%.
  - The authors say Synthea does "not currently model for deviations in care", assumes guideline-concordant care, and has limited heterogeneity in outcomes.
  — [Chen et al. 2019, PMC6416981](https://pmc.ncbi.nlm.nih.gov/articles/PMC6416981/)
- An OHDSI 2024 poster cites Hodges et al.: Synthea medication data was "unreliable without external modification", for example via the Medication Diversification Tool — [OHDSI 2024 poster (Wagner)](https://ohdsi.org/wp-content/uploads/2024/10/41-Wagner-Evaluating_Synthea-Clair-Blacketer.pdf) (secondary source; summary only, via search snippet)

### Inferences
- What Synthea does not model, and the benchmark must inject:
  - entered-in-error, preliminary, amended, corrected or cancelled statuses
  - Condition `problem-list-item`, unconfirmed or refuted verificationStatus
  - unit variants (mg/dL vs mmol/L for glucose or cholesterol)
  - local or non-standard code systems, ICD-10
  - missing values and `dataAbsentReason`
  - duplicate or near-duplicate resources
  - free-text-only codes
  - conflicting data across sources
- The conditional references and the separate practitioner and hospital batch files form a real tool-differentiating trap. A naive "read every file" agent sees an unresolvable `Practitioner?identifier=` string. Benchmark questions about "who was the practitioner" exercise that resolution.
- The median of 624 resources per patient (max 18.5k) means one large patient can exceed an agent's context by itself, so tooling advantage shows up even within a single patient.
- DocumentReference is 98% `superseded`, which gives a natural "current vs superseded" question for free.

### Gaps
- I could not find the exact `run_synthea` parameters (seed, population, state, reference date) used to build the published sample. Synthea's `.github/workflows` lists only `ci-build-test.yml` and `deploy.yml`, and neither matched a search for generation flags. **Treat the sample as an opaque pinned artifact; it is not reproducible from a seed.**
- I did not check whether DocumentReference attachments contain base64 clinical notes that could carry hidden facts. The scan did not decode attachments.
- I did not compute per-system code counts (distinct SNOMED or RxNorm codes).

## 2. Synthea config options for a reproducible custom corpus

### Takeaway
Synthea can regenerate a pinned corpus deterministically only if every time-derived input is pinned. Seed, clinician seed, reference date and end date all default to `System.currentTimeMillis()`. Pin the Synthea commit or jar too. The `-k` keep modules and the Flexporter (`-fm`) give cohort control and a built-in FHIRPath-based post-processor for injection.

### Cited Findings
- CLI usage: `run_synthea [options] [state [city]]`. The options are:
  - `-s seed`, `-cs clinicianSeed`, `-p populationSize`, `-ps singlePersonSeed`
  - `-r referenceDate (YYYYMMDD)`, `-e endDate`
  - `-g gender`, `-a minAge-maxAge`, `-o overflowPopulation`
  - `-m moduleFilter` (split on the path separator)
  - `-c localConfigFilePath`, `-d localModulesDirPath`
  - `-i`/`-u` population snapshots, `-t updateTimePeriodInDays`
  - `-f fixedRecordPath`, `-k keepMatchingPatientsPath`
  - `-fm flexporterMapping`, `-ig igDir`
  - `--config.setting=value` overrides (either `--key=value` or `--key value`)
  — [App.java](https://github.com/synthetichealth/synthea/blob/master/src/main/java/App.java)
- Generator defaults: `referenceTime = System.currentTimeMillis()`, `endTime = referenceTime`, `seed = referenceTime`, `clinicianSeed = referenceTime`. Per-person seeds come from `populationRandom.randLong()`. One code path, `generatePerson(index)`, uses `UUID.randomUUID()` for the person seed instead — [Generator.java](https://github.com/synthetichealth/synthea/blob/master/src/main/java/org/mitre/synthea/engine/Generator.java)
- `-k`: takes a file path, or a name under `src/main/resources/keep_modules`. The bundled modules are `keep_diabetes.json`, `keep_diabetes_no_dr.json`, `keep_hospice.json`, `keep_medicare_beneficiaries.json`, `keep_npdr.json`, `keep_npdr_no_pdr.json`, `keep_pdr.json`, `must_have_cabg.json` and `must_have_cardiac_surgery.json`. `generate.max_attempts_to_keep_patient = 1000` per slot before an exception; 0 means unlimited — [App.java](https://github.com/synthetichealth/synthea/blob/master/src/main/java/App.java), [keep_modules dir](https://github.com/synthetichealth/synthea/tree/master/src/main/resources/keep_modules), [synthea.properties](https://github.com/synthetichealth/synthea/blob/master/src/main/resources/synthea.properties)
- Exporter properties (master defaults):
  - `exporter.baseDirectory=./output/`
  - `exporter.years_of_history=10`. Older history "may be filtered out"; 0 keeps everything.
  - `exporter.fhir.export=true`
  - `exporter.fhir.use_us_core_ig=true`, `exporter.fhir.us_core_version=6.1.0`
  - `exporter.fhir.transaction_bundle=true`
  - `exporter.fhir.bulk_data=false`. Setting it true writes NDJSON per type and ignores pretty_print.
  - `exporter.fhir.included_resources=` / `excluded_resources=`. Only one may be set; Patient and Encounter are always included.
  - `exporter.hospital.fhir.export=true`, `exporter.practitioner.fhir.export=true`. These produce the two batch files.
  - `exporter.groups.fhir.export=false`
  - `exporter.split_records=false` with `.duplicate_data=false`. split_records gives one record per provider organization, which is a native way to get cross-source duplicates.
  - `exporter.use_uuid_filenames=false`, `exporter.pretty_print=true`, `exporter.metadata.export=true`
  — [synthea.properties](https://github.com/synthetichealth/synthea/blob/master/src/main/resources/synthea.properties)
- Generation properties: `generate.default_population=1`, `generate.thread_pool_size=-1` (all cores), `generate.timestep=604800000` (1 week), `generate.only_alive_patients=false`, `generate.only_dead_patients=false`, `generate.demographics.default_file=geography/demographics.csv` — [synthea.properties](https://github.com/synthetichealth/synthea/blob/master/src/main/resources/synthea.properties)
- Flexporter (experimental): `./run_synthea -fm mapping.yaml [-ig ig_dir]`, or standalone `./run_flexporter -fm mapping.yaml -s source_fhir`.
  - The YAML has `name`, `applicability` (FHIRPath), `variables`, `customValueSets` and `actions`.
  - Action types: `profiles`, `set_values` (`applicability` + `fields[location, value, transform]`), `create_resource` (with `based_on`, `writeback`, `if`), `keep_resources`, `delete_resources`, `execute_script` (JavaScript).
  - Value functions: `getField`, `findValue`, `setRef`, `findRef`, `getAttribute`, `randomCode`.
  - Known issues: dangling references after delete; only primitives copy reliably; the date transforms fill missing precision with random values; seeding is not documented.
  — [Flexporter wiki](https://github.com/synthetichealth/synthea/wiki/Flexporter)

### Inferences
- A reproducible recipe looks like this:
  ```
  java -jar synthea-with-dependencies.jar -s 20261015 -cs 20261015 -r 20260101 -e 20260101 -p 120 \
    --exporter.years_of_history=0 --exporter.fhir.bulk_data=false Massachusetts
  ```
  - Pin the jar (v4.0.0 or the commit).
  - Byte equality is unverified: thread scheduling or the UUID path could still vary. Verify by running twice and diffing a canonical hash.
- `-p` counts living patients by default, and the output also includes dead ones (the sample has 9 deceased patients out of 109). Expect slightly more than `-p` records.
- Use `-k` (for example `keep_diabetes.json`) or `-m` to guarantee enough patients for specific question families (diabetes, CABG, hospice).
- `bulk_data=true` gives NDJSON, which is better for a "folder of data" tool. Transaction bundles better mimic EHR exports. The choice changes reference style: verify whether bulk mode emits relative `Type/id` refs instead of urn:uuid.
- Flexporter `set_values`/`execute_script` could inject edge cases inside Synthea's pipeline. Because its randomness is undocumented, a separate deterministic post-processor (Section 4) is safer for gold.

### Gaps
- I did not check whether bulk_data mode rewrites references to `Type/id`, or how `-p` interacts with deaths. I did not test determinism by running Synthea twice.
- The wiki's Common Configuration page is stale relative to `synthea.properties` (US Core default and version). Prefer the properties file.

## 3. The user's generator: Ignixa.FhirFakes (plus CLI)

### Takeaway
FhirFakes lives inside `brendankowitz/ignixa-fhir` at `src/Core/Ignixa.FhirFakes`, with the CLI at `tools/Ignixa.FhirFakes.Cli`. It is published on NuGet as `Ignixa.FhirFakes` and `Ignixa.FhirFakes.Cli`, latest 0.6.82. It is a Bogus-based, Synthea-inspired generator with four layers: schema/binding-aware random resources, a ScenarioBuilder state machine with about 19 predefined scenarios, patient lifecycles, and population generation for 11 US cities. Its edge-case pipeline is seeded and writes a mutation manifest, but it only targets string, unicode and temporal fuzzing, not clinical-semantics edge cases. Population and scenario generation from the CLI are **not seedable**, so it cannot be the deterministic base of a pinned corpus as-is.

### Cited Findings
- No separate `fhir-fakes` repo exists. The `brendankowitz` FHIR repos include ignixa-fhir, `fhir-question-bench` (public), fhir-vectorized-patient-summary and fhirpath-lab-dotnet — `gh search repos` (local CLI)
- The package targets net9.0 and net10.0, has `PackageId Ignixa.FhirFakes`, `PackageStability=stable`, and depends on `Bogus`, `Ignixa.Specification` and `Ignixa.Serialization` — [Ignixa.FhirFakes.csproj](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/Ignixa.FhirFakes.csproj)
- NuGet versions for both `ignixa.fhirfakes` and `ignixa.fhirfakes.cli` run up to `0.6.82` — [NuGet flat container](https://api.nuget.org/v3-flatcontainer/ignixa.fhirfakes/index.json)
- Architecture, per the README:
  - Layer 1 is `BindingAwareGenerator`/`SchemaBasedFhirResourceFaker`, which respects profiles and terminology bindings, with a `ClinicalDomain` theme.
  - Layer 2 is `ScenarioBuilder` (fluent), `ProbabilisticBranchState`, `VitalSignCorrelationEngine` and `CommonScenarios`.
  - Layer 3 is lifecycles: healthy child, typical adult, metabolic syndrome, pediatric asthma, elderly multi-morbidity.
  - Layer 4 is `PopulationGenerator` with `KnownCities` (11 cities, census race distributions) and Bogus-locale names.
  - It is "inspired by Synthea".
  — [README](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/README.md)
- Predefined scenarios (one file each): AsthmaticChild, CancerCarePathway, Cardiovascular, ChronicDisease, ComprehensivePreventiveCare, DiabeticPatient, EarInfection, EmergencyDepartment, EnhancedWellnessVisit, HypertensivePatient, MentalHealthTreatment, MetabolicSyndromeProgression, PediatricAsthmaOnset, PregnantPatient, SurgicalPathway, UrinaryTractInfection, WellnessVisit. There is also the workflow `DailyAppointmentSchedule`. All are discoverable through `ScenarioCatalog.GetAll()/Find/Invoke` with parameter overrides — [Scenarios/Predefined tree](https://github.com/brendankowitz/ignixa-fhir/tree/main/src/Core/Ignixa.FhirFakes/Scenarios/Predefined), [README](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/README.md)
- Scenario states cover the resource types it can emit in scenarios: AllergyIntolerance, CarePlan, CareTeam, Condition onset/end, Coverage, DiagnosticReport, Encounter, Goal, Immunization, MedicationOrder (with `ConditionalMedicationEscalationState`), Observation, Organization, Patient, Practitioner, Procedure, ServiceRequest, Delay, Guard, ProbabilisticBranch, SetAttribute, CallSubScenario. Builders also exist for Group, HealthcareService, Location, MedicationDispense, OrganizationAffiliation, PractitionerRole, RiskAssessment and ValueSet — [Scenarios/States tree](https://github.com/brendankowitz/ignixa-fhir/tree/main/src/Core/Ignixa.FhirFakes/Scenarios/States), [Builders tree](https://github.com/brendankowitz/ignixa-fhir/tree/main/src/Core/Ignixa.FhirFakes/Builders)
- Patient profiles: Default, US Core, UK Core and AU Base, each with its own name strategy — [Builders/Profiles](https://github.com/brendankowitz/ignixa-fhir/tree/main/src/Core/Ignixa.FhirFakes/Builders/Profiles)
- CLI: `ignixa-fakes <stu3|r4|r4b|r5|r6> <resource|scenario|population|workflow> --out <dir>`. Options:
  - `--resolved-references`
  - `--ndjson` (population only; one file per type)
  - `--param name=value` (scenario)
  - `--density minimal|realistic|maximum`, `--theme`
  - `--seed` (**`resource` command only**)
  - `--edge-cases [selectors]` (**`resource` command only**)
  - `--validate`
  - Exit codes 0, 1, 2.
  — [CLI README](https://github.com/brendankowitz/ignixa-fhir/blob/main/tools/Ignixa.FhirFakes.Cli/README.md)
- Determinism:
  - `SchemaBasedFhirResourceFaker` has an unseeded constructor (`new Faker()`, `new Random()`) and a seeded one (`new Randomizer(seed)`, `new Random(seed)`).
  - `ScenarioBuilder(schemaProvider, int seed)` exists and seeds the faker and PatientBuilder.
  - `PopulationGenerator.Generate(state, n)` uses `new Bogus.Randomizer()` and `Random.Shared` (smoker 13.7%, family history 30%, allergies 25%), with no seed parameter.
  - The CLI `PopulationCommand` picks a random state with `new Random()` and has no `--seed`.
  — [SchemaBasedFhirResourceFaker.cs](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/SchemaBasedFhirResourceFaker.cs), [ScenarioBuilder.cs](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/Scenarios/ScenarioBuilder.cs), [PopulationGenerator.cs](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/Population/PopulationGenerator.cs), [PopulationCommand.cs](https://github.com/brendankowitz/ignixa-fhir/blob/main/tools/Ignixa.FhirFakes.Cli/Commands/PopulationCommand.cs)
- The design doc itself flags the problem: "the core itself is not deterministic … scenario states spray unseeded `new Faker()` inline (`ObservationState.cs:292-362`). Full end-to-end reproducibility needs a `WithSeed` on the core" — [adversarial-data-generation.md](https://github.com/brendankowitz/ignixa-fhir/blob/main/docs/features/fhir-faker/investigations/adversarial-data-generation.md)
- Edge-case pipeline:
  - `EdgeCasePipeline(int seed, IFhirSchemaProvider)` owns its own `Randomizer`. "Same seed, same input resource, same ordered strategy set" gives the same output.
  - Each mutation produces a `MutationRecord(Category, Path, Before, After, Description)` in a `MutationManifest`.
  - `ValidityIntent` is one of {PreservesValidity, MayViolate, AlwaysInvalid}.
  - Families: `Unicode` (CJK, RTL, combining, emoji, zero-width, multi-script long), `Temporal` (leap year, far past, far future, partial precision, year boundary), `StringBoundary` (max-length, whitespace-only, control chars, empty-present, injection-like, free-text). `Cardinality` and `Structural` are "Not yet implemented".
  - `EdgeCaseCatalog.Register` accepts custom strategies.
  — [EdgeCaseFamily.cs](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/EdgeCases/EdgeCaseFamily.cs), [EdgeCaseCatalog.cs](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/EdgeCases/EdgeCaseCatalog.cs), [MutationRecord.cs](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/EdgeCases/MutationRecord.cs), [EdgeCasePipeline.cs](https://github.com/brendankowitz/ignixa-fhir/blob/main/src/Core/Ignixa.FhirFakes/EdgeCases/EdgeCasePipeline.cs)
- The design docs also cover: scenario generation, workflow context, theme-consistent generation, a state-id pattern, cross-version support and UK Core — [docs/features/fhir-faker](https://github.com/brendankowitz/ignixa-fhir/tree/main/docs/features/fhir-faker)

### Inferences
- **The best role for FhirFakes is a seeded scenario injector, not the bulk base.** Use `new ScenarioBuilder(schemaProvider, seed)` from C# to create a small number (about 5–15) of hand-designed "needle" patients whose facts are known by construction. Examples: a diabetic with medication escalation at a known date, or a pregnant patient. Merge them into the Synthea base. Avoid `PopulationGenerator` and the CLI `population`/`scenario` commands for pinned data, because they are unseeded.
- Even seeded paths may leak nondeterminism through inline `new Faker()` in states such as ObservationState. Regenerating is not enough: generate once, **commit the output files and a sha256**, and treat them as the pinned artifact.
- `IEdgeCaseStrategy` plus `MutationManifest` is a ready-made pattern for recording gold. The built-in families (unicode, temporal, string) test parsers and search robustness more than clinical reasoning, which makes them secondary for an agent-reasoning benchmark. Clinical strategies (status, unit, code and duplicate mutations) could be written as custom `IEdgeCaseStrategy` registrations and reuse the manifest.
- A mixed corpus is itself a realism dimension: Synthea patients plus FhirFakes patients means two code and identifier styles in one folder.

### Gaps
- I did not verify which code systems FhirFakes emits per scenario (for example, whether it uses SNOMED/LOINC/RxNorm consistently with Synthea), and I did not check its `--ndjson` reference format.
- I did not run the CLI or check whether `ScenarioBuilder(seed)` output is byte-stable across runs.
- The `brendankowitz/fhir-question-bench` public repo appears related (updated 2026-10-15). I did not inspect it.

## 4. PR #504 "cli-on-fhir": the tool surface under test

### Takeaway
PR #504 is OPEN and in progress. Today it implements a `dotnet tool` with CLI verbs `inspect`, `search`, `get` and `mcp`. The MCP stdio server exposes 3 tools: `folder_inspect`, `search_fhir_resources` (FHIR search with compartment, `_include`/`_revinclude`, chains, `_has`, `_sort`) and `read_resources`. FHIRPath, terminology, patient-context, validation and SQL-view tools are designed but not landed ("Tasks 5–12" remain).

### Cited Findings
- Purpose: "a `dotnet tool` that gives LLMs and agents a fast way to understand a folder of FHIR data … through a CLI and an MCP stdio server, with no HTTP endpoint." Still to land: "compartments and `urn:uuid` resolution, patient tools, terminology, validation, `sql_view`, de-identification and FML, `draft_resource` and the `asOf` clock, and evals" — [PR #504](https://github.com/brendankowitz/ignixa-fhir/pull/504)
- Implemented:
  - A folder catalog with byte-range random access into single-resource JSON, NDJSON lines and Bundle entries.
  - Lazily built type indexes, cached in the user cache directory and never written into the source folder.
  - A response envelope (`facts`, `evidence`, `omitted`, `issues`, `freshness`) with a 50 KB cap.
  - Strict unknown search parameters, with an opt-in `--lenient`.
  — [PR #504](https://github.com/brendankowitz/ignixa-fhir/pull/504)
- In-memory search fixes and opt-in features: same-entry matching for multi-field criteria; range-aware date, number and quantity bounds; accent folding; `|code` meaning no system; `_id`/`_type`; `_sort`; `_include`/`_revinclude` (with `:iterate`); forward chains; `_has`. Quantity also indexes `unit`, and `urn:` references are kept — [PR #504](https://github.com/brendankowitz/ignixa-fhir/pull/504)
- MCP tools in source on the PR branch:
  - `folder_inspect` takes `issueLimit`.
  - `search_fhir_resources` takes `resourceType`, `query`, `count` (default 10, max 50), `offset`, `summary=count`, `elements`, `lenient`, and `compartment` ('Patient/123', also Encounter, Practitioner, RelatedPerson, Device).
  - `read_resources` takes `references[]` and `elements`.
  — [Mcp/ResourceTools.cs](https://github.com/brendankowitz/ignixa-fhir/blob/claude/confident-turing-tph6h1/tools/Ignixa.CliOnFhir/Mcp/ResourceTools.cs), [Mcp/FolderTools.cs](https://github.com/brendankowitz/ignixa-fhir/blob/claude/confident-turing-tph6h1/tools/Ignixa.CliOnFhir/Mcp/FolderTools.cs)
- Planned surface:
  - Orient: `folder_inspect`, `patient_list`.
  - Navigate: `context_get_root`, `context_expand`, `context_find`, `context_read_leaves`.
  - Query: `search`, `plan_fhir_search`, `fhirpath_evaluate`, `sql_view`.
  - Terminology: `terminology_list`, `_lookup`, `_search`, `_validate_code`, `_subsumes`, `_navigate`, `_expand`, `_translate`.
  - Check and transform: `validate`, `deid`, `fml_transform`.
  - P1 = CLI including FHIRPath, search and core-package terminology. P2 = MCP parity, SNOMED/LOINC/RxNorm native loaders, `:below`/`:in` subsumption-aware search.
  - The usage log records tool, argument shape, size, `omitted` count and latency, and is evaluated against "a small fixed question set per folder (lookup, explain, filter/count, absence, terminology)".
  — [agent-tool-surface.md (PR branch)](https://github.com/brendankowitz/ignixa-fhir/blob/claude/confident-turing-tph6h1/docs/features/local-cli/investigations/agent-tool-surface.md)
- `folder_inspect` is designed to report "diagnostics (parse errors, duplicate ids, broken references, resources without ids)". The P1 terminology scope excludes SNOMED, LOINC and RxNorm content: core packages have `content=not-present` stubs, and the tool must report "system not loaded" — [agent-tool-surface.md](https://github.com/brendankowitz/ignixa-fhir/blob/claude/confident-turing-tph6h1/docs/features/local-cli/investigations/agent-tool-surface.md)

### Inferences
- The benchmark should pin a specific PR #504 commit or tool version per run, because the tool surface is changing fast.
- Current tooling helps most with filter/count (`summary=count`), compartment scoping, `_include`, and quantity-with-unit search. Synthea's corpus exercises these heavily: 46k Observations, conditional refs, urn:uuid.
- Terminology tools in P1 cannot answer SNOMED or RxNorm hierarchy questions on Synthea data, because Synthea is SNOMED/LOINC/RxNorm-only. Subsumption questions ("any diabetes") will test whether the tool honestly reports "system not loaded" rather than whether it helps. Design the gold so that honest absence is scored correctly.
- Injected duplicates and broken references line up directly with `folder_inspect` diagnostics, which makes them good "does the tool help" probes.

### Gaps
- I did not verify whether `urn:uuid` and conditional-reference resolution work on the branch today. The PR body lists them as remaining, but `Indexing/BundleReferenceResolver.cs` exists.

## 5. Edge-case injection on top of Synthea with deterministic gold

### Takeaway
Build the corpus as follows. Start from the pinned Synthea base (sha-locked). Add a deterministic, seeded post-processor that applies a declared list of clinical mutations to selected resource IDs and writes a mutation manifest (resource ref, path, before, after, category). Optionally add a few seeded FhirFakes "needle" patients. Compute gold from the final mutated files with a reference evaluator, not from the generator's intent.

### Cited Findings
- Synthea's native levers:
  - `exporter.split_records=true` (+ `.duplicate_data`) gives per-organization record splits.
  - Flexporter `set_values`, `create_resource`, `delete_resources` and `execute_script` do FHIRPath-targeted mutation. `delete_resources` can leave dangling refs, and `randomCode` and date transforms add unseeded randomness.
  — [synthea.properties](https://github.com/synthetichealth/synthea/blob/master/src/main/resources/synthea.properties), [Flexporter wiki](https://github.com/synthetichealth/synthea/wiki/Flexporter)
- FhirFakes' model to copy: a seeded decorator pass that is reproducible from `(seed, resourceIndex)`, with "each applied mutation … recorded (category + JSON path + before/after) so the corpus is self-describing", plus a validate-and-bucket step (valid-hostile, invalid, round-trip-broken) — [adversarial-data-generation.md](https://github.com/brendankowitz/ignixa-fhir/blob/main/docs/features/fhir-faker/investigations/adversarial-data-generation.md)
- Measured baseline gaps to fill (local scan): 0 non-final Observations; 0 dataAbsentReason; 0 duplicate ids; all Conditions `confirmed`/`encounter-diagnosis`; 5 codes with more than one unit; no ICD-10 or local codes.

### Inferences
Suggested mutation catalog. Each item uses a fixed seed and an explicit target selection, sorted by `(patient, type, id)` before sampling.
1. **Status:** set a sampled set of Observations to `entered-in-error`, `preliminary`, `amended` or `corrected`; mark a MedicationRequest `stopped` or `entered-in-error`; set a Condition's verificationStatus to `refuted`, `entered-in-error` or `provisional`; add a `problem-list-item` category. Gold must exclude entered-in-error items in "latest value" questions.
2. **Units:** convert glucose 2339-0 or cholesterol 2093-3 from mg/dL to mmol/L (glucose ÷18.016, cholesterol ÷38.67), with the value rescaled and the UCUM code changed. Optionally add a non-UCUM `unit` text only.
3. **Codes:** replace the LOINC coding with a local `http://example.org/lab-codes` code and a display only; add ICD-10-CM alongside SNOMED on a Condition; make one code text-only (`code.text` with no coding).
4. **Missing data:** remove `valueQuantity` and add `dataAbsentReason`; drop `effectiveDateTime` precision to year-month.
5. **Duplicates:** copy an Observation with a new id and identical content (exact duplicate), or with a slightly shifted time (near-duplicate); add a second Patient record for the same person with a different id and the same identifier (MPI-style).
6. **References:** break one reference (dangling), or convert some `urn:uuid` refs to relative `Type/id` after assigning server ids.
7. **Conflicts:** add an AllergyIntolerance contradicted by a later MedicationRequest of the same drug class.

Determinism and gold:
- Implement the mutator as a pure function `(input files sha, mutation spec JSON, seed) → output files + manifest`.
- Store the spec, seed and output sha in the repo.
- Compute gold by running a reference evaluator (FHIRPath or SQL-on-FHIR queries) over the *final* files. Record which manifest entries each question depends on, so ablations can toggle a mutation and recompute gold.

Scale and size:
- 109 Synthea patients plus about 10 FhirFakes needle patients plus mutations comes to about 120k resources and about 390 MB. That is already well past any context window.
- To keep runs fast, consider `exporter.fhir.excluded_resources=Claim,ExplanationOfBenefit` in a custom run (about −19k resources), or keep them as distractors.
- Write the mutated output in both transaction-bundle and NDJSON layouts only if the benchmark varies the input format. Otherwise pick one, to avoid gold drift.

### Gaps
- I found no published prior work that specifically benchmarks LLM agents on Synthea with injected status or unit edge cases. A targeted search was not done because it is outside this assignment's scope.
- I did not empirically verify the realism of the unit-conversion factors against the Synthea value ranges.
