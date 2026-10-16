using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class EventOracleTests : IDisposable
{
    private const string Cvx = "http://hl7.org/fhir/sid/cvx";
    private const string Sct = "http://snomed.info/sct";
    private const string Rx = "http://www.nlm.nih.gov/research/umls/rxnorm";

    private static readonly OracleRegistry Oracles = OracleRegistry.CreateDefault();

    private readonly string _directory = Directory.CreateTempSubdirectory("fab-ev-").FullName;
    private readonly List<string> _entries = [];
    private OracleContext? _context;

    public EventOracleTests() => Add(
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

    private void AddImmunization(string id, string cvx, string? occurrence, string status = "completed") => Add(
        $"{{\"resourceType\":\"Immunization\",\"id\":\"{id}\",\"status\":\"{status}\",\"patient\":{{\"reference\":\"Patient/p\"}},"
        + $"\"vaccineCode\":{{\"coding\":[{{\"system\":\"{Cvx}\",\"code\":\"{cvx}\"}}]}}"
        + (occurrence is null ? "" : $",\"occurrenceDateTime\":\"{occurrence}\"") + "}");

    private void AddProcedure(string id, string snomed, string performed, string status = "completed", bool asPeriod = false) => Add(
        $"{{\"resourceType\":\"Procedure\",\"id\":\"{id}\",\"status\":\"{status}\",\"subject\":{{\"reference\":\"Patient/p\"}},"
        + $"\"code\":{{\"coding\":[{{\"system\":\"{Sct}\",\"code\":\"{snomed}\"}}]}},"
        + (asPeriod ? $"\"performedPeriod\":{{\"start\":\"{performed}\"}}" : $"\"performedDateTime\":\"{performed}\"") + "}");

    private void AddEncounter(string id, string start, string classCode = "AMB", string? type = null, string status = "finished") => Add(
        $"{{\"resourceType\":\"Encounter\",\"id\":\"{id}\",\"status\":\"{status}\",\"subject\":{{\"reference\":\"Patient/p\"}},"
        + $"\"class\":{{\"system\":\"http://terminology.hl7.org/CodeSystem/v3-ActCode\",\"code\":\"{classCode}\"}},"
        + (type is null ? "" : $"\"type\":[{{\"coding\":[{{\"system\":\"{Sct}\",\"code\":\"{type}\"}}]}}],")
        + $"\"period\":{{\"start\":\"{start}\"}}}}");

    private void AddAllergy(string id, string system, string code, string category = "medication", string? clinical = "active", string verification = "confirmed")
    {
        var clinicalPart = clinical is null ? "" : $"\"clinicalStatus\":{{\"coding\":[{{\"system\":\"s\",\"code\":\"{clinical}\"}}]}},";
        Add($"{{\"resourceType\":\"AllergyIntolerance\",\"id\":\"{id}\",\"patient\":{{\"reference\":\"Patient/p\"}},{clinicalPart}"
            + $"\"verificationStatus\":{{\"coding\":[{{\"system\":\"s\",\"code\":\"{verification}\"}}]}},\"category\":[\"{category}\"],"
            + $"\"code\":{{\"coding\":[{{\"system\":\"{system}\",\"code\":\"{code}\"}}]}}}}");
    }

    private Answer Ask(string oracle, Dictionary<string, object> parameters) => Oracles.Compute(Context, new Question
    {
        Id = "q-ev",
        Tier = 1,
        Template = "t",
        Persona = "nursing",
        Text = "?",
        Patient = new PatientContext("MRN-p", "n", "1960-01-01"),
        Answer = new AnswerSpec { Type = AnswerType.Integer },
        Oracle = new OracleCall(oracle, new OracleParameters(parameters)),
        SourcePath = "q.yaml",
    });

    private Answer Vaccine(string returns, string? window = null, params string[] cvx)
    {
        var parameters = new Dictionary<string, object> { ["cvx"] = L(cvx), ["return"] = returns };
        if (window is not null)
        {
            parameters["window_days"] = window;
        }

        return Ask("immunization-history", parameters);
    }

    private Answer Procedure(string returns, string? window = null, params string[] snomed)
    {
        var parameters = new Dictionary<string, object> { ["snomed"] = L(snomed), ["return"] = returns };
        if (window is not null)
        {
            parameters["window_days"] = window;
        }

        return Ask("procedure-history", parameters);
    }

    private Answer Encounters(string returns, Dictionary<string, object>? extra = null)
    {
        var parameters = new Dictionary<string, object> { ["return"] = returns };
        foreach (var pair in extra ?? [])
        {
            parameters[pair.Key] = pair.Value;
        }

        return Ask("encounter-query", parameters);
    }

    private Answer Allergies(string returns, string? category = null, params string[] substances)
    {
        var parameters = new Dictionary<string, object> { ["return"] = returns };
        if (category is not null)
        {
            parameters["category"] = category;
        }

        if (substances.Length > 0)
        {
            parameters["substances"] = L(substances);
        }

        return Ask("allergy-check", parameters);
    }

    private static List<object> L(params string[] values) => values.Cast<object>().ToList();

    private static JsonElement Value(Answer answer) => answer.Value!.Value;

    [Fact]
    public void Immunization_LatestAndFirstDate_IgnoreOtherVaccinesAndIncompleteShots()
    {
        AddImmunization("a", "113", "2020-03-04T10:00:00Z");
        AddImmunization("b", "20", "2024-07-01T10:00:00Z");
        AddImmunization("c", "999", "2025-01-01T00:00:00Z");
        AddImmunization("d", "113", "2025-06-01T00:00:00Z", status: "not-done");
        AddImmunization("e", "113", null);

        var latest = Vaccine("latest_date", null, "113", "20");
        Assert.Equal("2024-07-01", Value(latest).GetString());
        Assert.Equal(new[] { "Immunization/b" }, latest.Evidence);
        Assert.Equal("2020-03-04", Value(Vaccine("first_date", null, "113", "20")).GetString());
    }

    [Fact]
    public void Immunization_Count_CountsResourcesInWindowOnly()
    {
        AddImmunization("old", "140", "2020-01-01T00:00:00Z");
        AddImmunization("recent", "140", "2025-12-20T00:00:00Z");
        AddImmunization("future", "140", "2026-03-01T00:00:00Z");

        Assert.Equal(1, Value(Vaccine("count", "365", "140")).GetInt32());
        Assert.Equal(2, Value(Vaccine("count", null, "140")).GetInt32());
    }

    [Fact]
    public void Immunization_DaysSinceLatest_IsWholeCalendarDays()
    {
        AddImmunization("late", "140", "2026-01-13T23:59:00Z");

        Assert.Equal(2, Value(Vaccine("days_since_latest", null, "140")).GetInt32());
    }

    [Fact]
    public void Immunization_NonUtcOffset_LatestDateAndDaysSinceUseTheRecordedOffsetDate()
    {
        AddImmunization("offset", "140", "2025-03-04T23:30:00-05:00");

        Assert.Equal("2025-03-04", Value(Vaccine("latest_date", null, "140")).GetString());
        Assert.Equal(317, Value(Vaccine("days_since_latest", null, "140")).GetInt32());
    }

    [Fact]
    public void Immunization_NoMatch_IsNullOrZero()
    {
        AddImmunization("x", "999", "2020-01-01T00:00:00Z");

        Assert.Null(Vaccine("latest_date", null, "140").Value);
        Assert.Null(Vaccine("days_since_latest", null, "140").Value);
        Assert.Equal(0, Value(Vaccine("count", null, "140")).GetInt32());
    }

    [Fact]
    public void Immunization_InvalidReturn_NamesQuestion()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Vaccine("bogus", null, "140"));

        Assert.Contains("q-ev", ex.Message);
    }

    [Fact]
    public void Procedure_LatestDateAndCount_UseCompletedProceduresAndPerformedPeriodStart()
    {
        AddProcedure("a", "80146002", "2022-02-02T00:00:00Z");
        AddProcedure("b", "80146002", "2024-07-01T00:00:00Z", asPeriod: true);
        AddProcedure("c", "80146002", "2025-01-01T00:00:00Z", status: "entered-in-error");
        AddProcedure("d", "999", "2025-05-01T00:00:00Z");
        AddProcedure("e", "80146002", "2026-06-01T00:00:00Z");

        var latest = Procedure("latest_date", null, "80146002");
        Assert.Equal("2024-07-01", Value(latest).GetString());
        Assert.Equal(new[] { "Procedure/b" }, latest.Evidence);
        Assert.Equal(2, Value(Procedure("count", null, "80146002")).GetInt32());
    }

    [Fact]
    public void Procedure_WindowDays_FiltersAndNoMatchIsNull()
    {
        AddProcedure("old", "80146002", "2020-01-01T00:00:00Z");

        Assert.Null(Procedure("latest_date", "365", "80146002").Value);
        Assert.Equal(0, Value(Procedure("count", "365", "80146002")).GetInt32());
        Assert.Equal("2020-01-01", Value(Procedure("latest_date", null, "80146002")).GetString());
    }

    [Fact]
    public void Encounter_CountAndLatestDate_ExcludeCancelledAndFilterByClassAndType()
    {
        AddEncounter("a", "2025-01-01T09:00:00Z", "AMB", "308335008");
        AddEncounter("b", "2025-06-01T09:00:00Z", "EMER", "50849002");
        AddEncounter("c", "2025-09-01T09:00:00Z", "AMB", "308335008", status: "cancelled");
        AddEncounter("d", "2025-10-01T09:00:00Z", "AMB", "50849002", status: "entered-in-error");

        Assert.Equal(2, Value(Encounters("count")).GetInt32());
        Assert.Equal(1, Value(Encounters("count", new() { ["class"] = L("EMER", "IMP") })).GetInt32());
        Assert.Equal(1, Value(Encounters("count", new() { ["class"] = "AMB" })).GetInt32());
        var latest = Encounters("latest_date", new() { ["type_snomed"] = L("308335008") });
        Assert.Equal("2025-01-01", Value(latest).GetString());
        Assert.Equal(new[] { "Encounter/a" }, latest.Evidence);
    }

    [Fact]
    public void Encounter_WindowDays_AndNoMatch()
    {
        AddEncounter("old", "2020-01-01T09:00:00Z");

        Assert.Equal(0, Value(Encounters("count", new() { ["window_days"] = "365" })).GetInt32());
        Assert.Null(Encounters("latest_date", new() { ["window_days"] = "365" }).Value);
    }

    [Fact]
    public void Allergy_Any_MatchesSubstancePairWithinCategoryAndSkipsInactiveOrRefuted()
    {
        AddAllergy("amox", Rx, "723");
        AddAllergy("inactive", Rx, "7984", clinical: "inactive");
        AddAllergy("refuted", Rx, "7980", verification: "refuted");
        AddAllergy("food", Sct, "91935009", category: "food");

        Assert.True(Value(Allergies("any", "medication", $"{Rx}|723")).GetBoolean());
        Assert.False(Value(Allergies("any", "medication", $"{Rx}|7984", $"{Rx}|7980")).GetBoolean());
        Assert.False(Value(Allergies("any", "medication", $"{Sct}|723")).GetBoolean());
        Assert.False(Value(Allergies("any", "environment")).GetBoolean());
    }

    [Fact]
    public void Allergy_Codes_ReturnsDistinctSortedPairsAndTreatsMissingClinicalStatusAsActive()
    {
        AddAllergy("b", Rx, "7984", clinical: null);
        AddAllergy("a", Rx, "1191");
        AddAllergy("dup", Rx, "1191");
        AddAllergy("food", Sct, "91935009", category: "food");

        var answer = Allergies("codes", "medication");

        Assert.Equal(new[] { "1191", "7984" }, Value(answer).EnumerateArray().Select(e => e.GetProperty("code").GetString()!).ToArray());
        Assert.Equal(Rx, Value(answer)[0].GetProperty("system").GetString());
        Assert.Equal(3, answer.Evidence.Count);
    }

    [Fact]
    public void Allergy_NoAllergies_AnyIsFalseAndCodesIsEmpty()
    {
        Assert.False(Value(Allergies("any")).GetBoolean());
        Assert.Equal(0, Value(Allergies("codes")).GetArrayLength());
    }

    [Fact]
    public void Allergy_MalformedSubstance_NamesQuestion()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Allergies("any", null, "7984"));

        Assert.Contains("q-ev", ex.Message);
    }
}
