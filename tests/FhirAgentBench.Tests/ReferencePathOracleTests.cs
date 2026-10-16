using FhirAgentBench.Answers;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class ReferencePathOracleTests : IDisposable
{
    private const string RxNorm = "http://www.nlm.nih.gov/research/umls/rxnorm";
    private const string Npi = "http://hl7.org/fhir/sid/us-npi";

    private static readonly OracleContext Context = new(Fixture.Index, Fixture.Now, Fixture.MrnSystem);
    private static readonly OracleRegistry Oracles = OracleRegistry.CreateDefault();

    private readonly string _directory = Directory.CreateTempSubdirectory("fab-refpath-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private Answer Run(string parameters)
    {
        var path = Path.Combine(_directory, "q.yaml");
        File.WriteAllText(path, $$"""
            id: q
            tier: 4
            template: t
            persona: p
            patient: { mrn: MRN-REED, name: Reed Test, birth_date: "1970-01-01" }
            question: Q?
            answer: { type: name, kind: text }
            oracle:
              name: reference-path
              parameters: {{parameters}}
            """);
        return Oracles.Compute(Context, QuestionLoader.Load(path));
    }

    private string? Text(string parameters) => Run(parameters).Value?.GetString();

    private static string EncounterToPractitioner(string classCode, string pick = "latest") =>
        $$"""{ start: { type: Encounter, class: ["{{classCode}}"], pick: {{pick}} }, path: [{ ref: participant.individual, type: Practitioner }], project: name }""";

    [Fact]
    public void Name_PrefersOfficialEntryAndOmitsPrefixAndSuffix()
    {
        var answer = Run(EncounterToPractitioner("EMER", "earliest"));

        Assert.Equal("Quinn Ted955 Reilly981", answer.Value!.Value.GetString());
        Assert.Equal(new[] { "Encounter/rd-enc-emer-1", "Practitioner/pr-official" }, answer.Evidence);
    }

    [Fact]
    public void Name_WithoutOfficialEntry_UsesFirst() =>
        Assert.Equal("Ana Lopez", Text(EncounterToPractitioner("AMB")));

    [Fact]
    public void Name_WithOnlyText_UsesText() =>
        Assert.Equal("Text Only", Text(EncounterToPractitioner("TXT")));

    [Fact]
    public void Name_Missing_IsNull() =>
        Assert.Null(Run(EncounterToPractitioner("NONAME")).Value);

    [Fact]
    public void Name_OfPatientAndOrganization()
    {
        var patient = Run("""{ start: { type: Observation, loinc: ["6298-4"], pick: latest }, path: [{ ref: subject, type: Patient }], project: name }""");
        var organization = Run("""{ start: { type: Encounter, class: ["EMER"], pick: latest }, path: [{ ref: serviceProvider, type: Organization }], project: name }""");

        Assert.Equal("Reed Test", patient.Value!.Value.GetString());
        Assert.Equal(new[] { "Observation/rd-obs-k-ward", "Patient/reed" }, patient.Evidence);
        Assert.Equal("EMERSON HOSPITAL", organization.Value!.Value.GetString());
    }

    [Fact]
    public void Forward_SkipsUnusableReferencesAndTakesFirstThatResolvesToTheRequestedType()
    {
        var answer = Run(EncounterToPractitioner("EMER"));

        Assert.Equal("Ana Lopez", answer.Value!.Value.GetString());
        Assert.Equal(new[] { "Encounter/rd-enc-emer-2", "Practitioner/pr-first" }, answer.Evidence);
    }

    [Fact]
    public void Forward_WithoutType_TakesFirstResolvedTarget() =>
        Assert.Equal(
            "EMERSON HOSPITAL",
            Text("""{ start: { type: Encounter, class: ["EMER"], pick: latest }, path: [{ ref: participant.individual }], project: name }"""));

    [Theory]
    [InlineData("UNRES")]
    [InlineData("SEARCH")]
    [InlineData("ABS")]
    [InlineData("DISP")]
    [InlineData("NOPART")]
    public void Forward_UnresolvableOrMissingReference_IsNullWithNoEvidence(string classCode)
    {
        var answer = Run(EncounterToPractitioner(classCode));

        Assert.Equal(AnswerStatus.Answered, answer.Status);
        Assert.Null(answer.Value);
        Assert.Empty(answer.Evidence);
    }

    [Fact]
    public void Forward_ConditionalReferenceMatchingTwoResources_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Run(EncounterToPractitioner("DUP")));

        Assert.Contains("more than one", ex.Message);
    }

    [Theory]
    [InlineData("NOPROV")]
    [InlineData("WRONGTYPE")]
    public void Forward_MissingOrWrongTypedElement_IsNull(string classCode) =>
        Assert.Null(Run($$"""{ start: { type: Encounter, class: ["{{classCode}}"], pick: latest }, path: [{ ref: serviceProvider, type: Organization }], project: name }""").Value);

    [Fact]
    public void Forward_TargetFailingItsValidityOrInstant_IsSkipped()
    {
        var skipsRefutedCondition = Run("""{ start: { type: Procedure, snomed: ["80146002"], pick: latest }, path: [{ ref: reasonReference, type: Condition }], project: "code:http://snomed.info/sct" }""");
        var noQualifyingCondition = Run("""{ start: { type: Procedure, snomed: ["99999"], pick: latest }, path: [{ ref: reasonReference, type: Condition }], project: name }""");

        Assert.Equal("222", skipsRefutedCondition.Value!.Value.GetProperty("code").GetString());
        Assert.Equal(new[] { "Procedure/rd-proc-1", "Condition/rd-cond-ok" }, skipsRefutedCondition.Evidence);
        Assert.Null(noQualifyingCondition.Value);
    }

    [Fact]
    public void Start_IgnoresCancelledFutureAndIncompleteCandidates()
    {
        Assert.Equal("2025-09-01", Text("""{ start: { type: Encounter, class: ["EMER"], pick: latest }, project: date }"""));
        Assert.Equal("2025-08-01", Text("""{ start: { type: Procedure, snomed: ["80146002"], pick: latest }, project: date }"""));
        Assert.Equal("2025-03-01", Text("""{ start: { type: Immunization, cvx: ["140"], pick: latest }, project: date }"""));
    }

    [Fact]
    public void Start_BreaksInstantTiesOnSmallerIdForBothPicks()
    {
        var latest = Run(EncounterToPractitioner("TIE"));
        var earliest = Run(EncounterToPractitioner("TIE", "earliest"));

        Assert.Equal("Ana Lopez", latest.Value!.Value.GetString());
        Assert.Equal("Encounter/rd-enc-tie-a", latest.Evidence[0]);
        Assert.Equal("Encounter/rd-enc-tie-a", earliest.Evidence[0]);
    }

    [Fact]
    public void Start_WindowDaysKeepsOnlyRecentCandidates()
    {
        Assert.Equal("2025-09-01", Text("""{ start: { type: Encounter, class: ["EMER"], window_days: 200, pick: earliest }, project: date }"""));
        Assert.Null(Run("""{ start: { type: Encounter, class: ["EMER"], window_days: 10, pick: earliest }, project: date }""").Value);
    }

    [Fact]
    public void Start_NoQualifyingCandidate_IsNull() =>
        Assert.Null(Run("""{ start: { type: Encounter, class: ["NOSUCH"], pick: latest }, project: date }""").Value);

    [Fact]
    public void Start_CriteriaAreAnded()
    {
        Assert.Equal("2025-10-01", Text("""{ start: { type: Encounter, class: ["IMP"], type_snomed: ["305351004", "397821002"], pick: latest }, project: date }"""));
        Assert.Equal("2025-10-20", Text("""{ start: { type: Encounter, class: ["IMP"], pick: latest }, project: date }"""));
        Assert.Null(Run("""{ start: { type: Encounter, class: ["AMB"], type_snomed: ["305351004"], pick: latest }, project: date }""").Value);
    }

    [Fact]
    public void Start_MedicationRequestMatchesCodeableConceptReferenceAndContainedMedication()
    {
        const string ToRequester = "path: [{ ref: requester, type: Practitioner }], project: name }";

        Assert.Equal("Text Only", Text($$"""{ start: { type: MedicationRequest, rxnorm: ["1000126"], pick: latest }, {{ToRequester}}"""));
        Assert.Equal("Ana Lopez", Text($$"""{ start: { type: MedicationRequest, rxnorm: ["1000126"], pick: earliest }, {{ToRequester}}"""));
        Assert.Equal("Quinn Ted955 Reilly981", Text($$"""{ start: { type: MedicationRequest, rxnorm: ["900001"], pick: latest }, {{ToRequester}}"""));
        Assert.Equal("Text Only", Text($$"""{ start: { type: MedicationRequest, rxnorm: ["1000126"], window_days: 100, pick: earliest }, {{ToRequester}}"""));
    }

    [Fact]
    public void Forward_ResolvesContainedMedication()
    {
        var answer = Run($$"""{ start: { type: MedicationRequest, rxnorm: ["900001"], pick: latest }, path: [{ ref: medicationReference, type: Medication }], project: "code:{{RxNorm}}" }""");

        Assert.Equal("900001", answer.Value!.Value.GetProperty("code").GetString());
        Assert.Equal(RxNorm, answer.Value.Value.GetProperty("system").GetString());
        Assert.Equal(new[] { "MedicationRequest/rd-mr-b", "Medication/m1" }, answer.Evidence);
    }

    [Theory]
    [InlineData("earliest", "Ana Lopez", "MedicationRequest/rd-mr-a")]
    [InlineData("latest", "Quinn Ted955 Reilly981", "MedicationRequest/rd-mr-b")]
    public void Reverse_PicksAmongOrdersLinkedByLiteralOrFullUrlReference(string pick, string expectedName, string expectedOrder)
    {
        var answer = Run($$"""{ start: { type: Encounter, class: ["IMP"], type_snomed: ["305351004"], pick: latest }, path: [{ rev: MedicationRequest.encounter, pick: {{pick}} }, { ref: requester, type: Practitioner }], project: name }""");

        Assert.Equal(expectedName, answer.Value!.Value.GetString());
        Assert.Equal(expectedOrder, answer.Evidence[1]);
    }

    [Fact]
    public void Reverse_NoLinkedCandidate_IsNull() =>
        Assert.Null(Run("""{ start: { type: Encounter, class: ["AMB"], pick: latest }, path: [{ rev: MedicationRequest.encounter, pick: latest }, { ref: requester }], project: name }""").Value);

    [Fact]
    public void ReverseThenForward_AppliesCriteriaAtEachStep()
    {
        var answer = Run("""{ start: { type: Encounter, class: ["IMP"], type_snomed: ["305351004"], pick: latest }, path: [{ rev: DiagnosticReport.encounter, loinc: ["24323-8", "51990-0"], pick: latest }, { ref: result, type: Observation, loinc: ["6298-4", "2823-3"] }], project: value }""");

        Assert.Equal(4.1m, answer.Value!.Value.GetProperty("value").GetDecimal());
        Assert.Equal("mmol/L", answer.Value.Value.GetProperty("unit").GetString());
        Assert.Equal(new[] { "Encounter/rd-enc-icu", "DiagnosticReport/rd-dr-2", "Observation/rd-obs-k-2" }, answer.Evidence);
    }

    [Fact]
    public void Forward_SkipsTargetsWithInvalidStatus()
    {
        var answer = Run("""{ start: { type: Encounter, class: ["IMP"], type_snomed: ["11429006"], pick: latest }, path: [{ rev: DiagnosticReport.encounter, pick: latest }, { ref: result, type: Observation, loinc: ["6298-4"] }], project: value }""");

        Assert.Equal(3.9m, answer.Value!.Value.GetProperty("value").GetDecimal());
        Assert.Equal("Observation/rd-obs-k-ward", answer.Evidence[^1]);
    }

    [Fact]
    public void Value_NotNumeric_IsNull() =>
        Assert.Null(Run("""{ start: { type: Observation, loinc: ["9999-9"], pick: latest }, project: value }""").Value);

    [Fact]
    public void Date_ProjectsTheTypeInstantAsDate() =>
        Assert.Equal("2024-02-01", Text("""{ start: { type: Procedure, snomed: ["80146002"], pick: latest }, path: [{ ref: reasonReference, type: Condition }], project: date }"""));

    [Fact]
    public void Address_ReadsFirstEntryOrSingleObject()
    {
        Assert.Equal("WESTFORD", Text("""{ start: { type: Encounter, class: ["EMER"], pick: latest }, path: [{ ref: serviceProvider, type: Organization }], project: "address.city" }"""));
        Assert.Equal("Acton", Text("""{ start: { type: Immunization, cvx: ["140"], pick: latest }, path: [{ ref: location, type: Location }], project: "address.city" }"""));
        Assert.Null(Run("""{ start: { type: Encounter, class: ["AMB"], pick: latest }, path: [{ ref: serviceProvider, type: Organization }], project: "address.city" }""").Value);
    }

    [Fact]
    public void Identifier_ProjectsSystemAndValueOrNull()
    {
        var found = Run($$"""{ start: { type: Encounter, class: ["EMER"], pick: latest }, path: [{ ref: participant.individual, type: Practitioner }], project: "identifier:{{Npi}}" }""");
        var missing = Run("""{ start: { type: Encounter, class: ["EMER"], pick: latest }, path: [{ ref: participant.individual, type: Practitioner }], project: "identifier:urn:other" }""");

        Assert.Equal(Npi, found.Value!.Value.GetProperty("system").GetString());
        Assert.Equal("1111", found.Value.Value.GetProperty("value").GetString());
        Assert.Null(missing.Value);
    }

    [Fact]
    public void Code_ReadsVaccineCodeWhenThereIsNoCode()
    {
        var answer = Run("""{ start: { type: Immunization, cvx: ["140"], pick: latest }, project: "code:http://hl7.org/fhir/sid/cvx" }""");

        Assert.Equal("140", answer.Value!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public void MapProjection_BuildsCompositeAndIsNullWhenAnyFieldIsNull()
    {
        var complete = Run("""{ start: { type: Encounter, class: ["EMER"], pick: latest }, path: [{ ref: serviceProvider, type: Organization }], project: { facility: name, town: "address.city" } }""");
        var partial = Run("""{ start: { type: Encounter, class: ["AMB"], pick: latest }, path: [{ ref: serviceProvider, type: Organization }], project: { facility: name, town: "address.city" } }""");

        Assert.Equal("EMERSON HOSPITAL", complete.Value!.Value.GetProperty("facility").GetString());
        Assert.Equal("WESTFORD", complete.Value.Value.GetProperty("town").GetString());
        Assert.Null(partial.Value);
    }

    [Theory]
    [InlineData("""{ start: { type: Foo, pick: latest }, project: name }""")]
    [InlineData("""{ start: { type: Practitioner, pick: latest }, project: name }""")]
    [InlineData("""{ start: { type: Encounter, loinc: ["1-1"], pick: latest }, project: date }""")]
    [InlineData("""{ start: { type: Encounter, pick: newest }, project: date }""")]
    [InlineData("""{ start: { type: Encounter, pick: latest }, project: bogus }""")]
    [InlineData("""{ start: { type: Encounter, pick: latest }, path: [{ type: Practitioner }], project: name }""")]
    [InlineData("""{ start: { type: Encounter, pick: latest }, path: [{ ref: participant.individual, rev: Encounter.partOf }], project: name }""")]
    [InlineData("""{ start: { type: Encounter, pick: latest }, path: [{ ref: serviceProvider, type: Foo }], project: name }""")]
    [InlineData("""{ start: { type: Encounter, pick: latest }, path: [{ ref: serviceProvider, type: Organization, class: ["AMB"] }], project: name }""")]
    [InlineData("""{ start: { type: Encounter, pick: latest }, path: [{ rev: Practitioner.partOf, pick: latest }], project: name }""")]
    [InlineData("""{ start: { type: Encounter, pick: latest }, path: [{ rev: MedicationRequest, pick: latest }], project: name }""")]
    public void InvalidConfiguration_Throws(string parameters) =>
        Assert.Throws<InvalidDataException>(() => Run(parameters));

    [Fact]
    public void Ambiguous_ConditionalReference_IsReportedByTheCorpus()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Fixture.Index.ResolveKey($"Practitioner?identifier={Npi}|2222"));

        Assert.Contains("more than one", ex.Message);
        Assert.Equal("Practitioner/pr-first", Fixture.Index.ResolveKey($"Practitioner?identifier={Npi}|1111"));
    }
}
