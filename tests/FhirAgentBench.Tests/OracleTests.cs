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
            ["unit"] = "%",
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
