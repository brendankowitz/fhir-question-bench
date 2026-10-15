# FHIR Agent Bench Phase 0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the end-to-end measurement loop in C#, then prove it with a first real Copilot CLI run. The loop is: pinned Synthea corpus → 10 clinician-voiced questions → independent oracles → harness run → deterministic grading → paired report. Profiles: P1 (shell) and P2 (shell + `cli-on-fhir` MCP).

**Architecture:**
- **Projects.** One library (`FhirAgentBench`), one CLI (`fab`), one xUnit test project.
- **Oracles.** They read raw FHIR JSON through a bench-owned `CorpusIndex` (`System.Text.Json` only, no Ignixa), so gold cannot share bugs with the tool under test.
- **Harnesses.** They implement `IAgentHarness`. Every harness gets a prompt and must leave `answer.json` in its workspace, which keeps the design harness-agnostic. The Copilot CLI is the first real harness; the `oracle` and `abstain` harnesses are cost-free baselines that validate the pipeline.

**Tech Stack:** .NET 10, C#, xUnit 2.9, YamlDotNet 16, System.CommandLine 2.0, GitHub Copilot CLI 1.0.96+, `cli-on-fhir` from brendankowitz/ignixa-fhir#504.

**Spec:** `docs/superpowers/specs/2026-10-15-fhir-agent-bench-platform-design.md` (revision 2). Phase 0 row of §10.

## Global Constraints

- Target framework `net10.0`. SDK pinned by `global.json` to `10.0.113` with `rollForward: latestFeature`.
- `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, central package management (`Directory.Packages.props`).
- `src/FhirAgentBench` must not reference any `Ignixa.*` package. Oracles and grading stay independent of the tool under test.
- Gold answers are never written into an *agent* attempt directory. The only exception is the B0 oracle harness, which is not an agent.
- The Copilot token is only ever passed through the child process environment, and is redacted with `--secret-env-vars`. It is never written to disk or logs.
- Never pass `--allow-all`, `--yolo`, `--allow-all-paths` or `--allow-all-urls` to Copilot.
- Question text never contains a resource id or an invented code system.
- Repository coding standards (`~/.claude/CLAUDE.md`):
  - `sealed` classes, `_camelCase` private fields, `Async` suffix.
  - `CancellationToken` as the last parameter, defaulting to `default`.
  - `ConfigureAwait(false)` in library code.
  - No comments unless the *why* is non-obvious.
- Models available in Copilot CLI 1.0.96: model A = `claude-sonnet-5.5`, model B = `claude-haiku-5.5`.

## Review Focus

1. **Corpus not fetched.** `CorpusIndex.Load` on a missing directory must fail with a message naming `fab corpus fetch`, not a raw IO exception. Test in Task 1.
2. **Imperfect `answer.json` from a real agent.** A UTF-8 BOM must still parse. A number sent as a string (`"9.4"`) must grade `format_invalid`, not crash and not count as correct. Tests in Tasks 2 and 7.
3. **Profile names an environment variable that isn't set** (e.g. `${FAB_CLI_ON_FHIR}`). The run must stop before the first attempt, with a message naming the variable. Test in Task 9.
4. **Agent edits or deletes a corpus file.** The run must stop and say which file changed, rather than grading later attempts against a tampered corpus. Test in Task 6.
5. **Agent hangs.** The timeout must kill the whole process tree, including MCP server children, and the attempt grades `turn_limit`. Test in Task 9.

## File Structure

```
global.json, Directory.Build.props, Directory.Packages.props, FhirAgentBench.slnx
src/FhirAgentBench/
  Corpus/      CorpusIndex.cs, FhirTime.cs, CorpusDefinition.cs, CorpusManifest.cs,
               CorpusFetcher.cs, CorpusGuard.cs
  Answers/     Answer.cs, AnswerSpec.cs, AnswerJson.cs, AnswerShape.cs, Grader.cs,
               AnswerSpecDescriber.cs
  Questions/   Question.cs, QuestionLoader.cs, Yaml.cs
  Oracles/     IOracle.cs, OracleContext.cs, OracleParameters.cs, OracleRegistry.cs,
               FhirElements.cs, Observations.cs, Medications.cs, LatestObservationOracle.cs,
               ObservationWindowStatsOracle.cs, MedicationsStartedWithinOracle.cs,
               EverOnMedicationOracle.cs, CohortLabAboveOrMissingOracle.cs, UnanswerableOracle.cs
  Authoring/   GoldAdmitter.cs, GoldLock.cs, CorpusQueries.cs
  Execution/   Profile.cs, ProfileLoader.cs, IAgentHarness.cs, PromptRenderer.cs,
               BaselineHarnesses.cs, RunOrchestrator.cs, RunGrader.cs, JsonOptions.cs,
               ProcessRunner.cs, McpConfig.cs, CopilotArguments.cs, CopilotCliHarness.cs,
               HarnessFactory.cs, RunSnapshot.cs
  Reporting/   McNemar.cs, RunReport.cs
src/FhirAgentBench.Cli/   Program.cs, CorpusCommands.cs, AdmitCommand.cs, RunCommand.cs, ReportCommand.cs
tests/FhirAgentBench.Tests/
  Fixture.cs, Fixtures/corpus/data/{patients.json, practitioners.json, extra.ndjson}
  *Tests.cs (one per area)
corpora/synthea-sample-v1/corpus.yaml, data.manifest.json   (data/ is gitignored)
questions/*.yaml, questions/gold.lock.json, question-sets/phase0.txt
profiles/{b0-oracle,b1-abstain,p1-shell,p2-shell-cli-on-fhir}.yaml, prompts/default.md
```

---

### Task 1: Solution scaffold and corpus index

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `FhirAgentBench.slnx`
- Create: `src/FhirAgentBench/FhirAgentBench.csproj`, `src/FhirAgentBench/Corpus/FhirTime.cs`, `src/FhirAgentBench/Corpus/CorpusIndex.cs`
- Create: `tests/FhirAgentBench.Tests/FhirAgentBench.Tests.csproj`, `tests/FhirAgentBench.Tests/Fixture.cs`, `tests/FhirAgentBench.Tests/Fixtures/corpus/data/patients.json`, `.../practitioners.json`, `.../extra.ndjson`
- Test: `tests/FhirAgentBench.Tests/CorpusIndexTests.cs`
- Modify: `.gitignore`

**Interfaces:**
- Produces:
  - `FhirTime.Parse(string) → DateTimeOffset`
  - `FhirTime.Effective(JsonElement) → DateTimeOffset?`
  - `FhirTime.ToDate(DateTimeOffset) → string`
  - `FhirTime.AgeInYears(string birthDate, DateTimeOffset now) → int`
  - `CorpusIndex.Load(string directory)`, `.Count`, `.OfType(string type)`
  - `CorpusIndex.Resolve(string reference) → JsonElement?`, `.ResolveKey(string) → string?`, `.ResolveFrom(JsonElement container, string reference) → JsonElement?`
  - `CorpusIndex.FindByIdentifier(string type, string system, string value) → JsonElement?`
  - `CorpusIndex.ForPatient(string patientKey, string type) → IReadOnlyList<JsonElement>`
  - `CorpusIndex.KeyOf(JsonElement) → string`
  - Test helper `Fixture.Index`, `Fixture.Now` (2026-01-15T00:00:00Z), `Fixture.MrnSystem`

- [ ] **Step 1: Create the solution skeleton**

`global.json`:
```json
{ "sdk": { "version": "10.0.113", "rollForward": "latestFeature" } }
```

`Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

`Directory.Packages.props`:
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="YamlDotNet" Version="16.3.0" />
    <PackageVersion Include="System.CommandLine" Version="2.0.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
</Project>
```
If `dotnet restore` reports that a version doesn't exist, run `dotnet package search <id> --exact-match` and use the newest stable version.

`src/FhirAgentBench/FhirAgentBench.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="YamlDotNet" />
    <InternalsVisibleTo Include="FhirAgentBench.Tests" />
  </ItemGroup>
</Project>
```

`tests/FhirAgentBench.Tests/FhirAgentBench.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <ProjectReference Include="..\..\src\FhirAgentBench\FhirAgentBench.csproj" />
    <None Include="Fixtures\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
</Project>
```

Run:
```bash
dotnet new sln --format slnx -n FhirAgentBench
dotnet sln FhirAgentBench.slnx add src/FhirAgentBench/FhirAgentBench.csproj tests/FhirAgentBench.Tests/FhirAgentBench.Tests.csproj
```

Append to `.gitignore`:
```
bin/
obj/
.vs/
corpora/*/data/
runs/
```

- [ ] **Step 2: Add the fixture corpus**

`tests/FhirAgentBench.Tests/Fixtures/corpus/data/patients.json`. The fixture has four patients:
- **Ada:** diabetic, with A1c results that include one entered in error and one after "now"; glucose results; a statin ordered via `medicationReference`.
- **Bryn:** diabetic with no A1c, plus a medication on a contained reference.
- **Cedar:** diabetes resolved.
- **Dell:** a child.

```json
{
  "resourceType": "Bundle",
  "type": "transaction",
  "entry": [
    { "fullUrl": "urn:uuid:p-ada", "resource": { "resourceType": "Patient", "id": "ada", "identifier": [ { "system": "http://hospital.smarthealthit.org", "value": "MRN-ADA" } ], "name": [ { "given": [ "Ada" ], "family": "Lovelace" } ], "gender": "female", "birthDate": "1960-03-01" } },
    { "fullUrl": "urn:uuid:p-bryn", "resource": { "resourceType": "Patient", "id": "bryn", "identifier": [ { "system": "http://hospital.smarthealthit.org", "value": "MRN-BRYN" } ], "name": [ { "given": [ "Bryn" ], "family": "Jones" } ], "gender": "male", "birthDate": "1970-05-05" } },
    { "fullUrl": "urn:uuid:p-cedar", "resource": { "resourceType": "Patient", "id": "cedar", "identifier": [ { "system": "http://hospital.smarthealthit.org", "value": "MRN-CEDAR" } ], "name": [ { "given": [ "Cedar" ], "family": "Hill" } ], "gender": "female", "birthDate": "1950-07-07" } },
    { "fullUrl": "urn:uuid:p-dell", "resource": { "resourceType": "Patient", "id": "dell", "identifier": [ { "system": "http://hospital.smarthealthit.org", "value": "MRN-DELL" } ], "name": [ { "given": [ "Dell" ], "family": "Park" } ], "gender": "male", "birthDate": "2015-09-09" } },
    { "fullUrl": "urn:uuid:c-ada-dm", "resource": { "resourceType": "Condition", "id": "c-ada-dm", "code": { "coding": [ { "system": "http://snomed.info/sct", "code": "44054006" } ] }, "subject": { "reference": "urn:uuid:p-ada" }, "onsetDateTime": "2015-01-01T00:00:00+00:00" } },
    { "fullUrl": "urn:uuid:c-bryn-dm", "resource": { "resourceType": "Condition", "id": "c-bryn-dm", "code": { "coding": [ { "system": "http://snomed.info/sct", "code": "44054006" } ] }, "subject": { "reference": "urn:uuid:p-bryn" }, "onsetDateTime": "2020-01-01T00:00:00+00:00" } },
    { "fullUrl": "urn:uuid:c-cedar-dm", "resource": { "resourceType": "Condition", "id": "c-cedar-dm", "code": { "coding": [ { "system": "http://snomed.info/sct", "code": "44054006" } ] }, "subject": { "reference": "urn:uuid:p-cedar" }, "onsetDateTime": "2010-01-01T00:00:00+00:00", "abatementDateTime": "2019-01-01T00:00:00+00:00" } },
    { "fullUrl": "urn:uuid:c-dell-dm", "resource": { "resourceType": "Condition", "id": "c-dell-dm", "code": { "coding": [ { "system": "http://snomed.info/sct", "code": "44054006" } ] }, "subject": { "reference": "urn:uuid:p-dell" }, "onsetDateTime": "2024-01-01T00:00:00+00:00" } },
    { "fullUrl": "urn:uuid:o-ada-a1c-1", "resource": { "resourceType": "Observation", "id": "o-ada-a1c-1", "status": "final", "code": { "coding": [ { "system": "http://loinc.org", "code": "4548-4" } ] }, "subject": { "reference": "urn:uuid:p-ada" }, "effectiveDateTime": "2025-06-01T09:00:00+00:00", "valueQuantity": { "value": 8.1, "unit": "%", "system": "http://unitsofmeasure.org", "code": "%" } } },
    { "fullUrl": "urn:uuid:o-ada-a1c-2", "resource": { "resourceType": "Observation", "id": "o-ada-a1c-2", "status": "final", "code": { "coding": [ { "system": "http://loinc.org", "code": "4548-4" } ] }, "subject": { "reference": "urn:uuid:p-ada" }, "effectiveDateTime": "2025-12-01T09:00:00+00:00", "valueQuantity": { "value": 9.4, "unit": "%", "system": "http://unitsofmeasure.org", "code": "%" } } },
    { "fullUrl": "urn:uuid:o-ada-a1c-3", "resource": { "resourceType": "Observation", "id": "o-ada-a1c-3", "status": "entered-in-error", "code": { "coding": [ { "system": "http://loinc.org", "code": "4548-4" } ] }, "subject": { "reference": "urn:uuid:p-ada" }, "effectiveDateTime": "2026-01-10T09:00:00+00:00", "valueQuantity": { "value": 7.0, "unit": "%", "system": "http://unitsofmeasure.org", "code": "%" } } },
    { "fullUrl": "urn:uuid:o-ada-a1c-4", "resource": { "resourceType": "Observation", "id": "o-ada-a1c-4", "status": "final", "code": { "coding": [ { "system": "http://loinc.org", "code": "4548-4" } ] }, "subject": { "reference": "urn:uuid:p-ada" }, "effectiveDateTime": "2026-02-01T09:00:00+00:00", "valueQuantity": { "value": 6.0, "unit": "%", "system": "http://unitsofmeasure.org", "code": "%" } } },
    { "fullUrl": "urn:uuid:o-ada-glu-1", "resource": { "resourceType": "Observation", "id": "o-ada-glu-1", "status": "final", "code": { "coding": [ { "system": "http://loinc.org", "code": "2339-0" } ] }, "subject": { "reference": "urn:uuid:p-ada" }, "effectiveDateTime": "2026-01-14T08:00:00+00:00", "valueQuantity": { "value": 120, "unit": "mg/dL", "system": "http://unitsofmeasure.org", "code": "mg/dL" } } },
    { "fullUrl": "urn:uuid:o-ada-glu-2", "resource": { "resourceType": "Observation", "id": "o-ada-glu-2", "status": "final", "code": { "coding": [ { "system": "http://loinc.org", "code": "2339-0" } ] }, "subject": { "reference": "urn:uuid:p-ada" }, "effectiveDateTime": "2026-01-14T20:00:00+00:00", "valueQuantity": { "value": 180, "unit": "mg/dL", "system": "http://unitsofmeasure.org", "code": "mg/dL" } } },
    { "fullUrl": "urn:uuid:o-ada-glu-3", "resource": { "resourceType": "Observation", "id": "o-ada-glu-3", "status": "final", "code": { "coding": [ { "system": "http://loinc.org", "code": "2339-0" } ] }, "subject": { "reference": "urn:uuid:p-ada" }, "effectiveDateTime": "2025-12-01T08:00:00+00:00", "valueQuantity": { "value": 300, "unit": "mg/dL", "system": "http://unitsofmeasure.org", "code": "mg/dL" } } },
    { "fullUrl": "urn:uuid:o-cedar-a1c-1", "resource": { "resourceType": "Observation", "id": "o-cedar-a1c-1", "status": "final", "code": { "coding": [ { "system": "http://loinc.org", "code": "4548-4" } ] }, "subject": { "reference": "urn:uuid:p-cedar" }, "effectiveDateTime": "2025-10-01T09:00:00+00:00", "valueQuantity": { "value": 10.0, "unit": "%", "system": "http://unitsofmeasure.org", "code": "%" } } },
    { "fullUrl": "urn:uuid:o-dell-a1c-1", "resource": { "resourceType": "Observation", "id": "o-dell-a1c-1", "status": "final", "code": { "coding": [ { "system": "http://loinc.org", "code": "4548-4" } ] }, "subject": { "reference": "urn:uuid:p-dell" }, "effectiveDateTime": "2025-09-01T09:00:00+00:00", "valueQuantity": { "value": 9.9, "unit": "%", "system": "http://unitsofmeasure.org", "code": "%" } } },
    { "fullUrl": "urn:uuid:med-atorva", "resource": { "resourceType": "Medication", "id": "med-atorva", "code": { "coding": [ { "system": "http://www.nlm.nih.gov/research/umls/rxnorm", "code": "259255", "display": "atorvastatin 80 MG Oral Tablet" } ] } } },
    { "fullUrl": "urn:uuid:mr-ada-metformin", "resource": { "resourceType": "MedicationRequest", "id": "mr-ada-metformin", "status": "active", "intent": "order", "medicationCodeableConcept": { "coding": [ { "system": "http://www.nlm.nih.gov/research/umls/rxnorm", "code": "860975", "display": "24 HR metformin hydrochloride 500 MG Extended Release Oral Tablet" } ] }, "subject": { "reference": "urn:uuid:p-ada" }, "authoredOn": "2015-02-01T00:00:00+00:00", "requester": { "reference": "Practitioner?identifier=http://hl7.org/fhir/sid/us-npi|9999999999" } } },
    { "fullUrl": "urn:uuid:mr-ada-atorva", "resource": { "resourceType": "MedicationRequest", "id": "mr-ada-atorva", "status": "active", "intent": "order", "medicationReference": { "reference": "urn:uuid:med-atorva" }, "subject": { "reference": "urn:uuid:p-ada" }, "authoredOn": "2025-11-20T00:00:00+00:00" } },
    { "fullUrl": "urn:uuid:mr-bryn-amlo", "resource": { "resourceType": "MedicationRequest", "id": "mr-bryn-amlo", "status": "active", "intent": "order", "contained": [ { "resourceType": "Medication", "id": "m1", "code": { "coding": [ { "system": "http://www.nlm.nih.gov/research/umls/rxnorm", "code": "197361", "display": "amlodipine 5 MG Oral Tablet" } ] } } ], "medicationReference": { "reference": "#m1" }, "subject": { "reference": "urn:uuid:p-bryn" }, "authoredOn": "2025-12-20T00:00:00+00:00" } }
  ]
}
```

`tests/FhirAgentBench.Tests/Fixtures/corpus/data/practitioners.json`:
```json
{
  "resourceType": "Bundle",
  "type": "batch",
  "entry": [
    { "fullUrl": "urn:uuid:pr-1", "resource": { "resourceType": "Practitioner", "id": "pr-1", "identifier": [ { "system": "http://hl7.org/fhir/sid/us-npi", "value": "9999999999" } ], "name": [ { "family": "Willms", "given": [ "Gregg" ] } ] } }
  ]
}
```

`tests/FhirAgentBench.Tests/Fixtures/corpus/data/extra.ndjson` (one line):
```
{"resourceType":"Observation","id":"o-bryn-k-1","status":"final","code":{"coding":[{"system":"http://loinc.org","code":"6298-4"}]},"subject":{"reference":"Patient/bryn"},"effectiveDateTime":"2026-01-12T10:00:00+00:00","valueQuantity":{"value":4.2,"unit":"mmol/L","system":"http://unitsofmeasure.org","code":"mmol/L"}}
```

`tests/FhirAgentBench.Tests/Fixture.cs`:
```csharp
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Tests;

internal static class Fixture
{
    private static readonly Lazy<CorpusIndex> LazyIndex = new(() => CorpusIndex.Load(CorpusDirectory));

    public const string MrnSystem = "http://hospital.smarthealthit.org";

    public static string CorpusDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "corpus", "data");

    public static DateTimeOffset Now { get; } = new(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);

    public static CorpusIndex Index => LazyIndex.Value;
}
```

- [ ] **Step 3: Write the failing tests**

`tests/FhirAgentBench.Tests/CorpusIndexTests.cs`:
```csharp
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Tests;

public sealed class CorpusIndexTests
{
    [Fact]
    public void Load_IndexesBundleEntriesAndNdjsonLines()
    {
        Assert.Equal(23, Fixture.Index.Count);
        Assert.Equal(4, Fixture.Index.OfType("Patient").Count);
    }

    [Fact]
    public void Resolve_FollowsUrnUuidFullUrl()
    {
        Assert.Equal("Patient/ada", Fixture.Index.ResolveKey("urn:uuid:p-ada"));
    }

    [Fact]
    public void Resolve_FollowsConditionalIdentifierReference()
    {
        Assert.Equal("Practitioner/pr-1",
            Fixture.Index.ResolveKey("Practitioner?identifier=http://hl7.org/fhir/sid/us-npi|9999999999"));
    }

    [Fact]
    public void Resolve_ReturnsNullForUnknownReference()
    {
        Assert.Null(Fixture.Index.Resolve("Patient/nobody"));
    }

    [Fact]
    public void ResolveFrom_FindsContainedResource()
    {
        var request = Fixture.Index.Resolve("MedicationRequest/mr-bryn-amlo")!.Value;

        var medication = Fixture.Index.ResolveFrom(request, "#m1");

        Assert.Equal("m1", medication!.Value.GetProperty("id").GetString());
    }

    [Fact]
    public void ForPatient_IncludesRelativeTypeIdReferencesFromNdjson()
    {
        var observations = Fixture.Index.ForPatient("Patient/bryn", "Observation");

        Assert.Single(observations);
    }

    [Fact]
    public void FindByIdentifier_FindsPatientByMrn()
    {
        var patient = Fixture.Index.FindByIdentifier("Patient", Fixture.MrnSystem, "MRN-CEDAR");

        Assert.Equal("Patient/cedar", CorpusIndex.KeyOf(patient!.Value));
    }

    [Fact]
    public void Load_MissingDirectory_ExplainsHowToFetch()
    {
        var ex = Assert.Throws<DirectoryNotFoundException>(() => CorpusIndex.Load(Path.Combine(Path.GetTempPath(), "fab-missing-corpus")));

        Assert.Contains("fab corpus fetch", ex.Message);
    }

    [Theory]
    [InlineData("2015", "2015-01-01")]
    [InlineData("2015-02", "2015-02-01")]
    [InlineData("2015-02-03", "2015-02-03")]
    [InlineData("2015-02-03T23:30:00+00:00", "2015-02-03")]
    public void FhirTime_ParsesPartialDates(string value, string expectedDate)
    {
        Assert.Equal(expectedDate, FhirTime.ToDate(FhirTime.Parse(value)));
    }

    [Fact]
    public void FhirTime_AgeInYears_CountsBirthdayNotYetReached()
    {
        Assert.Equal(65, FhirTime.AgeInYears("1960-03-01", Fixture.Now));
        Assert.Equal(10, FhirTime.AgeInYears("2015-09-09", Fixture.Now));
    }
}
```
The expected count is 23: 4 Patient, 4 Condition, 10 Observation (9 in the bundle and 1 in NDJSON), 1 Medication, 3 MedicationRequest and 1 Practitioner. The contained Medication is not a top-level resource, so it is not counted.

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test`
Expected: build FAIL, `The type or namespace name 'CorpusIndex' could not be found`.

- [ ] **Step 5: Implement `FhirTime`**

`src/FhirAgentBench/Corpus/FhirTime.cs`:
```csharp
using System.Globalization;
using System.Text.Json;

namespace FhirAgentBench.Corpus;

public static class FhirTime
{
    private static readonly string[] InstantProperties =
        ["effectiveDateTime", "authoredOn", "onsetDateTime", "recordedDate", "occurrenceDateTime", "issued"];

    private static readonly string[] PeriodProperties = ["effectivePeriod", "period"];

    public static DateTimeOffset Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Length switch
        {
            4 => new DateTimeOffset(int.Parse(value, CultureInfo.InvariantCulture), 1, 1, 0, 0, 0, TimeSpan.Zero),
            7 => new DateTimeOffset(
                int.Parse(value[..4], CultureInfo.InvariantCulture),
                int.Parse(value[5..7], CultureInfo.InvariantCulture), 1, 0, 0, 0, TimeSpan.Zero),
            10 => new DateTimeOffset(DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture), TimeSpan.Zero),
            _ => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
        };
    }

    public static DateTimeOffset? Effective(JsonElement resource)
    {
        foreach (var name in InstantProperties)
        {
            if (resource.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return Parse(value.GetString()!);
            }
        }

        foreach (var name in PeriodProperties)
        {
            if (resource.TryGetProperty(name, out var period)
                && period.TryGetProperty("start", out var start)
                && start.ValueKind == JsonValueKind.String)
            {
                return Parse(start.GetString()!);
            }
        }

        return null;
    }

    public static string ToDate(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static int AgeInYears(string birthDate, DateTimeOffset now)
    {
        var birth = Parse(birthDate).UtcDateTime.Date;
        var today = now.UtcDateTime.Date;
        var age = today.Year - birth.Year;
        return today < birth.AddYears(age) ? age - 1 : age;
    }
}
```

- [ ] **Step 6: Implement `CorpusIndex`**

`src/FhirAgentBench/Corpus/CorpusIndex.cs`:
```csharp
using System.Text.Json;

namespace FhirAgentBench.Corpus;

public sealed class CorpusIndex
{
    private const string IdentifierQuery = "?identifier=";
    private static readonly string[] PatientReferenceProperties = ["subject", "patient"];

    private readonly Dictionary<string, JsonElement> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fullUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _identifiers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<JsonElement>> _byType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<JsonElement>> _byPatientAndType = new(StringComparer.Ordinal);

    private CorpusIndex()
    {
    }

    public int Count => _byKey.Count;

    public static CorpusIndex Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Corpus data directory '{directory}' does not exist. Run 'fab corpus fetch <corpus-dir>' first.");
        }

        var index = new CorpusIndex();
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            index.AddFile(file);
        }

        index.IndexPatients();
        return index;
    }

    public static string KeyOf(JsonElement resource) =>
        $"{resource.GetProperty("resourceType").GetString()}/{resource.GetProperty("id").GetString()}";

    public IReadOnlyList<JsonElement> OfType(string type) =>
        _byType.TryGetValue(type, out var list) ? list : [];

    public IReadOnlyList<JsonElement> ForPatient(string patientKey, string type) =>
        _byPatientAndType.TryGetValue($"{patientKey}|{type}", out var list) ? list : [];

    public JsonElement? Resolve(string reference) =>
        ResolveKey(reference) is { } key && _byKey.TryGetValue(key, out var resource) ? resource : null;

    public JsonElement? ResolveFrom(JsonElement container, string reference)
    {
        if (reference.StartsWith('#') && container.TryGetProperty("contained", out var contained))
        {
            foreach (var candidate in contained.EnumerateArray())
            {
                if (candidate.TryGetProperty("id", out var id) && id.GetString() == reference[1..])
                {
                    return candidate;
                }
            }

            return null;
        }

        return Resolve(reference);
    }

    public string? ResolveKey(string reference)
    {
        if (_fullUrls.TryGetValue(reference, out var fromFullUrl))
        {
            return fromFullUrl;
        }

        var query = reference.IndexOf(IdentifierQuery, StringComparison.Ordinal);
        if (query > 0)
        {
            var token = reference[(query + IdentifierQuery.Length)..];
            var bar = token.IndexOf('|');
            return bar >= 0 && _identifiers.TryGetValue($"{reference[..query]}|{token[..bar]}|{token[(bar + 1)..]}", out var byIdentifier)
                ? byIdentifier
                : null;
        }

        return _byKey.ContainsKey(reference) ? reference : null;
    }

    public JsonElement? FindByIdentifier(string type, string system, string value) =>
        _identifiers.TryGetValue($"{type}|{system}|{value}", out var key) ? _byKey[key] : null;

    private void AddFile(string path)
    {
        if (path.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                using var lineDocument = JsonDocument.Parse(line);
                AddResource(lineDocument.RootElement, fullUrl: null, path);
            }

            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.GetProperty("resourceType").GetString() != "Bundle")
        {
            AddResource(root, fullUrl: null, path);
            return;
        }

        foreach (var entry in root.GetProperty("entry").EnumerateArray())
        {
            var fullUrl = entry.TryGetProperty("fullUrl", out var url) ? url.GetString() : null;
            AddResource(entry.GetProperty("resource"), fullUrl, path);
        }
    }

    private void AddResource(JsonElement source, string? fullUrl, string path)
    {
        var resource = source.Clone();
        if (!resource.TryGetProperty("id", out _))
        {
            throw new InvalidDataException($"A resource in '{path}' has no id.");
        }

        var key = KeyOf(resource);
        if (!_byKey.TryAdd(key, resource))
        {
            throw new InvalidDataException($"Resource '{key}' appears more than once (second copy in '{path}').");
        }

        var type = resource.GetProperty("resourceType").GetString()!;
        if (!_byType.TryGetValue(type, out var list))
        {
            _byType[type] = list = [];
        }

        list.Add(resource);

        if (fullUrl is not null)
        {
            _fullUrls.TryAdd(fullUrl, key);
        }

        if (resource.TryGetProperty("identifier", out var identifiers) && identifiers.ValueKind == JsonValueKind.Array)
        {
            foreach (var identifier in identifiers.EnumerateArray())
            {
                if (identifier.TryGetProperty("system", out var system) && identifier.TryGetProperty("value", out var value))
                {
                    _identifiers.TryAdd($"{type}|{system.GetString()}|{value.GetString()}", key);
                }
            }
        }
    }

    private void IndexPatients()
    {
        foreach (var resource in _byKey.Values)
        {
            var patientKey = PatientKeyOf(resource);
            if (patientKey is null)
            {
                continue;
            }

            var bucket = $"{patientKey}|{resource.GetProperty("resourceType").GetString()}";
            if (!_byPatientAndType.TryGetValue(bucket, out var list))
            {
                _byPatientAndType[bucket] = list = [];
            }

            list.Add(resource);
        }
    }

    private string? PatientKeyOf(JsonElement resource)
    {
        foreach (var name in PatientReferenceProperties)
        {
            if (resource.TryGetProperty(name, out var reference)
                && reference.ValueKind == JsonValueKind.Object
                && reference.TryGetProperty("reference", out var value)
                && ResolveKey(value.GetString()!) is { } key
                && key.StartsWith("Patient/", StringComparison.Ordinal))
            {
                return key;
            }
        }

        return null;
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS (all `CorpusIndexTests`).

- [ ] **Step 8: Commit**

```bash
git add global.json Directory.Build.props Directory.Packages.props FhirAgentBench.slnx .gitignore src/FhirAgentBench tests/FhirAgentBench.Tests
git commit -m "feat: add C# solution and bench-owned FHIR corpus index"
```

---

### Task 2: Typed answers and deterministic grading

**Files:**
- Create: `src/FhirAgentBench/Answers/Answer.cs`, `AnswerSpec.cs`, `AnswerJson.cs`, `AnswerShape.cs`, `Grader.cs`, `AnswerSpecDescriber.cs`
- Test: `tests/FhirAgentBench.Tests/GraderTests.cs`

**Interfaces:**
- Produces:
  - `enum AnswerStatus { Answered, InsufficientData }`
  - `record Answer(AnswerStatus Status, JsonElement? Value, IReadOnlyList<string> Evidence)` with `Answer.Of(object? value, IEnumerable<string> evidence)` and `Answer.Insufficient()`
  - `enum AnswerType { Integer, Decimal, Quantity, Date, Code, Identifier, Set, Boolean, Composite }`
  - `record AnswerSpec { AnswerType Type; string? Unit; decimal Tolerance; bool Nullable; IReadOnlyDictionary<string, AnswerSpec> Fields }`
  - `enum GradeStatus { Correct, Wrong, Abstained, FormatInvalid, TurnLimit, HarnessError }`
  - `Grader.Grade(AnswerSpec spec, Answer gold, Answer agent) → GradeStatus`
  - `AnswerJson.Parse(string json) → Answer` (throws `FormatException`) and `AnswerJson.Serialize(Answer) → string`
  - `AnswerShape.Conforms(AnswerSpec, JsonElement?) → bool`
  - `AnswerSpecDescriber.Describe(AnswerSpec) → string`

- [ ] **Step 1: Write the failing tests**

`tests/FhirAgentBench.Tests/GraderTests.cs`:
```csharp
using FhirAgentBench.Answers;

namespace FhirAgentBench.Tests;

public sealed class GraderTests
{
    private static readonly AnswerSpec Quantity = new() { Type = AnswerType.Quantity, Unit = "%", Tolerance = 0.05m };

    private static readonly AnswerSpec LatestLab = new()
    {
        Type = AnswerType.Composite,
        Fields = new Dictionary<string, AnswerSpec>
        {
            ["value"] = Quantity,
            ["date"] = new() { Type = AnswerType.Date },
        },
    };

    private static Answer Agent(string json) => AnswerJson.Parse(json);

    [Fact]
    public void Composite_AllFieldsMatch_IsCorrect()
    {
        var gold = Answer.Of(new { value = new { value = 9.4m, unit = "%" }, date = "2025-12-01" }, ["Observation/o1"]);

        var grade = Grader.Grade(LatestLab, gold, Agent("""{"status":"answered","answer":{"value":{"value":9.42,"unit":"%"},"date":"2025-12-01"},"evidence":[]}"""));

        Assert.Equal(GradeStatus.Correct, grade);
    }

    [Fact]
    public void Composite_WrongDate_IsWrong()
    {
        var gold = Answer.Of(new { value = new { value = 9.4m, unit = "%" }, date = "2025-12-01" }, []);

        var grade = Grader.Grade(LatestLab, gold, Agent("""{"status":"answered","answer":{"value":{"value":9.4,"unit":"%"},"date":"2026-01-10"}}"""));

        Assert.Equal(GradeStatus.Wrong, grade);
    }

    [Fact]
    public void Quantity_OutsideTolerance_IsWrong()
    {
        var gold = Answer.Of(new { value = 9.4m, unit = "%" }, []);

        Assert.Equal(GradeStatus.Wrong, Grader.Grade(Quantity, gold, Agent("""{"status":"answered","answer":{"value":9.5,"unit":"%"}}""")));
    }

    [Fact]
    public void Quantity_UnitCaseInsensitive_IsCorrect()
    {
        var spec = new AnswerSpec { Type = AnswerType.Quantity, Unit = "mg/dL", Tolerance = 0.5m };
        var gold = Answer.Of(new { value = 150m, unit = "mg/dL" }, []);

        Assert.Equal(GradeStatus.Correct, Grader.Grade(spec, gold, Agent("""{"status":"answered","answer":{"value":150,"unit":"MG/DL"}}""")));
    }

    [Fact]
    public void NumberSentAsString_IsFormatInvalid()
    {
        var spec = new AnswerSpec { Type = AnswerType.Decimal, Tolerance = 0.1m };

        Assert.Equal(GradeStatus.FormatInvalid, Grader.Grade(spec, Answer.Of(9.4m, []), Agent("""{"status":"answered","answer":"9.4"}""")));
    }

    [Fact]
    public void Set_RequiresExactEquality()
    {
        var spec = new AnswerSpec { Type = AnswerType.Set };
        var gold = Answer.Of(new[] { new { system = "http://www.nlm.nih.gov/research/umls/rxnorm", code = "259255" } }, []);

        Assert.Equal(GradeStatus.Correct, Grader.Grade(spec, gold, Agent("""{"status":"answered","answer":[{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"259255"}]}""")));
        Assert.Equal(GradeStatus.Wrong, Grader.Grade(spec, gold, Agent("""{"status":"answered","answer":[{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"259255"},{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"860975"}]}""")));
    }

    [Fact]
    public void Nullable_NullGoldAndNullAnswer_IsCorrect()
    {
        var spec = new AnswerSpec { Type = AnswerType.Decimal, Nullable = true };

        Assert.Equal(GradeStatus.Correct, Grader.Grade(spec, Answer.Of(null, []), Agent("""{"status":"answered","answer":null}""")));
    }

    [Fact]
    public void NonNullable_NullAnswer_IsFormatInvalid()
    {
        var spec = new AnswerSpec { Type = AnswerType.Integer };

        Assert.Equal(GradeStatus.FormatInvalid, Grader.Grade(spec, Answer.Of(2, []), Agent("""{"status":"answered","answer":null}""")));
    }

    [Fact]
    public void AbstainOnAnswerableQuestion_IsAbstained()
    {
        var spec = new AnswerSpec { Type = AnswerType.Integer };

        Assert.Equal(GradeStatus.Abstained, Grader.Grade(spec, Answer.Of(2, []), Agent("""{"status":"insufficient_data","answer":null}""")));
    }

    [Fact]
    public void AbstainOnUnanswerableQuestion_IsCorrect_AndAnsweringIsWrong()
    {
        var spec = new AnswerSpec { Type = AnswerType.Boolean };

        Assert.Equal(GradeStatus.Correct, Grader.Grade(spec, Answer.Insufficient(), Agent("""{"status":"insufficient_data"}""")));
        Assert.Equal(GradeStatus.Wrong, Grader.Grade(spec, Answer.Insufficient(), Agent("""{"status":"answered","answer":true}""")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"status":"maybe"}""")]
    public void Parse_RejectsMalformedAnswers(string json)
    {
        Assert.Throws<FormatException>(() => AnswerJson.Parse(json));
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var answer = Answer.Of(new { ever = true, first = "2025-11-20" }, ["MedicationRequest/mr-ada-atorva"]);

        var roundTripped = AnswerJson.Parse(AnswerJson.Serialize(answer));

        Assert.Equal(AnswerStatus.Answered, roundTripped.Status);
        Assert.Equal(new[] { "MedicationRequest/mr-ada-atorva" }, roundTripped.Evidence);
        Assert.True(roundTripped.Value!.Value.GetProperty("ever").GetBoolean());
    }

    [Fact]
    public void Describe_ShowsCompositeShapeWithUnit()
    {
        var description = AnswerSpecDescriber.Describe(LatestLab);

        Assert.Equal("""{"value": {"value": <number>, "unit": "%"}, "date": "YYYY-MM-DD"}""", description);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter GraderTests`
Expected: build FAIL, `The type or namespace name 'Answers' does not exist`.

- [ ] **Step 3: Implement the answer types**

`src/FhirAgentBench/Answers/Answer.cs`:
```csharp
using System.Text.Json;

namespace FhirAgentBench.Answers;

public enum AnswerStatus
{
    Answered,
    InsufficientData,
}

public enum GradeStatus
{
    Correct,
    Wrong,
    Abstained,
    FormatInvalid,
    TurnLimit,
    HarnessError,
}

public sealed record Answer(AnswerStatus Status, JsonElement? Value, IReadOnlyList<string> Evidence)
{
    public static Answer Insufficient() => new(AnswerStatus.InsufficientData, null, []);

    public static Answer Of(object? value, IEnumerable<string> evidence) =>
        new(AnswerStatus.Answered,
            value is null ? null : JsonSerializer.SerializeToElement(value),
            evidence.Distinct(StringComparer.Ordinal).ToList());
}
```

`src/FhirAgentBench/Answers/AnswerSpec.cs`:
```csharp
namespace FhirAgentBench.Answers;

public enum AnswerType
{
    Integer,
    Decimal,
    Quantity,
    Date,
    Code,
    Identifier,
    Set,
    Boolean,
    Composite,
}

public sealed record AnswerSpec
{
    public required AnswerType Type { get; init; }

    public string? Unit { get; init; }

    public decimal Tolerance { get; init; }

    public bool Nullable { get; init; }

    public IReadOnlyDictionary<string, AnswerSpec> Fields { get; init; } = new Dictionary<string, AnswerSpec>();
}
```

`src/FhirAgentBench/Answers/AnswerJson.cs`:
```csharp
using System.Text.Json;

namespace FhirAgentBench.Answers;

public static class AnswerJson
{
    public static Answer Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"answer.json is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("answer.json must be a JSON object with a 'status' property.");
            }

            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var parsedStatus = status switch
            {
                "answered" => AnswerStatus.Answered,
                "insufficient_data" => AnswerStatus.InsufficientData,
                _ => throw new FormatException($"answer.json 'status' must be 'answered' or 'insufficient_data', not '{status}'."),
            };

            JsonElement? value = root.TryGetProperty("answer", out var a) && a.ValueKind != JsonValueKind.Null ? a.Clone() : null;
            var evidence = root.TryGetProperty("evidence", out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];
            return new Answer(parsedStatus, value, evidence);
        }
    }

    public static string Serialize(Answer answer) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["status"] = answer.Status == AnswerStatus.Answered ? "answered" : "insufficient_data",
            ["answer"] = answer.Value,
            ["evidence"] = answer.Evidence,
        });
}
```

`src/FhirAgentBench/Answers/AnswerShape.cs`:
```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FhirAgentBench.Answers;

public static partial class AnswerShape
{
    public static bool Conforms(AnswerSpec spec, JsonElement? value)
    {
        if (value is null)
        {
            return spec.Nullable;
        }

        var v = value.Value;
        return spec.Type switch
        {
            AnswerType.Integer => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
            AnswerType.Decimal => v.ValueKind == JsonValueKind.Number,
            AnswerType.Quantity => v.ValueKind == JsonValueKind.Object
                && v.TryGetProperty("value", out var q) && q.ValueKind == JsonValueKind.Number
                && v.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String,
            AnswerType.Date => v.ValueKind == JsonValueKind.String && DatePattern().IsMatch(v.GetString()!),
            AnswerType.Code => HasStrings(v, "system", "code"),
            AnswerType.Identifier => HasStrings(v, "system", "value"),
            AnswerType.Set => v.ValueKind == JsonValueKind.Array
                && v.EnumerateArray().All(i => i.ValueKind == JsonValueKind.String || HasStrings(i, "system", "code")),
            AnswerType.Boolean => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            AnswerType.Composite => v.ValueKind == JsonValueKind.Object
                && spec.Fields.All(f => Conforms(f.Value, Field(v, f.Key))),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Type, "Unknown answer type."),
        };
    }

    internal static JsonElement? Field(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Null ? field : null;

    private static bool HasStrings(JsonElement value, string first, string second) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(first, out var a) && a.ValueKind == JsonValueKind.String
        && value.TryGetProperty(second, out var b) && b.ValueKind == JsonValueKind.String;

    [GeneratedRegex(@"^\d{4}(-\d{2}(-\d{2})?)?$")]
    private static partial Regex DatePattern();
}
```

`src/FhirAgentBench/Answers/Grader.cs`:
```csharp
using System.Text.Json;

namespace FhirAgentBench.Answers;

public static class Grader
{
    public static GradeStatus Grade(AnswerSpec spec, Answer gold, Answer agent)
    {
        if (gold.Status == AnswerStatus.InsufficientData)
        {
            return agent.Status == AnswerStatus.InsufficientData ? GradeStatus.Correct : GradeStatus.Wrong;
        }

        if (agent.Status == AnswerStatus.InsufficientData)
        {
            return GradeStatus.Abstained;
        }

        if (!AnswerShape.Conforms(spec, agent.Value))
        {
            return GradeStatus.FormatInvalid;
        }

        return Matches(spec, gold.Value, agent.Value) ? GradeStatus.Correct : GradeStatus.Wrong;
    }

    private static bool Matches(AnswerSpec spec, JsonElement? gold, JsonElement? agent)
    {
        if (gold is null || agent is null)
        {
            return gold is null && agent is null;
        }

        var g = gold.Value;
        var a = agent.Value;
        return spec.Type switch
        {
            AnswerType.Integer => g.GetDecimal() == a.GetDecimal(),
            AnswerType.Decimal => Math.Abs(g.GetDecimal() - a.GetDecimal()) <= spec.Tolerance,
            AnswerType.Quantity => UnitsEqual(Text(a, "unit"), Text(g, "unit"))
                && Math.Abs(g.GetProperty("value").GetDecimal() - a.GetProperty("value").GetDecimal()) <= spec.Tolerance,
            AnswerType.Date => a.GetString() == g.GetString(),
            AnswerType.Code => Text(a, "system") == Text(g, "system") && Text(a, "code") == Text(g, "code"),
            AnswerType.Identifier => Text(a, "system") == Text(g, "system") && Text(a, "value") == Text(g, "value"),
            AnswerType.Set => SetOf(a).SetEquals(SetOf(g)),
            AnswerType.Boolean => a.GetBoolean() == g.GetBoolean(),
            AnswerType.Composite => spec.Fields.All(f =>
                Matches(f.Value, AnswerShape.Field(g, f.Key), AnswerShape.Field(a, f.Key))),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Type, "Unknown answer type."),
        };
    }

    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;

    private static bool UnitsEqual(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> SetOf(JsonElement array) =>
        array.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!.Trim()
                : $"{Text(item, "system")}|{Text(item, "code")}")
            .ToHashSet(StringComparer.Ordinal);
}
```

`src/FhirAgentBench/Answers/AnswerSpecDescriber.cs`:
```csharp
namespace FhirAgentBench.Answers;

public static class AnswerSpecDescriber
{
    public static string Describe(AnswerSpec spec)
    {
        var shape = spec.Type switch
        {
            AnswerType.Integer => "<integer>",
            AnswerType.Decimal => "<number>",
            AnswerType.Quantity => $$"""{"value": <number>, "unit": "{{spec.Unit}}"}""",
            AnswerType.Date => "\"YYYY-MM-DD\"",
            AnswerType.Code => """{"system": "<uri>", "code": "<code>"}""",
            AnswerType.Identifier => """{"system": "<uri>", "value": "<value>"}""",
            AnswerType.Set => """[{"system": "<uri>", "code": "<code>"}, ...]""",
            AnswerType.Boolean => "true | false",
            AnswerType.Composite => "{" + string.Join(", ", spec.Fields.Select(f => $"\"{f.Key}\": {Describe(f.Value)}")) + "}",
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Type, "Unknown answer type."),
        };
        return spec.Nullable ? $"{shape} (or null if there is no such result)" : shape;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter GraderTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FhirAgentBench/Answers tests/FhirAgentBench.Tests/GraderTests.cs
git commit -m "feat: add typed answers and deterministic grader"
```

---

### Task 3: Question, corpus and profile files

**Files:**
- Create: `src/FhirAgentBench/Questions/Yaml.cs`, `Question.cs`, `QuestionLoader.cs`
- Create: `src/FhirAgentBench/Oracles/OracleParameters.cs`
- Create: `src/FhirAgentBench/Corpus/CorpusDefinition.cs`
- Create: `src/FhirAgentBench/Execution/Profile.cs`, `ProfileLoader.cs`
- Test: `tests/FhirAgentBench.Tests/FileModelTests.cs`

**Interfaces:**
- Consumes: `AnswerSpec`, `AnswerType` (Task 2).
- Produces:
  - `record PatientContext(string Mrn, string Name, string BirthDate)`
  - `record OracleCall(string Name, OracleParameters Parameters)`
  - `record Question { Id, Tier, Template, Tags, ToolGroups, Persona, Patient, Text, Answer, Oracle, ExpectEmpty, VerificationStatus, SourcePath }`
  - `QuestionLoader.Load(string path) → Question`, `QuestionLoader.LoadSet(string questionsDirectory, string setFile) → IReadOnlyList<Question>`
  - `OracleParameters.GetString/GetDecimal/GetInt/GetStrings`
  - `record CorpusDefinition(string Id, DateTimeOffset Now, string MrnSystem, string SourceUrl, string SourceSha256, string Directory)` with `.DataDirectory`, `.ManifestPath`, `CorpusDefinition.Load(string corpusDirectory)`
  - `record McpServer(string Command, IReadOnlyList<string> Args, IReadOnlyList<string> Tools)`
  - `record Profile { Id, Harness, Model, ReasoningEffort, Executable, BuiltinTools, McpServers, PromptTemplatePath, MaxAiCredits, TimeoutMinutes, SourcePath }`
  - `ProfileLoader.Load(string path) → Profile`
  - Harness name constants `Profile.CopilotCli = "copilot-cli"`, `Profile.Oracle = "oracle"`, `Profile.Abstain = "abstain"`

- [ ] **Step 1: Write the failing tests**

`tests/FhirAgentBench.Tests/FileModelTests.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Execution;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class FileModelTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fab-files-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Question_LoadsAllFields()
    {
        var path = Write("questions/t1-last-a1c-001.yaml", """
            id: t1-last-a1c-001
            tier: 1
            template: latest-lab
            tags: [REF]
            tool_groups: [patient-context, search]
            persona: primary-care
            patient: { mrn: MRN-ADA, name: Ada Lovelace, birth_date: "1960-03-01" }
            question: When was her last A1c, and what was it?
            answer:
              type: composite
              fields:
                value: { type: quantity, unit: "%", tolerance: 0.05 }
                date: { type: date }
            oracle:
              name: latest-observation
              parameters: { loinc: 4548-4 }
            verification: { status: unverified }
            """);

        var question = QuestionLoader.Load(path);

        Assert.Equal(1, question.Tier);
        Assert.Equal("MRN-ADA", question.Patient!.Mrn);
        Assert.Equal(AnswerType.Composite, question.Answer.Type);
        Assert.Equal(0.05m, question.Answer.Fields["value"].Tolerance);
        Assert.Equal("4548-4", question.Oracle.Parameters.GetString("loinc"));
        Assert.Equal(new[] { "patient-context", "search" }, question.ToolGroups);
    }

    [Fact]
    public void Question_IdMustMatchFileName()
    {
        var path = Write("questions/other.yaml", """
            id: t1-last-a1c-001
            tier: 1
            template: latest-lab
            persona: primary-care
            question: Q?
            answer: { type: integer }
            oracle: { name: unanswerable }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.Load(path));
        Assert.Contains("must match the file name", ex.Message);
    }

    [Fact]
    public void Question_UnknownAnswerType_IsRejectedWithFileName()
    {
        var path = Write("questions/bad.yaml", """
            id: bad
            tier: 1
            template: x
            persona: nursing
            question: Q?
            answer: { type: integr }
            oracle: { name: unanswerable }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => QuestionLoader.Load(path));
        Assert.Contains("bad.yaml", ex.Message);
        Assert.Contains("integr", ex.Message);
    }

    [Fact]
    public void QuestionSet_SkipsBlankLinesAndComments()
    {
        Write("questions/q1.yaml", "id: q1\ntier: 7\ntemplate: t\npersona: nursing\nquestion: Q?\nanswer: { type: boolean }\noracle: { name: unanswerable }\n");
        var set = Write("question-sets/s.txt", "# phase 0\n\nq1\n");

        var questions = QuestionLoader.LoadSet(Path.Combine(_directory, "questions"), set);

        Assert.Equal("q1", Assert.Single(questions).Id);
    }

    [Fact]
    public void OracleParameters_ReadsListsAndNumbers()
    {
        var path = Write("questions/q2.yaml", """
            id: q2
            tier: 6
            template: cohort
            persona: researcher
            question: How many?
            answer: { type: integer }
            oracle:
              name: cohort-lab-above-or-missing
              parameters: { snomed: [44054006, 73211009], threshold: 9, loinc: 4548-4 }
            """);

        var parameters = QuestionLoader.Load(path).Oracle.Parameters;

        Assert.Equal(new[] { "44054006", "73211009" }, parameters.GetStrings("snomed"));
        Assert.Equal(9m, parameters.GetDecimal("threshold"));
        Assert.Throws<InvalidDataException>(() => parameters.GetString("missing"));
    }

    [Fact]
    public void Corpus_LoadsNowAndSource()
    {
        Write("corpora/c1/corpus.yaml", """
            id: c1
            now: "2026-08-18T00:00:00Z"
            mrn_system: http://hospital.smarthealthit.org
            source: { url: "https://example.org/c.zip", sha256: abc }
            """);

        var corpus = CorpusDefinition.Load(Path.Combine(_directory, "corpora", "c1"));

        Assert.Equal(new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero), corpus.Now);
        Assert.EndsWith(Path.Combine("c1", "data"), corpus.DataDirectory);
    }

    [Fact]
    public void Profile_ResolvesPromptRelativeToProfileFile()
    {
        Write("prompts/default.md", "prompt");
        var path = Write("profiles/p2.yaml", """
            id: p2
            harness: copilot-cli
            model: claude-sonnet-5.5
            builtin_tools: [view, rg]
            mcp_servers:
              fhir: { command: "${FAB_CLI_ON_FHIR}", args: [mcp, "{corpus}"], tools: [search_fhir_resources] }
            prompt_template: ../prompts/default.md
            max_ai_credits: 30
            timeout_minutes: 20
            """);

        var profile = ProfileLoader.Load(path);

        Assert.Equal(Profile.CopilotCli, profile.Harness);
        Assert.Equal("copilot", profile.Executable);
        Assert.True(File.Exists(profile.PromptTemplatePath));
        Assert.Equal(new[] { "search_fhir_resources" }, profile.McpServers["fhir"].Tools);
    }

    [Fact]
    public void Profile_UnknownHarness_IsRejected()
    {
        Write("prompts/default.md", "prompt");
        var path = Write("profiles/x.yaml", "id: x\nharness: claude-code\nprompt_template: ../prompts/default.md\n");

        var ex = Assert.Throws<InvalidDataException>(() => ProfileLoader.Load(path));
        Assert.Contains("claude-code", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter FileModelTests`
Expected: build FAIL, `The type or namespace name 'Questions' does not exist`.

- [ ] **Step 3: Implement the YAML helper and question model**

`src/FhirAgentBench/Questions/Yaml.cs`:
```csharp
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FhirAgentBench.Questions;

internal static class Yaml
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static T Read<T>(string path)
    {
        try
        {
            using var reader = File.OpenText(path);
            return Deserializer.Deserialize<T>(reader) ?? throw new InvalidDataException($"'{path}' is empty.");
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException($"'{path}' is not valid: {ex.Message}", ex);
        }
    }
}
```

`src/FhirAgentBench/Oracles/OracleParameters.cs`:
```csharp
using System.Globalization;

namespace FhirAgentBench.Oracles;

public sealed class OracleParameters(IReadOnlyDictionary<string, object> values)
{
    public static OracleParameters Empty { get; } = new(new Dictionary<string, object>());

    public string GetString(string name) =>
        Get(name) as string ?? throw new InvalidDataException($"Oracle parameter '{name}' must be a single value.");

    public string? GetOptionalString(string name) => values.TryGetValue(name, out var value) ? value as string : null;

    public decimal GetDecimal(string name) => decimal.Parse(GetString(name), CultureInfo.InvariantCulture);

    public int GetInt(string name) => int.Parse(GetString(name), CultureInfo.InvariantCulture);

    public IReadOnlyList<string> GetStrings(string name) =>
        Get(name) is List<object> list
            ? list.Select(item => item as string ?? throw new InvalidDataException($"Oracle parameter '{name}' must be a list of values.")).ToList()
            : throw new InvalidDataException($"Oracle parameter '{name}' must be a list.");

    private object Get(string name) =>
        values.TryGetValue(name, out var value) ? value : throw new InvalidDataException($"Oracle parameter '{name}' is required.");
}
```

`src/FhirAgentBench/Questions/Question.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;

namespace FhirAgentBench.Questions;

public sealed record PatientContext(string Mrn, string Name, string BirthDate);

public sealed record OracleCall(string Name, OracleParameters Parameters);

public sealed record Question
{
    public required string Id { get; init; }

    public required int Tier { get; init; }

    public required string Template { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<string> ToolGroups { get; init; } = [];

    public required string Persona { get; init; }

    public PatientContext? Patient { get; init; }

    public required string Text { get; init; }

    public required AnswerSpec Answer { get; init; }

    public required OracleCall Oracle { get; init; }

    public bool ExpectEmpty { get; init; }

    public string VerificationStatus { get; init; } = "unverified";

    public required string SourcePath { get; init; }
}
```

`src/FhirAgentBench/Questions/QuestionLoader.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;

namespace FhirAgentBench.Questions;

public static class QuestionLoader
{
    public static IReadOnlyList<Question> LoadSet(string questionsDirectory, string setFile) =>
        File.ReadLines(setFile)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(id => Load(Path.Combine(questionsDirectory, id + ".yaml")))
            .ToList();

    public static Question Load(string path)
    {
        var dto = Yaml.Read<QuestionDto>(path);
        var fileId = Path.GetFileNameWithoutExtension(path);
        if (dto.Id != fileId)
        {
            throw new InvalidDataException($"'{path}': id '{dto.Id}' must match the file name '{fileId}'.");
        }

        if (dto.Tier is < 1 or > 7)
        {
            throw new InvalidDataException($"'{path}': tier must be 1-7, not {dto.Tier}.");
        }

        return new Question
        {
            Id = dto.Id,
            Tier = dto.Tier,
            Template = Required(dto.Template, "template", path),
            Tags = dto.Tags,
            ToolGroups = dto.ToolGroups,
            Persona = Required(dto.Persona, "persona", path),
            Patient = dto.Patient is null
                ? null
                : new PatientContext(
                    Required(dto.Patient.Mrn, "patient.mrn", path),
                    Required(dto.Patient.Name, "patient.name", path),
                    Required(dto.Patient.BirthDate, "patient.birth_date", path)),
            Text = Required(dto.Question, "question", path),
            Answer = ToSpec(dto.Answer ?? throw new InvalidDataException($"'{path}': answer is required."), path),
            Oracle = new OracleCall(
                Required(dto.Oracle?.Name, "oracle.name", path),
                dto.Oracle?.Parameters is { } parameters ? new OracleParameters(parameters) : OracleParameters.Empty),
            ExpectEmpty = dto.ExpectEmpty,
            VerificationStatus = dto.Verification?.Status ?? "unverified",
            SourcePath = Path.GetFullPath(path),
        };
    }

    private static AnswerSpec ToSpec(AnswerSpecDto dto, string path) => new()
    {
        Type = Enum.TryParse<AnswerType>(dto.Type, ignoreCase: true, out var type)
            ? type
            : throw new InvalidDataException($"'{path}': unknown answer type '{dto.Type}'."),
        Unit = dto.Unit,
        Tolerance = dto.Tolerance,
        Nullable = dto.Nullable,
        Fields = dto.Fields?.ToDictionary(f => f.Key, f => ToSpec(f.Value, path)) ?? new Dictionary<string, AnswerSpec>(),
    };

    private static string Required(string? value, string name, string path) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"'{path}': {name} is required.") : value;

    private sealed class QuestionDto
    {
        public string? Id { get; set; }

        public int Tier { get; set; }

        public string? Template { get; set; }

        public List<string> Tags { get; set; } = [];

        public List<string> ToolGroups { get; set; } = [];

        public string? Persona { get; set; }

        public PatientDto? Patient { get; set; }

        public string? Question { get; set; }

        public AnswerSpecDto? Answer { get; set; }

        public OracleDto? Oracle { get; set; }

        public bool ExpectEmpty { get; set; }

        public VerificationDto? Verification { get; set; }
    }

    private sealed class PatientDto
    {
        public string? Mrn { get; set; }

        public string? Name { get; set; }

        public string? BirthDate { get; set; }
    }

    private sealed class AnswerSpecDto
    {
        public string? Type { get; set; }

        public string? Unit { get; set; }

        public decimal Tolerance { get; set; }

        public bool Nullable { get; set; }

        public Dictionary<string, AnswerSpecDto>? Fields { get; set; }
    }

    private sealed class OracleDto
    {
        public string? Name { get; set; }

        public Dictionary<string, object>? Parameters { get; set; }
    }

    private sealed class VerificationDto
    {
        public string? Status { get; set; }

        public string? By { get; set; }

        public string? At { get; set; }

        public string? Notes { get; set; }
    }
}
```

- [ ] **Step 4: Implement the corpus definition**

`src/FhirAgentBench/Corpus/CorpusDefinition.cs`:
```csharp
using FhirAgentBench.Questions;

namespace FhirAgentBench.Corpus;

public sealed record CorpusDefinition(
    string Id, DateTimeOffset Now, string MrnSystem, string SourceUrl, string SourceSha256, string Directory)
{
    public string DataDirectory => Path.Combine(Directory, "data");

    public string ManifestPath => Path.Combine(Directory, "data.manifest.json");

    public static CorpusDefinition Load(string corpusDirectory)
    {
        var path = Path.Combine(corpusDirectory, "corpus.yaml");
        var dto = Yaml.Read<CorpusDto>(path);
        return new CorpusDefinition(
            dto.Id ?? throw new InvalidDataException($"'{path}': id is required."),
            FhirTime.Parse(dto.Now ?? throw new InvalidDataException($"'{path}': now is required.")),
            dto.MrnSystem ?? throw new InvalidDataException($"'{path}': mrn_system is required."),
            dto.Source?.Url ?? throw new InvalidDataException($"'{path}': source.url is required."),
            dto.Source?.Sha256 ?? throw new InvalidDataException($"'{path}': source.sha256 is required."),
            Path.GetFullPath(corpusDirectory));
    }

    private sealed class CorpusDto
    {
        public string? Id { get; set; }

        public string? Now { get; set; }

        public string? MrnSystem { get; set; }

        public SourceDto? Source { get; set; }
    }

    private sealed class SourceDto
    {
        public string? Url { get; set; }

        public string? Sha256 { get; set; }
    }
}
```

- [ ] **Step 5: Implement the profile model**

`src/FhirAgentBench/Execution/Profile.cs`:
```csharp
namespace FhirAgentBench.Execution;

public sealed record McpServer(string Command, IReadOnlyList<string> Args, IReadOnlyList<string> Tools);

public sealed record Profile
{
    public const string CopilotCli = "copilot-cli";
    public const string Oracle = "oracle";
    public const string Abstain = "abstain";

    public required string Id { get; init; }

    public required string Harness { get; init; }

    public string? Model { get; init; }

    public string? ReasoningEffort { get; init; }

    public string Executable { get; init; } = "copilot";

    public IReadOnlyList<string> BuiltinTools { get; init; } = [];

    public IReadOnlyDictionary<string, McpServer> McpServers { get; init; } = new Dictionary<string, McpServer>();

    public required string PromptTemplatePath { get; init; }

    public int MaxAiCredits { get; init; } = 30;

    public int TimeoutMinutes { get; init; } = 20;

    public required string SourcePath { get; init; }
}
```

`src/FhirAgentBench/Execution/ProfileLoader.cs`:
```csharp
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public static class ProfileLoader
{
    private static readonly string[] KnownHarnesses = [Profile.CopilotCli, Profile.Oracle, Profile.Abstain];

    public static Profile Load(string path)
    {
        var dto = Yaml.Read<ProfileDto>(path);
        if (dto.Harness is null || !KnownHarnesses.Contains(dto.Harness))
        {
            throw new InvalidDataException(
                $"'{path}': harness '{dto.Harness}' is not supported. Use one of: {string.Join(", ", KnownHarnesses)}.");
        }

        if (dto.Harness == Profile.CopilotCli && string.IsNullOrWhiteSpace(dto.Model))
        {
            throw new InvalidDataException($"'{path}': model is required for the {Profile.CopilotCli} harness.");
        }

        var prompt = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path))!,
            dto.PromptTemplate ?? throw new InvalidDataException($"'{path}': prompt_template is required.")));
        if (!File.Exists(prompt))
        {
            throw new InvalidDataException($"'{path}': prompt template '{prompt}' does not exist.");
        }

        return new Profile
        {
            Id = dto.Id ?? throw new InvalidDataException($"'{path}': id is required."),
            Harness = dto.Harness!,
            Model = dto.Model,
            ReasoningEffort = dto.ReasoningEffort,
            Executable = dto.Executable ?? "copilot",
            BuiltinTools = dto.BuiltinTools,
            McpServers = dto.McpServers.ToDictionary(
                s => s.Key,
                s => new McpServer(
                    s.Value.Command ?? throw new InvalidDataException($"'{path}': mcp_servers.{s.Key}.command is required."),
                    s.Value.Args,
                    s.Value.Tools)),
            PromptTemplatePath = prompt,
            MaxAiCredits = dto.MaxAiCredits ?? 30,
            TimeoutMinutes = dto.TimeoutMinutes ?? 20,
            SourcePath = Path.GetFullPath(path),
        };
    }

    private sealed class ProfileDto
    {
        public string? Id { get; set; }

        public string? Harness { get; set; }

        public string? Model { get; set; }

        public string? ReasoningEffort { get; set; }

        public string? Executable { get; set; }

        public List<string> BuiltinTools { get; set; } = [];

        public Dictionary<string, McpServerDto> McpServers { get; set; } = [];

        public string? PromptTemplate { get; set; }

        public int? MaxAiCredits { get; set; }

        public int? TimeoutMinutes { get; set; }
    }

    private sealed class McpServerDto
    {
        public string? Command { get; set; }

        public List<string> Args { get; set; } = [];

        public List<string> Tools { get; set; } = [];
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter FileModelTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/FhirAgentBench tests/FhirAgentBench.Tests/FileModelTests.cs
git commit -m "feat: load question, corpus and profile files"
```

---

### Task 4: Independent oracles

**Files:**
- Create: `src/FhirAgentBench/Oracles/IOracle.cs`, `OracleContext.cs`, `OracleRegistry.cs`, `FhirElements.cs`, `Observations.cs`, `Medications.cs`, `LatestObservationOracle.cs`, `ObservationWindowStatsOracle.cs`, `MedicationsStartedWithinOracle.cs`, `EverOnMedicationOracle.cs`, `CohortLabAboveOrMissingOracle.cs`, `UnanswerableOracle.cs`
- Test: `tests/FhirAgentBench.Tests/OracleTests.cs`

**Interfaces:**
- Consumes: `CorpusIndex`, `FhirTime` (Task 1); `Answer` (Task 2); `Question`, `OracleParameters` (Task 3).
- Produces:
  - `record OracleContext(CorpusIndex Corpus, DateTimeOffset Now, string MrnSystem)` with `string PatientKey(PatientContext?)`
  - `interface IOracle { string Name { get; } Answer Compute(OracleContext context, Question question); }`
  - `OracleRegistry.CreateDefault()`, `OracleRegistry.Compute(OracleContext, Question) → Answer`
  - Oracle names: `latest-observation`, `observation-window-stats`, `medications-started-within`, `ever-on-medication`, `cohort-lab-above-or-missing`, `unanswerable`
  - Internal helpers used by Task 5: `Observations.Valid(OracleContext, string patientKey, string loinc) → IEnumerable<ValidObservation>`, `record ValidObservation(JsonElement Resource, DateTimeOffset When, decimal Value, string Unit)`, `FhirElements.Codings(JsonElement concept)`, `Medications.Concept(CorpusIndex, JsonElement request) → JsonElement?`

- [ ] **Step 1: Write the failing tests**

`tests/FhirAgentBench.Tests/OracleTests.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class OracleTests
{
    private static readonly OracleContext Context = new(Fixture.Index, Fixture.Now, Fixture.MrnSystem);
    private static readonly OracleRegistry Oracles = OracleRegistry.CreateDefault();

    private static Question Ask(string oracle, string? mrn, Dictionary<string, object> parameters) => new()
    {
        Id = "q",
        Tier = 1,
        Template = "t",
        Persona = "nursing",
        Text = "?",
        Patient = mrn is null ? null : new PatientContext(mrn, "n", "1960-01-01"),
        Answer = new AnswerSpec { Type = AnswerType.Integer },
        Oracle = new OracleCall(oracle, new OracleParameters(parameters)),
        SourcePath = "q.yaml",
    };

    [Fact]
    public void LatestObservation_SkipsEnteredInErrorAndFutureResults()
    {
        var gold = Oracles.Compute(Context, Ask("latest-observation", "MRN-ADA", new() { ["loinc"] = "4548-4" }));

        Assert.Equal(9.4m, gold.Value!.Value.GetProperty("value").GetProperty("value").GetDecimal());
        Assert.Equal("%", gold.Value.Value.GetProperty("value").GetProperty("unit").GetString());
        Assert.Equal("2025-12-01", gold.Value.Value.GetProperty("date").GetString());
        Assert.Equal(new[] { "Observation/o-ada-a1c-2" }, gold.Evidence);
    }

    [Fact]
    public void LatestObservation_NoResults_IsNullAnswer()
    {
        var gold = Oracles.Compute(Context, Ask("latest-observation", "MRN-BRYN", new() { ["loinc"] = "4548-4" }));

        Assert.Equal(AnswerStatus.Answered, gold.Status);
        Assert.Null(gold.Value);
    }

    [Fact]
    public void WindowStats_MeanAndCountWithinWindow()
    {
        var parameters = new Dictionary<string, object> { ["loinc"] = "2339-0", ["window_days"] = "1", ["statistic"] = "mean", ["unit"] = "mg/dL" };
        var mean = Oracles.Compute(Context, Ask("observation-window-stats", "MRN-ADA", parameters));
        parameters["statistic"] = "count";
        var count = Oracles.Compute(Context, Ask("observation-window-stats", "MRN-ADA", parameters));

        Assert.Equal(150m, mean.Value!.Value.GetDecimal());
        Assert.Equal(2, count.Value!.Value.GetInt32());
    }

    [Fact]
    public void MedicationsStartedWithin_ResolvesMedicationReference()
    {
        var gold = Oracles.Compute(Context, Ask("medications-started-within", "MRN-ADA", new() { ["window_days"] = "90" }));

        var item = Assert.Single(gold.Value!.Value.EnumerateArray());
        Assert.Equal("259255", item.GetProperty("code").GetString());
    }

    [Fact]
    public void MedicationsStartedWithin_ResolvesContainedMedication()
    {
        var gold = Oracles.Compute(Context, Ask("medications-started-within", "MRN-BRYN", new() { ["window_days"] = "90" }));

        Assert.Equal("197361", Assert.Single(gold.Value!.Value.EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public void EverOnMedication_ReportsFirstOrderDate()
    {
        var parameters = new Dictionary<string, object> { ["rxnorm"] = new List<object> { "259255", "617312" } };

        var ada = Oracles.Compute(Context, Ask("ever-on-medication", "MRN-ADA", parameters));
        var bryn = Oracles.Compute(Context, Ask("ever-on-medication", "MRN-BRYN", parameters));

        Assert.True(ada.Value!.Value.GetProperty("ever").GetBoolean());
        Assert.Equal("2025-11-20", ada.Value.Value.GetProperty("first").GetString());
        Assert.False(bryn.Value!.Value.GetProperty("ever").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, bryn.Value.Value.GetProperty("first").ValueKind);
    }

    [Fact]
    public void Cohort_CountsHighAndMissing_ExcludesResolvedAndOutOfAgeRange()
    {
        var parameters = new Dictionary<string, object>
        {
            ["snomed"] = new List<object> { "44054006" },
            ["loinc"] = "4548-4",
            ["threshold"] = "9",
            ["window_days"] = "365",
            ["age_min"] = "18",
            ["age_max"] = "75",
        };

        var gold = Oracles.Compute(Context, Ask("cohort-lab-above-or-missing", null, parameters));

        Assert.Equal(2, gold.Value!.Value.GetInt32());
        Assert.Equal(new[] { "Patient/ada", "Patient/bryn" }, gold.Evidence.Order());
    }

    [Fact]
    public void Unanswerable_ReturnsInsufficientData()
    {
        Assert.Equal(AnswerStatus.InsufficientData, Oracles.Compute(Context, Ask("unanswerable", "MRN-ADA", new())).Status);
    }

    [Fact]
    public void UnknownOracleOrPatient_FailsWithActionableMessage()
    {
        Assert.Contains("unknown oracle", Assert.Throws<InvalidDataException>(() => Oracles.Compute(Context, Ask("nope", "MRN-ADA", new()))).Message);
        Assert.Contains("MRN-NOBODY", Assert.Throws<InvalidDataException>(() => Oracles.Compute(Context, Ask("latest-observation", "MRN-NOBODY", new() { ["loinc"] = "4548-4" }))).Message);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter OracleTests`
Expected: build FAIL, `The type or namespace name 'OracleContext' could not be found`.

- [ ] **Step 3: Implement the oracle infrastructure**

`src/FhirAgentBench/Oracles/IOracle.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public interface IOracle
{
    string Name { get; }

    Answer Compute(OracleContext context, Question question);
}
```

`src/FhirAgentBench/Oracles/OracleContext.cs`:
```csharp
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed record OracleContext(CorpusIndex Corpus, DateTimeOffset Now, string MrnSystem)
{
    public string PatientKey(PatientContext? patient)
    {
        if (patient is null)
        {
            throw new InvalidDataException("This oracle needs a question with a patient context.");
        }

        var resource = Corpus.FindByIdentifier("Patient", MrnSystem, patient.Mrn)
            ?? throw new InvalidDataException($"No Patient with MRN '{patient.Mrn}' ({MrnSystem}) exists in the corpus.");
        return CorpusIndex.KeyOf(resource);
    }
}
```

`src/FhirAgentBench/Oracles/OracleRegistry.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class OracleRegistry
{
    private readonly Dictionary<string, IOracle> _oracles;

    public OracleRegistry(IEnumerable<IOracle> oracles)
    {
        _oracles = oracles.ToDictionary(o => o.Name, StringComparer.Ordinal);
    }

    public static OracleRegistry CreateDefault() => new(
    [
        new LatestObservationOracle(),
        new ObservationWindowStatsOracle(),
        new MedicationsStartedWithinOracle(),
        new EverOnMedicationOracle(),
        new CohortLabAboveOrMissingOracle(),
        new UnanswerableOracle(),
    ]);

    public Answer Compute(OracleContext context, Question question)
    {
        var oracle = _oracles.TryGetValue(question.Oracle.Name, out var found)
            ? found
            : throw new InvalidDataException($"Question '{question.Id}' names unknown oracle '{question.Oracle.Name}'.");
        return oracle.Compute(context, question);
    }
}
```

`src/FhirAgentBench/Oracles/FhirElements.cs`:
```csharp
using System.Text.Json;

namespace FhirAgentBench.Oracles;

internal static class FhirElements
{
    public const string Loinc = "http://loinc.org";
    public const string Snomed = "http://snomed.info/sct";
    public const string RxNorm = "http://www.nlm.nih.gov/research/umls/rxnorm";

    public static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static IEnumerable<(string System, string Code, string? Display)> Codings(JsonElement concept)
    {
        if (!concept.TryGetProperty("coding", out var codings) || codings.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var coding in codings.EnumerateArray())
        {
            if (String(coding, "system") is { } system && String(coding, "code") is { } code)
            {
                yield return (system, code, String(coding, "display"));
            }
        }
    }

    public static bool HasCoding(JsonElement resource, string property, string system, IReadOnlySet<string> codes) =>
        resource.TryGetProperty(property, out var concept)
        && Codings(concept).Any(c => c.System == system && codes.Contains(c.Code));
}
```

`src/FhirAgentBench/Oracles/Observations.cs`:
```csharp
using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal sealed record ValidObservation(JsonElement Resource, DateTimeOffset When, decimal Value, string Unit)
{
    public string Key => CorpusIndex.KeyOf(Resource);
}

internal static class Observations
{
    private static readonly HashSet<string> ValidStatuses = ["final", "amended", "corrected"];

    public static IEnumerable<ValidObservation> Valid(OracleContext context, string patientKey, string loinc)
    {
        var codes = new HashSet<string> { loinc };
        foreach (var observation in context.Corpus.ForPatient(patientKey, "Observation"))
        {
            if (!ValidStatuses.Contains(FhirElements.String(observation, "status") ?? "")
                || !FhirElements.HasCoding(observation, "code", FhirElements.Loinc, codes)
                || FhirTime.Effective(observation) is not { } when
                || when > context.Now
                || !observation.TryGetProperty("valueQuantity", out var quantity)
                || !quantity.TryGetProperty("value", out var value))
            {
                continue;
            }

            var unit = FhirElements.String(quantity, "code") ?? FhirElements.String(quantity, "unit") ?? "";
            yield return new ValidObservation(observation, when, value.GetDecimal(), unit);
        }
    }
}
```

`src/FhirAgentBench/Oracles/Medications.cs`:
```csharp
using System.Text.Json;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Oracles;

internal static class Medications
{
    public static JsonElement? Concept(CorpusIndex corpus, JsonElement request)
    {
        if (request.TryGetProperty("medicationCodeableConcept", out var concept))
        {
            return concept;
        }

        return request.TryGetProperty("medicationReference", out var reference)
            && FhirElements.String(reference, "reference") is { } target
            && corpus.ResolveFrom(request, target) is { } medication
            && medication.TryGetProperty("code", out var code)
                ? code
                : null;
    }

    public static string? RxNormCode(CorpusIndex corpus, JsonElement request) =>
        Concept(corpus, request) is { } concept
            ? FhirElements.Codings(concept).Where(c => c.System == FhirElements.RxNorm).Select(c => c.Code).FirstOrDefault()
            : null;
}
```

- [ ] **Step 4: Implement the six oracles**

`src/FhirAgentBench/Oracles/LatestObservationOracle.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class LatestObservationOracle : IOracle
{
    public string Name => "latest-observation";

    public Answer Compute(OracleContext context, Question question)
    {
        var patient = context.PatientKey(question.Patient);
        var latest = Observations.Valid(context, patient, question.Oracle.Parameters.GetString("loinc"))
            .OrderByDescending(o => o.When)
            .FirstOrDefault();
        return latest is null
            ? Answer.Of(null, [])
            : Answer.Of(
                new { value = new { value = latest.Value, unit = latest.Unit }, date = FhirTime.ToDate(latest.When) },
                [latest.Key]);
    }
}
```

`src/FhirAgentBench/Oracles/ObservationWindowStatsOracle.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class ObservationWindowStatsOracle : IOracle
{
    public string Name => "observation-window-stats";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var from = context.Now.AddDays(-parameters.GetInt("window_days"));
        var inWindow = Observations.Valid(context, context.PatientKey(question.Patient), parameters.GetString("loinc"))
            .Where(o => o.When > from)
            .ToList();
        var evidence = inWindow.Select(o => o.Key);
        var statistic = parameters.GetString("statistic");
        if (statistic == "count")
        {
            return Answer.Of(inWindow.Count, evidence);
        }

        if (inWindow.Count == 0)
        {
            return Answer.Of(null, []);
        }

        decimal value = statistic switch
        {
            "mean" => Math.Round(inWindow.Average(o => o.Value), 2, MidpointRounding.AwayFromZero),
            "max" => inWindow.Max(o => o.Value),
            "min" => inWindow.Min(o => o.Value),
            _ => throw new InvalidDataException($"Question '{question.Id}': statistic must be count, mean, max or min, not '{statistic}'."),
        };
        return Answer.Of(value, evidence);
    }
}
```

`src/FhirAgentBench/Oracles/MedicationsStartedWithinOracle.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class MedicationsStartedWithinOracle : IOracle
{
    public string Name => "medications-started-within";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var from = context.Now.AddDays(-parameters.GetInt("window_days"));
        var requiredStatus = parameters.GetOptionalString("status");
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        var evidence = new List<string>();
        foreach (var request in context.Corpus.ForPatient(context.PatientKey(question.Patient), "MedicationRequest"))
        {
            var status = FhirElements.String(request, "status");
            if (status == "entered-in-error"
                || (requiredStatus is not null && status != requiredStatus)
                || FhirTime.Effective(request) is not { } when
                || when <= from
                || when > context.Now
                || Medications.RxNormCode(context.Corpus, request) is not { } code)
            {
                continue;
            }

            codes.Add(code);
            evidence.Add(CorpusIndex.KeyOf(request));
        }

        return Answer.Of(codes.Select(code => new { system = FhirElements.RxNorm, code }).ToList(), evidence);
    }
}
```

`src/FhirAgentBench/Oracles/EverOnMedicationOracle.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class EverOnMedicationOracle : IOracle
{
    public string Name => "ever-on-medication";

    public Answer Compute(OracleContext context, Question question)
    {
        var codes = question.Oracle.Parameters.GetStrings("rxnorm").ToHashSet(StringComparer.Ordinal);
        var orders = context.Corpus.ForPatient(context.PatientKey(question.Patient), "MedicationRequest")
            .Where(r => FhirElements.String(r, "status") != "entered-in-error")
            .Select(r => new { Resource = r, When = FhirTime.Effective(r), Code = Medications.RxNormCode(context.Corpus, r) })
            .Where(r => r.Code is not null && codes.Contains(r.Code) && r.When is not null && r.When <= context.Now)
            .OrderBy(r => r.When)
            .ToList();
        var first = orders.Count > 0 ? FhirTime.ToDate(orders[0].When!.Value) : null;
        return Answer.Of(new { ever = orders.Count > 0, first }, orders.Select(o => CorpusIndex.KeyOf(o.Resource)));
    }
}
```

`src/FhirAgentBench/Oracles/CohortLabAboveOrMissingOracle.cs`:
```csharp
using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class CohortLabAboveOrMissingOracle : IOracle
{
    private static readonly HashSet<string> ExcludedVerification = ["entered-in-error", "refuted"];

    public string Name => "cohort-lab-above-or-missing";

    public Answer Compute(OracleContext context, Question question)
    {
        var parameters = question.Oracle.Parameters;
        var conditions = parameters.GetStrings("snomed").ToHashSet(StringComparer.Ordinal);
        var loinc = parameters.GetString("loinc");
        var threshold = parameters.GetDecimal("threshold");
        var from = context.Now.AddDays(-parameters.GetInt("window_days"));
        var ageMin = parameters.GetInt("age_min");
        var ageMax = parameters.GetInt("age_max");

        var counted = new List<string>();
        foreach (var patient in context.Corpus.OfType("Patient"))
        {
            var key = CorpusIndex.KeyOf(patient);
            if (!IsAliveAndInAgeRange(patient, context.Now, ageMin, ageMax)
                || !HasActiveCondition(context, key, conditions))
            {
                continue;
            }

            var latest = Observations.Valid(context, key, loinc)
                .Where(o => o.When > from)
                .OrderByDescending(o => o.When)
                .FirstOrDefault();
            if (latest is null || latest.Value > threshold)
            {
                counted.Add(key);
            }
        }

        return Answer.Of(counted.Count, counted);
    }

    private static bool IsAliveAndInAgeRange(JsonElement patient, DateTimeOffset now, int ageMin, int ageMax)
    {
        if (FhirElements.String(patient, "deceasedDateTime") is { } died && FhirTime.Parse(died) <= now)
        {
            return false;
        }

        if (FhirElements.String(patient, "birthDate") is not { } birthDate)
        {
            return false;
        }

        var age = FhirTime.AgeInYears(birthDate, now);
        return age >= ageMin && age <= ageMax;
    }

    private static bool HasActiveCondition(OracleContext context, string patientKey, IReadOnlySet<string> codes) =>
        context.Corpus.ForPatient(patientKey, "Condition").Any(condition =>
            FhirElements.HasCoding(condition, "code", FhirElements.Snomed, codes)
            && !IsExcludedVerification(condition)
            && FhirTime.Effective(condition) is { } onset
            && onset <= context.Now
            && (FhirElements.String(condition, "abatementDateTime") is not { } abated || FhirTime.Parse(abated) > context.Now));

    private static bool IsExcludedVerification(JsonElement condition) =>
        condition.TryGetProperty("verificationStatus", out var status)
        && FhirElements.Codings(status).Any(c => ExcludedVerification.Contains(c.Code));
}
```

`src/FhirAgentBench/Oracles/UnanswerableOracle.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Oracles;

public sealed class UnanswerableOracle : IOracle
{
    public string Name => "unanswerable";

    public Answer Compute(OracleContext context, Question question) => Answer.Insufficient();
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter OracleTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/FhirAgentBench/Oracles tests/FhirAgentBench.Tests/OracleTests.cs
git commit -m "feat: add six independent gold oracles"
```

---

### Task 5: Gold admission and authoring queries

**Files:**
- Create: `src/FhirAgentBench/Authoring/GoldAdmitter.cs`, `GoldLock.cs`, `CorpusQueries.cs`
- Test: `tests/FhirAgentBench.Tests/AuthoringTests.cs`

**Interfaces:**
- Consumes: `OracleRegistry`, `OracleContext`, `Observations`, `Medications`, `FhirElements` (Task 4); `Grader` (Task 2).
- Produces:
  - `record AdmissionResult(string QuestionId, Answer Gold, string GoldHash, IReadOnlyList<string> Problems)` with `IsAdmitted`
  - `GoldAdmitter(OracleRegistry, OracleContext).Admit(Question question, string? lockedHash) → AdmissionResult`
  - `GoldLock.Load(string path) → Dictionary<string,string>` and `GoldLock.Save(string path, IReadOnlyDictionary<string,string>)`
  - `CorpusQueries.LatestObservationTime(CorpusIndex) → DateTimeOffset?`
  - `CorpusQueries.PatientsWithObservation(OracleContext, string loinc) → IReadOnlyList<PatientRow>`, with `record PatientRow(string Mrn, string Name, string BirthDate, int Count, string LatestDate)`
  - `CorpusQueries.CodeCounts(CorpusIndex, string type, string displayContains) → IReadOnlyList<CodeRow>`, with `record CodeRow(string System, string Code, string Display, int Count)`

- [ ] **Step 1: Write the failing tests**

`tests/FhirAgentBench.Tests/AuthoringTests.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Authoring;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class AuthoringTests
{
    private static readonly OracleContext Context = new(Fixture.Index, Fixture.Now, Fixture.MrnSystem);
    private static readonly GoldAdmitter Admitter = new(OracleRegistry.CreateDefault(), Context);

    private static Question Ask(int tier, string oracle, string? mrn, AnswerSpec spec, Dictionary<string, object> parameters, bool expectEmpty = false) => new()
    {
        Id = "q",
        Tier = tier,
        Template = "t",
        Persona = "nursing",
        Text = "?",
        Patient = mrn is null ? null : new PatientContext(mrn, "n", "1960-01-01"),
        Answer = spec,
        Oracle = new OracleCall(oracle, new OracleParameters(parameters)),
        ExpectEmpty = expectEmpty,
        SourcePath = "q.yaml",
    };

    private static readonly AnswerSpec NullableDecimal = new() { Type = AnswerType.Decimal, Nullable = true, Tolerance = 0.5m };

    [Fact]
    public void Admit_AcceptsConsistentGold()
    {
        var result = Admitter.Admit(Ask(3, "observation-window-stats", "MRN-ADA", NullableDecimal,
            new() { ["loinc"] = "2339-0", ["window_days"] = "1", ["statistic"] = "mean", ["unit"] = "mg/dL" }), lockedHash: null);

        Assert.True(result.IsAdmitted, string.Join("; ", result.Problems));
        Assert.Equal(64, result.GoldHash.Length);
    }

    [Fact]
    public void Admit_RejectsEmptyGoldUnlessExpected()
    {
        var question = Ask(1, "latest-observation", "MRN-BRYN", NullableDecimal, new() { ["loinc"] = "4548-4" });

        Assert.Contains(Admitter.Admit(question, null).Problems, p => p.Contains("empty"));
        Assert.True(Admitter.Admit(question with { ExpectEmpty = true }, null).Problems.All(p => !p.Contains("empty")));
    }

    [Fact]
    public void Admit_RejectsGoldThatViolatesItsOwnSpec()
    {
        var wrongUnit = new AnswerSpec
        {
            Type = AnswerType.Composite,
            Fields = new Dictionary<string, AnswerSpec>
            {
                ["value"] = new() { Type = AnswerType.Integer },
                ["date"] = new() { Type = AnswerType.Date },
            },
        };

        var result = Admitter.Admit(Ask(1, "latest-observation", "MRN-ADA", wrongUnit, new() { ["loinc"] = "4548-4" }), null);

        Assert.Contains(result.Problems, p => p.Contains("answer spec"));
    }

    [Fact]
    public void Admit_DetectsDrift()
    {
        var question = Ask(6, "cohort-lab-above-or-missing", null, new AnswerSpec { Type = AnswerType.Integer }, new()
        {
            ["snomed"] = new List<object> { "44054006" }, ["loinc"] = "4548-4", ["threshold"] = "9",
            ["window_days"] = "365", ["age_min"] = "18", ["age_max"] = "75", ["unit"] = "%",
        });

        Assert.Contains(Admitter.Admit(question, new string('0', 64)).Problems, p => p.Contains("drift"));
    }

    [Fact]
    public void Admit_Tier7MustBeInsufficientData_AndOthersMustNotBe()
    {
        var spec = new AnswerSpec { Type = AnswerType.Boolean };

        Assert.True(Admitter.Admit(Ask(7, "unanswerable", "MRN-ADA", spec, new()), null).IsAdmitted);
        Assert.Contains(Admitter.Admit(Ask(2, "unanswerable", "MRN-ADA", spec, new()), null).Problems, p => p.Contains("tier 7"));
    }

    [Fact]
    public void Admit_ReportsOracleFailureInsteadOfThrowing()
    {
        var result = Admitter.Admit(Ask(1, "latest-observation", "MRN-NOBODY", NullableDecimal, new() { ["loinc"] = "4548-4" }), null);

        Assert.Contains(result.Problems, p => p.Contains("MRN-NOBODY"));
    }

    [Fact]
    public void GoldLock_RoundTripsSorted()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("fab-lock-").FullName, "gold.lock.json");

        GoldLock.Save(path, new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });

        Assert.Equal(new[] { "a", "b" }, GoldLock.Load(path).Keys);
        Assert.Empty(GoldLock.Load(path + ".missing"));
    }

    [Fact]
    public void CorpusQueries_FindPatientsAndCodes()
    {
        var rows = CorpusQueries.PatientsWithObservation(Context, "4548-4");
        var codes = CorpusQueries.CodeCounts(Fixture.Index, "MedicationRequest", "statin");

        Assert.Equal("MRN-ADA", rows[0].Mrn);
        Assert.Equal(2, rows[0].Count);
        Assert.Equal("259255", Assert.Single(codes).Code);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 9, 0, 0, TimeSpan.Zero), CorpusQueries.LatestObservationTime(Fixture.Index));
    }
}
```
Ada's A1c count of 2 is correct because `PatientsWithObservation` uses `Observations.Valid`, which drops the entered-in-error result and the one after "now".

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter AuthoringTests`
Expected: build FAIL, `The type or namespace name 'Authoring' does not exist`.

- [ ] **Step 3: Implement admission**

`src/FhirAgentBench/Authoring/GoldAdmitter.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Authoring;

public sealed record AdmissionResult(string QuestionId, Answer Gold, string GoldHash, IReadOnlyList<string> Problems)
{
    public bool IsAdmitted => Problems.Count == 0;
}

public sealed class GoldAdmitter(OracleRegistry oracles, OracleContext context)
{
    public AdmissionResult Admit(Question question, string? lockedHash)
    {
        Answer gold;
        try
        {
            gold = oracles.Compute(context, question);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or KeyNotFoundException)
        {
            return new AdmissionResult(question.Id, Answer.Insufficient(), "", [$"oracle failed: {ex.Message}"]);
        }

        var problems = new List<string>();
        var abstains = gold.Status == AnswerStatus.InsufficientData;
        if (question.Tier == 7 && !abstains)
        {
            problems.Add("tier 7 gold must be insufficient_data");
        }

        if (question.Tier != 7 && abstains)
        {
            problems.Add("only tier 7 questions may have insufficient_data gold");
        }

        if (Grader.Grade(question.Answer, gold, gold) != GradeStatus.Correct)
        {
            problems.Add("gold does not satisfy its own answer spec");
        }

        if (!question.ExpectEmpty && !abstains && IsEmpty(gold.Value))
        {
            problems.Add("gold is empty but expect_empty is false");
        }

        var hash = Hash(gold);
        if (lockedHash is not null && lockedHash != hash)
        {
            problems.Add($"gold drift: locked {lockedHash[..12]}, computed {hash[..12]}");
        }

        return new AdmissionResult(question.Id, gold, hash, problems);
    }

    private static bool IsEmpty(JsonElement? value) => value switch
    {
        null => true,
        { ValueKind: JsonValueKind.Array } array => array.GetArrayLength() == 0,
        { ValueKind: JsonValueKind.Number } number => number.GetDecimal() == 0,
        _ => false,
    };

    private static string Hash(Answer gold)
    {
        var canonical = $"{gold.Status}|{gold.Value?.GetRawText()}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
```

`src/FhirAgentBench/Authoring/GoldLock.cs`:
```csharp
using System.Text.Json;

namespace FhirAgentBench.Authoring;

public static class GoldLock
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static Dictionary<string, string> Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
            : [];

    public static void Save(string path, IReadOnlyDictionary<string, string> hashes)
    {
        var sorted = new SortedDictionary<string, string>(hashes.ToDictionary(), StringComparer.Ordinal);
        File.WriteAllText(path, JsonSerializer.Serialize(sorted, Indented) + "\n");
    }
}
```

- [ ] **Step 4: Implement authoring queries**

`src/FhirAgentBench/Authoring/CorpusQueries.cs`:
```csharp
using System.Text.Json;
using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;

namespace FhirAgentBench.Authoring;

public sealed record PatientRow(string Mrn, string Name, string BirthDate, int Count, string LatestDate);

public sealed record CodeRow(string System, string Code, string Display, int Count);

public static class CorpusQueries
{
    public static DateTimeOffset? LatestObservationTime(CorpusIndex index) =>
        index.OfType("Observation").Select(FhirTime.Effective).Where(t => t is not null).Max();

    public static IReadOnlyList<PatientRow> PatientsWithObservation(OracleContext context, string loinc)
    {
        var rows = new List<PatientRow>();
        foreach (var patient in context.Corpus.OfType("Patient"))
        {
            var observations = Observations.Valid(context, CorpusIndex.KeyOf(patient), loinc).ToList();
            if (observations.Count == 0 || Mrn(patient, context.MrnSystem) is not { } mrn)
            {
                continue;
            }

            rows.Add(new PatientRow(
                mrn,
                Name(patient),
                FhirElements.String(patient, "birthDate") ?? "",
                observations.Count,
                FhirTime.ToDate(observations.Max(o => o.When))));
        }

        return rows.OrderByDescending(r => r.Count).ThenBy(r => r.Mrn, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<CodeRow> CodeCounts(CorpusIndex index, string type, string displayContains)
    {
        var counts = new Dictionary<(string, string, string), int>();
        foreach (var resource in index.OfType(type))
        {
            var concept = type == "MedicationRequest"
                ? Medications.Concept(index, resource)
                : resource.TryGetProperty("code", out var code) ? code : (JsonElement?)null;
            if (concept is null)
            {
                continue;
            }

            foreach (var coding in FhirElements.Codings(concept.Value))
            {
                var display = coding.Display ?? "";
                if (display.Contains(displayContains, StringComparison.OrdinalIgnoreCase))
                {
                    var key = (coding.System, coding.Code, display);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
        }

        return counts.Select(c => new CodeRow(c.Key.Item1, c.Key.Item2, c.Key.Item3, c.Value))
            .OrderByDescending(r => r.Count)
            .ToList();
    }

    private static string? Mrn(JsonElement patient, string mrnSystem) =>
        patient.TryGetProperty("identifier", out var identifiers)
            ? identifiers.EnumerateArray()
                .Where(i => FhirElements.String(i, "system") == mrnSystem)
                .Select(i => FhirElements.String(i, "value"))
                .FirstOrDefault()
            : null;

    private static string Name(JsonElement patient)
    {
        if (!patient.TryGetProperty("name", out var names) || names.GetArrayLength() == 0)
        {
            return "";
        }

        var name = names[0];
        var given = name.TryGetProperty("given", out var g) && g.GetArrayLength() > 0 ? g[0].GetString() : "";
        return $"{given} {FhirElements.String(name, "family")}".Trim();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter AuthoringTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/FhirAgentBench/Authoring tests/FhirAgentBench.Tests/AuthoringTests.cs
git commit -m "feat: admit gold with drift lock and add authoring queries"
```

---

### Task 6: Corpus fetch, manifest and tamper guard

**Files:**
- Create: `src/FhirAgentBench/Corpus/CorpusManifest.cs`, `CorpusFetcher.cs`, `CorpusGuard.cs`
- Test: `tests/FhirAgentBench.Tests/CorpusFilesTests.cs`

**Interfaces:**
- Consumes: `CorpusDefinition` (Task 3).
- Produces:
  - `record CorpusFile(string Path, long Size, string Sha256)`
  - `record CorpusManifest(IReadOnlyList<CorpusFile> Files)` with `Create(string dataDirectory)`, `Load(string path)`, `Save(string path)`, `Verify(string dataDirectory) → IReadOnlyList<string>`, and `Sha256` (hash of the manifest JSON, used by run snapshots)
  - `CorpusFetcher.FetchAsync(CorpusDefinition corpus, HttpClient http, CancellationToken cancellationToken = default) → Task<string>`, which returns a status message
  - `CorpusGuard.Snapshot(string dataDirectory)` and `CorpusGuard.Changes() → IReadOnlyList<string>`

- [ ] **Step 1: Write the failing tests**

`tests/FhirAgentBench.Tests/CorpusFilesTests.cs`:
```csharp
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using FhirAgentBench.Corpus;

namespace FhirAgentBench.Tests;

public sealed class CorpusFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fab-corpus-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private string CopyFixtureData()
    {
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        foreach (var file in Directory.EnumerateFiles(Fixture.CorpusDirectory))
        {
            File.Copy(file, Path.Combine(data, Path.GetFileName(file)));
        }

        return data;
    }

    [Fact]
    public void Manifest_VerifiesCleanDataAndReportsChanges()
    {
        var data = CopyFixtureData();
        var manifest = CorpusManifest.Create(data);

        Assert.Empty(manifest.Verify(data));

        File.AppendAllText(Path.Combine(data, "extra.ndjson"), "\n");
        File.Delete(Path.Combine(data, "practitioners.json"));
        File.WriteAllText(Path.Combine(data, "new.json"), "{}");

        var problems = manifest.Verify(data);
        Assert.Contains(problems, p => p.Contains("extra.ndjson"));
        Assert.Contains(problems, p => p.Contains("practitioners.json") && p.Contains("missing"));
        Assert.Contains(problems, p => p.Contains("new.json") && p.Contains("unexpected"));
    }

    [Fact]
    public void Guard_DetectsEditedAndDeletedFiles()
    {
        var data = CopyFixtureData();
        var guard = CorpusGuard.Snapshot(data);

        Assert.Empty(guard.Changes());

        File.AppendAllText(Path.Combine(data, "extra.ndjson"), "\n");
        File.Delete(Path.Combine(data, "practitioners.json"));

        var changes = guard.Changes();
        Assert.Contains(changes, c => c.Contains("extra.ndjson"));
        Assert.Contains(changes, c => c.Contains("practitioners.json"));
    }

    [Fact]
    public async Task Fetch_DownloadsVerifiesExtractsAndMarksReadOnly()
    {
        var zipBytes = BuildZip(("synthea/fhir/a.json", "{\"resourceType\":\"Patient\",\"id\":\"a\"}"), ("synthea/README.txt", "x"));
        var corpus = new CorpusDefinition("t", DateTimeOffset.UnixEpoch, "s", "https://example.org/c.zip",
            Convert.ToHexStringLower(SHA256.HashData(zipBytes)), _root);
        using var http = new HttpClient(new StaticHandler(zipBytes));

        await CorpusFetcher.FetchAsync(corpus, http);

        var extracted = Path.Combine(corpus.DataDirectory, "a.json");
        Assert.True(File.Exists(extracted));
        Assert.False(File.Exists(Path.Combine(corpus.DataDirectory, "README.txt")));
        Assert.True(File.GetAttributes(extracted).HasFlag(FileAttributes.ReadOnly));
        Assert.Empty(CorpusManifest.Load(corpus.ManifestPath).Verify(corpus.DataDirectory));
    }

    [Fact]
    public async Task Fetch_RejectsHashMismatch()
    {
        var zipBytes = BuildZip(("a.json", "{}"));
        var corpus = new CorpusDefinition("t", DateTimeOffset.UnixEpoch, "s", "https://example.org/c.zip", new string('0', 64), _root);
        using var http = new HttpClient(new StaticHandler(zipBytes));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => CorpusFetcher.FetchAsync(corpus, http));

        Assert.Contains("sha256", ex.Message);
        Assert.False(Directory.Exists(corpus.DataDirectory));
    }

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private sealed class StaticHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter CorpusFilesTests`
Expected: build FAIL, `The type or namespace name 'CorpusManifest' could not be found`.

- [ ] **Step 3: Implement the manifest**

`src/FhirAgentBench/Corpus/CorpusManifest.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirAgentBench.Corpus;

public sealed record CorpusFile(string Path, long Size, string Sha256);

public sealed record CorpusManifest(IReadOnlyList<CorpusFile> Files)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [JsonIgnore]
    public string Sha256 =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Options))));

    public static CorpusManifest Create(string dataDirectory) =>
        new(EnumerateData(dataDirectory)
            .Select(path => new CorpusFile(Relative(dataDirectory, path), new FileInfo(path).Length, HashFile(path)))
            .ToList());

    public static CorpusManifest Load(string path) =>
        JsonSerializer.Deserialize<CorpusManifest>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Corpus manifest '{path}' is empty.");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options) + "\n");

    public IReadOnlyList<string> Verify(string dataDirectory)
    {
        var problems = new List<string>();
        var expected = Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var path in EnumerateData(dataDirectory))
        {
            var relative = Relative(dataDirectory, path);
            if (!expected.Remove(relative, out var file))
            {
                problems.Add($"{relative}: unexpected file");
            }
            else if (new FileInfo(path).Length != file.Size || HashFile(path) != file.Sha256)
            {
                problems.Add($"{relative}: content differs from manifest");
            }
        }

        problems.AddRange(expected.Keys.Select(missing => $"{missing}: missing"));
        return problems;
    }

    internal static IEnumerable<string> EnumerateData(string dataDirectory) =>
        Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    private static string Relative(string root, string path) =>
        System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
```

- [ ] **Step 4: Implement fetch and guard**

`src/FhirAgentBench/Corpus/CorpusFetcher.cs`:
```csharp
using System.IO.Compression;
using System.Security.Cryptography;

namespace FhirAgentBench.Corpus;

public static class CorpusFetcher
{
    public static async Task<string> FetchAsync(CorpusDefinition corpus, HttpClient http, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(corpus.DataDirectory) && File.Exists(corpus.ManifestPath)
            && CorpusManifest.Load(corpus.ManifestPath).Verify(corpus.DataDirectory).Count == 0)
        {
            return $"Corpus '{corpus.Id}' is already present and matches its manifest.";
        }

        var zipPath = Path.Combine(corpus.Directory, "source.zip.partial");
        try
        {
            await using (var file = File.Create(zipPath))
            await using (var download = await http.GetStreamAsync(corpus.SourceUrl, cancellationToken).ConfigureAwait(false))
            {
                await download.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            string actual;
            await using (var file = File.OpenRead(zipPath))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            }

            if (!string.Equals(actual, corpus.SourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Downloaded corpus sha256 {actual} does not match corpus.yaml ({corpus.SourceSha256}). The source changed or the download is corrupt.");
            }

            Extract(zipPath, corpus.DataDirectory);
        }
        finally
        {
            File.Delete(zipPath);
        }

        var manifest = CorpusManifest.Create(corpus.DataDirectory);
        manifest.Save(corpus.ManifestPath);
        return $"Fetched corpus '{corpus.Id}': {manifest.Files.Count} files.";
    }

    private static void Extract(string zipPath, string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                                      || e.Name.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase)))
        {
            var target = Path.Combine(dataDirectory, entry.Name);
            entry.ExtractToFile(target, overwrite: true);
            File.SetAttributes(target, FileAttributes.ReadOnly);
        }
    }
}
```

`src/FhirAgentBench/Corpus/CorpusGuard.cs`:
```csharp
namespace FhirAgentBench.Corpus;

public sealed class CorpusGuard
{
    private readonly string _dataDirectory;
    private readonly Dictionary<string, (long Size, DateTime LastWriteUtc)> _files;

    private CorpusGuard(string dataDirectory, Dictionary<string, (long, DateTime)> files)
    {
        _dataDirectory = dataDirectory;
        _files = files;
    }

    public static CorpusGuard Snapshot(string dataDirectory) => new(dataDirectory, Read(dataDirectory));

    public IReadOnlyList<string> Changes()
    {
        var current = Read(_dataDirectory);
        var changed = _files.Where(f => !current.TryGetValue(f.Key, out var now) || now != f.Value).Select(f => f.Key);
        var added = current.Keys.Where(path => !_files.ContainsKey(path));
        return changed.Concat(added).ToList();
    }

    private static Dictionary<string, (long, DateTime)> Read(string dataDirectory) =>
        CorpusManifest.EnumerateData(dataDirectory).ToDictionary(
            path => path,
            path =>
            {
                var info = new FileInfo(path);
                return (info.Length, info.LastWriteTimeUtc);
            },
            StringComparer.Ordinal);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter CorpusFilesTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/FhirAgentBench/Corpus tests/FhirAgentBench.Tests/CorpusFilesTests.cs
git commit -m "feat: fetch pinned corpus with manifest and tamper guard"
```

---

### Task 7: Run orchestration, baseline harnesses and grading

**Files:**
- Create: `src/FhirAgentBench/Execution/IAgentHarness.cs`, `PromptRenderer.cs`, `BaselineHarnesses.cs`, `RunOrchestrator.cs`, `RunGrader.cs`, `JsonOptions.cs`
- Create: `prompts/default.md`
- Test: `tests/FhirAgentBench.Tests/RunPipelineTests.cs`

**Interfaces:**
- Consumes: `Question` (Task 3), `CorpusDefinition` (Task 3), `CorpusGuard` (Task 6), `OracleRegistry` and `OracleContext` (Task 4), `Answer`, `AnswerJson`, `Grader`, `AnswerSpecDescriber` (Task 2).
- Produces:
  - `record AttemptRequest(Question Question, int Repeat, string AttemptDirectory, string WorkspaceDirectory, string Prompt)`
  - `enum AttemptEnd { Completed, TimedOut, Failed }`
  - `record AttemptOutcome(AttemptEnd End, TimeSpan Duration, string? Detail)`
  - `interface IAgentHarness { Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default); }`
  - `OracleHarness(OracleRegistry, OracleContext)` and `AbstainHarness()`
  - `PromptRenderer.Render(string template, Question question, CorpusDefinition corpus) → string`
  - `record AttemptRecord(string QuestionId, int Repeat, AttemptEnd End, double DurationSeconds, string? Detail, string AttemptDirectory)`
  - `RunOrchestrator(IAgentHarness harness, CorpusDefinition corpus, string promptTemplate).RunAsync(IReadOnlyList<Question>, int repeats, string runDirectory, CancellationToken) → Task<IReadOnlyList<AttemptRecord>>`
  - `record GradeRecord(string QuestionId, int Tier, int Repeat, GradeStatus Status, double? EvidenceRecall)`
  - `RunGrader(OracleRegistry, OracleContext).Grade(IReadOnlyList<Question>, IReadOnlyList<AttemptRecord>) → IReadOnlyList<GradeRecord>`
  - `JsonOptions.Default` (web defaults + snake_case enum strings)

- [ ] **Step 1: Create the default prompt template**

`prompts/default.md`:
```markdown
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
```

- [ ] **Step 2: Write the failing tests**

`tests/FhirAgentBench.Tests/RunPipelineTests.cs`:
```csharp
using System.Text;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Execution;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class RunPipelineTests : IDisposable
{
    private static readonly OracleContext Context = new(Fixture.Index, Fixture.Now, Fixture.MrnSystem);
    private static readonly OracleRegistry Oracles = OracleRegistry.CreateDefault();
    private static readonly CorpusDefinition Corpus = new("fixture", Fixture.Now, Fixture.MrnSystem, "u", "h",
        Path.GetDirectoryName(Fixture.CorpusDirectory)!);

    private readonly string _runDirectory = Directory.CreateTempSubdirectory("fab-run-").FullName;

    public void Dispose() => Directory.Delete(_runDirectory, recursive: true);

    private static readonly Question LatestA1c = new()
    {
        Id = "t1-a1c",
        Tier = 1,
        Template = "latest-lab",
        Persona = "primary-care",
        Text = "When was her last A1c, and what was it?",
        Patient = new PatientContext("MRN-ADA", "Ada Lovelace", "1960-03-01"),
        Answer = new AnswerSpec
        {
            Type = AnswerType.Composite,
            Fields = new Dictionary<string, AnswerSpec>
            {
                ["value"] = new() { Type = AnswerType.Quantity, Unit = "%", Tolerance = 0.05m },
                ["date"] = new() { Type = AnswerType.Date },
            },
        },
        Oracle = new OracleCall("latest-observation", new OracleParameters(new Dictionary<string, object> { ["loinc"] = "4548-4" })),
        SourcePath = "t1-a1c.yaml",
    };

    private static readonly Question Pharmacy = new()
    {
        Id = "t7-pharmacy",
        Tier = 7,
        Template = "out-of-record",
        Persona = "pharmacist",
        Text = "What's her home pharmacy's phone number?",
        Patient = new PatientContext("MRN-ADA", "Ada Lovelace", "1960-03-01"),
        Answer = new AnswerSpec { Type = AnswerType.Identifier },
        Oracle = new OracleCall("unanswerable", OracleParameters.Empty),
        SourcePath = "t7-pharmacy.yaml",
    };

    [Fact]
    public void Prompt_ContainsContextQuestionAndShape_ButNoGold()
    {
        var prompt = PromptRenderer.Render(File.ReadAllText(PromptPath()), LatestA1c, Corpus);

        Assert.Contains("Ada Lovelace", prompt);
        Assert.Contains("MRN-ADA", prompt);
        Assert.Contains("2026-01-15", prompt);
        Assert.Contains("When was her last A1c", prompt);
        Assert.Contains("\"unit\": \"%\"", prompt);
        Assert.DoesNotContain("9.4", prompt);
        Assert.DoesNotContain("{{", prompt);
    }

    [Fact]
    public async Task OracleHarness_ScoresEveryQuestionCorrect()
    {
        var grades = await RunAsync(new OracleHarness(Oracles, Context), repeats: 2);

        Assert.Equal(4, grades.Count);
        Assert.All(grades, g => Assert.Equal(GradeStatus.Correct, g.Status));
    }

    [Fact]
    public async Task AbstainHarness_IsCorrectOnlyOnTier7()
    {
        var grades = await RunAsync(new AbstainHarness(), repeats: 1);

        Assert.Equal(GradeStatus.Abstained, grades.Single(g => g.QuestionId == "t1-a1c").Status);
        Assert.Equal(GradeStatus.Correct, grades.Single(g => g.QuestionId == "t7-pharmacy").Status);
    }

    [Fact]
    public async Task Grader_HandlesMissingBomAndTimedOutAnswers()
    {
        var harness = new ScriptedHarness(new Dictionary<int, (AttemptEnd, string?)>
        {
            [1] = (AttemptEnd.Completed, null),
            [2] = (AttemptEnd.Completed, "\uFEFF{\"status\":\"answered\",\"answer\":{\"value\":{\"value\":9.4,\"unit\":\"%\"},\"date\":\"2025-12-01\"},\"evidence\":[\"Observation/o-ada-a1c-2\"]}"),
            [3] = (AttemptEnd.TimedOut, null),
            [4] = (AttemptEnd.Failed, null),
        });

        var grades = await RunAsync(harness, repeats: 4, [LatestA1c]);

        Assert.Equal(
            new[] { GradeStatus.FormatInvalid, GradeStatus.Correct, GradeStatus.TurnLimit, GradeStatus.HarnessError },
            grades.Select(g => g.Status));
        Assert.Equal(1.0, grades[1].EvidenceRecall);
    }

    [Fact]
    public async Task Orchestrator_WritesPromptAndAttemptLog()
    {
        await RunAsync(new AbstainHarness(), repeats: 1, [LatestA1c]);

        Assert.True(File.Exists(Path.Combine(_runDirectory, "attempts", "t1-a1c", "r1", "prompt.md")));
        Assert.Single(File.ReadAllLines(Path.Combine(_runDirectory, "attempts.jsonl")));
    }

    private async Task<IReadOnlyList<GradeRecord>> RunAsync(IAgentHarness harness, int repeats, IReadOnlyList<Question>? questions = null)
    {
        questions ??= [LatestA1c, Pharmacy];
        var orchestrator = new RunOrchestrator(harness, Corpus, File.ReadAllText(PromptPath()));
        var attempts = await orchestrator.RunAsync(questions, repeats, _runDirectory);
        return new RunGrader(Oracles, Context).Grade(questions, attempts);
    }

    private static string PromptPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "prompts", "default.md")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory.FullName, "prompts", "default.md");
    }

    private sealed class ScriptedHarness(Dictionary<int, (AttemptEnd End, string? AnswerJson)> script) : IAgentHarness
    {
        public Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default)
        {
            var (end, answer) = script[request.Repeat];
            if (answer is not null)
            {
                File.WriteAllText(Path.Combine(request.WorkspaceDirectory, "answer.json"), answer, new UTF8Encoding(false));
            }

            return Task.FromResult(new AttemptOutcome(end, TimeSpan.FromSeconds(1), null));
        }
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter RunPipelineTests`
Expected: build FAIL, `The type or namespace name 'IAgentHarness' could not be found`.

- [ ] **Step 4: Implement the harness contract and prompt renderer**

`src/FhirAgentBench/Execution/IAgentHarness.cs`:
```csharp
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public sealed record AttemptRequest(Question Question, int Repeat, string AttemptDirectory, string WorkspaceDirectory, string Prompt);

public enum AttemptEnd
{
    Completed,
    TimedOut,
    Failed,
}

public sealed record AttemptOutcome(AttemptEnd End, TimeSpan Duration, string? Detail);

public interface IAgentHarness
{
    Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default);
}
```

`src/FhirAgentBench/Execution/PromptRenderer.cs`:
```csharp
using System.Globalization;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public static class PromptRenderer
{
    public static string Render(string template, Question question, CorpusDefinition corpus)
    {
        var patient = question.Patient is { } p
            ? $"The patient is {p.Name}, born {p.BirthDate}, MRN {p.Mrn}."
            : "The question is about all patients in the folder (the clinician's panel).";
        return template
            .Replace("{{corpus_dir}}", corpus.DataDirectory, StringComparison.Ordinal)
            .Replace("{{now}}", corpus.Now.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{patient}}", patient, StringComparison.Ordinal)
            .Replace("{{persona}}", question.Persona, StringComparison.Ordinal)
            .Replace("{{question}}", question.Text, StringComparison.Ordinal)
            .Replace("{{answer_shape}}", AnswerSpecDescriber.Describe(question.Answer), StringComparison.Ordinal);
    }
}
```

`src/FhirAgentBench/Execution/BaselineHarnesses.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;

namespace FhirAgentBench.Execution;

public sealed class OracleHarness(OracleRegistry oracles, OracleContext context) : IAgentHarness
{
    public async Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default)
    {
        var gold = oracles.Compute(context, request.Question);
        await File.WriteAllTextAsync(Path.Combine(request.WorkspaceDirectory, "answer.json"), AnswerJson.Serialize(gold), cancellationToken)
            .ConfigureAwait(false);
        return new AttemptOutcome(AttemptEnd.Completed, TimeSpan.Zero, null);
    }
}

public sealed class AbstainHarness : IAgentHarness
{
    public async Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(Path.Combine(request.WorkspaceDirectory, "answer.json"), AnswerJson.Serialize(Answer.Insufficient()), cancellationToken)
            .ConfigureAwait(false);
        return new AttemptOutcome(AttemptEnd.Completed, TimeSpan.Zero, null);
    }
}
```

- [ ] **Step 5: Implement orchestration and grading**

`src/FhirAgentBench/Execution/JsonOptions.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FhirAgentBench.Execution;

public static class JsonOptions
{
    public static JsonSerializerOptions Default { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
```

`src/FhirAgentBench/Execution/RunOrchestrator.cs`:
```csharp
using System.Text.Json;
using FhirAgentBench.Corpus;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public sealed record AttemptRecord(string QuestionId, int Repeat, AttemptEnd End, double DurationSeconds, string? Detail, string AttemptDirectory);

public sealed class RunOrchestrator(IAgentHarness harness, CorpusDefinition corpus, string promptTemplate)
{
    public async Task<IReadOnlyList<AttemptRecord>> RunAsync(
        IReadOnlyList<Question> questions, int repeats, string runDirectory, CancellationToken cancellationToken = default)
    {
        var guard = CorpusGuard.Snapshot(corpus.DataDirectory);
        var log = Path.Combine(runDirectory, "attempts.jsonl");
        var records = new List<AttemptRecord>();
        foreach (var question in questions)
        {
            for (var repeat = 1; repeat <= repeats; repeat++)
            {
                var attemptDirectory = Path.Combine(runDirectory, "attempts", question.Id, $"r{repeat}");
                var workspace = Directory.CreateDirectory(Path.Combine(attemptDirectory, "workspace")).FullName;
                var prompt = PromptRenderer.Render(promptTemplate, question, corpus);
                await File.WriteAllTextAsync(Path.Combine(attemptDirectory, "prompt.md"), prompt, cancellationToken).ConfigureAwait(false);

                var outcome = await harness.RunAsync(new AttemptRequest(question, repeat, attemptDirectory, workspace, prompt), cancellationToken)
                    .ConfigureAwait(false);

                var changes = guard.Changes();
                if (changes.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Corpus files changed during attempt {question.Id}/r{repeat}: {string.Join(", ", changes.Take(5))}. " +
                        "Run 'fab corpus verify' and re-fetch the corpus before running again.");
                }

                var record = new AttemptRecord(question.Id, repeat, outcome.End, outcome.Duration.TotalSeconds, outcome.Detail, attemptDirectory);
                records.Add(record);
                await File.AppendAllTextAsync(log, JsonSerializer.Serialize(record, JsonOptions.Default) + "\n", cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return records;
    }
}
```

`src/FhirAgentBench/Execution/RunGrader.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Execution;

public sealed record GradeRecord(string QuestionId, int Tier, int Repeat, GradeStatus Status, double? EvidenceRecall);

public sealed class RunGrader(OracleRegistry oracles, OracleContext context)
{
    public IReadOnlyList<GradeRecord> Grade(IReadOnlyList<Question> questions, IReadOnlyList<AttemptRecord> attempts)
    {
        var byId = questions.ToDictionary(q => q.Id, StringComparer.Ordinal);
        var gold = new Dictionary<string, Answer>(StringComparer.Ordinal);
        var grades = new List<GradeRecord>();
        foreach (var attempt in attempts)
        {
            var question = byId[attempt.QuestionId];
            if (!gold.TryGetValue(question.Id, out var expected))
            {
                gold[question.Id] = expected = oracles.Compute(context, question);
            }

            grades.Add(GradeOne(question, expected, attempt));
        }

        return grades;
    }

    private static GradeRecord GradeOne(Question question, Answer gold, AttemptRecord attempt)
    {
        if (attempt.End != AttemptEnd.Completed)
        {
            var status = attempt.End == AttemptEnd.TimedOut ? GradeStatus.TurnLimit : GradeStatus.HarnessError;
            return new GradeRecord(question.Id, question.Tier, attempt.Repeat, status, null);
        }

        var agent = ReadAnswer(Path.Combine(attempt.AttemptDirectory, "workspace", "answer.json"));
        if (agent is null)
        {
            return new GradeRecord(question.Id, question.Tier, attempt.Repeat, GradeStatus.FormatInvalid, null);
        }

        return new GradeRecord(question.Id, question.Tier, attempt.Repeat, Grader.Grade(question.Answer, gold, agent), EvidenceRecall(gold, agent));
    }

    private static Answer? ReadAnswer(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return AnswerJson.Parse(File.ReadAllText(path));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static double? EvidenceRecall(Answer gold, Answer agent) =>
        gold.Evidence.Count == 0
            ? null
            : gold.Evidence.Count(e => agent.Evidence.Contains(e, StringComparer.Ordinal)) / (double)gold.Evidence.Count;
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter RunPipelineTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add prompts src/FhirAgentBench/Execution tests/FhirAgentBench.Tests/RunPipelineTests.cs
git commit -m "feat: orchestrate attempts with oracle and abstain baselines and grade them"
```

---

### Task 8: Run report and paired comparison

**Files:**
- Create: `src/FhirAgentBench/Reporting/McNemar.cs`, `RunReport.cs`
- Test: `tests/FhirAgentBench.Tests/ReportTests.cs`

**Interfaces:**
- Consumes: `GradeRecord`, `GradeStatus` (Tasks 2, 7).
- Produces:
  - `McNemar.ExactTwoSided(int onlyA, int onlyB) → double`
  - `record RunSummary(string RunId, string ProfileId, int Attempts, IReadOnlyDictionary<GradeStatus, int> ByStatus, double Accuracy, IReadOnlyDictionary<int, double> AccuracyByTier)`
  - `record PairedComparison(int Pairs, int BothCorrect, int OnlyA, int OnlyB, int BothWrong, double AccuracyDifference, double McNemarP)`
  - `RunReport.Summarize(string runId, string profileId, IReadOnlyList<GradeRecord>) → RunSummary`
  - `RunReport.Compare(IReadOnlyList<GradeRecord> a, IReadOnlyList<GradeRecord> b) → PairedComparison`
  - `RunReport.ToMarkdown(RunSummary)`, `RunReport.ToMarkdown(RunSummary a, RunSummary b, PairedComparison)`

- [ ] **Step 1: Write the failing tests**

`tests/FhirAgentBench.Tests/ReportTests.cs`:
```csharp
using FhirAgentBench.Answers;
using FhirAgentBench.Execution;
using FhirAgentBench.Reporting;

namespace FhirAgentBench.Tests;

public sealed class ReportTests
{
    [Theory]
    [InlineData(0, 0, 1.0)]
    [InlineData(0, 10, 0.001953125)]
    [InlineData(2, 8, 0.109375)]
    [InlineData(5, 5, 1.0)]
    public void McNemar_ExactTwoSided(int onlyA, int onlyB, double expected)
    {
        Assert.Equal(expected, McNemar.ExactTwoSided(onlyA, onlyB), precision: 9);
    }

    private static GradeRecord G(string id, int tier, int repeat, GradeStatus status) => new(id, tier, repeat, status, null);

    [Fact]
    public void Summarize_ExcludesHarnessErrorsFromAccuracy()
    {
        var grades = new[]
        {
            G("a", 1, 1, GradeStatus.Correct),
            G("b", 1, 1, GradeStatus.Wrong),
            G("c", 7, 1, GradeStatus.Correct),
            G("d", 7, 1, GradeStatus.HarnessError),
        };

        var summary = RunReport.Summarize("r", "p", grades);

        Assert.Equal(2.0 / 3, summary.Accuracy, precision: 9);
        Assert.Equal(0.5, summary.AccuracyByTier[1]);
        Assert.Equal(1.0, summary.AccuracyByTier[7]);
        Assert.Equal(1, summary.ByStatus[GradeStatus.HarnessError]);
    }

    [Fact]
    public void Compare_PairsByQuestionAndRepeat()
    {
        var a = new[] { G("q1", 1, 1, GradeStatus.Correct), G("q2", 1, 1, GradeStatus.Correct), G("q3", 1, 1, GradeStatus.Wrong), G("q4", 1, 1, GradeStatus.HarnessError) };
        var b = new[] { G("q1", 1, 1, GradeStatus.Correct), G("q2", 1, 1, GradeStatus.Wrong), G("q3", 1, 1, GradeStatus.Abstained), G("q4", 1, 1, GradeStatus.Correct) };

        var comparison = RunReport.Compare(a, b);

        Assert.Equal(3, comparison.Pairs);
        Assert.Equal((1, 1, 0, 1), (comparison.BothCorrect, comparison.OnlyA, comparison.OnlyB, comparison.BothWrong));
        Assert.Equal(1.0 / 3, comparison.AccuracyDifference, precision: 9);
    }

    [Fact]
    public void Markdown_ShowsProfileAndAccuracy()
    {
        var summary = RunReport.Summarize("run-1", "p1-shell", [G("a", 1, 1, GradeStatus.Correct)]);

        var markdown = RunReport.ToMarkdown(summary);

        Assert.Contains("p1-shell", markdown);
        Assert.Contains("100.0%", markdown);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter ReportTests`
Expected: build FAIL, `The type or namespace name 'Reporting' does not exist`.

- [ ] **Step 3: Implement McNemar and the report**

`src/FhirAgentBench/Reporting/McNemar.cs`:
```csharp
namespace FhirAgentBench.Reporting;

public static class McNemar
{
    public static double ExactTwoSided(int onlyA, int onlyB)
    {
        var n = onlyA + onlyB;
        if (n == 0)
        {
            return 1.0;
        }

        var term = Math.Pow(0.5, n);
        var tail = 0.0;
        for (var i = 0; i <= Math.Min(onlyA, onlyB); i++)
        {
            tail += term;
            term = term * (n - i) / (i + 1);
        }

        return Math.Min(1.0, 2 * tail);
    }
}
```

`src/FhirAgentBench/Reporting/RunReport.cs`:
```csharp
using System.Globalization;
using System.Text;
using FhirAgentBench.Answers;
using FhirAgentBench.Execution;

namespace FhirAgentBench.Reporting;

public sealed record RunSummary(
    string RunId,
    string ProfileId,
    int Attempts,
    IReadOnlyDictionary<GradeStatus, int> ByStatus,
    double Accuracy,
    IReadOnlyDictionary<int, double> AccuracyByTier);

public sealed record PairedComparison(int Pairs, int BothCorrect, int OnlyA, int OnlyB, int BothWrong, double AccuracyDifference, double McNemarP);

public static class RunReport
{
    public static RunSummary Summarize(string runId, string profileId, IReadOnlyList<GradeRecord> grades)
    {
        var scored = grades.Where(g => g.Status != GradeStatus.HarnessError).ToList();
        return new RunSummary(
            runId,
            profileId,
            grades.Count,
            grades.GroupBy(g => g.Status).ToDictionary(g => g.Key, g => g.Count()),
            AccuracyOf(scored),
            scored.GroupBy(g => g.Tier).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => AccuracyOf(g.ToList())));
    }

    public static PairedComparison Compare(IReadOnlyList<GradeRecord> a, IReadOnlyList<GradeRecord> b)
    {
        var right = b.Where(g => g.Status != GradeStatus.HarnessError).ToDictionary(g => (g.QuestionId, g.Repeat));
        var pairs = a.Where(g => g.Status != GradeStatus.HarnessError && right.ContainsKey((g.QuestionId, g.Repeat)))
            .Select(g => (A: g.Status == GradeStatus.Correct, B: right[(g.QuestionId, g.Repeat)].Status == GradeStatus.Correct))
            .ToList();
        var bothCorrect = pairs.Count(p => p.A && p.B);
        var onlyA = pairs.Count(p => p.A && !p.B);
        var onlyB = pairs.Count(p => !p.A && p.B);
        var difference = pairs.Count == 0 ? 0 : (onlyA - onlyB) / (double)pairs.Count;
        return new PairedComparison(pairs.Count, bothCorrect, onlyA, onlyB, pairs.Count - bothCorrect - onlyA - onlyB, difference, McNemar.ExactTwoSided(onlyA, onlyB));
    }

    public static string ToMarkdown(RunSummary summary)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"## {summary.ProfileId} ({summary.RunId})");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Accuracy: **{Percent(summary.Accuracy)}** over {summary.Attempts} attempts (harness errors excluded).");
        text.AppendLine();
        text.AppendLine("| Status | Attempts |");
        text.AppendLine("|---|---|");
        foreach (var (status, count) in summary.ByStatus.OrderBy(s => s.Key))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| {status} | {count} |");
        }

        text.AppendLine();
        text.AppendLine("| Tier | Accuracy |");
        text.AppendLine("|---|---|");
        foreach (var (tier, accuracy) in summary.AccuracyByTier)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"| T{tier} | {Percent(accuracy)} |");
        }

        return text.ToString();
    }

    public static string ToMarkdown(RunSummary a, RunSummary b, PairedComparison comparison)
    {
        var text = new StringBuilder();
        text.AppendLine(ToMarkdown(a));
        text.AppendLine(ToMarkdown(b));
        text.AppendLine(CultureInfo.InvariantCulture, $"## Paired: {a.ProfileId} vs {b.ProfileId}");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"{comparison.Pairs} pairs. Both correct {comparison.BothCorrect}, only {a.ProfileId} {comparison.OnlyA}, only {b.ProfileId} {comparison.OnlyB}, both wrong {comparison.BothWrong}.");
        text.AppendLine(CultureInfo.InvariantCulture, $"Accuracy difference ({a.ProfileId} − {b.ProfileId}): {Percent(comparison.AccuracyDifference)}. Exact McNemar p = {comparison.McNemarP:0.000}. Phase 0 sample sizes are descriptive, not conclusive.");
        return text.ToString();
    }

    private static double AccuracyOf(IReadOnlyList<GradeRecord> grades) =>
        grades.Count == 0 ? 0 : grades.Count(g => g.Status == GradeStatus.Correct) / (double)grades.Count;

    private static string Percent(double value) => value.ToString("0.0%", CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter ReportTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FhirAgentBench/Reporting tests/FhirAgentBench.Tests/ReportTests.cs
git commit -m "feat: summarise runs and compare profiles pairwise"
```

---

### Task 9: Copilot CLI harness

**Files:**
- Create: `src/FhirAgentBench/Execution/ProcessRunner.cs`, `McpConfig.cs`, `CopilotArguments.cs`, `CopilotCliHarness.cs`, `HarnessFactory.cs`
- Test: `tests/FhirAgentBench.Tests/CopilotHarnessTests.cs`

**Interfaces:**
- Consumes: `Profile`, `McpServer` (Task 3); `AttemptRequest`, `AttemptOutcome`, `IAgentHarness`, `OracleHarness`, `AbstainHarness` (Task 7); `CorpusDefinition` (Task 3); `OracleRegistry`, `OracleContext` (Task 4).
- Produces:
  - `record ProcessResult(int ExitCode, bool TimedOut, TimeSpan Duration)`
  - `ProcessRunner.RunAsync(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string,string> environment, string workingDirectory, string stdoutPath, string stderrPath, TimeSpan timeout, CancellationToken cancellationToken = default)`
  - `ProcessRunner.CaptureAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) → Task<string>`
  - `McpConfig.Write(Profile profile, string corpusDataDirectory, string path)`, which throws `InvalidOperationException` when `${VAR}` is unset
  - `CopilotArguments.Build(Profile profile, AttemptRequest request, string corpusDataDirectory, string? mcpConfigPath) → IReadOnlyList<string>`
  - `CopilotCliHarness(Profile profile, string corpusDataDirectory, string token)`
  - `HarnessFactory.CreateAsync(Profile, CorpusDefinition, OracleRegistry, OracleContext, CancellationToken) → Task<IAgentHarness>`

- [ ] **Step 1: Write the failing tests**

`tests/FhirAgentBench.Tests/CopilotHarnessTests.cs`:
```csharp
using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Execution;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class CopilotHarnessTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fab-copilot-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static Profile Shell(params (string Name, McpServer Server)[] servers) => new()
    {
        Id = "p",
        Harness = Profile.CopilotCli,
        Model = "claude-sonnet-5.5",
        BuiltinTools = ["view", "rg", "glob", "powershell", "read_powershell", "stop_powershell", "apply_patch"],
        McpServers = servers.ToDictionary(s => s.Name, s => s.Server),
        PromptTemplatePath = "prompt.md",
        MaxAiCredits = 25,
        SourcePath = "p.yaml",
    };

    private AttemptRequest Request() => new(
        new Question
        {
            Id = "q", Tier = 1, Template = "t", Persona = "nursing", Text = "?",
            Answer = new AnswerSpec { Type = AnswerType.Integer },
            Oracle = new OracleCall("unanswerable", OracleParameters.Empty), SourcePath = "q.yaml",
        },
        1, _directory, Path.Combine(_directory, "workspace"), "the prompt");

    [Fact]
    public void Arguments_AreLockedDown()
    {
        var args = CopilotArguments.Build(Shell(), Request(), @"C:\corpus\data", mcpConfigPath: null);

        Assert.Equal(new[] { "-C", Path.Combine(_directory, "workspace"), "-p", "the prompt", "--model", "claude-sonnet-5.5" }, args.Take(6));
        Assert.Contains("--no-custom-instructions", args);
        Assert.Contains("--disable-builtin-mcps", args);
        Assert.Equal(@"C:\corpus\data", args[args.ToList().IndexOf("--add-dir") + 1]);
        Assert.Equal("25", args[args.ToList().IndexOf("--max-ai-credits") + 1]);
        Assert.Equal("COPILOT_GITHUB_TOKEN", args[args.ToList().IndexOf("--secret-env-vars") + 1]);
        Assert.DoesNotContain(args, a => a is "--allow-all" or "--yolo" or "--allow-all-paths" or "--allow-all-urls");
        Assert.DoesNotContain("--additional-mcp-config", args);
    }

    [Fact]
    public void Arguments_IncludePrefixedMcpToolsAndConfig()
    {
        var profile = Shell(("fhir", new McpServer("cli-on-fhir", ["mcp", "{corpus}"], ["search_fhir_resources", "read_resources"])));

        var args = CopilotArguments.Build(profile, Request(), "/data", Path.Combine(_directory, "mcp.json")).ToList();

        Assert.Contains("fhir-search_fhir_resources", args);
        Assert.Contains("fhir-read_resources", args);
        Assert.Equal("@" + Path.Combine(_directory, "mcp.json"), args[args.IndexOf("--additional-mcp-config") + 1]);
    }

    [Fact]
    public void McpConfig_ExpandsCorpusAndEnvironment()
    {
        Environment.SetEnvironmentVariable("FAB_TEST_TOOL", @"C:\tools\cli-on-fhir.exe");
        var profile = Shell(("fhir", new McpServer("${FAB_TEST_TOOL}", ["mcp", "{corpus}"], ["search_fhir_resources"])));
        var path = Path.Combine(_directory, "mcp.json");

        McpConfig.Write(profile, @"C:\corpus\data", path);

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var server = json.RootElement.GetProperty("mcpServers").GetProperty("fhir");
        Assert.Equal(@"C:\tools\cli-on-fhir.exe", server.GetProperty("command").GetString());
        Assert.Equal(@"C:\corpus\data", server.GetProperty("args")[1].GetString());
    }

    [Fact]
    public void McpConfig_UnsetVariable_NamesTheVariable()
    {
        var profile = Shell(("fhir", new McpServer("${FAB_DEFINITELY_UNSET}", [], ["x"])));

        var ex = Assert.Throws<InvalidOperationException>(() => McpConfig.Write(profile, "/data", Path.Combine(_directory, "mcp.json")));

        Assert.Contains("FAB_DEFINITELY_UNSET", ex.Message);
    }

    [Fact]
    public async Task ProcessRunner_CapturesExitCodeAndOutput()
    {
        var stdout = Path.Combine(_directory, "out.txt");

        var result = await ProcessRunner.RunAsync("dotnet", ["--version"], new Dictionary<string, string>(), _directory, stdout, Path.Combine(_directory, "err.txt"), TimeSpan.FromMinutes(1));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Matches(@"\d+\.\d+", File.ReadAllText(stdout));
    }

    [Fact]
    public async Task ProcessRunner_TimeoutKillsProcessTree()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "ping -n 30 127.0.0.1 > nul" })
            : ("sh", new[] { "-c", "sleep 30" });

        var result = await ProcessRunner.RunAsync(file, args, new Dictionary<string, string>(), _directory,
            Path.Combine(_directory, "out.txt"), Path.Combine(_directory, "err.txt"), TimeSpan.FromSeconds(2));

        Assert.True(result.TimedOut);
        Assert.True(result.Duration < TimeSpan.FromSeconds(20));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter CopilotHarnessTests`
Expected: build FAIL, `The name 'CopilotArguments' does not exist`.

- [ ] **Step 3: Implement the process runner**

`src/FhirAgentBench/Execution/ProcessRunner.cs`:
```csharp
using System.Diagnostics;

namespace FhirAgentBench.Execution;

public sealed record ProcessResult(int ExitCode, bool TimedOut, TimeSpan Duration);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string workingDirectory,
        string stdoutPath,
        string stderrPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            info.Environment[name] = value;
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        process.StandardInput.Close();
        await using var stdout = File.Create(stdoutPath);
        await using var stderr = File.Create(stderrPath);
        var copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdout, CancellationToken.None);
        var copyErr = process.StandardError.BaseStream.CopyToAsync(stderr, CancellationToken.None);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        await Task.WhenAll(copyOut, copyErr).ConfigureAwait(false);
        return new ProcessResult(timedOut ? -1 : process.ExitCode, timedOut, stopwatch.Elapsed);
    }

    public static async Task<string> CaptureAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{fileName} {string.Join(' ', arguments)}' exited with code {process.ExitCode}.");
        }

        return output.Trim();
    }
}
```

- [ ] **Step 4: Implement MCP config and Copilot arguments**

`src/FhirAgentBench/Execution/McpConfig.cs`:
```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FhirAgentBench.Execution;

public static partial class McpConfig
{
    public static void Write(Profile profile, string corpusDataDirectory, string path)
    {
        var servers = profile.McpServers.ToDictionary(
            s => s.Key,
            s => new
            {
                type = "local",
                command = Expand(s.Value.Command, corpusDataDirectory, profile),
                args = s.Value.Args.Select(a => Expand(a, corpusDataDirectory, profile)).ToList(),
                tools = new[] { "*" },
            });
        File.WriteAllText(path, JsonSerializer.Serialize(new { mcpServers = servers }));
    }

    private static string Expand(string value, string corpusDataDirectory, Profile profile) =>
        EnvironmentReference().Replace(value.Replace("{corpus}", corpusDataDirectory, StringComparison.Ordinal), match =>
            Environment.GetEnvironmentVariable(match.Groups[1].Value)
            ?? throw new InvalidOperationException(
                $"Profile '{profile.Id}' needs environment variable '{match.Groups[1].Value}', which is not set."));

    [GeneratedRegex(@"\$\{([A-Za-z0-9_]+)\}")]
    private static partial Regex EnvironmentReference();
}
```

`src/FhirAgentBench/Execution/CopilotArguments.cs`:
```csharp
using System.Globalization;

namespace FhirAgentBench.Execution;

public static class CopilotArguments
{
    public const string TokenVariable = "COPILOT_GITHUB_TOKEN";

    public static IReadOnlyList<string> Build(Profile profile, AttemptRequest request, string corpusDataDirectory, string? mcpConfigPath)
    {
        var args = new List<string>
        {
            "-C", request.WorkspaceDirectory,
            "-p", request.Prompt,
            "--model", profile.Model!,
        };
        if (profile.ReasoningEffort is { } effort)
        {
            args.AddRange(["--reasoning-effort", effort]);
        }

        args.AddRange(
        [
            "--output-format", "json",
            "--stream", "off",
            "--usage-output-file", Path.Combine(request.AttemptDirectory, "usage.json"),
            "--log-dir", Path.Combine(request.AttemptDirectory, "logs"),
            "--no-custom-instructions", "--no-ask-user", "--no-auto-update", "--no-remote", "--no-remote-export",
            "--disable-builtin-mcps", "--disallow-temp-dir",
            "--add-dir", corpusDataDirectory,
            "--allow-all-tools",
            "--available-tools",
        ]);
        args.AddRange(profile.BuiltinTools);
        args.AddRange(profile.McpServers.SelectMany(s => s.Value.Tools.Select(tool => $"{s.Key}-{tool}")));
        if (mcpConfigPath is not null)
        {
            args.AddRange(["--additional-mcp-config", "@" + mcpConfigPath]);
        }

        args.AddRange(
        [
            "--secret-env-vars", TokenVariable,
            "--max-ai-credits", profile.MaxAiCredits.ToString(CultureInfo.InvariantCulture),
        ]);
        return args;
    }
}
```

- [ ] **Step 5: Implement the harness and factory**

`src/FhirAgentBench/Execution/CopilotCliHarness.cs`:
```csharp
namespace FhirAgentBench.Execution;

public sealed class CopilotCliHarness(Profile profile, string corpusDataDirectory, string token) : IAgentHarness
{
    public async Task<AttemptOutcome> RunAsync(AttemptRequest request, CancellationToken cancellationToken = default)
    {
        string? mcpConfig = null;
        if (profile.McpServers.Count > 0)
        {
            mcpConfig = Path.Combine(request.AttemptDirectory, "mcp.json");
            McpConfig.Write(profile, corpusDataDirectory, mcpConfig);
        }

        var environment = new Dictionary<string, string>
        {
            ["COPILOT_HOME"] = Directory.CreateDirectory(Path.Combine(request.AttemptDirectory, "copilot-home")).FullName,
            [CopilotArguments.TokenVariable] = token,
        };
        var result = await ProcessRunner.RunAsync(
                profile.Executable,
                CopilotArguments.Build(profile, request, corpusDataDirectory, mcpConfig),
                environment,
                request.WorkspaceDirectory,
                Path.Combine(request.AttemptDirectory, "transcript.jsonl"),
                Path.Combine(request.AttemptDirectory, "stderr.txt"),
                TimeSpan.FromMinutes(profile.TimeoutMinutes),
                cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            { TimedOut: true } => new AttemptOutcome(AttemptEnd.TimedOut, result.Duration, $"timed out after {profile.TimeoutMinutes} minutes"),
            { ExitCode: 0 } => new AttemptOutcome(AttemptEnd.Completed, result.Duration, null),
            _ => new AttemptOutcome(AttemptEnd.Failed, result.Duration, $"copilot exited with code {result.ExitCode}; see stderr.txt"),
        };
    }
}
```

`src/FhirAgentBench/Execution/HarnessFactory.cs`:
```csharp
using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;

namespace FhirAgentBench.Execution;

public static class HarnessFactory
{
    public static async Task<IAgentHarness> CreateAsync(
        Profile profile, CorpusDefinition corpus, OracleRegistry oracles, OracleContext context, CancellationToken cancellationToken = default)
    {
        switch (profile.Harness)
        {
            case Profile.Oracle:
                return new OracleHarness(oracles, context);
            case Profile.Abstain:
                return new AbstainHarness();
            case Profile.CopilotCli:
                if (profile.McpServers.Count > 0)
                {
                    var probe = Path.Combine(Path.GetTempPath(), $"fab-mcp-check-{Guid.NewGuid():N}.json");
                    McpConfig.Write(profile, corpus.DataDirectory, probe);
                    File.Delete(probe);
                }

                return new CopilotCliHarness(profile, corpus.DataDirectory, await ResolveTokenAsync(cancellationToken).ConfigureAwait(false));
            default:
                throw new InvalidOperationException($"Profile '{profile.Id}' uses unsupported harness '{profile.Harness}'.");
        }
    }

    private static async Task<string> ResolveTokenAsync(CancellationToken cancellationToken)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(CopilotArguments.TokenVariable)
            ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        return fromEnvironment ?? await ProcessRunner.CaptureAsync("gh", ["auth", "token"], cancellationToken).ConfigureAwait(false);
    }
}
```
The probe `McpConfig.Write` exists to fail fast: an unset `${VAR}` is reported before any attempt starts (Review Focus 3).

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter CopilotHarnessTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/FhirAgentBench/Execution tests/FhirAgentBench.Tests/CopilotHarnessTests.cs
git commit -m "feat: add locked-down Copilot CLI harness"
```

---

### Task 10: `fab` command line

**Files:**
- Create: `src/FhirAgentBench.Cli/FhirAgentBench.Cli.csproj`, `Program.cs`, `CorpusCommands.cs`, `AdmitCommand.cs`, `RunCommand.cs`, `ReportCommand.cs`
- Create: `src/FhirAgentBench/Execution/RunSnapshot.cs`
- Modify: `FhirAgentBench.slnx` (add CLI project)
- Test: `tests/FhirAgentBench.Tests/RunSnapshotTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `record RunSnapshot(string RunId, string ProfileId, string ProfileSha256, string CorpusId, string CorpusManifestSha256, IReadOnlyList<string> QuestionIds, string QuestionsSha256, int Repeats, string? HarnessVersion, string? CliOnFhirCommit, DateTimeOffset StartedUtc)` with `Create(...)`, `WriteAsync(string runDirectory)` and `ReadAsync(string runDirectory)`
  - CLI verbs: `fab corpus fetch|verify|stats|patients|codes`, `fab admit`, `fab run`, `fab report`

- [ ] **Step 1: Write the failing test for the run snapshot**

`tests/FhirAgentBench.Tests/RunSnapshotTests.cs`:
```csharp
using FhirAgentBench.Execution;

namespace FhirAgentBench.Tests;

public sealed class RunSnapshotTests
{
    [Fact]
    public async Task Snapshot_RoundTrips()
    {
        var directory = Directory.CreateTempSubdirectory("fab-snap-").FullName;
        var snapshot = new RunSnapshot("r", "p1", "aa", "c", "bb", ["q1", "q2"], "cc", 3, "GitHub Copilot CLI 1.0.96.", "abc123", DateTimeOffset.UnixEpoch);

        await snapshot.WriteAsync(directory);
        var read = await RunSnapshot.ReadAsync(directory);

        Assert.Equal(snapshot with { QuestionIds = read.QuestionIds }, read);
        Assert.Equal(new[] { "q1", "q2" }, read.QuestionIds);
    }

    [Fact]
    public void HashFiles_ChangesWhenContentChanges()
    {
        var directory = Directory.CreateTempSubdirectory("fab-hash-").FullName;
        var file = Path.Combine(directory, "a.yaml");
        File.WriteAllText(file, "one");
        var first = RunSnapshot.HashFiles([file]);
        File.WriteAllText(file, "two");

        Assert.NotEqual(first, RunSnapshot.HashFiles([file]));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter RunSnapshotTests`
Expected: build FAIL, `The type or namespace name 'RunSnapshot' could not be found`.

- [ ] **Step 3: Implement the snapshot**

`src/FhirAgentBench/Execution/RunSnapshot.cs`:
```csharp
using System.Security.Cryptography;
using System.Text.Json;

namespace FhirAgentBench.Execution;

public sealed record RunSnapshot(
    string RunId,
    string ProfileId,
    string ProfileSha256,
    string CorpusId,
    string CorpusManifestSha256,
    IReadOnlyList<string> QuestionIds,
    string QuestionsSha256,
    int Repeats,
    string? HarnessVersion,
    string? CliOnFhirCommit,
    DateTimeOffset StartedUtc)
{
    private const string FileName = "run.json";

    public static string HashFiles(IEnumerable<string> paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths)
        {
            hash.AppendData(File.ReadAllBytes(path));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public Task WriteAsync(string runDirectory, CancellationToken cancellationToken = default) =>
        File.WriteAllTextAsync(Path.Combine(runDirectory, FileName), JsonSerializer.Serialize(this, JsonOptions.Default), cancellationToken);

    public static async Task<RunSnapshot> ReadAsync(string runDirectory, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(runDirectory, FileName), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<RunSnapshot>(json, JsonOptions.Default)
            ?? throw new InvalidDataException($"'{runDirectory}/{FileName}' is empty.");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter RunSnapshotTests`
Expected: PASS.

- [ ] **Step 5: Create the CLI project**

`src/FhirAgentBench.Cli/FhirAgentBench.Cli.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>fab</AssemblyName>
    <RootNamespace>FhirAgentBench.Cli</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.CommandLine" />
    <ProjectReference Include="..\FhirAgentBench\FhirAgentBench.csproj" />
  </ItemGroup>
</Project>
```
Run: `dotnet sln FhirAgentBench.slnx add src/FhirAgentBench.Cli/FhirAgentBench.Cli.csproj`

`src/FhirAgentBench.Cli/Program.cs`:
```csharp
using System.CommandLine;
using FhirAgentBench.Cli;

var root = new RootCommand("FHIR agent bench: measure how agent harnesses and models answer clinical questions over FHIR data.");
root.Subcommands.Add(CorpusCommands.Create());
root.Subcommands.Add(AdmitCommand.Create());
root.Subcommands.Add(RunCommand.Create());
root.Subcommands.Add(ReportCommand.Create());
return await root.Parse(args).InvokeAsync();
```

`src/FhirAgentBench.Cli/CorpusCommands.cs`:
```csharp
using System.CommandLine;
using FhirAgentBench.Authoring;
using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;

namespace FhirAgentBench.Cli;

internal static class CorpusCommands
{
    public static Command Create()
    {
        var corpus = new Command("corpus", "Fetch, verify and explore a pinned corpus.");
        corpus.Subcommands.Add(Fetch());
        corpus.Subcommands.Add(Verify());
        corpus.Subcommands.Add(Stats());
        corpus.Subcommands.Add(Patients());
        corpus.Subcommands.Add(Codes());
        return corpus;
    }

    private static Argument<string> CorpusArgument() => new("corpus-dir") { Description = "Folder containing corpus.yaml." };

    private static Command Fetch()
    {
        var dir = CorpusArgument();
        var command = new Command("fetch", "Download the corpus source, check its sha256 and extract it read-only.") { dir };
        command.SetAction(async (parse, cancellationToken) =>
        {
            using var http = new HttpClient();
            Console.WriteLine(await CorpusFetcher.FetchAsync(CorpusDefinition.Load(parse.GetValue(dir)!), http, cancellationToken));
            return 0;
        });
        return command;
    }

    private static Command Verify()
    {
        var dir = CorpusArgument();
        var command = new Command("verify", "Check every data file against data.manifest.json.") { dir };
        command.SetAction(parse =>
        {
            var corpus = CorpusDefinition.Load(parse.GetValue(dir)!);
            var problems = CorpusManifest.Load(corpus.ManifestPath).Verify(corpus.DataDirectory);
            foreach (var problem in problems)
            {
                Console.Error.WriteLine(problem);
            }

            Console.WriteLine(problems.Count == 0 ? "Corpus matches its manifest." : $"{problems.Count} problem(s).");
            return problems.Count == 0 ? 0 : 1;
        });
        return command;
    }

    private static Command Stats()
    {
        var dir = CorpusArgument();
        var command = new Command("stats", "Resource counts by type and the latest Observation time.") { dir };
        command.SetAction(parse =>
        {
            var corpus = CorpusDefinition.Load(parse.GetValue(dir)!);
            var index = CorpusIndex.Load(corpus.DataDirectory);
            Console.WriteLine($"{index.Count} resources");
            foreach (var type in new[] { "Patient", "Encounter", "Condition", "Observation", "MedicationRequest", "Medication", "AllergyIntolerance", "Immunization", "Procedure" })
            {
                Console.WriteLine($"  {type}: {index.OfType(type).Count}");
            }

            Console.WriteLine($"Latest Observation: {CorpusQueries.LatestObservationTime(index):O}");
            return 0;
        });
        return command;
    }

    private static Command Patients()
    {
        var dir = CorpusArgument();
        var loinc = new Option<string>("--loinc") { Description = "LOINC code to count per patient.", Required = true };
        var top = new Option<int>("--top") { Description = "Rows to print.", DefaultValueFactory = _ => 20 };
        var command = new Command("patients", "List patients with valid results for a LOINC code, as of the corpus 'now'.") { dir, loinc, top };
        command.SetAction(parse =>
        {
            var corpus = CorpusDefinition.Load(parse.GetValue(dir)!);
            var context = new OracleContext(CorpusIndex.Load(corpus.DataDirectory), corpus.Now, corpus.MrnSystem);
            Console.WriteLine("MRN\tName\tBirthDate\tCount\tLatest");
            foreach (var row in CorpusQueries.PatientsWithObservation(context, parse.GetValue(loinc)!).Take(parse.GetValue(top)))
            {
                Console.WriteLine($"{row.Mrn}\t{row.Name}\t{row.BirthDate}\t{row.Count}\t{row.LatestDate}");
            }

            return 0;
        });
        return command;
    }

    private static Command Codes()
    {
        var dir = CorpusArgument();
        var type = new Option<string>("--type") { Description = "Resource type, e.g. Condition or MedicationRequest.", Required = true };
        var display = new Option<string>("--display-contains") { Description = "Case-insensitive display filter.", Required = true };
        var command = new Command("codes", "Count codes whose display contains a phrase, to build oracle code lists.") { dir, type, display };
        command.SetAction(parse =>
        {
            var corpus = CorpusDefinition.Load(parse.GetValue(dir)!);
            var index = CorpusIndex.Load(corpus.DataDirectory);
            Console.WriteLine("Count\tSystem\tCode\tDisplay");
            foreach (var row in CorpusQueries.CodeCounts(index, parse.GetValue(type)!, parse.GetValue(display)!))
            {
                Console.WriteLine($"{row.Count}\t{row.System}\t{row.Code}\t{row.Display}");
            }

            return 0;
        });
        return command;
    }
}
```

`src/FhirAgentBench.Cli/AdmitCommand.cs`:
```csharp
using System.CommandLine;
using FhirAgentBench.Answers;
using FhirAgentBench.Authoring;
using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Cli;

internal static class AdmitCommand
{
    public static Command Create()
    {
        var corpusDir = new Option<string>("--corpus") { Description = "Folder containing corpus.yaml.", Required = true };
        var set = new Option<string>("--set") { Description = "Question-set file listing question ids.", Required = true };
        var questionsDir = new Option<string>("--questions-dir") { Description = "Folder of question YAML files.", DefaultValueFactory = _ => "questions" };
        var updateLock = new Option<bool>("--update-lock") { Description = "Write gold hashes to questions/gold.lock.json when every question is admitted." };
        var show = new Option<bool>("--show") { Description = "Print each computed gold answer (authoring only; never paste gold into a run)." };
        var command = new Command("admit", "Recompute gold for a question set and check it against the answer spec and gold lock.")
        {
            corpusDir, set, questionsDir, updateLock, show,
        };
        command.SetAction(parse =>
        {
            var corpus = CorpusDefinition.Load(parse.GetValue(corpusDir)!);
            var questions = QuestionLoader.LoadSet(parse.GetValue(questionsDir)!, parse.GetValue(set)!);
            var lockPath = Path.Combine(parse.GetValue(questionsDir)!, "gold.lock.json");
            var locked = GoldLock.Load(lockPath);
            var update = parse.GetValue(updateLock);
            var admitter = new GoldAdmitter(OracleRegistry.CreateDefault(), new OracleContext(CorpusIndex.Load(corpus.DataDirectory), corpus.Now, corpus.MrnSystem));

            var results = questions.Select(q => admitter.Admit(q, update ? null : locked.GetValueOrDefault(q.Id))).ToList();
            foreach (var result in results)
            {
                Console.WriteLine(result.IsAdmitted ? $"OK   {result.QuestionId}" : $"FAIL {result.QuestionId}: {string.Join("; ", result.Problems)}");
                if (parse.GetValue(show))
                {
                    Console.WriteLine($"     {AnswerJson.Serialize(result.Gold)}");
                }
            }

            var failures = results.Count(r => !r.IsAdmitted);
            if (update && failures == 0)
            {
                foreach (var result in results)
                {
                    locked[result.QuestionId] = result.GoldHash;
                }

                GoldLock.Save(lockPath, locked);
                Console.WriteLine($"Updated {lockPath}.");
            }

            return failures == 0 ? 0 : 1;
        });
        return command;
    }
}
```

`src/FhirAgentBench.Cli/RunCommand.cs`:
```csharp
using System.CommandLine;
using FhirAgentBench.Corpus;
using FhirAgentBench.Execution;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;
using FhirAgentBench.Reporting;

namespace FhirAgentBench.Cli;

internal static class RunCommand
{
    public static Command Create()
    {
        var profilePath = new Option<string>("--profile") { Description = "Profile YAML file.", Required = true };
        var corpusDir = new Option<string>("--corpus") { Description = "Folder containing corpus.yaml.", Required = true };
        var set = new Option<string>("--set") { Description = "Question-set file listing question ids.", Required = true };
        var questionsDir = new Option<string>("--questions-dir") { DefaultValueFactory = _ => "questions" };
        var repeats = new Option<int>("--repeats") { Description = "Attempts per question (K).", DefaultValueFactory = _ => 3 };
        var runsDir = new Option<string>("--runs-dir")
        {
            Description = "Where run folders are written (outside the repository by default, so agents cannot browse it).",
            DefaultValueFactory = _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fab", "runs"),
        };
        var yes = new Option<bool>("--yes") { Description = "Confirm the paid run after reading the cost estimate." };
        var command = new Command("run", "Run a profile over a question set, then grade and summarise it.")
        {
            profilePath, corpusDir, set, questionsDir, repeats, runsDir, yes,
        };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var profile = ProfileLoader.Load(parse.GetValue(profilePath)!);
            var corpus = CorpusDefinition.Load(parse.GetValue(corpusDir)!);
            var questions = QuestionLoader.LoadSet(parse.GetValue(questionsDir)!, parse.GetValue(set)!);
            var k = parse.GetValue(repeats);
            if (profile.Harness == Profile.CopilotCli && !parse.GetValue(yes))
            {
                Console.Error.WriteLine($"{questions.Count * k} attempts, each capped at {profile.MaxAiCredits} AI credits (worst case {questions.Count * k * profile.MaxAiCredits}). Re-run with --yes to start.");
                return 2;
            }

            Console.WriteLine($"Loading corpus '{corpus.Id}'...");
            var context = new OracleContext(CorpusIndex.Load(corpus.DataDirectory), corpus.Now, corpus.MrnSystem);
            var oracles = OracleRegistry.CreateDefault();
            var harness = await HarnessFactory.CreateAsync(profile, corpus, oracles, context, cancellationToken);

            var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{profile.Id}";
            var runDirectory = Directory.CreateDirectory(Path.Combine(parse.GetValue(runsDir)!, runId)).FullName;
            var harnessVersion = profile.Harness == Profile.CopilotCli
                ? await ProcessRunner.CaptureAsync(profile.Executable, ["--version"], cancellationToken)
                : null;
            await new RunSnapshot(
                    runId,
                    profile.Id,
                    RunSnapshot.HashFiles([profile.SourcePath, profile.PromptTemplatePath]),
                    corpus.Id,
                    CorpusManifest.Load(corpus.ManifestPath).Sha256,
                    questions.Select(q => q.Id).ToList(),
                    RunSnapshot.HashFiles(questions.Select(q => q.SourcePath)),
                    k,
                    harnessVersion,
                    Environment.GetEnvironmentVariable("FAB_CLI_ON_FHIR_COMMIT"),
                    DateTimeOffset.UtcNow)
                .WriteAsync(runDirectory, cancellationToken);

            Console.WriteLine($"Run {runId}: {questions.Count} questions x {k} -> {runDirectory}");
            var attempts = await new RunOrchestrator(harness, corpus, await File.ReadAllTextAsync(profile.PromptTemplatePath, cancellationToken))
                .RunAsync(questions, k, runDirectory, cancellationToken);
            var grades = new RunGrader(oracles, context).Grade(questions, attempts);
            await File.WriteAllLinesAsync(
                Path.Combine(runDirectory, "grades.jsonl"),
                grades.Select(g => System.Text.Json.JsonSerializer.Serialize(g, JsonOptions.Default)),
                cancellationToken);
            Console.WriteLine(RunReport.ToMarkdown(RunReport.Summarize(runId, profile.Id, grades)));
            return 0;
        });
        return command;
    }
}
```

`src/FhirAgentBench.Cli/ReportCommand.cs`:
```csharp
using System.CommandLine;
using System.Text.Json;
using FhirAgentBench.Execution;
using FhirAgentBench.Reporting;

namespace FhirAgentBench.Cli;

internal static class ReportCommand
{
    public static Command Create()
    {
        var first = new Argument<string>("run-dir") { Description = "Run folder." };
        var second = new Argument<string?>("compare-run-dir") { Description = "Optional second run folder for a paired comparison.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("report", "Summarise a run, or compare two runs pairwise.") { first, second };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var (summaryA, gradesA) = await LoadAsync(parse.GetValue(first)!, cancellationToken);
            if (parse.GetValue(second) is not { } secondDirectory)
            {
                Console.WriteLine(RunReport.ToMarkdown(summaryA));
                return 0;
            }

            var (summaryB, gradesB) = await LoadAsync(secondDirectory, cancellationToken);
            Console.WriteLine(RunReport.ToMarkdown(summaryA, summaryB, RunReport.Compare(gradesA, gradesB)));
            return 0;
        });
        return command;
    }

    private static async Task<(RunSummary, IReadOnlyList<GradeRecord>)> LoadAsync(string runDirectory, CancellationToken cancellationToken)
    {
        var snapshot = await RunSnapshot.ReadAsync(runDirectory, cancellationToken);
        var grades = (await File.ReadAllLinesAsync(Path.Combine(runDirectory, "grades.jsonl"), cancellationToken))
            .Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<GradeRecord>(line, JsonOptions.Default)!)
            .ToList();
        return (RunReport.Summarize(snapshot.RunId, snapshot.ProfileId, grades), grades);
    }
}
```

- [ ] **Step 6: Build and smoke-test the CLI**

Run:
```bash
dotnet build
dotnet run --project src/FhirAgentBench.Cli -- --help
dotnet run --project src/FhirAgentBench.Cli -- run --help
```
Expected: the build reports 0 warnings and 0 errors. Help lists `corpus`, `admit`, `run` and `report`, and `run --help` shows `--yes` and `--repeats`.

- [ ] **Step 7: Run all tests and commit**

Run: `dotnet test`
Expected: PASS.

```bash
git add FhirAgentBench.slnx src/FhirAgentBench.Cli src/FhirAgentBench/Execution/RunSnapshot.cs tests/FhirAgentBench.Tests/RunSnapshotTests.cs
git commit -m "feat: add fab CLI for corpus, admit, run and report"
```

---

### Task 11: Pinned sample corpus, 10 Phase 0 questions, baseline runs

**Files:**
- Create: `corpora/synthea-sample-v1/corpus.yaml`, `corpora/synthea-sample-v1/data.manifest.json` (generated)
- Create: `questions/*.yaml` (10 files), `questions/gold.lock.json` (generated), `question-sets/phase0.txt`
- Create: `profiles/b0-oracle.yaml`, `profiles/b1-abstain.yaml`, `profiles/p1-shell.yaml`, `profiles/p2-shell-cli-on-fhir.yaml`

**Interfaces:**
- Consumes: the `fab` CLI (Task 10).
- Produces: an admitted Phase 0 question set and baseline run results (B0 = 100%, B1 = 20%).

- [ ] **Step 1: Pin and fetch the corpus**

`corpora/synthea-sample-v1/corpus.yaml`:
```yaml
id: synthea-sample-v1
now: "2026-08-18T00:00:00Z"
mrn_system: http://hospital.smarthealthit.org
source:
  url: https://raw.githubusercontent.com/synthetichealth/synthea-sample-data/9959d9178ea28f4ec10f17ee238b6fabe6eb0de5/downloads/latest/synthea_sample_data_fhir_latest.zip
  sha256: 56cb9e49f7ba6ad4e61c40aa80999f8c10a710823fed1becdf2502053777a521
```
Run:
```bash
dotnet run --project src/FhirAgentBench.Cli -- corpus fetch corpora/synthea-sample-v1
dotnet run --project src/FhirAgentBench.Cli -- corpus stats corpora/synthea-sample-v1
```
Expected:
- `fetch` reports `Fetched corpus 'synthea-sample-v1': 111 files.`
- `stats` reports `119981 resources`.

If `stats` throws `appears more than once`, the batch files share an id. Record which one, and change `CorpusIndex.AddResource` to keep the first copy *only when the two copies have identical raw JSON* (compare `GetRawText()`); different content must still throw. Add a test for both cases before changing the code.

- [ ] **Step 2: Set the frozen "now"**

Take the `Latest Observation` timestamp printed by `stats`. Set `now` in `corpus.yaml` to the following midnight UTC. For example, latest `2026-08-17T14:03:00Z` gives `now: "2026-08-18T00:00:00Z"`. Re-run `stats` to confirm the file still loads.

- [ ] **Step 3: Choose patients and code lists**

Run each of these and keep the output for the next step:
```bash
dotnet run --project src/FhirAgentBench.Cli -- corpus patients corpora/synthea-sample-v1 --loinc 4548-4
dotnet run --project src/FhirAgentBench.Cli -- corpus patients corpora/synthea-sample-v1 --loinc 2339-0
dotnet run --project src/FhirAgentBench.Cli -- corpus patients corpora/synthea-sample-v1 --loinc 6298-4
dotnet run --project src/FhirAgentBench.Cli -- corpus codes corpora/synthea-sample-v1 --type MedicationRequest --display-contains statin
dotnet run --project src/FhirAgentBench.Cli -- corpus codes corpora/synthea-sample-v1 --type Condition --display-contains diabetes
```
Selection rules:
- For each patient question, pick a patient whose `Latest` result falls inside that question's window. Use a different patient for each question where possible. No patient may be used for more than 3 of the 10 questions.
- For the statin question, use *every* statin RxNorm code printed.
- For the cohort question, use the SNOMED code(s) whose display is type 2 diabetes.

- [ ] **Step 4: Write the 10 questions**

`question-sets/phase0.txt`:
```
# Phase 0: 10 questions proving the loop
t1-last-a1c-001
t1-last-a1c-002
t2-max-potassium-year-001
t2-meds-started-year-001
t3-mean-glucose-year-001
t3-a1c-count-3y-001
t4-ever-statin-001
t6-dm2-a1c-over-9-001
t7-pharmacy-phone-001
t7-emergency-contact-001
```

Fill each `patient` block with the MRN, name and birth date of the patient chosen in Step 3. Each file follows the pattern below. Give the file the exact name in the heading, set the fields shown, and set `verification: { status: unverified }` on all ten.

`questions/t1-last-a1c-001.yaml`:
```yaml
id: t1-last-a1c-001
tier: 1
template: latest-lab
tags: [REF]
tool_groups: [patient-context, search]
persona: primary-care
patient: { mrn: "<MRN from step 3>", name: "<name>", birth_date: "<birth date>" }
question: When was her last A1c, and what was it?
answer:
  type: composite
  fields:
    value: { type: quantity, unit: "%", tolerance: 0.05 }
    date: { type: date }
oracle:
  name: latest-observation
  parameters: { loinc: 4548-4 }
verification: { status: unverified }
```
Use "his" or "her" to match the patient's recorded gender.

`questions/t1-last-a1c-002.yaml`: identical shape for a second patient. The question is "What's his most recent hemoglobin A1c, and when was it drawn?"

`questions/t2-max-potassium-year-001.yaml`:
```yaml
id: t2-max-potassium-year-001
tier: 2
template: window-max-lab
tags: [TEMP]
tool_groups: [patient-context, search]
persona: hospitalist
patient: { mrn: "<MRN>", name: "<name>", birth_date: "<birth date>" }
question: What was her highest potassium over the past year, in mmol/L? If she had none, say so.
answer: { type: decimal, tolerance: 0.05, nullable: true }
oracle:
  name: observation-window-stats
  parameters: { loinc: 6298-4, window_days: 365, statistic: max, unit: mmol/L }
verification: { status: unverified }
```

`questions/t2-meds-started-year-001.yaml`:
```yaml
id: t2-meds-started-year-001
tier: 2
template: meds-started-window
tags: [TEMP, REF]
tool_groups: [patient-context, search]
persona: pharmacist
patient: { mrn: "<MRN of a patient with MedicationRequests authored in the last 365 days>", name: "<name>", birth_date: "<birth date>" }
question: Which medications were newly ordered for him in the past year? Give RxNorm codes.
answer: { type: set }
oracle:
  name: medications-started-within
  parameters: { window_days: 365 }
verification: { status: unverified }
```
To find a patient for this question, run `dotnet run --project src/FhirAgentBench.Cli -- admit ... --show` against a draft with a candidate MRN. If admission reports `gold is empty`, try another patient from the A1c list.

`questions/t3-mean-glucose-year-001.yaml`:
```yaml
id: t3-mean-glucose-year-001
tier: 3
template: window-mean-lab
tags: [AGG, TEMP, UNIT]
tool_groups: [patient-context, search, fhirpath]
persona: specialist
patient: { mrn: "<MRN>", name: "<name>", birth_date: "<birth date>" }
question: What has her average blood glucose been over the past year, in mg/dL?
answer: { type: decimal, tolerance: 0.5 }
oracle:
  name: observation-window-stats
  parameters: { loinc: 2339-0, window_days: 365, statistic: mean, unit: mg/dL }
verification: { status: unverified }
```

`questions/t3-a1c-count-3y-001.yaml`:
```yaml
id: t3-a1c-count-3y-001
tier: 3
template: window-count-lab
tags: [AGG, TEMP]
tool_groups: [patient-context, search]
persona: primary-care
patient: { mrn: "<MRN>", name: "<name>", birth_date: "<birth date>" }
question: How many A1c tests has he had in the last three years?
answer: { type: integer }
oracle:
  name: observation-window-stats
  parameters: { loinc: 4548-4, window_days: 1095, statistic: count, unit: "%" }
verification: { status: unverified }
```

`questions/t4-ever-statin-001.yaml`:
```yaml
id: t4-ever-statin-001
tier: 4
template: ever-on-class
tags: [TERM, TEMP]
tool_groups: [patient-context, search, terminology]
persona: primary-care
patient: { mrn: "<MRN>", name: "<name>", birth_date: "<birth date>" }
question: Has she ever been on a statin? If so, when was the first one ordered?
answer:
  type: composite
  fields:
    ever: { type: boolean }
    first: { type: date, nullable: true }
oracle:
  name: ever-on-medication
  parameters: { rxnorm: [<every statin code from step 3>] }
verification: { status: unverified }
```

`questions/t6-dm2-a1c-over-9-001.yaml`:
```yaml
id: t6-dm2-a1c-over-9-001
tier: 6
template: cohort-poor-control
tags: [AGG, TERM, ABS]
tool_groups: [sql-view, search, terminology]
persona: researcher
question: >-
  How many living patients aged 18 to 75 with type 2 diabetes had a most recent A1c in the past year above 9%?
  Count patients with no A1c in that year as above 9.
answer: { type: integer }
oracle:
  name: cohort-lab-above-or-missing
  parameters: { snomed: [<type 2 diabetes code(s) from step 3>], loinc: 4548-4, threshold: 9, window_days: 365, age_min: 18, age_max: 75, unit: "%" }
verification: { status: unverified }
```

`questions/t7-pharmacy-phone-001.yaml`:
```yaml
id: t7-pharmacy-phone-001
tier: 7
template: out-of-record
tags: [ABS]
tool_groups: [patient-context]
persona: pharmacist
patient: { mrn: "<MRN>", name: "<name>", birth_date: "<birth date>" }
question: What's the phone number of her preferred pharmacy?
answer: { type: identifier }
oracle: { name: unanswerable }
verification: { status: unverified }
```

`questions/t7-emergency-contact-001.yaml`: same shape. Use persona `nursing` and the question "Who is his emergency contact, and how do we reach them?"

Before admitting the two tier-7 questions, confirm they really are unanswerable. Search the patient's file for `"contact"` and for pharmacy-related resources (`Organization` entries with pharmacy type referenced from the patient):
```bash
rg -l '"contact"' corpora/synthea-sample-v1/data | head
```
If the chosen patient's file has a `contact` entry, pick another patient.

- [ ] **Step 5: Admit and lock the gold**

Run:
```bash
dotnet run --project src/FhirAgentBench.Cli -- admit --corpus corpora/synthea-sample-v1 --set question-sets/phase0.txt --show
```
Expected: 10 × `OK`. Read every printed gold answer and check it against the patient's file by hand; this is the first derivation check. Fix and re-run any `FAIL`. Then run:
```bash
dotnet run --project src/FhirAgentBench.Cli -- admit --corpus corpora/synthea-sample-v1 --set question-sets/phase0.txt --update-lock
```
Expected: `Updated questions/gold.lock.json.`

- [ ] **Step 6: Write the profiles**

`profiles/b0-oracle.yaml`:
```yaml
id: b0-oracle
harness: oracle
prompt_template: ../prompts/default.md
```

`profiles/b1-abstain.yaml`:
```yaml
id: b1-abstain
harness: abstain
prompt_template: ../prompts/default.md
```

`profiles/p1-shell.yaml`:
```yaml
id: p1-shell
harness: copilot-cli
model: claude-sonnet-5.5
builtin_tools: [view, rg, glob, powershell, read_powershell, stop_powershell, apply_patch]
prompt_template: ../prompts/default.md
max_ai_credits: 30
timeout_minutes: 20
```

`profiles/p2-shell-cli-on-fhir.yaml`:
```yaml
id: p2-shell-cli-on-fhir
harness: copilot-cli
model: claude-sonnet-5.5
builtin_tools: [view, rg, glob, powershell, read_powershell, stop_powershell, apply_patch]
mcp_servers:
  fhir:
    command: "${FAB_CLI_ON_FHIR}"
    args: [mcp, "{corpus}"]
    tools: [folder_inspect, search_fhir_resources, read_resources]
prompt_template: ../prompts/default.md
max_ai_credits: 30
timeout_minutes: 20
```
When `cli-on-fhir` lands more tools (patient context, `fhirpath_evaluate`, `plan_fhir_search`, `sql_view`, terminology), add their names to `tools`. Record the commit in `FAB_CLI_ON_FHIR_COMMIT`.

- [ ] **Step 7: Run the free baselines (Gate 0, part 1)**

Run:
```bash
dotnet run --project src/FhirAgentBench.Cli -- run --profile profiles/b0-oracle.yaml --corpus corpora/synthea-sample-v1 --set question-sets/phase0.txt --repeats 1
dotnet run --project src/FhirAgentBench.Cli -- run --profile profiles/b1-abstain.yaml --corpus corpora/synthea-sample-v1 --set question-sets/phase0.txt --repeats 1
```
Expected: B0 accuracy `100.0%`; B1 accuracy `20.0%`, with 8 `Abstained` and 2 `Correct` (the tier-7 questions). Any other result is a pipeline bug. Fix it before continuing.

- [ ] **Step 8: Commit**

```bash
git add corpora/synthea-sample-v1/corpus.yaml corpora/synthea-sample-v1/data.manifest.json questions question-sets profiles
git commit -m "feat: pin Synthea sample corpus and admit 10 Phase 0 questions"
```

---

### Task 12: First real run, P1 vs P2 (needs user approval)

**Files:**
- Create: `docs/superpowers/reports/2026-10-phase0-findings.md`

**Interfaces:**
- Consumes: profiles and the question set from Task 11.
- Produces: real trajectories and a findings document that decides Gate 0.

- [ ] **Step 1: Build cli-on-fhir from the PR branch**

```bash
git clone https://github.com/brendankowitz/ignixa-fhir "$LOCALAPPDATA/fab/ignixa-fhir"
cd "$LOCALAPPDATA/fab/ignixa-fhir" && git checkout claude/confident-turing-tph6h1 && git rev-parse HEAD
dotnet publish tools/Ignixa.CliOnFhir -c Release -o "$LOCALAPPDATA/fab/cli-on-fhir"
```
Set `FAB_CLI_ON_FHIR` to the published executable (`$LOCALAPPDATA/fab/cli-on-fhir/cli-on-fhir.exe`, or whatever name `publish` produced). Set `FAB_CLI_ON_FHIR_COMMIT` to the printed commit. Then check indexing at scale, timing it:
```bash
"$FAB_CLI_ON_FHIR" inspect "$(pwd)/corpora/synthea-sample-v1/data" --fhir-version r4 --build-index
```
Record the wall time and any `issues` in the findings document.

- [ ] **Step 2: Ask the user to approve the spend**

Run each profile once without `--yes` and paste the printed estimate to the user. The run is 10 questions × K=1 × 2 profiles. Do not continue without explicit approval.
```bash
dotnet run --project src/FhirAgentBench.Cli -- run --profile profiles/p1-shell.yaml --corpus corpora/synthea-sample-v1 --set question-sets/phase0.txt --repeats 1
```

- [ ] **Step 3: Smoke-test one attempt**

After approval, run P1 on a one-question set (`question-sets/smoke.txt` containing `t1-last-a1c-001`) with `--repeats 1 --yes`. Open the attempt folder and check that:
- `transcript.jsonl` shows tool calls;
- `workspace/answer.json` exists;
- `stderr.txt` has no authentication or folder-trust errors;
- the token does not appear in any file (`rg -l "$(gh auth token)" <run-dir>` must print nothing).

Fix harness flags if the agent could not write `answer.json` or could not read the corpus. Do the same for P2, and check `transcript.jsonl` shows `fhir-` tool calls.

- [ ] **Step 4: Run P1 and P2 on all 10 questions**

```bash
dotnet run --project src/FhirAgentBench.Cli -- run --profile profiles/p1-shell.yaml --corpus corpora/synthea-sample-v1 --set question-sets/phase0.txt --repeats 1 --yes
dotnet run --project src/FhirAgentBench.Cli -- run --profile profiles/p2-shell-cli-on-fhir.yaml --corpus corpora/synthea-sample-v1 --set question-sets/phase0.txt --repeats 1 --yes
dotnet run --project src/FhirAgentBench.Cli -- report <p2 run dir> <p1 run dir>
```

- [ ] **Step 5: Write the findings and decide Gate 0**

`docs/superpowers/reports/2026-10-phase0-findings.md` must record:
- **Inputs:** the `run.json` snapshots of all four runs, the `cli-on-fhir` commit, and the indexing time.
- **Results:** the `report` output.
- **Per question:** the grade status for P1 and P2, and one sentence on *why* each failure happened, from reading `transcript.jsonl`. Classify each as data, reasoning, format or harness.
- **Usage:** AI credits and tokens per attempt from `usage.json`. Use them to estimate the cost of the Phase 4 matrix (8 profiles × 150 × K=3).
- **Gate 0 checklist (spec §10):**
  - B0 = 100%.
  - B1 = 20%.
  - Real P1 and P2 trajectories were graded.
  - The token was not found on disk.
  - The agent never read gold (none exists on disk during a run).
  - `cli-on-fhir` indexes the corpus in practical time.
- **Recommendations for the Phase 1 plan:** harness flag changes, missing tools, and question wording problems.

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/reports/2026-10-phase0-findings.md question-sets/smoke.txt
git commit -m "docs: record Phase 0 findings from first real P1/P2 run"
```

---

### Task 13: Retire the Python prototype

**Files:**
- Delete: `src/fhir_question_bench/`, `tests/*.py`, `tests/fake_copilot.py`, `pyproject.toml`, `configs/`, `agent-working/`
- Delete: `docs/superpowers/specs/2026-10-15-fhir-question-benchmark-design.md`, `docs/superpowers/plans/2026-10-15-fhir-question-benchmark.md`, `docs/superpowers/plans/2026-10-15-fhir-agent-bench-platform.md`
- Modify: `README.md`, `.gitignore` (drop Python entries)

- [ ] **Step 1: Remove the prototype**

```bash
git rm -r src/fhir_question_bench tests/test_*.py tests/fake_copilot.py pyproject.toml configs agent-working
git rm docs/superpowers/specs/2026-10-15-fhir-question-benchmark-design.md docs/superpowers/plans/2026-10-15-fhir-question-benchmark.md docs/superpowers/plans/2026-10-15-fhir-agent-bench-platform.md
```

- [ ] **Step 2: Rewrite the README**

Replace `README.md` with:
```markdown
# FHIR Agent Bench

Clinician-voiced questions over a pinned FHIR corpus, for measuring whether an agent harness + model
answers better when you change its tools (e.g. the `cli-on-fhir` MCP), skills, prompt or model.

- Design: `docs/superpowers/specs/2026-10-15-fhir-agent-bench-platform-design.md`
- Research behind it: `docs/research/reports/FHIR agent bench revised plan.md`
- Status: Phase 0 (10 questions, loop proven). See `docs/superpowers/reports/`.

## Quick start

    dotnet test
    dotnet run --project src/FhirAgentBench.Cli -- corpus fetch corpora/synthea-sample-v1
    dotnet run --project src/FhirAgentBench.Cli -- admit --corpus corpora/synthea-sample-v1 --set question-sets/phase0.txt
    dotnet run --project src/FhirAgentBench.Cli -- run --profile profiles/b0-oracle.yaml --corpus corpora/synthea-sample-v1 --set question-sets/phase0.txt --repeats 1

Paid runs (`harness: copilot-cli`) print a credit estimate and need `--yes`. They use your
Copilot login (`COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, or `gh auth token`); the token is never written to disk.

## Layout

- `corpora/<id>/corpus.yaml`: pinned source, frozen "now", MRN system; `data/` is fetched, not committed.
- `questions/*.yaml`: one question each (tier, tags, tool groups, patient context, answer spec, oracle).
- `questions/gold.lock.json`: gold hashes; `fab admit` fails on drift.
- `profiles/*.yaml`: harness, model, tools, MCP servers, prompt, budget.
- `src/FhirAgentBench`: corpus index, oracles, grader, harnesses, reports (no Ignixa dependency).
```

- [ ] **Step 3: Verify and commit**

Run: `dotnet build && dotnet test`
Expected: 0 warnings, all tests PASS.

```bash
git add -A README.md .gitignore
git commit -m "chore: retire Python prototype in favour of the C# bench"
```

---

## Self-Review Notes

- **Spec coverage (Phase 0 row of spec §10):**
  - C# skeleton: T1.
  - Corpus index: T1.
  - Grader: T2.
  - 6 oracles: T4.
  - 10 questions: T11.
  - Copilot CLI harness: T9.
  - B0/B1/P1/P2 profiles: T11.
  - First real run and Gate 0: T12.
  - Prototype disposition (spec §11): T13.
  - Workspace isolation, token handling, cost guard (spec §8): T6, T7, T9 and T10.
- **Deliberately deferred to later phase plans:**
  - Mutation layer, needles and the custom Synthea run (Phase 1).
  - Grader CI variants, per-field logging and the dev/holdout splits (Phase 2).
  - Clustered SEs, pass^k and the Pareto plot (Phase 3).
  - Profiles P2a–P6 and a skills directory (Phase 3/4).
  - UI and hosting (Phase 5).
