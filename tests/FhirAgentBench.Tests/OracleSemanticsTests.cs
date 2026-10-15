using System.Text;
using System.Text.Json;
using FhirAgentBench.Answers;
using FhirAgentBench.Corpus;
using FhirAgentBench.Oracles;
using FhirAgentBench.Questions;

namespace FhirAgentBench.Tests;

public sealed class OracleSemanticsTests : IDisposable
{
    private const string Rx = "http://www.nlm.nih.gov/research/umls/rxnorm";
    private const string Sct = "http://snomed.info/sct";

    private static readonly OracleRegistry Oracles = OracleRegistry.CreateDefault();

    private readonly string _directory = Directory.CreateTempSubdirectory("fab-oracle-").FullName;
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

    private void AddPatient(string id, string extra = "") => Add(
        $"{{\"resourceType\":\"Patient\",\"id\":\"{id}\",\"identifier\":[{{\"system\":\"{Fixture.MrnSystem}\",\"value\":\"MRN-{id}\"}}],\"birthDate\":\"1980-01-01\"{extra}}}");

    private void AddObservation(string id, string patient, string loinc, string when, string valueJson, string unit) => Add(
        $"{{\"resourceType\":\"Observation\",\"id\":\"{id}\",\"status\":\"final\",\"code\":{{\"coding\":[{{\"system\":\"http://loinc.org\",\"code\":\"{loinc}\"}}]}},\"subject\":{{\"reference\":\"Patient/{patient}\"}},\"effectiveDateTime\":\"{when}\",\"valueQuantity\":{{\"value\":{valueJson},\"unit\":\"{unit}\",\"code\":\"{unit}\"}}}}");

    private void AddOrder(string id, string patient, string status, string authoredOn, params string[] rxnorm) => Add(
        $"{{\"resourceType\":\"MedicationRequest\",\"id\":\"{id}\",\"status\":\"{status}\",\"intent\":\"order\",\"subject\":{{\"reference\":\"Patient/{patient}\"}},\"authoredOn\":\"{authoredOn}\",\"medicationCodeableConcept\":{{\"coding\":[{string.Join(",", rxnorm.Select(c => $"{{\"system\":\"{Rx}\",\"code\":\"{c}\"}}"))}]}}}}");

    private void AddCondition(string id, string patient, string extra, string snomed = "44054006") => Add(
        $"{{\"resourceType\":\"Condition\",\"id\":\"{id}\",\"subject\":{{\"reference\":\"Patient/{patient}\"}},\"code\":{{\"coding\":[{{\"system\":\"{Sct}\",\"code\":\"{snomed}\"}}]}}{extra}}}");

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

    private static string StringDate(Answer gold, string property) => gold.Value!.Value.GetProperty(property).GetString()!;

    [Fact]
    public void LatestObservation_ReportsDateInTheResultsOwnOffset()
    {
        AddPatient("p");
        AddObservation("late", "p", "4548-4", "2025-12-01T21:30:00-05:00", "7.0", "%");

        var gold = Oracles.Compute(Context, Ask("latest-observation", "MRN-p", new() { ["loinc"] = "4548-4" }));

        Assert.Equal("2025-12-01", StringDate(gold, "date"));
    }

    [Fact]
    public void LatestObservation_TieOnTimeBreaksByResourceKey()
    {
        AddPatient("p");
        AddObservation("t-b", "p", "4548-4", "2025-12-01T09:00:00+00:00", "8.0", "%");
        AddObservation("t-a", "p", "4548-4", "2025-12-01T09:00:00+00:00", "7.0", "%");

        var gold = Oracles.Compute(Context, Ask("latest-observation", "MRN-p", new() { ["loinc"] = "4548-4" }));

        Assert.Equal(new[] { "Observation/t-a" }, gold.Evidence);
    }

    [Fact]
    public void LatestObservation_NonNumericValue_NamesTheResource()
    {
        AddPatient("p");
        AddObservation("bad", "p", "4548-4", "2025-12-01T09:00:00+00:00", "\"high\"", "%");

        var ex = Assert.Throws<InvalidDataException>(() =>
            Oracles.Compute(Context, Ask("latest-observation", "MRN-p", new() { ["loinc"] = "4548-4" })));

        Assert.Contains("Observation/bad", ex.Message);
    }

    [Fact]
    public void WindowStats_ConvertsMixedGlucoseUnits()
    {
        AddPatient("p");
        AddObservation("g1", "p", "2345-7", "2026-01-14T08:00:00+00:00", "180", "mg/dL");
        AddObservation("g2", "p", "2345-7", "2026-01-14T12:00:00+00:00", "5", "mmol/L");
        var parameters = new Dictionary<string, object>
        {
            ["loinc"] = "2345-7", ["window_days"] = "1", ["statistic"] = "mean", ["unit"] = "mg/dL",
        };

        var gold = Oracles.Compute(Context, Ask("observation-window-stats", "MRN-p", parameters));

        Assert.Equal(135.04m, gold.Value!.Value.GetDecimal());
    }

    [Fact]
    public void WindowStats_UnconvertibleUnit_ThrowsNamingResourceAndUnits()
    {
        AddPatient("p");
        AddObservation("odd", "p", "4548-4", "2026-01-14T08:00:00+00:00", "50", "mmol/mol");
        var parameters = new Dictionary<string, object>
        {
            ["loinc"] = "4548-4", ["window_days"] = "30", ["statistic"] = "max", ["unit"] = "%",
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Oracles.Compute(Context, Ask("observation-window-stats", "MRN-p", parameters)));

        Assert.Contains("Observation/odd", ex.Message);
        Assert.Contains("mmol/mol", ex.Message);
        Assert.Contains("'%'", ex.Message);
    }

    [Fact]
    public void WindowStats_UnitMatchingIsCaseSensitive()
    {
        AddPatient("p");
        AddObservation("g", "p", "2345-7", "2026-01-14T08:00:00+00:00", "100", "MG/DL");
        var parameters = new Dictionary<string, object>
        {
            ["loinc"] = "2345-7", ["window_days"] = "30", ["statistic"] = "max", ["unit"] = "mg/dL",
        };

        Assert.Throws<InvalidOperationException>(() => Oracles.Compute(Context, Ask("observation-window-stats", "MRN-p", parameters)));
    }

    [Fact]
    public void WindowStats_UnknownStatistic_ThrowsEvenWhenWindowIsEmpty()
    {
        AddPatient("p");
        var parameters = new Dictionary<string, object>
        {
            ["loinc"] = "4548-4", ["window_days"] = "30", ["statistic"] = "median", ["unit"] = "%",
        };

        var ex = Assert.Throws<InvalidDataException>(() => Oracles.Compute(Context, Ask("observation-window-stats", "MRN-p", parameters)));

        Assert.Contains("median", ex.Message);
    }

    [Fact]
    public void EverOnMedication_MatchesAnyRxNormCodingOfTheConcept()
    {
        AddPatient("p");
        AddOrder("mr", "p", "active", "2025-01-02T00:00:00+00:00", "111", "222");

        var gold = Oracles.Compute(Context, Ask("ever-on-medication", "MRN-p", new() { ["rxnorm"] = new List<object> { "222" } }));

        Assert.True(gold.Value!.Value.GetProperty("ever").GetBoolean());
    }

    [Fact]
    public void EverOnMedication_IgnoresCancelledAndDraftButKeepsStopped()
    {
        AddPatient("p");
        AddOrder("c", "p", "cancelled", "2020-01-01T00:00:00+00:00", "111");
        AddOrder("d", "p", "draft", "2021-01-01T00:00:00+00:00", "111");
        AddOrder("s", "p", "stopped", "2022-03-04T00:00:00+00:00", "111");

        var gold = Oracles.Compute(Context, Ask("ever-on-medication", "MRN-p", new() { ["rxnorm"] = new List<object> { "111" } }));

        Assert.Equal("2022-03-04", StringDate(gold, "first"));
        Assert.Equal(new[] { "MedicationRequest/s" }, gold.Evidence);
    }

    [Fact]
    public void MedicationsStartedWithin_ExcludesRenewalOfLongStandingDrug()
    {
        AddPatient("p");
        AddOrder("old", "p", "completed", "2023-01-10T00:00:00+00:00", "111");
        AddOrder("renewal", "p", "active", "2025-12-15T00:00:00+00:00", "111");
        AddOrder("new", "p", "active", "2025-12-01T00:00:00+00:00", "222");
        AddOrder("stopped", "p", "stopped", "2025-11-01T00:00:00+00:00", "444");

        var gold = Oracles.Compute(Context, Ask("medications-started-within", "MRN-p", new() { ["window_days"] = "365" }));

        Assert.Equal(new[] { "222", "444" }, gold.Value!.Value.EnumerateArray().Select(i => i.GetProperty("code").GetString()));
    }

    [Fact]
    public void MedicationsStartedWithin_CancelledAndDraftOrdersDoNotCount()
    {
        AddPatient("p");
        AddOrder("c", "p", "cancelled", "2025-12-01T00:00:00+00:00", "333");
        AddOrder("d", "p", "draft", "2025-12-02T00:00:00+00:00", "334");
        AddOrder("early-cancelled", "p", "cancelled", "2023-01-01T00:00:00+00:00", "777");
        AddOrder("real", "p", "active", "2025-12-03T00:00:00+00:00", "777");

        var gold = Oracles.Compute(Context, Ask("medications-started-within", "MRN-p", new() { ["window_days"] = "365" }));

        Assert.Equal("777", Assert.Single(gold.Value!.Value.EnumerateArray()).GetProperty("code").GetString());
    }

    private Answer Cohort(string snomed, string loinc = "4548-4", string unit = "%", string threshold = "9") =>
        Oracles.Compute(Context, Ask("cohort-lab-above-or-missing", null, new()
        {
            ["snomed"] = new List<object> { snomed },
            ["loinc"] = loinc,
            ["unit"] = unit,
            ["threshold"] = threshold,
            ["window_days"] = "365",
            ["age_min"] = "18",
            ["age_max"] = "75",
        }));

    [Fact]
    public void Cohort_ConditionStatusAndAbatementRules()
    {
        string[] excluded =
        [
            "x-inactive", "x-resolved", "x-remission", "x-abperiod", "x-abbool", "x-abdate", "x-refuted", "x-future",
        ];
        string[] included = ["i-active", "i-noonset", "i-period", "i-futureabate"];
        foreach (var id in excluded.Concat(included))
        {
            AddPatient(id);
        }

        static string Status(string code) => $",\"clinicalStatus\":{{\"coding\":[{{\"system\":\"http://terminology.hl7.org/CodeSystem/condition-clinical\",\"code\":\"{code}\"}}]}},\"onsetDateTime\":\"2020-01-01\"";
        AddCondition("c1", "x-inactive", Status("inactive"));
        AddCondition("c2", "x-resolved", Status("resolved"));
        AddCondition("c3", "x-remission", Status("remission"));
        AddCondition("c4", "x-abperiod", ",\"onsetDateTime\":\"2020-01-01\",\"abatementPeriod\":{\"start\":\"2020-02-01\",\"end\":\"2021-01-01\"}");
        AddCondition("c5", "x-abbool", ",\"abatementBoolean\":true");
        AddCondition("c6", "x-abdate", ",\"abatementDateTime\":\"2021-01-01\"");
        AddCondition("c7", "x-refuted", ",\"verificationStatus\":{\"coding\":[{\"system\":\"http://terminology.hl7.org/CodeSystem/condition-ver-status\",\"code\":\"refuted\"}]}");
        AddCondition("c8", "x-future", ",\"onsetDateTime\":\"2027-01-01\"");
        AddCondition("c9", "i-active", Status("active"));
        AddCondition("c10", "i-noonset", "");
        AddCondition("c11", "i-period", ",\"onsetPeriod\":{\"start\":\"2020-01-01\"}");
        AddCondition("c12", "i-futureabate", ",\"onsetDateTime\":\"2020-01-01\",\"abatementDateTime\":\"2027-01-01\"");

        var gold = Cohort("44054006");

        Assert.Equal(included.Select(id => $"Patient/{id}").Order(), gold.Evidence.Order());
    }

    [Fact]
    public void Cohort_ExcludesDeceasedBoolean()
    {
        AddPatient("alive");
        AddPatient("dead", ",\"deceasedBoolean\":true");
        AddCondition("c1", "alive", ",\"onsetDateTime\":\"2020-01-01\"");
        AddCondition("c2", "dead", ",\"onsetDateTime\":\"2020-01-01\"");

        Assert.Equal(new[] { "Patient/alive" }, Cohort("44054006").Evidence);
    }

    [Fact]
    public void Cohort_ComparesThresholdInTheRequestedUnit()
    {
        AddPatient("low");
        AddPatient("high");
        AddCondition("c1", "low", "", "99999");
        AddCondition("c2", "high", "", "99999");
        AddObservation("g1", "low", "2339-0", "2025-12-01T08:00:00+00:00", "6.0", "mmol/L");
        AddObservation("g2", "high", "2339-0", "2025-12-01T08:00:00+00:00", "7.0", "mmol/L");

        var gold = Cohort("99999", loinc: "2339-0", unit: "mg/dL", threshold: "120");

        Assert.Equal(new[] { "Patient/high" }, gold.Evidence);
    }
}
