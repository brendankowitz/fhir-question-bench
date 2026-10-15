using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class CohortCountOracleTests : IDisposable
{
    private const string Sct = "http://snomed.info/sct";
    private const string Rx = "http://www.nlm.nih.gov/research/umls/rxnorm";
    private const string Cvx = "http://hl7.org/fhir/sid/cvx";

    private static readonly OracleRegistry Oracles = OracleRegistry.CreateDefault();

    private readonly string _directory = Directory.CreateTempSubdirectory("fab-cohort-").FullName;
    private readonly List<string> _entries = [];
    private OracleContext? _context;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private OracleContext Context
    {
        get
        {
            if (_context is null)
            {
                var bundle = $"{{\"resourceType\":\"Bundle\",\"type\":\"transaction\",\"entry\":[{string.Join(",", _entries)}]}}";
                File.WriteAllText(Path.Combine(_directory, "bundle.json"), bundle);
                _context = new OracleContext(CorpusIndex.Load(_directory), Fixture.Now, Fixture.MrnSystem);
            }

            return _context;
        }
    }

    private void Add(string resource) => _entries.Add($"{{\"resource\":{resource}}}");

    private void AddPatient(string id, string birthDate = "1980-01-01", string extra = "") => Add(
        $"{{\"resourceType\":\"Patient\",\"id\":\"{id}\",\"identifier\":[{{\"system\":\"{Fixture.MrnSystem}\",\"value\":\"MRN-{id}\"}}],\"birthDate\":\"{birthDate}\"{extra}}}");

    private static string Concept(string system, string code) => $"{{\"coding\":[{{\"system\":\"{system}\",\"code\":\"{code}\"}}]}}";

    private void AddCondition(string patient, string snomed, string clinical = "active") => Add(
        $"{{\"resourceType\":\"Condition\",\"id\":\"c-{patient}-{snomed}\",\"subject\":{{\"reference\":\"Patient/{patient}\"}},"
        + $"\"clinicalStatus\":{Concept("http://terminology.hl7.org/CodeSystem/condition-clinical", clinical)},"
        + $"\"code\":{Concept(Sct, snomed)},\"onsetDateTime\":\"2010-01-01\"}}");

    private void AddOrder(string patient, string rxnorm, string status = "active") => Add(
        $"{{\"resourceType\":\"MedicationRequest\",\"id\":\"m-{patient}-{rxnorm}\",\"status\":\"{status}\",\"intent\":\"order\","
        + $"\"subject\":{{\"reference\":\"Patient/{patient}\"}},\"authoredOn\":\"2025-01-01\",\"medicationCodeableConcept\":{Concept(Rx, rxnorm)}}}");

    private void AddImmunization(string patient, string cvx, string occurrence, string status = "completed") => Add(
        $"{{\"resourceType\":\"Immunization\",\"id\":\"i-{patient}-{cvx}-{occurrence}\",\"status\":\"{status}\",\"patient\":{{\"reference\":\"Patient/{patient}\"}},"
        + $"\"vaccineCode\":{Concept(Cvx, cvx)},\"occurrenceDateTime\":\"{occurrence}\"}}");

    private void AddLab(string patient, string loinc, string when, string value, string unit = "mg/dL") => Add(
        $"{{\"resourceType\":\"Observation\",\"id\":\"o-{patient}-{loinc}-{when}\",\"status\":\"final\",\"subject\":{{\"reference\":\"Patient/{patient}\"}},"
        + $"\"code\":{Concept("http://loinc.org", loinc)},\"effectiveDateTime\":\"{when}\",\"valueQuantity\":{{\"value\":{value},\"unit\":\"{unit}\",\"code\":\"{unit}\"}}}}");

    private static List<object> L(params string[] values) => values.Cast<object>().ToList();

    private static Dictionary<object, object> M(params (string Key, object Value)[] pairs) =>
        pairs.ToDictionary(pair => (object)pair.Key, pair => pair.Value);

    private Answer Count(Dictionary<string, object> parameters) => Oracles.Compute(Context, new Question
    {
        Id = "q-cohort",
        Tier = 6,
        Template = "t",
        Persona = "quality",
        Text = "?",
        Answer = new AnswerSpec { Type = AnswerType.Integer },
        Oracle = new OracleCall("cohort-count", new OracleParameters(parameters)),
        SourcePath = "q.yaml",
    });

    private int CountOf(Dictionary<string, object> parameters) => Count(parameters).Value!.Value.GetInt32();

    [Fact]
    public void NoCriteria_CountsLivingPatientsOnly()
    {
        AddPatient("alive");
        AddPatient("past", extra: ",\"deceasedDateTime\":\"2020-01-01T00:00:00Z\"");
        AddPatient("future", extra: ",\"deceasedDateTime\":\"2030-01-01T00:00:00Z\"");
        AddPatient("flag", extra: ",\"deceasedBoolean\":true");
        AddPatient("nobirth", extra: "");

        var gold = Count(new());

        Assert.Equal(3, gold.Value!.Value.GetInt32());
        Assert.Equal(new[] { "Patient/alive", "Patient/future", "Patient/nobirth" }.Order(), gold.Evidence.Order());
    }

    [Fact]
    public void AgeBounds_AreInclusiveAtNow()
    {
        AddPatient("turns65today", birthDate: "1961-01-15");
        AddPatient("turns65tomorrow", birthDate: "1961-01-16");
        AddPatient("exactly40", birthDate: "1986-01-15");

        Assert.Equal(1, CountOf(new() { ["age_min"] = "65" }));
        Assert.Equal(2, CountOf(new() { ["age_max"] = "64" }));
        Assert.Equal(1, CountOf(new() { ["age_min"] = "40", ["age_max"] = "40" }));
    }

    [Fact]
    public void ConditionsAny_NeedsAnActiveListedCondition()
    {
        AddPatient("active");
        AddCondition("active", "44054006");
        AddPatient("resolved");
        AddCondition("resolved", "44054006", clinical: "resolved");
        AddPatient("other");
        AddCondition("other", "999");
        AddPatient("none");

        Assert.Equal(1, CountOf(new() { ["conditions_any"] = L("44054006", "127013003") }));
    }

    [Fact]
    public void MedicationsActiveAny_NeedsAnActiveListedOrder()
    {
        AddPatient("active");
        AddOrder("active", "312961");
        AddPatient("stopped");
        AddOrder("stopped", "312961", status: "stopped");
        AddPatient("cancelled");
        AddOrder("cancelled", "312961", status: "cancelled");
        AddPatient("other");
        AddOrder("other", "1");

        Assert.Equal(1, CountOf(new() { ["medications_active_any"] = L("312961", "314231") }));
    }

    [Fact]
    public void ImmunizationAny_ReadsNestedMapAndCountsCompletedListedVaccines()
    {
        AddPatient("done");
        AddImmunization("done", "133", "2020-05-01");
        AddPatient("notdone");
        AddImmunization("notdone", "133", "2020-05-01", status: "not-done");
        AddPatient("other");
        AddImmunization("other", "999", "2020-05-01");
        AddPatient("none");

        Assert.Equal(1, CountOf(new() { ["immunization_any"] = M(("cvx", L("33", "133"))) }));
    }

    [Fact]
    public void ImmunizationAny_WindowDays_ExcludesOlderDoses()
    {
        AddPatient("recent");
        AddImmunization("recent", "133", "2025-10-01");
        AddPatient("old");
        AddImmunization("old", "133", "2020-10-01");

        Assert.Equal(1, CountOf(new() { ["immunization_any"] = M(("cvx", L("133")), ("window_days", "365")) }));
    }

    [Fact]
    public void Lab_UsesLatestReadingInWindow_AndExcludesMissingByDefault()
    {
        AddPatient("high");
        AddLab("high", "18262-6", "2025-06-01T00:00:00Z", "130");
        AddPatient("lowNow");
        AddLab("lowNow", "18262-6", "2025-01-01T00:00:00Z", "200");
        AddLab("lowNow", "18262-6", "2025-12-01T00:00:00Z", "100");
        AddPatient("outsideWindow");
        AddLab("outsideWindow", "18262-6", "2020-01-01T00:00:00Z", "200");
        AddPatient("missing");

        var lab = M(("loinc", L("18262-6", "13457-7")), ("window_days", "1095"), ("comparator", "ge"), ("threshold", "130"), ("unit", "mg/dL"));

        Assert.Equal(1, CountOf(new() { ["lab"] = lab }));
    }

    [Fact]
    public void Lab_MissingCount_IncludesPatientsWithoutAReadingInWindow()
    {
        AddPatient("high");
        AddLab("high", "18262-6", "2025-06-01T00:00:00Z", "130");
        AddPatient("outsideWindow");
        AddLab("outsideWindow", "18262-6", "2020-01-01T00:00:00Z", "10");
        AddPatient("missing");

        var lab = M(("loinc", "18262-6"), ("window_days", "365"), ("comparator", "gt"), ("threshold", "129"), ("missing", "count"));

        Assert.Equal(3, CountOf(new() { ["lab"] = lab }));
    }

    [Fact]
    public void Lab_ConvertsToRequestedUnitBeforeComparing()
    {
        AddPatient("glucose");
        AddLab("glucose", "2339-0", "2025-12-01T00:00:00Z", "5.5", unit: "mmol/L");

        var lab = M(("loinc", "2339-0"), ("comparator", "ge"), ("threshold", "99"), ("unit", "mg/dL"));

        Assert.Equal(1, CountOf(new() { ["lab"] = lab }));
    }

    [Fact]
    public void AllCriteria_MustHoldTogether()
    {
        AddPatient("match", birthDate: "1950-01-01");
        AddCondition("match", "414545008");
        AddOrder("match", "312961");
        AddImmunization("match", "133", "2020-05-01");
        AddLab("match", "18262-6", "2025-12-01T00:00:00Z", "150");
        AddPatient("youngMatch", birthDate: "2000-01-01");
        AddCondition("youngMatch", "414545008");
        AddOrder("youngMatch", "312961");
        AddImmunization("youngMatch", "133", "2020-05-01");
        AddLab("youngMatch", "18262-6", "2025-12-01T00:00:00Z", "150");
        AddPatient("noOrder", birthDate: "1950-01-01");
        AddCondition("noOrder", "414545008");
        AddImmunization("noOrder", "133", "2020-05-01");
        AddLab("noOrder", "18262-6", "2025-12-01T00:00:00Z", "150");

        var parameters = new Dictionary<string, object>
        {
            ["age_min"] = "65",
            ["conditions_any"] = L("414545008"),
            ["medications_active_any"] = L("312961"),
            ["immunization_any"] = M(("cvx", L("133"))),
            ["lab"] = M(("loinc", L("18262-6")), ("comparator", "ge"), ("threshold", "130")),
        };

        var gold = Count(parameters);

        Assert.Equal(1, gold.Value!.Value.GetInt32());
        Assert.Equal(new[] { "Patient/match" }, gold.Evidence);
    }

    [Fact]
    public void NoPatientMatches_CountsZero()
    {
        AddPatient("p");

        Assert.Equal(0, CountOf(new() { ["conditions_any"] = L("44054006") }));
    }

    [Fact]
    public void LabMissingWithUnknownValue_NamesQuestionAndParameter()
    {
        AddPatient("p");
        var lab = M(("loinc", "18262-6"), ("comparator", "ge"), ("threshold", "1"), ("missing", "maybe"));

        var ex = Assert.Throws<InvalidDataException>(() => Count(new() { ["lab"] = lab }));

        Assert.Contains("q-cohort", ex.Message);
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void LabWithUnknownComparator_NamesQuestionAndParameter()
    {
        AddPatient("p");
        var lab = M(("loinc", "18262-6"), ("comparator", "eq"), ("threshold", "1"));

        var ex = Assert.Throws<InvalidDataException>(() => Count(new() { ["lab"] = lab }));

        Assert.Contains("q-cohort", ex.Message);
        Assert.Contains("comparator", ex.Message);
    }

    [Fact]
    public void NestedParameterThatIsNotAMap_NamesParameter()
    {
        AddPatient("p");

        var ex = Assert.Throws<InvalidDataException>(() => Count(new() { ["lab"] = "high" }));

        Assert.Contains("'lab'", ex.Message);
    }
}
