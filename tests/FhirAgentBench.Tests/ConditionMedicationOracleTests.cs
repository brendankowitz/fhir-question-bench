using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class ConditionMedicationOracleTests : IDisposable
{
    private const string Rx = "http://www.nlm.nih.gov/research/umls/rxnorm";
    private const string Sct = "http://snomed.info/sct";

    private static readonly OracleRegistry Oracles = OracleRegistry.CreateDefault();

    private readonly string _directory = Directory.CreateTempSubdirectory("fab-cm-").FullName;
    private readonly List<string> _entries = [];
    private OracleContext? _context;

    public ConditionMedicationOracleTests() => Add(
        $"{{\"resourceType\":\"Patient\",\"id\":\"p\",\"identifier\":[{{\"system\":\"{Fixture.MrnSystem}\",\"value\":\"MRN-p\"}}],\"birthDate\":\"1980-06-15\"}}");

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

    private void AddCondition(string id, string snomed, string? onset, string clinical = "active", string verification = "confirmed", string extra = "")
    {
        var onsetPart = onset is null ? "" : $",\"onsetDateTime\":\"{onset}\"";
        Add($"{{\"resourceType\":\"Condition\",\"id\":\"{id}\",\"subject\":{{\"reference\":\"Patient/p\"}},"
            + $"\"clinicalStatus\":{{\"coding\":[{{\"system\":\"s\",\"code\":\"{clinical}\"}}]}},"
            + $"\"verificationStatus\":{{\"coding\":[{{\"system\":\"s\",\"code\":\"{verification}\"}}]}},"
            + $"\"code\":{{\"coding\":[{{\"system\":\"{Sct}\",\"code\":\"{snomed}\"}}]}}{onsetPart}{extra}}}");
    }

    private void AddOrder(string id, string authoredOn, string rxnorm, string status = "active", string extra = "") => Add(
        $"{{\"resourceType\":\"MedicationRequest\",\"id\":\"{id}\",\"status\":\"{status}\",\"intent\":\"order\",\"subject\":{{\"reference\":\"Patient/p\"}},\"authoredOn\":\"{authoredOn}\",\"medicationCodeableConcept\":{{\"coding\":[{{\"system\":\"{Rx}\",\"code\":\"{rxnorm}\"}}]}}{extra}}}");

    private void AddObservation(string id, string loinc, string when, string value, string unit) => Add(
        $"{{\"resourceType\":\"Observation\",\"id\":\"{id}\",\"status\":\"final\",\"subject\":{{\"reference\":\"Patient/p\"}},"
        + $"\"code\":{{\"coding\":[{{\"system\":\"http://loinc.org\",\"code\":\"{loinc}\"}}]}},\"effectiveDateTime\":\"{when}\","
        + $"\"valueQuantity\":{{\"value\":{value},\"unit\":\"{unit}\",\"code\":\"{unit}\"}}}}");

    private Answer Ask(string oracle, Dictionary<string, object> parameters, string? mrn = "MRN-p") => Oracles.Compute(Context, new Question
    {
        Id = "q-cm",
        Tier = 1,
        Template = "t",
        Persona = "nursing",
        Text = "?",
        Patient = mrn is null ? null : new PatientContext(mrn, "n", "1960-01-01"),
        Answer = new AnswerSpec { Type = AnswerType.Integer },
        Oracle = new OracleCall(oracle, new OracleParameters(parameters)),
        SourcePath = "q.yaml",
    });

    private Answer Status(string returns, params string[] snomed) =>
        Ask("condition-status", new() { ["snomed"] = L(snomed), ["return"] = returns });

    private static List<object> L(params string[] values) => values.Cast<object>().ToList();

    private static JsonElement Value(Answer answer) => answer.Value!.Value;

    [Fact]
    public void ConditionStatus_FirstAndLatestOnset_ReturnDatesOfEarliestAndLatestEpisode()
    {
        AddCondition("late", "44054006", "2020-03-04T10:00:00Z");
        AddCondition("early", "127013003", "2009-11-05T10:00:00Z");
        AddCondition("other", "59621000", "2001-01-01T00:00:00Z");

        Assert.Equal("2009-11-05", Value(Status("first_onset", "44054006", "127013003")).GetString());
        var latest = Status("latest_onset", "44054006", "127013003");
        Assert.Equal("2020-03-04", Value(latest).GetString());
        Assert.Equal(new[] { "Condition/late" }, latest.Evidence);
    }

    [Fact]
    public void ConditionStatus_OnsetTie_TakesSmallerIdForBothDirections()
    {
        AddCondition("b", "44054006", "2010-01-01T00:00:00Z");
        AddCondition("a", "44054006", "2010-01-01T00:00:00Z");

        Assert.Equal(new[] { "Condition/a" }, Status("first_onset", "44054006").Evidence);
        Assert.Equal(new[] { "Condition/a" }, Status("latest_onset", "44054006").Evidence);
    }

    [Fact]
    public void ConditionStatus_Count_CountsEachResourceAndSkipsRefutedAndOnsetless()
    {
        AddCondition("a", "307426000", "2020-01-01T00:00:00Z");
        AddCondition("b", "307426000", "2021-01-01T00:00:00Z", clinical: "resolved");
        AddCondition("refuted", "307426000", "2022-01-01T00:00:00Z", verification: "refuted");
        AddCondition("error", "307426000", "2022-02-01T00:00:00Z", verification: "entered-in-error");
        AddCondition("noonset", "307426000", null);
        AddCondition("future", "307426000", "2027-01-01T00:00:00Z");

        Assert.Equal(2, Value(Status("count", "307426000")).GetInt32());
    }

    [Fact]
    public void ConditionStatus_WindowDays_FiltersOnOnset()
    {
        AddCondition("old", "307426000", "2020-01-01T00:00:00Z");
        AddCondition("recent", "307426000", "2025-12-20T00:00:00Z");

        var answer = Ask("condition-status", new() { ["snomed"] = L("307426000"), ["return"] = "count", ["window_days"] = "30" });

        Assert.Equal(1, Value(answer).GetInt32());
    }

    [Fact]
    public void ConditionStatus_OnsetPeriodStart_IsUsedWhenNoOnsetDateTime()
    {
        Add($"{{\"resourceType\":\"Condition\",\"id\":\"per\",\"subject\":{{\"reference\":\"Patient/p\"}},\"code\":{{\"coding\":[{{\"system\":\"{Sct}\",\"code\":\"1\"}}]}},\"onsetPeriod\":{{\"start\":\"2015-05-05\"}}}}");

        Assert.Equal("2015-05-05", Value(Status("first_onset", "1")).GetString());
    }

    [Fact]
    public void ConditionStatus_AgeAtFirstOnset_UsesFullYearsOnEarliestOnsetDate()
    {
        AddCondition("later", "44054006", "2020-01-01T00:00:00Z");
        AddCondition("first", "44054006", "2009-06-14T00:00:00Z");

        Assert.Equal(28, Value(Status("age_at_first_onset", "44054006")).GetInt32());
    }

    [Fact]
    public void ConditionStatus_ActiveCount_CountsDistinctFirstCodesOfActiveConditionsMinusExcluded()
    {
        AddCondition("a1", "111", "2010-01-01T00:00:00Z");
        AddCondition("a2", "111", "2012-01-01T00:00:00Z");
        AddCondition("b", "222", null);
        AddCondition("c", "333", "2011-01-01T00:00:00Z");
        AddCondition("resolved", "444", "2011-01-01T00:00:00Z", clinical: "resolved");
        AddCondition("abated", "555", "2011-01-01T00:00:00Z", extra: ",\"abatementDateTime\":\"2020-01-01T00:00:00Z\"");
        AddCondition("abatedFlag", "666", "2011-01-01T00:00:00Z", extra: ",\"abatementBoolean\":true");
        AddCondition("refuted", "777", "2011-01-01T00:00:00Z", verification: "refuted");
        AddCondition("future", "888", "2027-01-01T00:00:00Z");

        var all = Ask("condition-status", new() { ["return"] = "active_count" });
        var excluding = Ask("condition-status", new() { ["return"] = "active_count", ["exclude_snomed"] = L("333") });

        Assert.Equal(3, Value(all).GetInt32());
        Assert.Equal(2, Value(excluding).GetInt32());
    }

    [Fact]
    public void ConditionStatus_NoMatch_IsNullForDatesAndZeroForCounts()
    {
        Assert.Null(Status("first_onset", "999").Value);
        Assert.Null(Status("latest_onset", "999").Value);
        Assert.Null(Status("age_at_first_onset", "999").Value);
        Assert.Equal(0, Value(Status("count", "999")).GetInt32());
        Assert.Equal(0, Value(Ask("condition-status", new() { ["return"] = "active_count" })).GetInt32());
    }

    [Fact]
    public void ConditionStatus_UnknownReturn_ThrowsNamingQuestion()
    {
        var error = Assert.Throws<InvalidDataException>(() => Status("middle_onset", "1"));

        Assert.Contains("q-cm", error.Message);
        Assert.Contains("middle_onset", error.Message);
    }

    [Fact]
    public void ConditionCrosswalk_ActiveSourceCondition_ReturnsTargetCode()
    {
        AddCondition("htn", "59621000", "2023-05-19T00:00:00Z");

        var answer = Ask("condition-crosswalk", Crosswalk("59621000", "38341003"));

        Assert.Equal("I10", Value(answer).GetProperty("code").GetString());
        Assert.Equal("http://hl7.org/fhir/sid/icd-10-cm", Value(answer).GetProperty("system").GetString());
        Assert.Equal(new[] { "Condition/htn" }, answer.Evidence);
    }

    [Fact]
    public void ConditionCrosswalk_ResolvedOrMissingCondition_IsNull()
    {
        AddCondition("htn", "59621000", "2023-05-19T00:00:00Z", clinical: "resolved");

        Assert.Null(Ask("condition-crosswalk", Crosswalk("59621000")).Value);
        Assert.Null(Ask("condition-crosswalk", Crosswalk("55822004")).Value);
    }

    private static Dictionary<string, object> Crosswalk(params string[] source) => new()
    {
        ["source_snomed"] = L(source),
        ["target_system"] = "http://hl7.org/fhir/sid/icd-10-cm",
        ["target_code"] = "I10",
    };

    [Fact]
    public void ActiveMedications_ReturnsDistinctSortedCodesOfActiveOrdersInClass()
    {
        AddOrder("o1", "2024-01-01T00:00:00Z", "314076");
        AddOrder("o2", "2025-01-01T00:00:00Z", "314076");
        AddOrder("o3", "2025-02-01T00:00:00Z", "308136");
        AddOrder("stopped", "2024-01-01T00:00:00Z", "314077", status: "stopped");
        AddOrder("draft", "2024-01-01T00:00:00Z", "314077", status: "draft");
        AddOrder("outside", "2024-01-01T00:00:00Z", "999");
        AddOrder("future", "2027-01-01T00:00:00Z", "314077");

        var answer = Ask("active-medications", new() { ["rxnorm"] = L("314076", "314077", "308136"), ["return"] = "codes" });

        Assert.Equal(new[] { "308136", "314076" }, Value(answer).EnumerateArray().Select(e => e.GetProperty("code").GetString()));
        Assert.All(Value(answer).EnumerateArray(), e => Assert.Equal(Rx, e.GetProperty("system").GetString()));
    }

    [Fact]
    public void ActiveMedications_ResolvesMedicationReference_AndEmptySetWhenNone()
    {
        Add($"{{\"resourceType\":\"Medication\",\"id\":\"m\",\"code\":{{\"coding\":[{{\"system\":\"{Rx}\",\"code\":\"243670\"}}]}}}}");
        Add("{\"resourceType\":\"MedicationRequest\",\"id\":\"ref\",\"status\":\"active\",\"intent\":\"order\",\"subject\":{\"reference\":\"Patient/p\"},\"authoredOn\":\"2024-01-01T00:00:00Z\",\"medicationReference\":{\"reference\":\"Medication/m\"}}");

        var found = Ask("active-medications", new() { ["rxnorm"] = L("243670") });
        var none = Ask("active-medications", new() { ["rxnorm"] = L("1") });

        Assert.Equal("243670", Value(found).EnumerateArray().Single().GetProperty("code").GetString());
        Assert.Equal(0, Value(none).GetArrayLength());
    }

    [Fact]
    public void ActiveMedications_ReturnOtherThanCodes_Throws()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => Ask("active-medications", new() { ["rxnorm"] = L("1"), ["return"] = "count" }));

        Assert.Contains("q-cm", error.Message);
    }

    private void AddReasonedOrder(string id, string authoredOn, string? reasonReference) => AddOrder(
        id, authoredOn, "106892",
        extra: reasonReference is null ? "" : $",\"reasonReference\":[{{\"reference\":\"{reasonReference}\"}}]");

    [Fact]
    public void MedicationReason_NewestOrderWithResolvableReason_SuppliesConditionCode()
    {
        AddCondition("old-reason", "111", "2010-01-01T00:00:00Z");
        AddCondition("new-reason", "222", "2015-01-01T00:00:00Z");
        AddReasonedOrder("o1", "2016-01-01T00:00:00Z", "Condition/old-reason");
        AddReasonedOrder("o2", "2020-01-01T00:00:00Z", "Condition/new-reason");
        AddReasonedOrder("o3", "2024-01-01T00:00:00Z", null);
        AddReasonedOrder("o4", "2025-01-01T00:00:00Z", "Condition/missing");

        var answer = Ask("medication-reason", new() { ["rxnorm"] = L("106892") });

        Assert.Equal("222", Value(answer).GetProperty("code").GetString());
        Assert.Equal(Sct, Value(answer).GetProperty("system").GetString());
        Assert.Equal(new[] { "MedicationRequest/o2", "Condition/new-reason" }, answer.Evidence);
    }

    [Fact]
    public void MedicationReason_SameInstant_PrefersSmallerOrderId()
    {
        AddCondition("c-a", "111", "2010-01-01T00:00:00Z");
        AddCondition("c-b", "222", "2010-01-01T00:00:00Z");
        AddReasonedOrder("b", "2020-01-01T00:00:00Z", "Condition/c-b");
        AddReasonedOrder("a", "2020-01-01T00:00:00Z", "Condition/c-a");

        Assert.Equal("111", Value(Ask("medication-reason", new() { ["rxnorm"] = L("106892") })).GetProperty("code").GetString());
    }

    [Fact]
    public void MedicationReason_NoOrderOrNoReason_IsNull()
    {
        AddReasonedOrder("o", "2020-01-01T00:00:00Z", null);

        Assert.Null(Ask("medication-reason", new() { ["rxnorm"] = L("106892") }).Value);
        Assert.Null(Ask("medication-reason", new() { ["rxnorm"] = L("1") }).Value);
    }

    private Answer Before(string loinc, params string[] rxnorm) =>
        Ask("observation-before-medication", new() { ["loinc"] = loinc, ["rxnorm"] = L(rxnorm) });

    [Fact]
    public void ObservationBeforeMedication_LatestReadingOnOrBeforeEarliestOrder_IncludingSameInstant()
    {
        AddOrder("later", "2020-06-01T00:00:00Z", "106892");
        AddOrder("first", "2018-03-01T00:00:00Z", "311034", status: "completed");
        AddObservation("before", "4548-4", "2017-01-01T00:00:00Z", "7.1", "%");
        AddObservation("same", "4548-4", "2018-03-01T00:00:00Z", "6.97", "%");
        AddObservation("after", "4548-4", "2018-03-02T00:00:00Z", "9.0", "%");

        var answer = Before("4548-4", "106892", "311034");

        Assert.Equal(6.97m, Value(answer).GetProperty("value").GetProperty("value").GetDecimal());
        Assert.Equal("2018-03-01", Value(answer).GetProperty("date").GetString());
        Assert.Equal(new[] { "MedicationRequest/first", "Observation/same" }, answer.Evidence);
    }

    [Fact]
    public void ObservationBeforeMedication_CancelledOrderIsNotTheAnchor()
    {
        AddOrder("cancelled", "2010-01-01T00:00:00Z", "106892", status: "cancelled");
        AddOrder("real", "2018-03-01T00:00:00Z", "106892");
        AddObservation("early", "4548-4", "2012-01-01T00:00:00Z", "8.0", "%");

        Assert.Equal("2012-01-01", Value(Before("4548-4", "106892")).GetProperty("date").GetString());
    }

    [Fact]
    public void ObservationBeforeMedication_NoOrderOrNoReading_IsNull()
    {
        AddOrder("o", "2018-03-01T00:00:00Z", "106892");
        AddObservation("after", "4548-4", "2019-01-01T00:00:00Z", "9.0", "%");

        Assert.Null(Before("4548-4", "106892").Value);
        Assert.Null(Before("4548-4", "1").Value);
    }

    [Fact]
    public void ObservationBeforeMedication_ConvertsToRequestedUnit()
    {
        AddOrder("o", "2018-03-01T00:00:00Z", "106892");
        AddObservation("glucose", "2339-0", "2018-01-01T00:00:00Z", "90.08", "mg/dL");

        var answer = Ask("observation-before-medication", new() { ["loinc"] = L("2339-0"), ["rxnorm"] = L("106892"), ["unit"] = "mmol/L" });

        Assert.Equal("mmol/L", Value(answer).GetProperty("value").GetProperty("unit").GetString());
        Assert.Equal(5m, Math.Round(Value(answer).GetProperty("value").GetProperty("value").GetDecimal(), 2));
    }
}
