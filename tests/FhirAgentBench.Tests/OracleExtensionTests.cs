using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class OracleExtensionTests : IDisposable
{
    private const string Rx = "http://www.nlm.nih.gov/research/umls/rxnorm";
    private const string Sct = "http://snomed.info/sct";

    private static readonly OracleRegistry Oracles = OracleRegistry.CreateDefault();

    private readonly string _directory = Directory.CreateTempSubdirectory("fab-ext-").FullName;
    private readonly List<string> _entries = [];
    private OracleContext? _context;

    public OracleExtensionTests() => AddPatient("p");

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

    private void AddPatient(string id, string extra = "") => Add(
        $"{{\"resourceType\":\"Patient\",\"id\":\"{id}\",\"identifier\":[{{\"system\":\"{Fixture.MrnSystem}\",\"value\":\"MRN-{id}\"}}],\"birthDate\":\"1980-01-01\"{extra}}}");

    private static string Quantity(string value, string unit) => $"{{\"value\":{value},\"unit\":\"{unit}\",\"code\":\"{unit}\"}}";

    private static string Concept(string loinc) => $"{{\"coding\":[{{\"system\":\"http://loinc.org\",\"code\":\"{loinc}\"}}]}}";

    private void AddObservation(string id, string loinc, string when, string value, string unit, string patient = "p") => AddRawObservation(
        id, patient, $"\"code\":{Concept(loinc)},\"effectiveDateTime\":\"{when}\",\"valueQuantity\":{Quantity(value, unit)}");

    private void AddRawObservation(string id, string patient, string body) => Add(
        $"{{\"resourceType\":\"Observation\",\"id\":\"{id}\",\"status\":\"final\",\"subject\":{{\"reference\":\"Patient/{patient}\"}},{body}}}");

    private void AddPanel(string id, string when, string systolic, string diastolic) => AddRawObservation(
        id, "p",
        $"\"code\":{Concept("85354-9")},\"effectiveDateTime\":\"{when}\",\"component\":["
        + $"{{\"code\":{Concept("8480-6")},\"valueQuantity\":{Quantity(systolic, "mm[Hg]")}}},"
        + $"{{\"code\":{Concept("8462-4")},\"valueQuantity\":{Quantity(diastolic, "mm[Hg]")}}}]");

    private void AddOrder(string id, string authoredOn, string rxnorm, string status = "active", string patient = "p") => Add(
        $"{{\"resourceType\":\"MedicationRequest\",\"id\":\"{id}\",\"status\":\"{status}\",\"intent\":\"order\",\"subject\":{{\"reference\":\"Patient/{patient}\"}},\"authoredOn\":\"{authoredOn}\",\"medicationCodeableConcept\":{{\"coding\":[{{\"system\":\"{Rx}\",\"code\":\"{rxnorm}\"}}]}}}}");

    private static Question Ask(string oracle, string? mrn, Dictionary<string, object> parameters) => new()
    {
        Id = "q-ext",
        Tier = 1,
        Template = "t",
        Persona = "nursing",
        Text = "?",
        Patient = mrn is null ? null : new PatientContext(mrn, "n", "1960-01-01"),
        Answer = new AnswerSpec { Type = AnswerType.Integer },
        Oracle = new OracleCall(oracle, new OracleParameters(parameters)),
        SourcePath = "q.yaml",
    };

    private Answer Latest(Dictionary<string, object> parameters) =>
        Oracles.Compute(Context, Ask("latest-observation", "MRN-p", parameters));

    private Answer Stats(Dictionary<string, object> parameters) =>
        Oracles.Compute(Context, Ask("observation-window-stats", "MRN-p", parameters));

    private Answer Ever(Dictionary<string, object> parameters) =>
        Oracles.Compute(Context, Ask("ever-on-medication", "MRN-p", parameters));

    private static List<object> L(params string[] values) => values.Cast<object>().ToList();

    private static JsonElement Value(Answer answer) => answer.Value!.Value;

    [Fact]
    public void LatestObservation_LoincList_TakesLatestAcrossAllListedCodes()
    {
        AddObservation("blood", "2339-0", "2026-01-10T00:00:00Z", "100", "mg/dL");
        AddObservation("serum", "2345-7", "2026-01-12T00:00:00Z", "110", "mg/dL");

        var gold = Latest(new() { ["loinc"] = L("2339-0", "2345-7") });

        Assert.Equal(110m, Value(gold).GetProperty("value").GetProperty("value").GetDecimal());
        Assert.Equal(new[] { "Observation/serum" }, gold.Evidence);
    }

    [Fact]
    public void LatestObservation_ReadsComponentCarriedLoinc()
    {
        AddPanel("panel", "2026-01-10T00:00:00Z", "130", "85");

        var gold = Latest(new() { ["loinc"] = "8480-6" });

        Assert.Equal(130m, Value(gold).GetProperty("value").GetProperty("value").GetDecimal());
        Assert.Equal("mm[Hg]", Value(gold).GetProperty("value").GetProperty("unit").GetString());
    }

    [Fact]
    public void LatestObservation_CodeLevelMatchWinsAndResourceYieldsOneReading()
    {
        AddRawObservation(
            "both", "p",
            $"\"code\":{Concept("8480-6")},\"effectiveDateTime\":\"2026-01-10T00:00:00Z\",\"valueQuantity\":{Quantity("111", "mm[Hg]")},"
            + $"\"component\":[{{\"code\":{Concept("8480-6")},\"valueQuantity\":{Quantity("222", "mm[Hg]")}}}]");

        var stats = Stats(new() { ["loinc"] = "8480-6", ["window_days"] = "30", ["statistic"] = "count", ["unit"] = "mm[Hg]" });
        var latest = Latest(new() { ["loinc"] = "8480-6" });

        Assert.Equal(1, Value(stats).GetInt32());
        Assert.Equal(111m, Value(latest).GetProperty("value").GetProperty("value").GetDecimal());
    }

    [Fact]
    public void LatestObservation_FirstMatchingComponentInArrayOrderSuppliesTheValue()
    {
        AddRawObservation(
            "multi", "p",
            $"\"code\":{Concept("85354-9")},\"effectiveDateTime\":\"2026-01-10T00:00:00Z\",\"component\":["
            + $"{{\"code\":{Concept("8480-6")},\"valueQuantity\":{Quantity("120", "mm[Hg]")}}},"
            + $"{{\"code\":{Concept("8480-6")},\"valueQuantity\":{Quantity("999", "mm[Hg]")}}}]");

        var gold = Latest(new() { ["loinc"] = "8480-6" });

        Assert.Equal(120m, Value(gold).GetProperty("value").GetProperty("value").GetDecimal());
    }

    [Fact]
    public void LatestObservation_InstantFallsBackFromEffectivePeriodStartToIssued()
    {
        AddRawObservation(
            "period", "p",
            $"\"code\":{Concept("4548-4")},\"effectivePeriod\":{{\"start\":\"2026-01-05T00:00:00Z\"}},\"issued\":\"2026-01-09T00:00:00Z\",\"valueQuantity\":{Quantity("7", "%")}");
        AddRawObservation(
            "issued", "p",
            $"\"code\":{Concept("4548-4")},\"issued\":\"2026-01-07T00:00:00Z\",\"valueQuantity\":{Quantity("8", "%")}");

        var gold = Latest(new() { ["loinc"] = "4548-4" });

        Assert.Equal("2026-01-07", Value(gold).GetProperty("date").GetString());
        Assert.Equal(new[] { "Observation/issued" }, gold.Evidence);
    }

    [Fact]
    public void LatestObservation_WindowDays_ExcludesOlderReadings()
    {
        AddObservation("old", "4548-4", "2025-11-01T00:00:00Z", "7", "%");

        Assert.Null(Latest(new() { ["loinc"] = "4548-4", ["window_days"] = "30" }).Value);
        Assert.NotNull(Latest(new() { ["loinc"] = "4548-4", ["window_days"] = "90" }).Value);
    }

    [Theory]
    [InlineData("29463-7", "kg", "[lb_av]", "70", 154.323583526)]
    [InlineData("8302-2", "cm", "[in_i]", "177.8", 70)]
    [InlineData("8310-5", "Cel", "[degF]", "37", 98.6)]
    [InlineData("2160-0", "mg/dL", "umol/L", "1.5", 132.6)]
    [InlineData("38483-4", "mg/dL", "umol/L", "2", 176.8)]
    [InlineData("2345-7", "mg/dL", "mmol/L", "180.16", 10)]
    public void LatestObservation_ConvertsToRequestedUnit(string loinc, string from, string to, string value, double expected)
    {
        AddObservation("o", loinc, "2026-01-10T00:00:00Z", value, from);

        var gold = Latest(new() { ["loinc"] = loinc, ["unit"] = to, ["return"] = "value" });

        Assert.Equal((double)expected, (double)Value(gold).GetProperty("value").GetDecimal(), 6);
        Assert.Equal(to, Value(gold).GetProperty("unit").GetString());
    }

    [Fact]
    public void LatestObservation_EgfrUnitSpellingsAreIdentical()
    {
        AddObservation("o", "33914-3", "2026-01-10T00:00:00Z", "75.5", "mL/min");

        var gold = Latest(new() { ["loinc"] = "33914-3", ["unit"] = "mL/min/{1.73_m2}", ["return"] = "value" });

        Assert.Equal(75.5m, Value(gold).GetProperty("value").GetDecimal());
        Assert.Equal("mL/min/{1.73_m2}", Value(gold).GetProperty("unit").GetString());
    }

    [Fact]
    public void LatestObservation_UnsupportedConversion_Throws()
    {
        AddObservation("o", "29463-7", "2026-01-10T00:00:00Z", "70", "kg");

        Assert.Throws<InvalidOperationException>(() => Latest(new() { ["loinc"] = "29463-7", ["unit"] = "mmol/L" }));
    }

    [Fact]
    public void LatestObservation_ReturnValue_IsTheQuantityOnly()
    {
        AddObservation("o", "4548-4", "2026-01-10T00:00:00Z", "7.2", "%");

        var gold = Latest(new() { ["loinc"] = "4548-4", ["return"] = "value" });

        Assert.Equal(7.2m, Value(gold).GetProperty("value").GetDecimal());
        Assert.False(Value(gold).TryGetProperty("date", out _));
    }

    [Fact]
    public void LatestObservation_ReturnCode_TakesLatestCodedValueInWindow()
    {
        AddRawObservation(
            "older", "p",
            $"\"code\":{Concept("72166-2")},\"effectiveDateTime\":\"2026-01-01T00:00:00Z\",\"valueCodeableConcept\":{{\"coding\":[{{\"system\":\"{Sct}\",\"code\":\"8517006\"}}]}}");
        AddRawObservation(
            "newer", "p",
            $"\"code\":{Concept("72166-2")},\"effectiveDateTime\":\"2026-01-10T00:00:00Z\",\"valueCodeableConcept\":{{\"coding\":[{{\"system\":\"{Sct}\",\"code\":\"449868002\"}}]}}");
        AddObservation("numeric-latest", "72166-2", "2026-01-12T00:00:00Z", "1", "1");

        var gold = Latest(new() { ["loinc"] = "72166-2", ["return"] = "code" });

        Assert.Equal("449868002", Value(gold).GetProperty("code").GetString());
        Assert.Equal(Sct, Value(gold).GetProperty("system").GetString());
        Assert.Equal(new[] { "Observation/newer" }, gold.Evidence);
    }

    [Fact]
    public void LatestObservation_ReturnCode_NothingCoded_IsNull()
    {
        AddObservation("numeric", "72166-2", "2026-01-12T00:00:00Z", "1", "1");

        Assert.Null(Latest(new() { ["loinc"] = "72166-2", ["return"] = "code" }).Value);
    }

    [Fact]
    public void LatestObservation_ReturnComponents_NamesEachComponentAndSkipsIncompletePanels()
    {
        AddPanel("complete", "2026-01-05T00:00:00Z", "120", "80");
        AddRawObservation(
            "partial", "p",
            $"\"code\":{Concept("85354-9")},\"effectiveDateTime\":\"2026-01-10T00:00:00Z\",\"component\":[{{\"code\":{Concept("8480-6")},\"valueQuantity\":{Quantity("150", "mm[Hg]")}}}]");
        var parameters = new Dictionary<string, object>
        {
            ["loinc"] = "85354-9",
            ["return"] = "components",
            ["components"] = new Dictionary<object, object> { ["systolic"] = "8480-6", ["diastolic"] = "8462-4" },
        };

        var gold = Latest(parameters);

        Assert.Equal(120m, Value(gold).GetProperty("systolic").GetProperty("value").GetDecimal());
        Assert.Equal("mm[Hg]", Value(gold).GetProperty("diastolic").GetProperty("unit").GetString());
        Assert.Equal("2026-01-05", Value(gold).GetProperty("date").GetString());
        Assert.Equal(new[] { "Observation/complete" }, gold.Evidence);
    }

    [Fact]
    public void LatestObservation_ReturnComponents_NoPanels_IsNull()
    {
        var parameters = new Dictionary<string, object>
        {
            ["loinc"] = "85354-9",
            ["return"] = "components",
            ["components"] = new Dictionary<object, object> { ["systolic"] = "8480-6" },
        };

        Assert.Null(Latest(parameters).Value);
    }

    [Fact]
    public void LatestObservation_InvalidReturnOrMissingComponents_NameQuestionAndParameter()
    {
        var badReturn = Assert.Throws<InvalidDataException>(() => Latest(new() { ["loinc"] = "4548-4", ["return"] = "bogus" }));
        var noComponents = Assert.Throws<InvalidDataException>(() => Latest(new() { ["loinc"] = "85354-9", ["return"] = "components" }));

        Assert.Contains("q-ext", badReturn.Message);
        Assert.Contains("return", badReturn.Message);
        Assert.Contains("q-ext", noComponents.Message);
        Assert.Contains("components", noComponents.Message);
    }

    [Fact]
    public void WindowStats_LoincListAndComponentCarriedReadings()
    {
        AddPanel("a", "2026-01-10T00:00:00Z", "120", "80");
        AddObservation("b", "8480-6", "2026-01-11T00:00:00Z", "140", "mm[Hg]");

        var gold = Stats(new() { ["loinc"] = L("8480-6", "8459-0"), ["window_days"] = "30", ["statistic"] = "mean", ["unit"] = "mm[Hg]" });

        Assert.Equal(130m, Value(gold).GetDecimal());
    }

    [Fact]
    public void WindowStats_Change_IsLatestMinusEarliestRoundedToTwoDecimals()
    {
        AddObservation("first", "4548-4", "2026-01-01T00:00:00Z", "7.123", "%");
        AddObservation("mid", "4548-4", "2026-01-05T00:00:00Z", "9", "%");
        AddObservation("last", "4548-4", "2026-01-10T00:00:00Z", "6.5", "%");

        var gold = Stats(new() { ["loinc"] = "4548-4", ["window_days"] = "30", ["statistic"] = "change", ["unit"] = "%" });

        Assert.Equal(-0.62m, Value(gold).GetDecimal());
        Assert.Equal(3, gold.Evidence.Count);
    }

    [Fact]
    public void WindowStats_PercentChange_IsRelativeToEarliest()
    {
        AddObservation("first", "4548-4", "2026-01-01T00:00:00Z", "8", "%");
        AddObservation("last", "4548-4", "2026-01-10T00:00:00Z", "10", "%");

        var gold = Stats(new() { ["loinc"] = "4548-4", ["window_days"] = "30", ["statistic"] = "percent_change", ["unit"] = "%" });

        Assert.Equal(25m, Value(gold).GetDecimal());
    }

    [Theory]
    [InlineData("change")]
    [InlineData("percent_change")]
    public void WindowStats_ChangeNeedsTwoReadings(string statistic)
    {
        AddObservation("only", "4548-4", "2026-01-10T00:00:00Z", "8", "%");

        Assert.Null(Stats(new() { ["loinc"] = "4548-4", ["window_days"] = "30", ["statistic"] = statistic, ["unit"] = "%" }).Value);
    }

    [Theory]
    [InlineData("gt", 2)]
    [InlineData("ge", 3)]
    [InlineData("lt", 1)]
    [InlineData("le", 2)]
    public void WindowStats_ComparatorFiltersBeforeCounting(string comparator, int expected)
    {
        AddObservation("a", "4548-4", "2026-01-01T00:00:00Z", "6", "%");
        AddObservation("b", "4548-4", "2026-01-02T00:00:00Z", "7", "%");
        AddObservation("c", "4548-4", "2026-01-03T00:00:00Z", "8", "%");
        AddObservation("d", "4548-4", "2026-01-04T00:00:00Z", "9", "%");

        var gold = Stats(new()
        {
            ["loinc"] = "4548-4", ["window_days"] = "30", ["statistic"] = "count", ["unit"] = "%",
            ["comparator"] = comparator, ["threshold"] = "7",
        });

        Assert.Equal(expected, Value(gold).GetInt32());
    }

    [Fact]
    public void WindowStats_ComparatorAppliesToConvertedValueBeforeMax()
    {
        AddObservation("a", "2339-0", "2026-01-10T00:00:00Z", "5", "mmol/L");
        AddObservation("b", "2339-0", "2026-01-11T00:00:00Z", "100", "mg/dL");

        var gold = Stats(new()
        {
            ["loinc"] = "2339-0", ["window_days"] = "30", ["statistic"] = "max", ["unit"] = "mg/dL",
            ["comparator"] = "lt", ["threshold"] = "95",
        });

        Assert.Equal(90.08m, Value(gold).GetDecimal());
    }

    [Fact]
    public void WindowStats_ComparatorWithoutThresholdOrUnknownComparator_Throws()
    {
        var unknown = Assert.Throws<InvalidDataException>(() =>
            Stats(new() { ["loinc"] = "4548-4", ["window_days"] = "30", ["statistic"] = "count", ["unit"] = "%", ["comparator"] = "eq", ["threshold"] = "1" }));
        var missing = Assert.Throws<InvalidDataException>(() =>
            Stats(new() { ["loinc"] = "4548-4", ["window_days"] = "30", ["statistic"] = "count", ["unit"] = "%", ["comparator"] = "gt" }));

        Assert.Contains("q-ext", unknown.Message);
        Assert.Contains("comparator", unknown.Message);
        Assert.Contains("threshold", missing.Message);
    }

    [Fact]
    public void WindowStats_NoReadings_CountIsZeroAndOthersNull()
    {
        var count = Stats(new() { ["loinc"] = "4548-4", ["window_days"] = "30", ["statistic"] = "count", ["unit"] = "%" });
        var change = Stats(new() { ["loinc"] = "4548-4", ["window_days"] = "30", ["statistic"] = "change", ["unit"] = "%" });

        Assert.Equal(0, Value(count).GetInt32());
        Assert.Null(change.Value);
    }

    [Fact]
    public void EverOnMedication_WindowDays_FiltersOnAuthoredOn()
    {
        AddOrder("old", "2025-06-01T00:00:00Z", "111");
        AddOrder("new", "2026-01-05T00:00:00Z", "111");

        var gold = Ever(new() { ["rxnorm"] = L("111"), ["window_days"] = "30" });

        Assert.True(Value(gold).GetProperty("ever").GetBoolean());
        Assert.Equal("2026-01-05", Value(gold).GetProperty("first").GetString());
        Assert.Equal(new[] { "MedicationRequest/new" }, gold.Evidence);
    }

    [Fact]
    public void EverOnMedication_ReturnModes()
    {
        AddOrder("a", "2025-06-01T00:00:00Z", "111");
        AddOrder("b", "2025-09-01T00:00:00Z", "111", status: "completed");
        AddOrder("c", "2025-12-01T00:00:00Z", "111");
        AddOrder("cancelled", "2025-12-20T00:00:00Z", "111", status: "cancelled");
        AddOrder("other", "2025-12-25T00:00:00Z", "222");

        Assert.Equal("2025-06-01", Value(Ever(new() { ["rxnorm"] = L("111"), ["return"] = "first_date" })).GetString());
        Assert.Equal("2025-12-01", Value(Ever(new() { ["rxnorm"] = L("111"), ["return"] = "latest_date" })).GetString());
        Assert.Equal(3, Value(Ever(new() { ["rxnorm"] = L("111"), ["return"] = "order_count" })).GetInt32());
    }

    [Fact]
    public void EverOnMedication_NoMatch_DatesAreNullAndCountIsZero()
    {
        Assert.Null(Ever(new() { ["rxnorm"] = L("111"), ["return"] = "first_date" }).Value);
        Assert.Null(Ever(new() { ["rxnorm"] = L("111"), ["return"] = "latest_date" }).Value);
        Assert.Equal(0, Value(Ever(new() { ["rxnorm"] = L("111"), ["return"] = "order_count" })).GetInt32());
    }

    [Fact]
    public void EverOnMedication_InvalidReturn_NamesQuestionAndParameter()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Ever(new() { ["rxnorm"] = L("111"), ["return"] = "bogus" }));

        Assert.Contains("q-ext", ex.Message);
        Assert.Contains("return", ex.Message);
    }

    private void AddCohortPatient(string id, string? labValue, string labLoinc = "13457-7")
    {
        AddPatient(id);
        Add($"{{\"resourceType\":\"Condition\",\"id\":\"c-{id}\",\"subject\":{{\"reference\":\"Patient/{id}\"}},\"code\":{{\"coding\":[{{\"system\":\"{Sct}\",\"code\":\"414545008\"}}]}}}}");
        if (labValue is not null)
        {
            AddObservation($"o-{id}", labLoinc, "2026-01-10T00:00:00Z", labValue, "mg/dL", id);
        }
    }

    private Answer Cohort(string comparator)
    {
        var parameters = new Dictionary<string, object>
        {
            ["snomed"] = L("414545008"), ["loinc"] = L("18262-6", "13457-7"), ["unit"] = "mg/dL", ["threshold"] = "70",
            ["window_days"] = "365", ["age_min"] = "18", ["age_max"] = "100",
        };
        if (comparator.Length > 0)
        {
            parameters["comparator"] = comparator;
        }

        return Oracles.Compute(Context, Ask("cohort-lab-above-or-missing", null, parameters));
    }

    [Fact]
    public void Cohort_LoincListAndComparator()
    {
        AddCohortPatient("at", "70");
        AddCohortPatient("above", "90", "18262-6");
        AddCohortPatient("below", "50");
        AddCohortPatient("missing", null);

        Assert.Equal(2, Value(Cohort("")).GetInt32());
        Assert.Equal(2, Value(Cohort("gt")).GetInt32());
        Assert.Equal(3, Value(Cohort("ge")).GetInt32());
        Assert.Equal(2, Value(Cohort("lt")).GetInt32());
        Assert.Equal(3, Value(Cohort("le")).GetInt32());
    }

    [Fact]
    public void Cohort_UnknownComparator_NamesQuestionAndParameter()
    {
        AddCohortPatient("x", "1");

        var ex = Assert.Throws<InvalidDataException>(() => Cohort("eq"));

        Assert.Contains("q-ext", ex.Message);
        Assert.Contains("comparator", ex.Message);
    }
}
